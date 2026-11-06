namespace PayGate.Tests.Core;

using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using PayGate.Webhooks;

public sealed class WebhookProcessorTests
{
    private static readonly PaymentAccount _account = new(PaymentProviderId.Kashier);
    private static readonly PaymentAccount _secondary = new(PaymentProviderId.Kashier, "secondary");
    private static readonly Money _amount = new(100m, Currency.Egp);

    [Fact]
    public async Task An_invalid_signature_never_reaches_the_handler_Async()
    {
        using var harness = new Harness(WebhookParseResult.InvalidSignature("bad signature"));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.InvalidSignature);
        result.Ack.StatusCode.ShouldBe(401);
        result.Reason.ShouldBe("bad signature");
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_malformed_payload_never_reaches_the_handler_Async()
    {
        using var harness = new Harness(WebhookParseResult.Malformed("not json"));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Malformed);
        result.Ack.StatusCode.ShouldBe(400);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_ignored_webhook_is_acknowledged_without_calling_the_handler_Async()
    {
        using var harness = new Harness(WebhookParseResult.Ignored("token event"));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Ignored);
        result.Ack.StatusCode.ShouldBe(200);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unregistered_account_is_rejected_before_verifying_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1")]));

        var result = await harness.ProcessAsync(new PaymentAccount(PaymentProviderId.Kashier, "unknown"));

        result.Status.ShouldBe(WebhookProcessingStatus.UnknownAccount);
        result.Ack.StatusCode.ShouldBe(404);
        harness.Script.Calls.ShouldBe(0);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_verifier_fails_so_that_the_provider_retries_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1")]), provider: PaymentProviderId.Paymob);

