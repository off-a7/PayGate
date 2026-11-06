namespace PayGate.Conformance;

using PayGate.Webhooks;
using Xunit;

/// <summary>
/// What every provider package must do. A provider's test project inherits this class and supplies the hooks, which arrange a
/// state at the provider: on a fake directly, and on a real provider through a mock server.
/// </summary>
public abstract class PaymentGatewayConformanceTests
{
    /// <summary>The gateway under test, resolved through PayGate so that its registration is covered too.</summary>
    protected abstract IPaymentGateway Gateway { get; }

    /// <summary>The provider's webhook verifier.</summary>
    protected abstract IWebhookVerifier Verifier { get; }

    /// <summary>The idempotency key of every write that reached the provider so far, oldest first. Retries count.</summary>
    protected abstract IReadOnlyList<string?> WriteAttemptKeys { get; }

    /// <summary>The amount of each test payment.</summary>
    protected virtual Money PaymentAmount => new(100m, Currency.Egp);

    /// <summary>Makes the provider report the payment as paid.</summary>
    /// <param name="session">The payment.</param>
    /// <returns>A task that completes when the payment is paid.</returns>
    protected abstract Task MakePaidAsync(PaymentSession session);

    /// <summary>Makes the provider report a status its package has no mapping for.</summary>
    /// <param name="session">The payment.</param>
    /// <param name="rawStatus">The status to report.</param>
    /// <returns>A task that completes when the status is reported.</returns>
    protected abstract Task MakeStatusUnknownAsync(PaymentSession session, string rawStatus);

    /// <summary>Makes the next write reach the provider and then lose its response.</summary>
    /// <returns>A task that completes when the timeout is armed.</returns>
    protected abstract Task TimeOutNextWriteAsync();

    /// <summary>Builds a correctly signed webhook about a paid payment.</summary>
    /// <param name="session">The payment.</param>
    /// <returns>The webhook.</returns>
    protected abstract Task<WebhookRequest> SignedWebhookAsync(PaymentSession session);

    /// <summary>Lets pending refunds finish, for providers whose refunds are asynchronous.</summary>
    /// <param name="session">The payment.</param>
    /// <returns>A task that completes when the refunds have settled.</returns>
    protected virtual Task SettleRefundsAsync(PaymentSession session) => Task.CompletedTask;

    /// <summary>Changes a signed webhook so that its signature no longer matches. Override when the provider signs something other than the body.</summary>
    /// <param name="webhook">The webhook.</param>
    /// <returns>The tampered webhook.</returns>
    protected virtual WebhookRequest Tamper(WebhookRequest webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        byte[] body = webhook.Body.ToArray();
        body[0] ^= 1;
        return webhook with { Body = body };
    }

    [Fact]
    public async Task Create_returns_a_next_action_the_gateway_declares_Async()
    {
        var session = await CreateAsync();

        Assert.False(string.IsNullOrWhiteSpace(session.ProviderPaymentId));
        Assert.False(session.Status.IsTerminal());
        Assert.Contains(session.NextAction.Kind, Gateway.Capabilities.SupportedNextActions);
    }

    [Fact]
    public async Task A_different_request_with_a_used_reference_is_a_duplicate_Async()
    {
        var request = Request();
        await CreateAsync(request);

        var result = await Gateway.CreatePaymentAsync(request with { Amount = PaymentAmount + PaymentAmount }, Token);

        Assert.True(result.IsFailure);
        Assert.Equal(PaymentErrorCode.DuplicateReference, result.Error.Code);
    }

    [Fact]
    public async Task An_identical_replay_returns_the_original_session_or_a_duplicate_Async()
    {
        var request = Request();
        var original = await CreateAsync(request);

        var result = await Gateway.CreatePaymentAsync(request, Token);

        if (result.IsSuccess)
        {
            Assert.True(Gateway.Capabilities.IdempotentWrites, "Only a gateway with idempotent writes may return the original session.");
            Assert.Equal(original.ProviderPaymentId, result.Value.ProviderPaymentId);
        }
        else
        {
            Assert.Equal(PaymentErrorCode.DuplicateReference, result.Error.Code);
        }
    }

    [Fact]
    public async Task A_payment_can_be_read_by_its_provider_id_Async()
    {
        var request = Request();
        var session = await CreateAsync(request);

        var details = await GetAsync(session);

        Assert.Equal(session.ProviderPaymentId, details.ProviderPaymentId);
        Assert.Equal(request.Amount, details.Amount);
        Assert.True(details.RefundedAmount.IsZero);
        Assert.False(string.IsNullOrEmpty(details.RawStatus));
    }

    [Fact]
    public async Task Lookup_by_merchant_reference_follows_the_capability_Async()
    {
        var request = Request();
        var session = await CreateAsync(request);

        var result = await Gateway.GetPaymentAsync(PaymentLookup.ByMerchantReference(request.MerchantReference), Token);

        if (Gateway.Capabilities.LookupByMerchantReference)
        {
            Assert.True(result.IsSuccess);
            Assert.Equal(session.ProviderPaymentId, result.Value.ProviderPaymentId);
        }
        else
        {
            Assert.True(result.IsFailure);
            Assert.Equal(PaymentErrorCode.NotSupported, result.Error.Code);
        }
    }

