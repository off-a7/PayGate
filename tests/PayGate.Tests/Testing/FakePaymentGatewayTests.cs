namespace PayGate.Tests.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using PayGate.Testing;
using PayGate.Webhooks;

public sealed class FakePaymentGatewayTests
{
    private static readonly Money _amount = new(100m, Currency.Egp);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(FakeFlow.Redirect, typeof(RedirectAction))]
    [InlineData(FakeFlow.ClientSdk, typeof(ClientSdkAction))]
    [InlineData(FakeFlow.DisplayReference, typeof(DisplayReferenceAction))]
    public async Task Each_flow_returns_its_next_action_Async(FakeFlow flow, Type expected)
    {
        var gateway = new FakePaymentGateway(new FakeGatewayOptions { Flow = flow });

        var session = await CreateAsync(gateway);

        session.NextAction.ShouldBeOfType(expected);
        gateway.Capabilities.SupportedNextActions.ShouldBe([session.NextAction.Kind]);
    }

    [Fact]
    public async Task A_payment_moves_as_the_test_drives_it_Async()
    {
        var gateway = new FakePaymentGateway();
        var paid = await CreateAsync(gateway);
        var failed = await CreateAsync(gateway);
        var expired = await CreateAsync(gateway);

        gateway.Succeed(paid.ProviderPaymentId);
        gateway.Fail(failed.ProviderPaymentId, "No funds");
        gateway.Expire(expired.ProviderPaymentId);

        var paidDetails = await GetAsync(gateway, paid);
        paidDetails.Status.ShouldBe(PaymentStatus.Succeeded);
        paidDetails.RawStatus.ShouldBe("SUCCEEDED");
        paidDetails.ProviderTransactionId.ShouldNotBeNull();
        paidDetails.CapturedAmount.ShouldBe(_amount);

        var failedDetails = await GetAsync(gateway, failed);
        failedDetails.Status.ShouldBe(PaymentStatus.Failed);
        failedDetails.Failure.ShouldNotBeNull().Message.ShouldBe("No funds");

        (await GetAsync(gateway, expired)).Status.ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public async Task A_move_the_status_rules_forbid_throws_Async()
    {
        var gateway = new FakePaymentGateway();
        var session = await CreateAsync(gateway);
        gateway.Expire(session.ProviderPaymentId);

        Should.Throw<InvalidOperationException>(() => gateway.Succeed(session.ProviderPaymentId));
    }

    [Fact]
    public void Driving_a_payment_that_does_not_exist_throws()
    {
        Should.Throw<InvalidOperationException>(() => new FakePaymentGateway().Succeed("nope"));
    }

    [Fact]
    public async Task A_payment_expires_on_the_injected_clock_Async()
    {
        var clock = new ManualClock();
        var gateway = new FakePaymentGateway(timeProvider: clock);
        var session = await CreateAsync(gateway, expiresAt: clock.GetUtcNow().AddMinutes(30));

        (await GetAsync(gateway, session)).Status.ShouldBe(PaymentStatus.Pending);

        clock.Advance(TimeSpan.FromMinutes(31));

        (await GetAsync(gateway, session)).Status.ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public async Task A_display_reference_expires_by_default_Async()
    {
        var clock = new ManualClock();
        var gateway = new FakePaymentGateway(new FakeGatewayOptions { Flow = FakeFlow.DisplayReference }, timeProvider: clock);

        var session = await CreateAsync(gateway);

        session.ExpiresAt.ShouldBe(clock.GetUtcNow().AddHours(48));
        session.NextAction.ShouldBeOfType<DisplayReferenceAction>().ExpiresAt.ShouldBe(session.ExpiresAt);
    }

    [Fact]
    public async Task An_unpaid_payment_cannot_be_refunded_Async()
    {
        var gateway = new FakePaymentGateway();
        var session = await CreateAsync(gateway);

        var result = await RefundAsync(gateway, session, amount: null);

        result.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.NotSupported);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task A_refund_outside_the_remaining_amount_is_invalid_Async(int value)
    {
        var gateway = new FakePaymentGateway();
        var session = await PaidAsync(gateway);

        var result = await RefundAsync(gateway, session, new Money(value, Currency.Egp));

        result.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.Validation);
    }

    [Fact]
    public async Task A_refund_in_another_currency_is_invalid_Async()
    {
        var gateway = new FakePaymentGateway();
        var session = await PaidAsync(gateway);

        var result = await RefundAsync(gateway, session, new Money(1m, Currency.Usd));

        result.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.Validation);
    }

    [Fact]
    public async Task A_refund_of_a_payment_that_does_not_exist_is_not_found_Async()
    {
        var result = await new FakePaymentGateway().RefundAsync(new RefundRequest { ProviderPaymentId = "nope" }, Token);

        result.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.NotFound);
    }

    [Fact]
    public async Task An_idempotent_refund_replay_refunds_once_Async()
    {
        var gateway = new FakePaymentGateway(new FakeGatewayOptions { IdempotentWrites = true });
        var session = await PaidAsync(gateway);
        var request = new RefundRequest
        {
            ProviderPaymentId = session.ProviderPaymentId,
            Amount = new Money(10m, Currency.Egp),
            MerchantRefundReference = "refund-1",
        };

        var first = await gateway.RefundAsync(request, Token);
        var replay = await gateway.RefundAsync(request, Token);

        replay.Value.ShouldBe(first.Value);
        (await GetAsync(gateway, session)).RefundedAmount.ShouldBe(new Money(10m, Currency.Egp));
    }

    [Fact]
    public async Task A_timed_out_refund_still_took_effect_Async()
    {
        var gateway = new FakePaymentGateway();
        var session = await PaidAsync(gateway);
        gateway.TimeOutNextWrite();

        var result = await RefundAsync(gateway, session, new Money(10m, Currency.Egp));

        result.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
        (await GetAsync(gateway, session)).RefundedAmount.ShouldBe(new Money(10m, Currency.Egp));
    }

    [Fact]
    public async Task A_pending_payment_can_be_cancelled_through_the_feature_Async()
    {
        var gateway = new FakePaymentGateway();
        var session = await CreateAsync(gateway);
        gateway.TryGetFeature<IPaymentCancelFeature>(out var cancel).ShouldBeTrue();

        var result = await cancel.CancelAsync(new PaymentCancelRequest { ProviderPaymentId = session.ProviderPaymentId }, Token);

        result.Value.Status.ShouldBe(PaymentStatus.Canceled);
        (await GetAsync(gateway, session)).Status.ShouldBe(PaymentStatus.Canceled);
    }

    [Fact]
    public async Task A_finished_payment_cannot_be_cancelled_Async()
    {
        var gateway = new FakePaymentGateway();
        var session = await CreateAsync(gateway);
        gateway.Expire(session.ProviderPaymentId);

        var result = await gateway.CancelAsync(new PaymentCancelRequest { ProviderPaymentId = session.ProviderPaymentId }, Token);

        result.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.NotSupported);
    }

    [Fact]
    public async Task A_payment_needs_a_reference_and_a_positive_amount_Async()
    {
        var gateway = new FakePaymentGateway();

        var noReference = await gateway.CreatePaymentAsync(new CreatePaymentRequest { MerchantReference = " ", Amount = _amount }, Token);
        var noAmount = await gateway.CreatePaymentAsync(new CreatePaymentRequest { MerchantReference = "a", Amount = Money.Zero(Currency.Egp) }, Token);

        noReference.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.Validation);
        noAmount.Error.ShouldNotBeNull().Code.ShouldBe(PaymentErrorCode.Validation);
    }

    [Fact]
    public void Options_hide_the_webhook_secret()
    {
        new FakeGatewayOptions { WebhookSecret = "super-secret" }.ToString().ShouldNotContain("super-secret");
    }

    [Theory]
    [InlineData("Production", false, true)]
    [InlineData("Production", true, false)]
    [InlineData("Development", false, false)]
    public void The_fake_refuses_to_run_in_production_unless_allowed(string environment, bool allowed, bool refused)
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        var services = new ServiceCollection();
        services.AddSingleton(host);
        services.AddPayGate().AddPayGateFake(options => options.AllowInProduction = allowed);

        using var provider = services.BuildServiceProvider();

        if (refused)
        {
            Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
                .Message.ShouldContain("Production");
            Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<FakePaymentGateway>());
        }
        else
        {
            provider.GetRequiredService<FakePaymentGateway>().ShouldNotBeNull();
        }
    }

    [Fact]
    public void Each_fake_account_gets_its_own_gateway()
    {
        var services = new ServiceCollection();
        services.AddPayGate().AddPayGateFake().AddPayGateFake(accountName: "second");

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<FakePaymentGateway>().Account.ShouldBe(new PaymentAccount(FakePaymentGateway.ProviderId));
        provider.GetRequiredKeyedService<FakePaymentGateway>("fake:second").Account.Name.ShouldBe("second");
        services.Count(d => d.ServiceType == typeof(IWebhookVerifier)).ShouldBe(1);
    }

    [Fact]
    public async Task A_paid_webhook_reaches_the_apps_handler_once_Async()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Inbox>();
        services.AddPayGate().AddPayGateFake().AddEventHandler<InboxHandler>();
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<FakePaymentGateway>();
        var processor = provider.GetRequiredService<IWebhookProcessor>();
        var session = await CreateAsync(gateway);
        gateway.Succeed(session.ProviderPaymentId);
        var webhook = new FakeWebhookBuilder(gateway).ForPayment(session.ProviderPaymentId);

        var first = await processor.ProcessAsync(webhook, Token);
        var replay = await processor.ProcessAsync(webhook, Token);

        first.Status.ShouldBe(WebhookProcessingStatus.Processed);
        replay.Status.ShouldBe(WebhookProcessingStatus.AlreadyProcessed);
        var received = provider.GetRequiredService<Inbox>().Events.ShouldHaveSingleItem();
        received.Type.ShouldBe(PaymentEventType.PaymentSucceeded);
        received.Amount.ShouldBe(_amount);
        received.MerchantReference.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_partial_refund_webhook_carries_the_delta_and_the_total_Async()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Inbox>();
        services.AddPayGate().AddPayGateFake().AddEventHandler<InboxHandler>();
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<FakePaymentGateway>();
        var session = await PaidAsync(gateway);
        await RefundAsync(gateway, session, new Money(10m, Currency.Egp));
        await RefundAsync(gateway, session, new Money(20m, Currency.Egp));

        var webhook = new FakeWebhookBuilder(gateway).ForPayment(session.ProviderPaymentId);
        await provider.GetRequiredService<IWebhookProcessor>().ProcessAsync(webhook, Token);

        var received = provider.GetRequiredService<Inbox>().Events.ShouldHaveSingleItem();
        received.Type.ShouldBe(PaymentEventType.RefundSucceeded);
        received.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        received.RefundAmount.ShouldBe(new Money(20m, Currency.Egp));
        received.RefundedTotal.ShouldBe(new Money(30m, Currency.Egp));
    }

    [Fact]
    public async Task Tampered_and_malformed_webhooks_are_rejected_Async()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Inbox>();
        services.AddPayGate().AddPayGateFake().AddEventHandler<InboxHandler>();
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<FakePaymentGateway>();
        var processor = provider.GetRequiredService<IWebhookProcessor>();
        var session = await PaidAsync(gateway);
        var builder = new FakeWebhookBuilder(gateway);

        var tampered = await processor.ProcessAsync(FakeWebhookBuilder.Tampered(builder.ForPayment(session.ProviderPaymentId)), Token);
        var malformed = await processor.ProcessAsync(builder.Malformed(), Token);

        tampered.Status.ShouldBe(WebhookProcessingStatus.InvalidSignature);
        malformed.Status.ShouldBe(WebhookProcessingStatus.Malformed);
        provider.GetRequiredService<Inbox>().Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_status_with_no_event_is_ignored_Async()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Inbox>();
        services.AddPayGate().AddPayGateFake().AddEventHandler<InboxHandler>();
        await using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<FakePaymentGateway>();
        var session = await CreateAsync(gateway);
        gateway.ReportUnknownStatus(session.ProviderPaymentId, "SOMETHING_NEW");

        var result = await provider.GetRequiredService<IWebhookProcessor>()
            .ProcessAsync(new FakeWebhookBuilder(gateway).ForPayment(session.ProviderPaymentId), Token);

        result.Status.ShouldBe(WebhookProcessingStatus.Ignored);
    }

    private static async Task<PaymentSession> CreateAsync(FakePaymentGateway gateway, DateTimeOffset? expiresAt = null)
    {
        var result = await gateway.CreatePaymentAsync(
            new CreatePaymentRequest { MerchantReference = $"order-{Guid.NewGuid():N}", Amount = _amount, ExpiresAt = expiresAt },
            Token);
        return result.Value;
    }

    private static async Task<PaymentSession> PaidAsync(FakePaymentGateway gateway)
    {
        var session = await CreateAsync(gateway);
        gateway.Succeed(session.ProviderPaymentId);
        return session;
    }

    private static async Task<PaymentDetails> GetAsync(FakePaymentGateway gateway, PaymentSession session) =>
        (await gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId(session.ProviderPaymentId), Token)).Value;

    private static Task<PaymentResult<RefundResult>> RefundAsync(FakePaymentGateway gateway, PaymentSession session, Money? amount) =>
        gateway.RefundAsync(new RefundRequest { ProviderPaymentId = session.ProviderPaymentId, Amount = amount }, Token);

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Inbox
    {
        public List<PaymentEvent> Events { get; } = [];
    }

    /// <summary>The handler the docs ask apps to write: dedupe on the event id.</summary>
    private sealed class InboxHandler(Inbox inbox) : IPaymentEventHandler
    {
        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
        {
            if (inbox.Events.Exists(e => e.EventId == paymentEvent.EventId))
            {
                return ValueTask.FromResult(WebhookHandlingOutcome.AlreadyProcessed);
            }

            inbox.Events.Add(paymentEvent);
            return ValueTask.FromResult(WebhookHandlingOutcome.Processed);
        }
    }
}
