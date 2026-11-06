namespace PayGate.Testing;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

/// <summary>
/// An in-memory gateway for tests and local development. It simulates the redirect, client-SDK and pay-later flows, and tests
/// drive each payment with <see cref="Succeed"/>, <see cref="Fail"/>, <see cref="Expire"/> and <see cref="TimeOutNextWrite"/>.
/// </summary>
/// <remarks>Thread-safe. Register it with <see cref="PayGateBuilderFakeExtensions.AddPayGateFake"/>, which also refuses to start in Production.</remarks>
public sealed class FakePaymentGateway : IPaymentGateway, IPaymentCancelFeature
{
    private static readonly TimeSpan _displayReferenceLifetime = TimeSpan.FromHours(48);

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, FakePayment> _payments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakePayment> _byReference = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefundResult> _refundsByReference = new(StringComparer.Ordinal);
    private readonly List<FakeWriteAttempt> _writeAttempts = [];
    private int _lastId;
    private bool _timeOutNextWrite;

    /// <summary>Creates a fake gateway.</summary>
    /// <param name="options">The settings. Defaults to the redirect flow.</param>
    /// <param name="accountName">The account name under <see cref="ProviderId"/>.</param>
    /// <param name="timeProvider">The clock, for expiry. Defaults to the system clock.</param>
    public FakePaymentGateway(
        FakeGatewayOptions? options = null,
        string accountName = PaymentAccount.DefaultName,
        TimeProvider? timeProvider = null)
    {
        Options = options ?? new FakeGatewayOptions();
        Account = new PaymentAccount(ProviderId, accountName);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Capabilities = new PaymentCapabilities
        {
            SupportedNextActions = new HashSet<NextActionKind> { KindOf(Options.Flow) },
            SupportedPaymentMethods = new HashSet<PaymentMethodType> { PaymentMethodType.Card, PaymentMethodType.Wallet, PaymentMethodType.CashReference },
            LookupByMerchantReference = Options.LookupByMerchantReference,
            IdempotentWrites = Options.IdempotentWrites,
            PartialRefund = true,
            MultipleRefunds = true,
            PaymentExpiry = true,
            PerRequestNotificationUrl = true,
            MetadataPassthrough = true,
        };
    }

    /// <summary>The fake provider's id, <c>fake</c>.</summary>
    public static PaymentProviderId ProviderId { get; } = new("fake");

    /// <inheritdoc/>
    public PaymentAccount Account { get; }

    /// <inheritdoc/>
    public PaymentCapabilities Capabilities { get; }

    /// <summary>Every write that reached the fake, retries included, oldest first.</summary>
    public IReadOnlyList<FakeWriteAttempt> WriteAttempts
    {
        get
        {
            lock (_gate)
            {
                return [.. _writeAttempts];
            }
        }
    }

    internal FakeGatewayOptions Options { get; }

    internal DateTimeOffset Now => _timeProvider.GetUtcNow();