    [Fact]
    public async Task A_partial_then_a_full_refund_add_up_Async()
    {
        Assert.SkipUnless(Gateway.Capabilities.PartialRefund && Gateway.Capabilities.MultipleRefunds, "The gateway does not support several partial refunds.");

        var session = await CreateAsync();
        await MakePaidAsync(session);
        var partial = new Money(PaymentAmount.Amount / 4, PaymentAmount.Currency);

        await RefundAsync(session, partial);
        var afterPartial = await GetAsync(session);

        Assert.Equal(partial, afterPartial.RefundedAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, afterPartial.Status);

        await RefundAsync(session, amount: null);
        var afterFull = await GetAsync(session);

        Assert.Equal(PaymentAmount, afterFull.RefundedAmount);
        Assert.Equal(PaymentStatus.Refunded, afterFull.Status);
    }

    [Fact]
    public async Task An_unknown_provider_status_maps_to_unknown_and_keeps_the_raw_value_Async()
    {
        var session = await CreateAsync();
        await MakeStatusUnknownAsync(session, "SOMETHING_NEW");

        var details = await GetAsync(session);

        Assert.Equal(PaymentStatus.Unknown, details.Status);
        Assert.Equal("SOMETHING_NEW", details.RawStatus);
    }

    [Fact]
    public async Task A_signed_webhook_is_verified_Async()
    {
        var session = await PaidSessionAsync();

        var parsed = await Verifier.VerifyAndParseAsync(await SignedWebhookAsync(session), Token);

        Assert.Equal(WebhookParseKind.Verified, parsed.Kind);
        Assert.Equal(session.ProviderPaymentId, Assert.Single(parsed.Events).ProviderPaymentId);
    }

    [Fact]
    public async Task A_tampered_webhook_is_rejected_Async()
    {
        var session = await PaidSessionAsync();
        var webhook = Tamper(await SignedWebhookAsync(session));

        var parsed = await Verifier.VerifyAndParseAsync(webhook, Token);

        Assert.Equal(WebhookParseKind.InvalidSignature, parsed.Kind);
        Assert.Empty(parsed.Events);
    }

    [Fact]
    public async Task A_replayed_webhook_keeps_its_event_id_Async()
    {
        var session = await PaidSessionAsync();
        var webhook = await SignedWebhookAsync(session);

        var first = await Verifier.VerifyAndParseAsync(webhook, Token);
        var replay = await Verifier.VerifyAndParseAsync(webhook, Token);

        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(first.Events).EventId));
        Assert.Equal(Assert.Single(first.Events).EventId, Assert.Single(replay.Events).EventId);
    }

    [Fact]
    public async Task A_write_that_times_out_follows_the_idempotency_capability_Async()
    {
        var request = Request();
        await TimeOutNextWriteAsync();
        int before = WriteAttemptKeys.Count;

        var result = await Gateway.CreatePaymentAsync(request, Token);

        var attempts = WriteAttemptKeys.Skip(before).ToList();
        if (Gateway.Capabilities.IdempotentWrites)
        {
            Assert.True(result.IsSuccess);
            Assert.True(attempts.Count > 1, "A timed-out idempotent write should be retried.");
            Assert.NotNull(attempts[0]);
            Assert.All(attempts, key => Assert.Equal(attempts[0], key));
        }
        else
        {
            Assert.True(result.IsFailure);
            Assert.Equal(PaymentErrorCode.OutcomeUnknown, result.Error.Code);
            Assert.False(result.Error.IsRetryable);
            Assert.Single(attempts);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private CreatePaymentRequest Request() => new()
    {
        MerchantReference = $"conformance-{Guid.NewGuid():N}",
        Amount = PaymentAmount,
        Description = "Conformance test",
    };

    private async Task<PaymentSession> CreateAsync(CreatePaymentRequest? request = null)
    {
        var result = await Gateway.CreatePaymentAsync(request ?? Request(), Token);
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    private async Task<PaymentSession> PaidSessionAsync()
    {
        var session = await CreateAsync();
        await MakePaidAsync(session);
        return session;
    }

    private async Task<PaymentDetails> GetAsync(PaymentSession session)
    {
        var result = await Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId(session.ProviderPaymentId), Token);
        Assert.True(result.IsSuccess, result.ToString());
        return result.Value;
    }

    private async Task RefundAsync(PaymentSession session, Money? amount)
    {
        var details = await GetAsync(session);
        var result = await Gateway.RefundAsync(
            new RefundRequest
            {
                ProviderPaymentId = session.ProviderPaymentId,
                ProviderTransactionId = details.ProviderTransactionId,
                Amount = amount,
                MerchantRefundReference = $"refund-{Guid.NewGuid():N}",
            },
            Token);
        Assert.True(result.IsSuccess, result.ToString());
        await SettleRefundsAsync(session);
    }
}