        var result = await harness.ProcessAsync(new PaymentAccount(PaymentProviderId.Paymob));

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        result.Ack.StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Each_event_is_dispatched_in_its_own_scope_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1"), Event("evt-2")]));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Processed);
        result.Ack.StatusCode.ShouldBe(200);
        result.EventIds.ShouldBe(["evt-1", "evt-2"]);
        harness.Recorder.Events.Select(e => e.EventId).ShouldBe(["evt-1", "evt-2"]);
        harness.Recorder.ScopedIds.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Events_the_library_does_not_classify_are_not_dispatched_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1", PaymentEventType.Other)]));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Ignored);
        result.Ack.StatusCode.ShouldBe(200);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_verified_webhook_with_no_events_is_ignored_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([]));

        (await harness.ProcessAsync()).Status.ShouldBe(WebhookProcessingStatus.Ignored);
    }

    [Theory]
    [InlineData(WebhookHandlingOutcome.Processed, WebhookHandlingOutcome.Processed, WebhookProcessingStatus.Processed)]
    [InlineData(WebhookHandlingOutcome.AlreadyProcessed, WebhookHandlingOutcome.Processed, WebhookProcessingStatus.Processed)]
    [InlineData(WebhookHandlingOutcome.Processed, WebhookHandlingOutcome.AlreadyProcessed, WebhookProcessingStatus.Processed)]
    [InlineData(WebhookHandlingOutcome.AlreadyProcessed, WebhookHandlingOutcome.AlreadyProcessed, WebhookProcessingStatus.AlreadyProcessed)]
    [InlineData(WebhookHandlingOutcome.Ignored, WebhookHandlingOutcome.AlreadyProcessed, WebhookProcessingStatus.AlreadyProcessed)]
    [InlineData(WebhookHandlingOutcome.Ignored, WebhookHandlingOutcome.Ignored, WebhookProcessingStatus.Ignored)]
    public async Task The_handlers_outcomes_combine_into_one_Async(WebhookHandlingOutcome first, WebhookHandlingOutcome second, WebhookProcessingStatus expected)
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1"), Event("evt-2")]));
        harness.Recorder.Outcomes.Enqueue(first);
        harness.Recorder.Outcomes.Enqueue(second);

        (await harness.ProcessAsync()).Status.ShouldBe(expected);
    }

    [Fact]
    public async Task A_handler_exception_is_a_failure_that_hides_its_message_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1"), Event("evt-2"), Event("evt-3")]));
        harness.Recorder.FailOn = "evt-2";

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        result.Ack.StatusCode.ShouldBe(500);
        result.EventIds.ShouldBe(["evt-1"]);
        result.Reason.ShouldNotBeNull().ShouldNotContain("customer@example.com");
        harness.Recorder.Events.Select(e => e.EventId).ShouldBe(["evt-1", "evt-2"]);
    }

    [Fact]
    public async Task A_missing_event_handler_fails_rather_than_losing_the_event_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1")]), addHandler: false);

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        result.Ack.StatusCode.ShouldBe(500);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_reported_as_a_failure_Async()
    {
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1")]));
        using var cancellation = new CancellationTokenSource();
        harness.Recorder.OnHandle = cancellation.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(() => harness.ProcessAsync(cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task A_providers_ack_policy_shapes_the_response_Async()
    {
        using var harness = new Harness(
            WebhookParseResult.Verified([Event("evt-1")]),
            configure: builder => builder.AddWebhookAckPolicy<ConflictOnDuplicatePolicy>());
        harness.Recorder.Outcomes.Enqueue(WebhookHandlingOutcome.AlreadyProcessed);

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.AlreadyProcessed);
        result.Ack.ShouldBe(new WebhookAck(409, "duplicate"));
    }

    [Theory]
    [InlineData(WebhookProcessingStatus.Processed, 200)]
    [InlineData(WebhookProcessingStatus.AlreadyProcessed, 200)]
    [InlineData(WebhookProcessingStatus.Ignored, 200)]
    [InlineData(WebhookProcessingStatus.InvalidSignature, 401)]
    [InlineData(WebhookProcessingStatus.Malformed, 400)]
    [InlineData(WebhookProcessingStatus.UnknownAccount, 404)]
    [InlineData(WebhookProcessingStatus.Failed, 500)]
    public void The_default_ack_policy_sends_empty_bodies(WebhookProcessingStatus status, int expected)
    {
        var ack = new WebhookAckPolicy(PaymentProviderId.Fawry).GetAck(status);

        ack.ShouldBe(new WebhookAck(expected));
        ack.Body.ShouldBeNull();
    }

    [Fact]
    public void Registering_a_verifier_for_each_account_keeps_one()
    {
        var services = new ServiceCollection();

        services.AddPayGate().AddWebhookVerifier<ScriptedVerifier>().AddWebhookVerifier<ScriptedVerifier>();

        services.Count(d => d.ServiceType == typeof(IWebhookVerifier)).ShouldBe(1);
    }

    [Fact]
    public async Task Confirming_is_off_by_default_Async()
    {
        var gateway = Gateway();
        using var harness = new Harness(WebhookParseResult.Verified([Event("evt-1")]), gateway);

        await harness.ProcessAsync();

        await gateway.DidNotReceiveWithAnyArgs().GetPaymentAsync(default!, TestContext.Current.CancellationToken);
        harness.Recorder.Events.Single().Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task Confirming_applies_to_the_configured_account_only_Async()
    {
        var gateway = Gateway(result: Details(PaymentStatus.Succeeded));
        using var harness = new Harness(
            WebhookParseResult.Verified([Event("evt-1")]),
            gateway,
            configure: builder => builder.ConfigureWebhooks(_secondary, o => o.ConfirmWithProvider = true));

        await harness.ProcessAsync();

        await gateway.DidNotReceiveWithAnyArgs().GetPaymentAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Confirming_takes_the_status_and_amount_from_the_provider_Async()
    {
        var paid = new Money(90m, Currency.Egp);
        var gateway = Gateway(result: Details(PaymentStatus.Failed) with { RawStatus = "FAILURE", Amount = paid });
        using var harness = Confirming(WebhookParseResult.Verified([Event("evt-1")]), gateway);

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Processed);
        var dispatched = harness.Recorder.Events.Single();
        dispatched.EventId.ShouldBe("evt-1");
        dispatched.Type.ShouldBe(PaymentEventType.PaymentSucceeded);
        dispatched.Status.ShouldBe(PaymentStatus.Failed);
        dispatched.RawStatus.ShouldBe("FAILURE");
        dispatched.Amount.ShouldBe(paid);
        dispatched.RefundedTotal.ShouldBeNull();
        await gateway.Received(1).GetPaymentAsync(PaymentLookup.ByProviderPaymentId("pay-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Confirming_a_refund_fills_the_total_and_keeps_the_delta_Async()
    {
        var delta = new Money(30m, Currency.Egp);
        var total = new Money(50m, Currency.Egp);
        var gateway = Gateway(result: Details(PaymentStatus.PartiallyRefunded) with { RefundedAmount = total });
        var refund = Event("evt-1", PaymentEventType.RefundSucceeded) with { RefundAmount = delta };
        using var harness = Confirming(WebhookParseResult.Verified([refund]), gateway);

        await harness.ProcessAsync();

        var dispatched = harness.Recorder.Events.Single();
        dispatched.RefundAmount.ShouldBe(delta);
        dispatched.RefundedTotal.ShouldBe(total);
    }

    [Fact]
    public async Task Confirming_falls_back_to_the_merchant_reference_when_the_provider_supports_it_Async()
    {
        var gateway = Gateway(new PaymentCapabilities { LookupByMerchantReference = true }, Details(PaymentStatus.Succeeded));
        var paymentEvent = Event("evt-1") with { ProviderPaymentId = null };
        using var harness = Confirming(WebhookParseResult.Verified([paymentEvent]), gateway);

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Processed);
        harness.Recorder.Events.Single().ProviderPaymentId.ShouldBe("pay-1");
        await gateway.Received(1).GetPaymentAsync(PaymentLookup.ByMerchantReference("order-1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_event_that_cannot_be_looked_up_is_not_dispatched_Async()
    {
        var paymentEvent = Event("evt-1") with { ProviderPaymentId = null };
        using var harness = Confirming(WebhookParseResult.Verified([paymentEvent]), Gateway());

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_lookup_is_not_dispatched_Async()
    {
        var error = new PaymentError { Code = PaymentErrorCode.ProviderUnavailable, Message = "down", IsRetryable = true };
        using var harness = Confirming(WebhookParseResult.Verified([Event("evt-1")]), Gateway(result: error));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        result.Ack.StatusCode.ShouldBe(500);
        result.Reason.ShouldNotBeNull().ShouldContain(nameof(PaymentErrorCode.ProviderUnavailable));
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("other-payment", "order-1")]
    [InlineData("pay-1", "other-order")]
    public async Task A_payment_that_does_not_match_the_event_is_not_dispatched_Async(string paymentId, string reference)
    {
        var details = Details(PaymentStatus.Succeeded) with { ProviderPaymentId = paymentId, MerchantReference = reference };
        using var harness = Confirming(WebhookParseResult.Verified([Event("evt-1")]), Gateway(result: details));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_provider_status_is_not_dispatched_Async()
    {
        using var harness = Confirming(WebhookParseResult.Verified([Event("evt-1")]), Gateway(result: Details(PaymentStatus.Unknown)));

        var result = await harness.ProcessAsync();

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        harness.Recorder.Events.ShouldBeEmpty();
    }

    private static Harness Confirming(WebhookParseResult parsed, IPaymentGateway gateway) =>
        new(parsed, gateway, configure: builder => builder.ConfigureWebhooks(_account, o => o.ConfirmWithProvider = true));

    private static PaymentEvent Event(string eventId, PaymentEventType type = PaymentEventType.PaymentSucceeded) => new()
    {
        EventId = eventId,
        Account = _account,
        Type = type,
        Status = PaymentStatus.Succeeded,
        ProviderPaymentId = "pay-1",
        MerchantReference = "order-1",
        OccurredAt = DateTimeOffset.UnixEpoch,
    };

    private static PaymentDetails Details(PaymentStatus status) => new()
    {
        ProviderPaymentId = "pay-1",
        MerchantReference = "order-1",
        Status = status,
        RawStatus = status.ToString(),
        Amount = _amount,
        RefundedAmount = new Money(0m, Currency.Egp),
    };

    private static IPaymentGateway Gateway(PaymentCapabilities? capabilities = null, PaymentResult<PaymentDetails>? result = null)
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.Capabilities.Returns(capabilities ?? new PaymentCapabilities());
        if (result is not null)
        {
            gateway.GetPaymentAsync(Arg.Any<PaymentLookup>(), Arg.Any<CancellationToken>()).Returns(result);
        }

        return gateway;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Harness(
            WebhookParseResult parsed,
            IPaymentGateway? gateway = null,
            PaymentProviderId? provider = null,
            bool addHandler = true,
            Action<PayGateBuilder>? configure = null)
        {
            var services = new ServiceCollection();
            services.AddSingleton(Script = new VerifierScript(parsed));
            services.AddSingleton(Recorder);
            services.AddScoped<ScopedId>();

            var resolved = gateway ?? Gateway();
            var builder = services.AddPayGate()
                .AddGateway(_account, _ => resolved)
                .AddWebhookVerifier<ScriptedVerifier>();
            if (provider is { } other)
            {
                builder.AddGateway(new PaymentAccount(other), _ => resolved);
            }

            if (addHandler)
            {
                builder.AddEventHandler<RecordingHandler>();
            }

            configure?.Invoke(builder);
            _provider = services.BuildServiceProvider(validateScopes: true);
        }

        public VerifierScript Script { get; }

        public Recorder Recorder { get; } = new();

        public Task<WebhookProcessingResult> ProcessAsync(PaymentAccount? account = null, CancellationToken? cancellationToken = null)
        {
            var request = new WebhookRequest { Account = account ?? _account, Method = "POST", ReceivedAt = DateTimeOffset.UnixEpoch };
            return _provider.GetRequiredService<IWebhookProcessor>()
                .ProcessAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);
        }

        public void Dispose() => _provider.Dispose();
    }

    private sealed class VerifierScript(WebhookParseResult parsed)
    {
        public WebhookParseResult Parsed { get; } = parsed;

        public int Calls { get; set; }
    }

    private sealed class ScriptedVerifier(VerifierScript script) : IWebhookVerifier
    {
        public PaymentProviderId Provider => PaymentProviderId.Kashier;

        public ValueTask<WebhookParseResult> VerifyAndParseAsync(WebhookRequest request, CancellationToken cancellationToken = default)
        {
            script.Calls++;
            return ValueTask.FromResult(script.Parsed);
        }
    }

    private sealed class Recorder
    {
        public List<PaymentEvent> Events { get; } = [];

        public List<Guid> ScopedIds { get; } = [];

        public Queue<WebhookHandlingOutcome> Outcomes { get; } = new();

        public string? FailOn { get; set; }

        public Action? OnHandle { get; set; }
    }

    private sealed class ScopedId
    {
        public Guid Value { get; } = Guid.NewGuid();
    }

    private sealed class RecordingHandler(Recorder recorder, ScopedId scopedId) : IPaymentEventHandler
    {
        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
        {
            recorder.Events.Add(paymentEvent);
            recorder.ScopedIds.Add(scopedId.Value);
            recorder.OnHandle?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            if (paymentEvent.EventId == recorder.FailOn)
            {
                throw new InvalidOperationException("Could not save customer@example.com");
            }

            return ValueTask.FromResult(recorder.Outcomes.TryDequeue(out var outcome) ? outcome : WebhookHandlingOutcome.Processed);
        }
    }

    private sealed class ConflictOnDuplicatePolicy() : WebhookAckPolicy(PaymentProviderId.Kashier)
    {
        public override WebhookAck GetAck(WebhookProcessingStatus status) =>
            status == WebhookProcessingStatus.AlreadyProcessed ? new WebhookAck(409, "duplicate") : base.GetAck(status);
    }
}
