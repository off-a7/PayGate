namespace PayGate.Kashier;

using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using PayGate.Kashier.Transport;
using PayGate.Providers;

/// <summary>
/// Kashier hosted payment sessions behind <see cref="IPaymentGateway"/>. The provider payment id is Kashier's session id:
/// a lookup by it reads the session's single outcome, and refunds and voids first read it to find the Kashier order the
/// money moved on, so the app only keeps the session id.
/// </summary>
internal sealed partial class KashierPaymentGateway(
    KashierApiClient api,
    IPaymentCredentialsProvider<KashierAccountOptions> credentials,
    TimeProvider timeProvider,
    ILogger<KashierPaymentGateway> logger) : IPaymentGateway, IPaymentCancelFeature
{
    private const string _sessionsPath = "v3/payment/sessions";

    private static readonly PaymentCapabilities _capabilities = new()
    {
        SupportedNextActions = new HashSet<NextActionKind> { NextActionKind.Redirect },
        SupportedPaymentMethods = new HashSet<PaymentMethodType> { PaymentMethodType.Card, PaymentMethodType.Wallet, PaymentMethodType.InstaPay },
        LookupByMerchantReference = true,
        IdempotentWrites = false,
        PartialRefund = true,
        MultipleRefunds = true,
        PaymentExpiry = true,
        PerRequestNotificationUrl = true,
        MetadataPassthrough = true,

        // Kashier opens a second session for a used reference; CheckReferenceBeforeCreate only catches the paid ones.
        RejectsDuplicateReference = false,
    };

    public PaymentAccount Account => api.Account;

    public PaymentCapabilities Capabilities => _capabilities;

    public bool TryGetFeature<TFeature>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TFeature? feature)
        where TFeature : class
    {
        feature = this as TFeature;
        return feature is not null;
    }

    public async Task<PaymentResult<PaymentSession>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = await credentials.GetCredentialsAsync(Account, cancellationToken).ConfigureAwait(false);
        var kashier = request.ProviderOptions as KashierPaymentOptions;
        if (request.ProviderOptions is not null && kashier is null)
        {
            return PaymentError.Validation($"ProviderOptions are for {request.ProviderOptions.Provider}, not Kashier.");
        }

        var now = timeProvider.GetUtcNow();
        if (Invalid(request, now) is { } invalid)
        {
            return invalid;
        }

        string? allowedMethods = options.AllowedMethods;
        if (request.AllowedPaymentMethods.Count > 0)
        {
            var names = request.AllowedPaymentMethods.Order().Select(KashierMapping.AllowedMethod).ToList();
            if (names.Contains(null))
            {
                return PaymentError.NotSupported(
                    $"Kashier hosted payments offer card, wallet and InstaPay; {string.Join(", ", request.AllowedPaymentMethods)} was requested.");
            }

            allowedMethods = string.Join(",", names);
        }

        if (options.CheckReferenceBeforeCreate)
        {
            var duplicate = await FindUsedReferenceAsync(request.MerchantReference, cancellationToken).ConfigureAwait(false);
            if (duplicate is not null)
            {
                return duplicate;
            }
        }

        var expiresAt = request.ExpiresAt ?? now + options.SessionLifetime;
        var body = new KashierSessionRequest
        {
            MerchantId = options.MerchantId,
            Order = request.MerchantReference,
            Amount = WireFormat.ToDecimalString(request.Amount),
            Currency = request.Amount.Currency.Code,
            MerchantRedirect = request.ReturnUrl!.AbsoluteUri,
            PaymentType = "credit",
            Display = kashier?.Display ?? Language(request.Locale) ?? options.Display,
            AllowedMethods = allowedMethods,
            MaxFailureAttempts = kashier?.MaxFailureAttempts ?? options.MaxFailureAttempts,
            ExpireAt = expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            FailureRedirect = kashier?.FailureRedirect,
            Enable3DS = kashier?.Enable3DS,
            ServerWebhook = request.NotificationUrl?.AbsoluteUri,
            Description = request.Description,
            InteractionSource = "ECOMMERCE",
            // Kashier refuses a session without customer.reference (recorded 2026-11-24). Without one from the app, the
            // merchant reference stands in: PayGate saves no cards, so the customer id only labels the payment.
            Customer = new KashierCustomer
            {
                Reference = string.IsNullOrWhiteSpace(request.Customer?.Reference) ? request.MerchantReference : request.Customer.Reference,
                Email = request.Customer?.Email,
            },
            MetaData = request.Metadata.Count > 0 ? request.Metadata : null,
        };

        var sent = await api.SendAsync(
            KashierHost.Api,
            HttpMethod.Post,
            _sessionsPath,
            body,
            KashierJsonContext.Default.KashierSessionRequest,
            KashierJsonContext.Default.KashierSessionResponse,
            includeApiKey: true,
            cancellationToken).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        var response = sent.Value;
        if (!response.IsSuccess)
        {
            return KashierMapping.Error("Creating the Kashier session", response.StatusCode, response.Error, afterSuccessfulRead: false);
        }

        if (string.IsNullOrEmpty(response.Body.Id) || !Uri.TryCreate(response.Body.SessionUrl, UriKind.Absolute, out var sessionUrl))
        {
            return new PaymentError
            {
                Code = PaymentErrorCode.ProviderError,
                Message = "Kashier created the session but returned no session id or URL. Look the payment up by its merchant reference.",
            };
        }

        return new PaymentSession
        {
            ProviderPaymentId = response.Body.Id,
            Status = PaymentStatus.Pending,
            NextAction = new RedirectAction { Url = sessionUrl },
            ExpiresAt = response.Body.ExpireAt ?? expiresAt,
        };
    }

    public async Task<PaymentResult<PaymentDetails>> GetPaymentAsync(PaymentLookup lookup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        string sessionId = lookup.Value;
        if (lookup.Kind == PaymentLookupKind.MerchantReference)
        {
            var found = await FindSessionByReferenceAsync(lookup.Value, cancellationToken).ConfigureAwait(false);
            if (found.IsFailure)
            {
                return found.Error;
            }

            sessionId = found.Value;
        }

        var read = await ReadSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return read.IsFailure ? read.Error : Details(sessionId, read.Value);
    }

    public async Task<PaymentResult<RefundResult>> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is { IsZero: true })
        {
            return PaymentError.Validation("A refund amount must be more than zero. Leave it unset to refund what is left.");
        }

        var read = await ReadSessionAsync(request.ProviderPaymentId, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var payment = read.Value;
        var status = KashierMapping.Status(payment.Status);
        if (status == PaymentStatus.Authorized)
        {
            return PaymentError.NotSupported("Kashier can't refund a payment that is only authorized. Cancel it to release the funds.");
        }

        if (status is not (PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded))
        {
            return PaymentError.Validation($"The payment is {status} at Kashier ({payment.Status}), so there is nothing to refund.");
        }

        if (request.Amount is { } amount && !string.Equals(amount.Currency.Code, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return PaymentError.Validation($"The refund is in {amount.Currency.Code} but the payment is in {payment.Currency}.");
        }

        string? orderId = KashierMapping.Value(payment.OrderId);
        if (orderId is null)
        {
            return new PaymentError { Code = PaymentErrorCode.ProviderError, Message = "Kashier reports the payment as paid but returned no order id." };
        }

        var operation = await OperateAsync(orderId, new KashierOrderOperationRequest
        {
            ApiOperation = "REFUND",
            Reason = request.Reason,
            Transaction = Transaction(request.Amount?.Amount, request.ProviderTransactionId),
        }, "Refunding the Kashier payment", cancellationToken).ConfigureAwait(false);
        if (operation.IsFailure)
        {
            return operation.Error;
        }

        var result = operation.Value;
        var refundStatus = result.Status?.ToUpperInvariant() switch
        {
            "SUCCESS" => RefundStatus.Succeeded,
            "PENDING" => RefundStatus.Pending,
            "FAILURE" or "FAILED" => RefundStatus.Failed,
            _ => RefundStatus.Pending,
        };

        var refunded = result.Response?.Amount is { } refundedAmount && Currency.TryFromCode(payment.Currency, out var currency)
            ? Money.Round(refundedAmount, currency)
            : request.Amount;

        return new RefundResult
        {
            ProviderRefundId = result.Response?.TransactionId,
            Status = refundStatus,
            Amount = refunded,
            RawStatus = result.Response?.Status ?? result.Status,
        };
    }

    public async Task<PaymentResult<PaymentCancelResult>> CancelAsync(PaymentCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var read = await ReadSessionAsync(request.ProviderPaymentId, cancellationToken).ConfigureAwait(false);
        if (read.IsFailure)
        {
            return read.Error;
        }

        var payment = read.Value;
        var status = KashierMapping.Status(payment.Status);
        if (status == PaymentStatus.Canceled)
        {
            return new PaymentCancelResult { Status = PaymentStatus.Canceled, RawStatus = payment.Status };
        }

        if (status is not (PaymentStatus.Succeeded or PaymentStatus.Authorized))
        {
            return PaymentError.Validation(
                $"The payment is {status} at Kashier ({payment.Status}). Only a paid or authorized payment can be voided; " +
                "an unpaid session simply expires.");
        }

        string? orderId = KashierMapping.Value(payment.OrderId);
        if (orderId is null)
        {
            return new PaymentError { Code = PaymentErrorCode.ProviderError, Message = "Kashier reports the payment as paid but returned no order id." };
        }

        var operation = await OperateAsync(orderId, new KashierOrderOperationRequest
        {
            ApiOperation = "VOID",
            Reason = request.Reason,
            Transaction = Transaction(null, request.ProviderTransactionId),
        }, "Voiding the Kashier payment", cancellationToken).ConfigureAwait(false);
        if (operation.IsFailure)
        {
            return operation.Error;
        }

        var result = operation.Value;
        bool voided = string.Equals(result.Status, "SUCCESS", StringComparison.OrdinalIgnoreCase);
        return new PaymentCancelResult
        {
            Status = voided ? PaymentStatus.Canceled : KashierMapping.Status(result.Response?.Status ?? payment.Status),
            RawStatus = result.Response?.Status ?? result.Status,
        };
    }

    private static PaymentError? Invalid(CreatePaymentRequest request, DateTimeOffset now)
    {
        if (request.Amount.IsZero)
        {
            return PaymentError.Validation("The amount must be more than zero.");
        }

        if (request.ReturnUrl is null)
        {
            return PaymentError.Validation("Kashier needs a ReturnUrl: the page the customer comes back to after paying.");
        }

        if (!request.ReturnUrl.IsAbsoluteUri || request.ReturnUrl.Scheme is not ("https" or "http"))
        {
            return PaymentError.Validation("The ReturnUrl must be an absolute http or https URL.");
        }

        if (request.ReturnUrl.IsLoopback)
        {
            // Seen on 2026-11-22: 400 "merchantRedirect" must be a valid URL.
            return PaymentError.Validation(
                "Kashier refuses localhost return URLs. Use a public URL, such as a dev tunnel or ngrok, while developing.");
        }

        if (request.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return PaymentError.Validation("ExpiresAt is in the past.");
        }

        return null;
    }

    private static string? Language(string? locale) => locale?.Trim().ToUpperInvariant() switch
    {
        null or "" => null,
        var l when l.StartsWith("AR", StringComparison.Ordinal) => "ar",
        var l when l.StartsWith("EN", StringComparison.Ordinal) => "en",
        _ => null,
    };

    private static KashierOperationTransaction? Transaction(decimal? amount, string? targetTransactionId) =>
        amount is null && string.IsNullOrWhiteSpace(targetTransactionId)
            ? null
            : new KashierOperationTransaction { Amount = amount, TargetTransactionId = string.IsNullOrWhiteSpace(targetTransactionId) ? null : targetTransactionId };

    /// <summary>Refuses a reference that already has an order past a failed try: paying a second session for it would fail silently.</summary>
    private async Task<PaymentError?> FindUsedReferenceAsync(string merchantReference, CancellationToken cancellationToken)
    {
        var orders = await SearchAsync(merchantReference, cancellationToken).ConfigureAwait(false);
        if (orders.IsFailure)
        {
            return orders.Error;
        }

        var used = orders.Value.FirstOrDefault(o => KashierMapping.Status(o.Status) is not (PaymentStatus.Failed or PaymentStatus.Pending));
        return used is null
            ? null
            : PaymentError.DuplicateReference(
                $"The merchant reference '{merchantReference}' already has a Kashier order ({used.Status}). A second payment page for it " +
                "would fail when the customer pays. Use a new merchant reference for every payment attempt.");
    }

    /// <summary>
    /// Every try inside one session is its own Kashier order, so a reference can have several. The payment is the order that
    /// got past a failed try, else the newest one (Kashier lists newest first).
    /// </summary>
    private async Task<PaymentResult<string>> FindSessionByReferenceAsync(string merchantReference, CancellationToken cancellationToken)
    {
        var orders = await SearchAsync(merchantReference, cancellationToken).ConfigureAwait(false);
        if (orders.IsFailure)
        {
            return orders.Error;
        }

        var order = orders.Value.FirstOrDefault(o => KashierMapping.Status(o.Status) is not (PaymentStatus.Failed or PaymentStatus.Pending))
            ?? (orders.Value.Count > 0 ? orders.Value[0] : null);
        if (order is null)
        {
            return new PaymentError
            {
                Code = PaymentErrorCode.NotFound,
                Message = $"Kashier has no order for '{merchantReference}'. A session appears here once the customer tries to pay.",
            };
        }

        return string.IsNullOrEmpty(order.SessionId)
            ? new PaymentError { Code = PaymentErrorCode.ProviderError, Message = "Kashier returned an order without its session id." }
            : order.SessionId;
    }

    /// <summary>The orders whose merchant reference is exactly this one: Kashier's search is a partial match.</summary>
    private async Task<PaymentResult<IReadOnlyList<KashierOrder>>> SearchAsync(string merchantReference, CancellationToken cancellationToken)
    {
        var sent = await api.SendAsync<KashierOrderOperationRequest, KashierOrdersResponse>(
            KashierHost.Api,
            HttpMethod.Get,
            $"v3/payment/orders?search={Uri.EscapeDataString(merchantReference)}",
            null,
            null,
            KashierJsonContext.Default.KashierOrdersResponse,
            includeApiKey: false,
            cancellationToken).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        var response = sent.Value;
        if (!response.IsSuccess)
        {
            return KashierMapping.Error("Searching Kashier orders", response.StatusCode, response.Error, afterSuccessfulRead: false);
        }

        return PaymentResult.Success<IReadOnlyList<KashierOrder>>(
            [.. response.Body.Data.Where(o => string.Equals(o.MerchantOrderId, merchantReference, StringComparison.Ordinal))]);
    }

    private async Task<PaymentResult<KashierSessionPayment>> ReadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var sent = await api.SendAsync<KashierOrderOperationRequest, KashierSessionPaymentResponse>(
            KashierHost.Api,
            HttpMethod.Get,
            $"{_sessionsPath}/{Uri.EscapeDataString(sessionId)}/payment",
            null,
            null,
            KashierJsonContext.Default.KashierSessionPaymentResponse,
            includeApiKey: false,
            cancellationToken).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        var response = sent.Value;
        if (!response.IsSuccess)
        {
            return response.StatusCode == HttpStatusCode.NotFound
                ? new PaymentError { Code = PaymentErrorCode.NotFound, Message = $"Kashier has no payment session '{sessionId}'.", ProviderCode = "404" }
                : KashierMapping.Error("Reading the Kashier session", response.StatusCode, response.Error, afterSuccessfulRead: false);
        }

        return response.Body.Data is { } data
            ? data
            : new PaymentError { Code = PaymentErrorCode.ProviderError, Message = "Kashier answered the session lookup without data." };
    }

    private async Task<PaymentResult<KashierOrderOperationResponse>> OperateAsync(
        string orderId,
        KashierOrderOperationRequest body,
        string call,
        CancellationToken cancellationToken)
    {
        var sent = await api.SendAsync(
            KashierHost.Fep,
            HttpMethod.Put,
            $"v3/orders/{Uri.EscapeDataString(orderId)}",
            body,
            KashierJsonContext.Default.KashierOrderOperationRequest,
            KashierJsonContext.Default.KashierOrderOperationResponse,
            includeApiKey: false,
            cancellationToken).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return sent.Error;
        }

        var response = sent.Value;
        return response.IsSuccess
            ? response.Body
            : KashierMapping.Error(call, response.StatusCode, response.Error, afterSuccessfulRead: true);
    }

    private PaymentResult<PaymentDetails> Details(string sessionId, KashierSessionPayment payment)
    {
        if (payment.Amount is not { } amount || !Currency.TryFromCode(payment.Currency, out var currency))
        {
            return new PaymentError
            {
                Code = PaymentErrorCode.ProviderError,
                Message = $"Kashier returned the session without a usable amount and currency ({payment.Currency}).",
            };
        }

        var status = KashierMapping.Status(payment.Status);
        if (status == PaymentStatus.Unknown)
        {
            LogUnknownStatus(logger, Account.ToString(), payment.Status ?? "(none)");
        }

        var card = payment.SourceOfFunds;
        var method = KashierMapping.Value(payment.Method) is { } name
            ? new PaymentMethodInfo { Type = KashierMapping.Method(name), Brand = card?.CardBrand, Last4 = KashierMapping.Last4(card?.MaskedCard) }
            : null;

        return new PaymentDetails
        {
            ProviderPaymentId = payment.SessionId ?? sessionId,
            ProviderTransactionId = KashierMapping.Value(payment.TargetTransactionId),
            MerchantReference = payment.MerchantOrderId,
            Status = status,
            RawStatus = payment.Status ?? string.Empty,
            Amount = Money.Round(amount, currency),
            CapturedAmount = payment.CapturedAmount is { } captured ? Money.Round(captured, currency) : null,
            RefundedAmount = Money.Round(payment.RefundedAmount ?? 0m, currency),
            PaymentMethod = method,
            Failure = status is PaymentStatus.Failed or PaymentStatus.Expired ? KashierMapping.Failure(payment) : null,
            CreatedAt = payment.CreatedAt,
            UpdatedAt = payment.UpdatedAt,
            ProviderData = new KashierPaymentData
            {
                OrderId = KashierMapping.Value(payment.OrderId),
                LastOperation = KashierMapping.Value(payment.LastTransactionType),
                Fees = payment.Fees,
                Vat = payment.Vat,
                SettlementAmount = payment.SettlementAmount,
            },
        };
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Warning, Message = "Kashier account {Account} reported a status PayGate has no mapping for: {RawStatus}. It is reported as Unknown.")]
    private static partial void LogUnknownStatus(ILogger logger, string account, string rawStatus);
}