    /// <inheritdoc/>
    public Task<PaymentResult<PaymentSession>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Write("create", request.IdempotencyKey ?? request.MerchantReference, () => Create(request)));
    }

    /// <inheritdoc/>
    public Task<PaymentResult<PaymentDetails>> GetPaymentAsync(PaymentLookup lookup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        cancellationToken.ThrowIfCancellationRequested();

        if (lookup.Kind == PaymentLookupKind.MerchantReference && !Capabilities.LookupByMerchantReference)
        {
            return Task.FromResult(PaymentResult.Failure<PaymentDetails>(PaymentError.NotSupported("This fake does not look up by merchant reference.")));
        }

        lock (_gate)
        {
            var payments = lookup.Kind == PaymentLookupKind.MerchantReference ? _byReference : _payments;
            if (!payments.TryGetValue(lookup.Value, out var payment))
            {
                return Task.FromResult(PaymentResult.Failure<PaymentDetails>(NotFound(lookup.Value)));
            }

            ExpireIfDue(payment);
            return Task.FromResult(PaymentResult.Success(Details(payment)));
        }
    }

    /// <inheritdoc/>
    public Task<PaymentResult<RefundResult>> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Write("refund", request.MerchantRefundReference, () => Refund(request)));
    }

    /// <inheritdoc/>
    public Task<PaymentResult<PaymentCancelResult>> CancelAsync(PaymentCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Write("cancel", key: null, () => Cancel(request)));
    }

    /// <inheritdoc/>
    public bool TryGetFeature<TFeature>([NotNullWhen(true)] out TFeature? feature)
        where TFeature : class
    {
        feature = this as TFeature;
        return feature is not null;
    }

    /// <summary>The customer paid.</summary>
    /// <param name="providerPaymentId">The payment.</param>
    /// <exception cref="InvalidOperationException">The payment does not exist, or its status cannot move to succeeded.</exception>
    public void Succeed(string providerPaymentId) => Move(providerPaymentId, PaymentStatus.Succeeded, payment =>
        payment.TransactionId ??= $"fake_txn_{NextId()}");

    /// <summary>The payment failed.</summary>
    /// <param name="providerPaymentId">The payment.</param>
    /// <param name="message">The failure message.</param>
    /// <exception cref="InvalidOperationException">The payment does not exist, or its status cannot move to failed.</exception>
    public void Fail(string providerPaymentId, string message = "Declined by the fake gateway.") =>
        Move(providerPaymentId, PaymentStatus.Failed, payment =>
            payment.Failure = new PaymentFailureReason { Code = PaymentErrorCode.Declined, Message = message });

    /// <summary>The payment expired before the customer paid.</summary>
    /// <param name="providerPaymentId">The payment.</param>
    /// <exception cref="InvalidOperationException">The payment does not exist, or its status cannot move to expired.</exception>
    public void Expire(string providerPaymentId) => Move(providerPaymentId, PaymentStatus.Expired, _ => { });

    /// <summary>The provider starts reporting a status PayGate does not know.</summary>
    /// <param name="providerPaymentId">The payment.</param>
    /// <param name="rawStatus">The provider's own status.</param>
    /// <exception cref="InvalidOperationException">The payment does not exist.</exception>
    public void ReportUnknownStatus(string providerPaymentId, string rawStatus)
    {
        lock (_gate)
        {
            var payment = Find(providerPaymentId);
            payment.Status = PaymentStatus.Unknown;
            payment.RawStatus = rawStatus;
            payment.UpdatedAt = Now;
        }
    }

    /// <summary>
    /// Makes the next write take effect and then lose its response. Without <see cref="FakeGatewayOptions.IdempotentWrites"/> it
    /// returns <see cref="PaymentErrorCode.OutcomeUnknown"/>. With them, and an idempotency key, the fake retries with the same key.
    /// </summary>
    public void TimeOutNextWrite()
    {
        lock (_gate)
        {
            _timeOutNextWrite = true;
        }
    }

    internal FakeWebhookPayload Payload(string providerPaymentId, Webhooks.PaymentEventType? type)
    {
        lock (_gate)
        {
            return FakeWebhookPayload.From(Find(providerPaymentId), type);
        }
    }

    private static NextActionKind KindOf(FakeFlow flow) => flow switch
    {
        FakeFlow.ClientSdk => NextActionKind.ClientSdk,
        FakeFlow.DisplayReference => NextActionKind.DisplayReference,
        _ => NextActionKind.Redirect,
    };

    private static PaymentError NotFound(string id) => new() { Code = PaymentErrorCode.NotFound, Message = $"The fake has no payment '{id}'." };

    private static PaymentDetails Details(FakePayment payment) => new()
    {
        ProviderPaymentId = payment.Id,
        ProviderTransactionId = payment.TransactionId,
        MerchantReference = payment.Request.MerchantReference,
        Status = payment.Status,
        RawStatus = payment.RawStatus,
        Amount = payment.Request.Amount,
        CapturedAmount = payment.TransactionId is null ? null : payment.Request.Amount,
        RefundedAmount = payment.Refunded,
        Failure = payment.Failure,
        CreatedAt = payment.CreatedAt,
        UpdatedAt = payment.UpdatedAt,
    };

    private PaymentResult<T> Write<T>(string operation, string? key, Func<PaymentResult<T>> apply)
    {
        lock (_gate)
        {
            _writeAttempts.Add(new FakeWriteAttempt(operation, key));
            var result = apply();
            if (!_timeOutNextWrite)
            {
                return result;
            }

            _timeOutNextWrite = false;
            if (Options.IdempotentWrites && key is not null)
            {
                _writeAttempts.Add(new FakeWriteAttempt(operation, key));
                return result;
            }

            return PaymentError.OutcomeUnknown($"The fake's {operation} timed out.");
        }
    }

    private PaymentResult<PaymentSession> Create(CreatePaymentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MerchantReference))
        {
            return PaymentError.Validation("A merchant reference is required.");
        }

        if (request.Amount.IsZero)
        {
            return PaymentError.Validation("The amount must be positive.");
        }

        if (_byReference.TryGetValue(request.MerchantReference, out var existing))
        {
            bool sameRequest = existing.Request.Amount == request.Amount
                && string.Equals(existing.Request.Description, request.Description, StringComparison.Ordinal);
            return Options.IdempotentWrites && sameRequest
                ? existing.Session
                : PaymentError.DuplicateReference($"The merchant reference '{request.MerchantReference}' was already used.");
        }

        int id = NextId();
        string paymentId = $"fake_pay_{id}";
        var now = Now;
        var expiresAt = request.ExpiresAt ?? (Options.Flow == FakeFlow.DisplayReference ? now + _displayReferenceLifetime : null);
        var session = new PaymentSession
        {
            ProviderPaymentId = paymentId,
            Status = PaymentStatus.Pending,
            NextAction = NextActionFor(id, paymentId, expiresAt),
            ExpiresAt = expiresAt,
        };

        var payment = new FakePayment(paymentId, request, session, now);
        _payments.Add(paymentId, payment);
        _byReference.Add(request.MerchantReference, payment);
        return session;
    }

    private NextAction NextActionFor(int id, string paymentId, DateTimeOffset? expiresAt) => Options.Flow switch
    {
        FakeFlow.ClientSdk => new ClientSdkAction { ClientSecret = $"{paymentId}_secret", PublicKey = "pk_fake" },
        FakeFlow.DisplayReference => new DisplayReferenceAction
        {
            Code = id.ToString("D9", CultureInfo.InvariantCulture),
            ExpiresAt = expiresAt,
            Instructions = "Pay this code at any fake kiosk.",
        },
        _ => new RedirectAction { Url = new Uri($"https://fake.paygate.invalid/pay/{paymentId}") },
    };

    private PaymentResult<RefundResult> Refund(RefundRequest request)
    {
        if (!_payments.TryGetValue(request.ProviderPaymentId, out var payment))
        {
            return NotFound(request.ProviderPaymentId);
        }

        if (request.MerchantRefundReference is { } reference
            && Options.IdempotentWrites
            && _refundsByReference.TryGetValue(reference, out var earlier))
        {
            return earlier;
        }

        ExpireIfDue(payment);
        if (payment.Status is not (PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded))
        {
            return PaymentError.NotSupported($"A payment in status {payment.Status} cannot be refunded.");
        }

        var remaining = payment.Request.Amount - payment.Refunded;
        var amount = request.Amount ?? remaining;
        if (amount.Currency != remaining.Currency)
        {
            return PaymentError.Validation("The refund's currency must match the payment's.");
        }

        if (amount.IsZero || amount > remaining)
        {
            return PaymentError.Validation($"The refund must be positive and at most {remaining}.");
        }

        payment.Refunded += amount;
        payment.LastRefund = amount;
        var status = payment.Refunded == payment.Request.Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        payment.SetStatus(status, Now);

        var result = new RefundResult
        {
            ProviderRefundId = $"fake_ref_{NextId()}",
            Status = RefundStatus.Succeeded,
            Amount = amount,
            RawStatus = nameof(RefundStatus.Succeeded),
        };
        if (request.MerchantRefundReference is not null)
        {
            _refundsByReference[request.MerchantRefundReference] = result;
        }

        return result;
    }

    private PaymentResult<PaymentCancelResult> Cancel(PaymentCancelRequest request)
    {
        if (!_payments.TryGetValue(request.ProviderPaymentId, out var payment))
        {
            return NotFound(request.ProviderPaymentId);
        }

        ExpireIfDue(payment);
        if (!payment.Status.CanTransitionTo(PaymentStatus.Canceled))
        {
            return PaymentError.NotSupported($"A payment in status {payment.Status} cannot be cancelled.");
        }

        payment.SetStatus(PaymentStatus.Canceled, Now);
        return new PaymentCancelResult { Status = payment.Status, RawStatus = payment.RawStatus };
    }

    private void Move(string providerPaymentId, PaymentStatus status, Action<FakePayment> apply)
    {
        lock (_gate)
        {
            var payment = Find(providerPaymentId);
            if (!payment.Status.CanTransitionTo(status))
            {
                throw new InvalidOperationException($"Payment '{providerPaymentId}' cannot move from {payment.Status} to {status}.");
            }

            apply(payment);
            payment.SetStatus(status, Now);
        }
    }

    private FakePayment Find(string providerPaymentId)
    {
        if (!_payments.TryGetValue(providerPaymentId, out var payment))
        {
            throw new InvalidOperationException($"The fake has no payment '{providerPaymentId}'.");
        }

        ExpireIfDue(payment);
        return payment;
    }

    private void ExpireIfDue(FakePayment payment)
    {
        if (payment.Status == PaymentStatus.Pending && payment.Session.ExpiresAt is { } expiresAt && Now >= expiresAt)
        {
            payment.SetStatus(PaymentStatus.Expired, expiresAt);
        }
    }

    private int NextId() => ++_lastId;
}

/// <summary>A write that reached a <see cref="FakePaymentGateway"/>.</summary>
/// <param name="Operation"><c>create</c>, <c>refund</c> or <c>cancel</c>.</param>
/// <param name="IdempotencyKey">The key the write carried, if any.</param>
public sealed record FakeWriteAttempt(string Operation, string? IdempotencyKey);
