namespace PayGate.Kashier.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PayGate.Testing;
using PayGate.Webhooks;

/// <summary>The webhooks <see cref="KashierWebhookBuilder"/> builds for apps' tests, checked by the real Kashier verifier.</summary>
public sealed class KashierWebhookBuilderTests
{
    private const string _key = "app-test-payment-api-key";
    private static readonly Money _amount = new(250m, Currency.Egp);
    private static readonly DateTimeOffset _now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static KashierWebhookBuilder Builder => new(_key, timeProvider: new FixedTimeProvider(_now));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("paid", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("declined", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("authorized", PaymentEventType.PaymentAuthorized, PaymentStatus.Authorized)]
    [InlineData("captured", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("refunded", PaymentEventType.RefundSucceeded, PaymentStatus.PartiallyRefunded)]
    [InlineData("voided", PaymentEventType.PaymentCanceled, PaymentStatus.Canceled)]
    public async Task Each_webhook_verifies_and_reads_as_what_it_reports_Async(string kind, PaymentEventType type, PaymentStatus status)
    {
        var webhook = kind switch
        {
            "paid" => Builder.Paid("ORD-1", _amount),
            "declined" => Builder.Declined("ORD-1", _amount),
            "authorized" => Builder.Authorized("ORD-1", _amount),
            "captured" => Builder.Captured("ORD-1", _amount),
            "refunded" => Builder.Refunded("ORD-1", _amount),
            _ => Builder.Voided("ORD-1", _amount),
        };

        var paymentEvent = await SingleEventAsync(webhook);

        paymentEvent.Type.ShouldBe(type);
        paymentEvent.Status.ShouldBe(status);
        paymentEvent.MerchantReference.ShouldBe("ORD-1");
        (paymentEvent.Amount ?? paymentEvent.RefundAmount).ShouldBe(_amount);
        paymentEvent.OccurredAt.ShouldBe(_now);
    }

    [Fact]
    public void The_body_has_the_shape_kashier_sends()
    {
        var body = JsonNode.Parse(Builder.Paid("ORD-1", _amount).Body.Span)!;
        var recorded = Fixtures.WebhookBody("pay-success-card");

        body["event"]!.GetValue<string>().ShouldBe("pay");
        body["data"]!["signatureKeys"]!.ToJsonString().ShouldBe(recorded["data"]!["signatureKeys"]!.ToJsonString());
        body["data"]!["amount"]!.ToJsonString().ShouldBe("250", "Kashier writes major units without trailing zeros");
        body["data"]!["currency"]!.GetValue<string>().ShouldBe("EGP");
        body["data"]!["transactionId"]!.GetValue<string>().ShouldStartWith("TX-");
    }

    [Fact]
    public void The_signature_is_kashiers_scheme_computed_independently()
    {
        var webhook = Builder.Paid("ORD 1/2", new Money(75.50m, Currency.Egp));
        var data = JsonNode.Parse(webhook.Body.Span)!["data"]!.AsObject();

        webhook.GetHeader("x-kashier-signature").ShouldBe(Fixtures.SignWebhook(data, _key));
        data["amount"]!.ToJsonString().ShouldBe("75.5");
    }

    [Fact]
    public async Task Building_the_same_webhook_twice_gives_the_same_event_id_like_a_retry_Async()
    {
        var first = await SingleEventAsync(Builder.Paid("ORD-1", _amount));
        var again = await SingleEventAsync(Builder.Paid("ORD-1", _amount));
        var refund = await SingleEventAsync(Builder.Refunded("ORD-1", new Money(50m, Currency.Egp)));
        var secondRefund = await SingleEventAsync(Builder.Refunded("ORD-1", new Money(70m, Currency.Egp)));

        again.EventId.ShouldBe(first.EventId);
        refund.EventId.ShouldNotBe(first.EventId);
        secondRefund.EventId.ShouldNotBe(refund.EventId);
    }

    [Fact]
    public async Task Any_event_can_be_built_with_chosen_ids_and_metadata_Async()
    {
        var webhook = Builder.Build(new KashierWebhook
        {
            Event = "refund",
            MerchantReference = "ORD-1",
            Amount = new Money(10m, Currency.Egp),
            TransactionId = "TX-REFUND-2",
            Method = "wallet",
            OccurredAt = _now.AddMinutes(-5),
            Metadata = new Dictionary<string, string> { ["orderId"] = "42" },
        });

        var paymentEvent = await SingleEventAsync(webhook);

        paymentEvent.ProviderTransactionId.ShouldBe("TX-REFUND-2");
        paymentEvent.OccurredAt.ShouldBe(_now.AddMinutes(-5));
        paymentEvent.Metadata["orderId"].ShouldBe("42");
    }

    [Fact]
    public async Task An_event_name_paygate_does_not_report_is_ignored_Async()
    {
        var webhook = Builder.Build(new KashierWebhook { Event = "chargeback", MerchantReference = "ORD-1", Amount = _amount });

        var result = await VerifyAsync(webhook);

        result.Kind.ShouldBe(WebhookParseKind.Ignored);
    }

    [Fact]
    public async Task A_tampered_copy_is_refused_Async()
    {
        var result = await VerifyAsync(KashierWebhookBuilder.Tampered(Builder.Paid("ORD-1", _amount)));

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
    }

    [Fact]
    public async Task A_webhook_signed_for_another_key_is_refused_Async()
    {
        var result = await VerifyAsync(new KashierWebhookBuilder("another-key").Paid("ORD-1", _amount));

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
    }

    [Fact]
    public void A_named_account_is_kept()
    {
        var webhook = new KashierWebhookBuilder(_key, "egypt").Paid("ORD-1", _amount);

        webhook.Account.ShouldBe(new PaymentAccount(PaymentProviderId.Kashier, "egypt"));
        webhook.ReceivedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task An_app_handler_can_be_tested_end_to_end_without_kashier_Async()
    {
        var handled = new List<PaymentEvent>();
        using var harness = new KashierHarness(o => o.PaymentApiKey = _key, services =>
        {
            services.AddScoped<IPaymentEventHandler>(_ => new InboxHandler(handled));
            services.AddPayGate().ConfigureWebhooks(KashierHarness.Account, o => o.ConfirmWithProvider = false);
        });
        var processor = harness.Provider.GetRequiredService<IWebhookProcessor>();
        var webhook = Builder.Paid("ORD-1", _amount);

        var first = await processor.ProcessAsync(webhook, Token);
        var retry = await processor.ProcessAsync(webhook, Token);

        first.Ack.StatusCode.ShouldBe(200);
        retry.Ack.StatusCode.ShouldBe(409);
        handled.ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_builder_needs_the_payment_api_key(string? key)
    {
        Should.Throw<ArgumentException>(() => new KashierWebhookBuilder(key!));
    }

    private static async Task<WebhookParseResult> VerifyAsync(WebhookRequest webhook)
    {
        using var harness = new KashierHarness(o => o.PaymentApiKey = _key);
        var verifier = harness.Provider.GetServices<IWebhookVerifier>().Single(v => v.Provider == PaymentProviderId.Kashier);
        return await verifier.VerifyAndParseAsync(webhook, Token);
    }

    private static async Task<PaymentEvent> SingleEventAsync(WebhookRequest webhook)
    {
        var result = await VerifyAsync(webhook);
        result.Kind.ShouldBe(WebhookParseKind.Verified, result.Reason);
        return result.Events.ShouldHaveSingleItem();
    }

    /// <summary>A handler that dedupes on the event id, as the docs ask apps to.</summary>
    private sealed class InboxHandler(List<PaymentEvent> handled) : IPaymentEventHandler
    {
        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
        {
            if (handled.Any(e => e.EventId == paymentEvent.EventId))
            {
                return ValueTask.FromResult(WebhookHandlingOutcome.AlreadyProcessed);
            }

            handled.Add(paymentEvent);
            return ValueTask.FromResult(WebhookHandlingOutcome.Processed);
        }
    }
}
