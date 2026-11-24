namespace PayGate.Kashier.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayGate.Kashier.Webhooks;
using PayGate.Webhooks;

/// <summary>Kashier's webhooks, verified and parsed, against the recorded sandbox traffic (tests/fixtures/kashier/webhooks).</summary>
public sealed class KashierWebhookTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- every recorded webhook ----

    [Theory]
    [InlineData("pay-success-card", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("pay-success-wallet", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("pay-failure", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("retry-1-pay-failure", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("retry-2-pay-success", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("authorize-success", PaymentEventType.PaymentAuthorized, PaymentStatus.Authorized)]
    [InlineData("capture-success", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("refund-partial", PaymentEventType.RefundSucceeded, PaymentStatus.PartiallyRefunded)]
    [InlineData("refund-remainder", PaymentEventType.RefundSucceeded, PaymentStatus.PartiallyRefunded)]
    [InlineData("void-success", PaymentEventType.PaymentCanceled, PaymentStatus.Canceled)]
    public async Task Every_recorded_webhook_verifies_and_reads_as_what_happened_Async(string name, PaymentEventType type, PaymentStatus status)
    {
        var result = await VerifyAsync(Fixtures.Webhook(name));

        result.Kind.ShouldBe(WebhookParseKind.Verified, result.Reason);
        var paymentEvent = result.Events.ShouldHaveSingleItem();
        paymentEvent.Type.ShouldBe(type);
        paymentEvent.Status.ShouldBe(status);
    }

    [Fact]
    public async Task A_payment_webhook_carries_the_reference_transaction_amount_and_time_Async()
    {
        var paymentEvent = await SingleEventAsync(Fixtures.Webhook("pay-success-card"));

        paymentEvent.Account.ShouldBe(KashierHarness.Account);
        paymentEvent.MerchantReference.ShouldBe("PG-20261122212627-3721");
        paymentEvent.ProviderTransactionId.ShouldBe("TX-00000000118");
        paymentEvent.ProviderPaymentId.ShouldBeNull("the webhook names the order, not the session");
        paymentEvent.Amount.ShouldBe(new Money(50m, Currency.Egp));
        paymentEvent.RefundAmount.ShouldBeNull();
        paymentEvent.RawStatus.ShouldBe("pay/SUCCESS");
        paymentEvent.OccurredAt.ShouldBe(new DateTimeOffset(2026, 11, 22, 21, 27, 16, 595, TimeSpan.Zero));
        paymentEvent.OccurredAtIsReceiptTime.ShouldBeFalse();
    }

    [Fact]
    public async Task Echoed_metadata_keeps_string_values_and_drops_kashiers_objects_Async()
    {
        var paymentEvent = await SingleEventAsync(Fixtures.Webhook("refund-partial"));

        paymentEvent.Metadata["playgroundRef"].ShouldBe("PG-20261122141508-1791");
        paymentEvent.Metadata["source"].ShouldBe("paygate-playground");
        paymentEvent.Metadata.ShouldNotContainKey("kashier_user");
        paymentEvent.Metadata.ShouldNotContainKey("termsAndConditions");
    }

    [Fact]
    public async Task The_raw_body_is_never_passed_on_because_it_holds_the_payment_api_key_Async()
    {
        var paymentEvent = await SingleEventAsync(Fixtures.Webhook("pay-success-card"));

        paymentEvent.RawPayload.ShouldBeNull();
    }

    [Fact]
    public async Task A_refund_webhook_is_the_refund_alone_never_a_payment_Async()
    {
        var paymentEvent = await SingleEventAsync(Fixtures.Webhook("refund-partial"));

        paymentEvent.Type.ShouldBe(PaymentEventType.RefundSucceeded);
        paymentEvent.RefundAmount.ShouldBe(new Money(20m, Currency.Egp));
        paymentEvent.RefundedTotal.ShouldBeNull("the webhook has no running total; confirming with Kashier fills it");
        paymentEvent.Amount.ShouldBeNull("the webhook's amount is the refund's, not the payment's");
    }

    [Fact]
    public async Task Each_retry_in_a_session_is_its_own_event_under_one_reference_Async()
    {
        var failed = await SingleEventAsync(Fixtures.Webhook("retry-1-pay-failure"));
        var paid = await SingleEventAsync(Fixtures.Webhook("retry-2-pay-success"));

        failed.MerchantReference.ShouldBe(paid.MerchantReference);
        failed.EventId.ShouldNotBe(paid.EventId);
        failed.Status.CanTransitionTo(paid.Status).ShouldBeTrue("a late success after a failed try still counts");
    }

    [Fact]
    public async Task The_same_webhook_delivered_again_has_the_same_event_id_Async()
    {
        var first = await SingleEventAsync(Fixtures.Webhook("refund-partial"));
        var again = await SingleEventAsync(Fixtures.Webhook("refund-partial"));
        var other = await SingleEventAsync(Fixtures.Webhook("refund-remainder"));

        again.EventId.ShouldBe(first.EventId);
        other.EventId.ShouldNotBe(first.EventId, "a second refund on the same order is a new transaction");
        first.EventId.ShouldStartWith("kashier_evt_");
    }

    [Fact]
    public async Task A_webhook_without_a_usable_time_falls_back_to_receipt_time_Async()
    {
        var request = Fixtures.Webhook("pay-success-card", body => body["data"]!.AsObject().Remove("creationDate"));

        var paymentEvent = await SingleEventAsync(request);

        paymentEvent.OccurredAt.ShouldBe(request.ReceivedAt);
        paymentEvent.OccurredAtIsReceiptTime.ShouldBeTrue();
    }

    // ---- the signature ----

    [Fact]
    public void The_signed_string_is_the_sorted_keys_with_only_the_values_encoded()
    {
        var data = JsonNode.Parse("""
            {
              "status": "SUCCESS", "amount": 113.34, "currency": "EGP", "channel": "online | e-commerce",
              "merchantOrderId": "ORD 1/2", "transactionId": "TX-1", "unsigned": "ignored",
              "signatureKeys": ["transactionId", "status", "merchantOrderId", "currency", "channel", "amount"]
            }
            """)!;
        using var document = System.Text.Json.JsonDocument.Parse(data.ToJsonString());

        KashierSignature.TryBuildWebhookString(document.RootElement, out string signed, out string? problem).ShouldBeTrue(problem);

        signed.ShouldBe("amount=113.34&channel=online%20%7C%20e-commerce&currency=EGP&merchantOrderId=ORD%201%2F2&status=SUCCESS&transactionId=TX-1");
    }

    [Fact]
    public async Task A_number_is_signed_as_kashier_wrote_it_not_reformatted_Async()
    {
        // 113.340 would be 113.34 after a round trip through decimal; Kashier signs the text it sent.
        var request = Fixtures.Webhook("authorize-success", body => body["data"]!["amount"] = JsonNode.Parse("113.340"));

        var result = await VerifyAsync(request);

        result.Kind.ShouldBe(WebhookParseKind.Verified, result.Reason);
        result.Events[0].Amount.ShouldBe(new Money(113.34m, Currency.Egp));
    }

    [Theory]
    [InlineData("tampered amount")]
    [InlineData("tampered status")]
    [InlineData("tampered reference")]
    [InlineData("key removed from signatureKeys")]
    [InlineData("signed field removed")]
    public async Task A_changed_signed_part_breaks_the_signature_Async(string change)
    {
        var request = Fixtures.Webhook("pay-failure", body =>
        {
            var data = body["data"]!.AsObject();
            switch (change)
            {
                case "tampered amount": data["amount"] = 5000; break;
                case "tampered status": data["status"] = "SUCCESS"; break;
                case "tampered reference": data["merchantOrderId"] = "ORD-SOMEONE-ELSE"; break;
                case "key removed from signatureKeys": data["signatureKeys"]!.AsArray().RemoveAt(1); break;
                default: data.Remove("method"); break;
            }
        }, resign: false);

        var result = await VerifyAsync(request);

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
        result.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("no signatureKeys", "has no data.signatureKeys")]
    [InlineData("empty signatureKeys", "has no data.signatureKeys")]
    [InlineData("listed field absent", "'method' is missing or null")]
    [InlineData("listed field null", "'method' is missing or null")]
    [InlineData("amount not signed", "does not sign 'amount'")]
    [InlineData("status not signed", "does not sign 'status'")]
    public async Task It_fails_closed_even_when_the_signature_matches_what_was_sent_Async(string change, string reason)
    {
        // Each body is signed with the right key over exactly what it carries. It is still refused: a field PayGate acts on
        // would be outside the signature, or the signed string could not be built the way Kashier builds it.
        var body = Fixtures.WebhookBody("pay-success-card");
        var data = body["data"]!.AsObject();
        string signature = Fixtures.SignWebhook(data, Fixtures.SigningKey);
        switch (change)
        {
            case "no signatureKeys": data.Remove("signatureKeys"); break;
            case "empty signatureKeys": data["signatureKeys"] = new JsonArray(); break;
            case "listed field absent": data.Remove("method"); break;
            case "listed field null": data["method"] = null; break;
            case "amount not signed": Unsign(data, "amount", out signature); break;
            default: Unsign(data, "status", out signature); break;
        }

        var result = await VerifyAsync(Fixtures.Webhook(body, signature));

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
        result.Reason.ShouldNotBeNull().ShouldContain(reason);
    }

    [Fact]
    public async Task A_signature_made_with_another_key_is_refused_Async()
    {
        var body = Fixtures.WebhookBody("pay-success-card");

        var result = await VerifyAsync(Fixtures.Webhook(body, Fixtures.SignWebhook(body["data"]!.AsObject(), "some-other-key")));

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("52929faf")]
    public async Task A_missing_or_unreadable_signature_is_refused_Async(string? signature)
    {
        var result = await VerifyAsync(Fixtures.Webhook(Fixtures.WebhookBody("pay-success-card"), signature));

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
    }

    [Fact]
    public async Task The_header_name_and_hex_case_and_surrounding_spaces_do_not_matter_Async()
    {
        string signature = Fixtures.Load("webhooks/pay-success-card.json")["headers"]!["x-kashier-signature"]!.GetValue<string>();

        var result = await VerifyAsync(Fixtures.Webhook(Fixtures.WebhookBody("pay-success-card"), $" {signature.ToUpperInvariant()} ", "X-Kashier-Signature"));

        result.Kind.ShouldBe(WebhookParseKind.Verified, result.Reason);
    }

    [Fact]
    public async Task The_reason_never_shows_the_signature_or_the_key_Async()
    {
        var request = Fixtures.Webhook("pay-success-card", body => body["data"]!["amount"] = 1, resign: false);

        var result = await VerifyAsync(request);

        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldNotContain(request.GetHeader("x-kashier-signature")!);
        result.Reason.ShouldNotContain(Fixtures.SigningKey);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2]")]
    [InlineData("""{"event":"pay"}""")]
    [InlineData("""{"event":"pay","data":"text"}""")]
    public async Task A_body_that_is_not_a_kashier_webhook_is_malformed_Async(string body)
    {
        var request = Fixtures.Webhook(new JsonObject(), "00") with { Body = System.Text.Encoding.UTF8.GetBytes(body) };

        var result = await VerifyAsync(request);

        result.Kind.ShouldBe(WebhookParseKind.Malformed);
    }

    [Fact]
    public async Task An_unsigned_event_name_can_be_changed_without_breaking_the_signature_Async()
    {
        // This is why AddKashier turns ConfirmWithProvider on: the status the app acts on is then Kashier's.
        var request = Fixtures.Webhook("authorize-success", body => body["event"] = "pay", resign: false);

        var paymentEvent = await SingleEventAsync(request);

        paymentEvent.Type.ShouldBe(PaymentEventType.PaymentSucceeded);
    }

    // ---- what each event means ----

    [Theory]
    [InlineData("pay", "SUCCESS", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("pay", "FAILURE", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("pay", "PENDING", PaymentEventType.PaymentPending, PaymentStatus.Pending)]
    [InlineData("authorize", "SUCCESS", PaymentEventType.PaymentAuthorized, PaymentStatus.Authorized)]
    [InlineData("authorize", "FAILURE", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("capture", "SUCCESS", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    [InlineData("void", "SUCCESS", PaymentEventType.PaymentCanceled, PaymentStatus.Canceled)]
    [InlineData("reversal", "SUCCESS", PaymentEventType.PaymentCanceled, PaymentStatus.Canceled)]
    [InlineData("reject", "SUCCESS", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("refund", "SUCCESS", PaymentEventType.RefundSucceeded, PaymentStatus.PartiallyRefunded)]
    [InlineData("partial_refund", "SUCCESS", PaymentEventType.RefundSucceeded, PaymentStatus.PartiallyRefunded)]
    [InlineData("refund", "FAILURE", PaymentEventType.RefundFailed, PaymentStatus.Unknown)]
    public void The_event_says_what_happened_and_the_status_whether_it_worked(string eventName, string status, PaymentEventType type, PaymentStatus paymentStatus)
    {
        var kind = KashierWebhookVerifier.Classify(eventName, status).ShouldNotBeNull();

        kind.Type.ShouldBe(type);
        kind.Status.ShouldBe(paymentStatus);
    }

    [Theory]
    [InlineData("capture", "FAILURE")]
    [InlineData("void", "FAILURE")]
    [InlineData("pay", "SOMETHING_NEW")]
    [InlineData("chargeback", "SUCCESS")]
    public void A_change_that_moves_nothing_reads_as_nothing(string eventName, string status)
    {
        KashierWebhookVerifier.Classify(eventName, status).ShouldBeNull();
    }

    [Theory]
    [InlineData("idempotency")]
    [InlineData("chargeback")]
    [InlineData("")]
    public async Task A_verified_webhook_with_an_event_paygate_does_not_report_is_ignored_Async(string eventName)
    {
        var request = Fixtures.Webhook("pay-success-card", body => body["event"] = eventName, resign: false);

        var result = await VerifyAsync(request);

        result.Kind.ShouldBe(WebhookParseKind.Ignored);
    }

    [Fact]
    public async Task Event_names_and_statuses_are_read_whatever_their_case_Async()
    {
        var request = Fixtures.Webhook("refund-partial", body =>
        {
            body["event"] = "Refund";
            body["data"]!["status"] = "success";
        });

        var paymentEvent = await SingleEventAsync(request);

        paymentEvent.Type.ShouldBe(PaymentEventType.RefundSucceeded);
        paymentEvent.RawStatus.ShouldBe("refund/SUCCESS");
    }

    [Theory]
    [InlineData("currency", "XYZ1")]
    [InlineData("amount", "fifty")]
    [InlineData("amount", "-5")]
    public async Task A_signed_webhook_without_a_usable_amount_is_malformed_Async(string field, string value)
    {
        var request = Fixtures.Webhook("pay-success-card", body => body["data"]![field] = value);

        var result = await VerifyAsync(request);

        result.Kind.ShouldBe(WebhookParseKind.Malformed);
    }

    // ---- through the app's endpoint ----

    [Fact]
    public void Adding_kashier_confirms_each_webhook_with_kashier()
    {
        using var harness = new KashierHarness();

        harness.Provider.GetRequiredService<IOptionsMonitor<WebhookOptions>>().Get(KashierHarness.Account.ToString())
            .ConfirmWithProvider.ShouldBeTrue();
    }

    [Fact]
    public void An_app_can_still_turn_confirming_off()
    {
        using var harness = new KashierHarness(services: s => s.AddPayGate().ConfigureWebhooks(KashierHarness.Account, o => o.ConfirmWithProvider = false));

        harness.Provider.GetRequiredService<IOptionsMonitor<WebhookOptions>>().Get(KashierHarness.Account.ToString())
            .ConfirmWithProvider.ShouldBeFalse();
    }

    [Fact]
    public async Task A_confirmed_webhook_reaches_the_app_with_kashiers_status_and_session_Async()
    {
        var handler = new RecordingEventHandler();
        using var harness = Harness(handler);
        harness.Handler.RespondWithFixture("search-orders-retried-session").RespondWithFixture("session-retried-paid");

        var result = await Processor(harness).ProcessAsync(Fixtures.Webhook("retry-2-pay-success"), Token);

        result.Status.ShouldBe(WebhookProcessingStatus.Processed, result.Reason);
        result.Ack.StatusCode.ShouldBe(200);
        var paymentEvent = handler.Events.ShouldHaveSingleItem();
        paymentEvent.Status.ShouldBe(PaymentStatus.Succeeded);
        paymentEvent.ProviderPaymentId.ShouldBe("6b035e79f6ff30226e46f29b", "confirming fills in the session id");
        paymentEvent.Amount.ShouldBe(new Money(75.5m, Currency.Egp));
    }

    [Fact]
    public async Task A_webhook_renamed_to_a_refund_cannot_make_the_app_refund_Async()
    {
        // A paid webhook, replayed with event "refund", verifies. Confirming reads the session, which is simply PAID.
        var handler = new RecordingEventHandler();
        using var harness = Harness(handler);
        harness.Handler.RespondWithFixture("search-orders-retried-session").RespondWithFixture("session-retried-paid");
        var request = Fixtures.Webhook("retry-2-pay-success", body => body["event"] = "refund", resign: false);

        var result = await Processor(harness).ProcessAsync(request, Token);

        result.Status.ShouldBe(WebhookProcessingStatus.Processed, result.Reason);
        var paymentEvent = handler.Events.ShouldHaveSingleItem();
        paymentEvent.Status.ShouldBe(PaymentStatus.Succeeded);
        paymentEvent.RefundedTotal.ShouldBe(Money.Zero(Currency.Egp));
    }

    [Theory]
    [InlineData(WebhookHandlingOutcome.Processed, WebhookProcessingStatus.Processed, 200)]
    [InlineData(WebhookHandlingOutcome.Ignored, WebhookProcessingStatus.Ignored, 200)]
    [InlineData(WebhookHandlingOutcome.AlreadyProcessed, WebhookProcessingStatus.AlreadyProcessed, 409)]
    public async Task Kashier_is_answered_200_or_409_for_a_duplicate_Async(WebhookHandlingOutcome outcome, WebhookProcessingStatus status, int code)
    {
        using var harness = Harness(new RecordingEventHandler(outcome), confirm: false);

        var result = await Processor(harness).ProcessAsync(Fixtures.Webhook("pay-success-card"), Token);

        result.Status.ShouldBe(status);
        result.Ack.StatusCode.ShouldBe(code);
        harness.Handler.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("forged", WebhookProcessingStatus.InvalidSignature, 401)]
    [InlineData("ignored", WebhookProcessingStatus.Ignored, 200)]
    public async Task A_webhook_the_app_never_sees_is_still_answered_Async(string kind, WebhookProcessingStatus status, int code)
    {
        var handler = new RecordingEventHandler();
        using var harness = Harness(handler);
        var request = kind == "forged"
            ? Fixtures.Webhook("pay-success-card", body => body["data"]!["amount"] = 1, resign: false)
            : Fixtures.Webhook("pay-success-card", body => body["event"] = "idempotency", resign: false);

        var result = await Processor(harness).ProcessAsync(request, Token);

        result.Status.ShouldBe(status);
        result.Ack.StatusCode.ShouldBe(code);
        handler.Events.ShouldBeEmpty();
        harness.Handler.Requests.ShouldBeEmpty("nothing is confirmed before the signature holds");
    }

    private static void Unsign(JsonObject data, string key, out string signature)
    {
        var keys = data["signatureKeys"]!.AsArray();
        keys.Remove(keys.First(k => k!.GetValue<string>() == key));
        signature = Fixtures.SignWebhook(data, Fixtures.SigningKey);
    }

    private static KashierHarness Harness(RecordingEventHandler handler, bool confirm = true) => new(
        o => o.PaymentApiKey = Fixtures.SigningKey,
        services =>
        {
            services.AddSingleton<IPaymentEventHandler>(handler);
            if (!confirm)
            {
                services.AddPayGate().ConfigureWebhooks(KashierHarness.Account, o => o.ConfirmWithProvider = false);
            }
        });

    private static IWebhookProcessor Processor(KashierHarness harness) => harness.Provider.GetRequiredService<IWebhookProcessor>();

    private static async Task<WebhookParseResult> VerifyAsync(WebhookRequest request)
    {
        using var harness = new KashierHarness(o => o.PaymentApiKey = Fixtures.SigningKey);
        var verifier = harness.Provider.GetServices<IWebhookVerifier>().Single(v => v.Provider == PaymentProviderId.Kashier);
        return await verifier.VerifyAndParseAsync(request, Token);
    }

    private static async Task<PaymentEvent> SingleEventAsync(WebhookRequest request)
    {
        var result = await VerifyAsync(request);
        result.Kind.ShouldBe(WebhookParseKind.Verified, result.Reason);
        return result.Events.ShouldHaveSingleItem();
    }

    private sealed class RecordingEventHandler(WebhookHandlingOutcome outcome = WebhookHandlingOutcome.Processed) : IPaymentEventHandler
    {
        public List<PaymentEvent> Events { get; } = [];

        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(paymentEvent);
            return ValueTask.FromResult(outcome);
        }
    }
}
