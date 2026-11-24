namespace PayGate.Kashier.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PayGate.Webhooks;

/// <summary>
/// Parity with the in-app Kashier integration PayGate replaces. The payload is the one that integration's own regression
/// tests use (a wallet payment of 165 EGP). Each test says what the old integration decided. Where PayGate decides the same,
/// the test pins it; where it decides differently on purpose, the test pins the fix and the comment says why.
/// </summary>
public sealed class KashierParityTests
{
    private const string _reference = "1d91cf423ea04321bf59999a4cc576bc";
    private const string _transactionId = "TX-433127467290";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- the same decisions ----

    [Fact]
    public async Task A_paid_wallet_payment_succeeds_Async()
    {
        // Old: Succeeded, fulfilled.
        var paymentEvent = await SingleEventAsync(Payload());

        paymentEvent.Type.ShouldBe(PaymentEventType.PaymentSucceeded);
        paymentEvent.Status.ShouldBe(PaymentStatus.Succeeded);
        paymentEvent.MerchantReference.ShouldBe(_reference);
        paymentEvent.ProviderTransactionId.ShouldBe(_transactionId);
        paymentEvent.Amount.ShouldBe(new Money(165m, Currency.Egp));
    }

    [Theory]
    [InlineData("FAILED", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("FAILURE", PaymentEventType.PaymentFailed, PaymentStatus.Failed)]
    [InlineData("CANCELLED", PaymentEventType.PaymentCanceled, PaymentStatus.Canceled)]
    [InlineData("PENDING", PaymentEventType.PaymentPending, PaymentStatus.Pending)]
    [InlineData("INITIATED", PaymentEventType.PaymentPending, PaymentStatus.Pending)]
    [InlineData("AUTHENTICATION_IN_PROGRESS", PaymentEventType.PaymentPending, PaymentStatus.Pending)]
    [InlineData("CAPTURED", PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded)]
    public async Task Each_status_the_old_integration_read_reads_the_same_Async(string status, PaymentEventType type, PaymentStatus paymentStatus)
    {
        // Old: the same status, through its status mapper. None of these touched the ledger except CAPTURED.
        var paymentEvent = await SingleEventAsync(Payload(status: status));

        paymentEvent.Type.ShouldBe(type);
        paymentEvent.Status.ShouldBe(paymentStatus);
    }

    [Fact]
    public async Task A_bad_signature_is_refused_before_anything_happens_Async()
    {
        // Old: rejected, nothing fulfilled.
        var result = await VerifyAsync(Payload(), signature: new string('0', 64));

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
        result.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_redelivered_webhook_is_recognised_as_the_same_event_Async()
    {
        // Old: deduplicated on the transaction id and event.
        var first = await SingleEventAsync(Payload());
        var again = await SingleEventAsync(Payload());

        again.EventId.ShouldBe(first.EventId);
    }

    [Fact]
    public async Task A_refund_never_reads_as_a_payment_Async()
    {
        // Old: a refund event never fulfilled.
        var paymentEvent = await SingleEventAsync(Payload(eventName: "refund"));

        paymentEvent.Type.ShouldBe(PaymentEventType.RefundSucceeded);
    }

    // ---- intended differences ----

    [Fact]
    public async Task A_refund_carries_its_own_amount_instead_of_marking_the_payment_fully_refunded_Async()
    {
        // Old: every successful refund set the payment to RefundedFull, so a partial refund read as a full one.
        var paymentEvent = await SingleEventAsync(Payload(eventName: "refund", amount: "40"));

        paymentEvent.RefundAmount.ShouldBe(new Money(40m, Currency.Egp));
        paymentEvent.Status.ShouldBe(PaymentStatus.PartiallyRefunded, "the least it can be; confirming with Kashier gives the real status");
    }

    [Fact]
    public async Task A_partial_refund_event_is_a_refund_not_a_payment_Async()
    {
        // Old: only "refund" counted as a refund, so partial_refund SUCCESS was read as a payment and fulfilled again.
        var paymentEvent = await SingleEventAsync(Payload(eventName: "partial_refund", amount: "40"));

        paymentEvent.Type.ShouldBe(PaymentEventType.RefundSucceeded);
    }

    [Fact]
    public async Task An_authorization_holds_the_funds_but_is_not_a_payment_Async()
    {
        // Old: authorize SUCCESS read as Succeeded and fulfilled, though the money was only held.
        var paymentEvent = await SingleEventAsync(Payload(eventName: "authorize"));

        paymentEvent.Type.ShouldBe(PaymentEventType.PaymentAuthorized);
        paymentEvent.Status.ShouldBe(PaymentStatus.Authorized);
    }

    [Fact]
    public async Task A_void_cancels_the_payment_Async()
    {
        // Old: void SUCCESS read as Succeeded, so a voided payment stayed paid and was fulfilled again.
        var paymentEvent = await SingleEventAsync(Payload(eventName: "void"));

        paymentEvent.Type.ShouldBe(PaymentEventType.PaymentCanceled);
        paymentEvent.Status.ShouldBe(PaymentStatus.Canceled);
    }

    [Fact]
    public async Task An_unknown_status_is_ignored_not_guessed_Async()
    {
        // Old: any status it didn't know became Initiated.
        var result = await VerifyAsync(Payload(status: "SOMETHING_NEW"));

        result.Kind.ShouldBe(WebhookParseKind.Ignored);
    }

    [Fact]
    public async Task A_webhook_without_an_event_name_is_ignored_not_read_as_pay_Async()
    {
        // Old: a missing event defaulted to "pay". The event name is not signed, so that default let anyone strip it.
        var result = await VerifyAsync(Payload(eventName: null));

        result.Kind.ShouldBe(WebhookParseKind.Ignored);
    }

    [Fact]
    public async Task A_signed_field_missing_from_the_body_fails_closed_Async()
    {
        // Old: a listed key absent from the body was skipped, and the rest was checked (fail open).
        var body = Payload();
        string signature = Fixtures.SignWebhook(body["data"]!.AsObject(), Fixtures.SigningKey);
        body["data"]!.AsObject().Remove("channel");

        var result = await VerifyAsync(body, signature);

        result.Kind.ShouldBe(WebhookParseKind.InvalidSignature);
    }

    [Fact]
    public async Task A_status_change_on_one_transaction_is_a_new_event_Async()
    {
        // Old: deduplicated on transaction id and event only, so a SUCCESS after a PENDING for the same transaction was
        // dropped as already processed.
        var pending = await SingleEventAsync(Payload(status: "PENDING"));
        var paid = await SingleEventAsync(Payload(status: "SUCCESS"));

        paid.EventId.ShouldNotBe(pending.EventId);
    }

    [Fact]
    public void A_late_failure_cannot_undo_a_success()
    {
        // Old: Succeeded and Failed were both terminal, and a terminal status could move to another one, so a late FAILURE
        // from an earlier try in the same session turned a paid payment into a failed one.
        PaymentStatus.Succeeded.CanTransitionTo(PaymentStatus.Failed).ShouldBeFalse();
        PaymentStatus.Failed.CanTransitionTo(PaymentStatus.Succeeded).ShouldBeTrue("a retry after a decline still counts, as before");
    }

    [Fact]
    public async Task A_duplicate_is_answered_409_and_a_forged_copy_401_Async()
    {
        // Old: a duplicate was answered 200 and a bad signature 400. It also looked the event up before checking the
        // signature, so a forged copy of a processed event was answered 200.
        var seen = new HashSet<string>();
        using var harness = new KashierHarness(o => o.PaymentApiKey = Fixtures.SigningKey, services =>
        {
            services.AddSingleton<IPaymentEventHandler>(new DedupingHandler(seen));
            services.AddPayGate().ConfigureWebhooks(KashierHarness.Account, o => o.ConfirmWithProvider = false);
        });
        var processor = harness.Provider.GetRequiredService<IWebhookProcessor>();

        var first = await processor.ProcessAsync(Request(Payload()), Token);
        var duplicate = await processor.ProcessAsync(Request(Payload()), Token);
        var forged = await processor.ProcessAsync(Request(Payload(), new string('0', 64)), Token);

        first.Ack.StatusCode.ShouldBe(200);
        duplicate.Ack.StatusCode.ShouldBe(409);
        forged.Ack.StatusCode.ShouldBe(401);
    }

    /// <summary>The body the old integration's regression tests send, with the event, status and amount as parameters.</summary>
    private static JsonObject Payload(string? eventName = "pay", string status = "SUCCESS", string amount = "165")
    {
        var body = JsonNode.Parse($$"""
            {
              "data": {
                "merchantOrderId": "{{_reference}}",
                "kashierOrderId": "2baf00b3-61df-4e00-a290-518c55e35ce5",
                "orderReference": "ORD-29884637",
                "transactionId": "{{_transactionId}}",
                "status": "{{status}}",
                "method": "wallet",
                "amount": {{amount}},
                "currency": "EGP",
                "channel": "online | e-commerce",
                "transactionResponseCode": "00",
                "transactionResponseMessage": { "ar": "تمت الموافقة", "en": "Approved" },
                "settlementInfo": { "vat": "0.71", "sellingRate": 1.85, "sellingFlat": 2, "settledAmount": "159.24" },
                "signatureKeys": ["amount","channel","currency","kashierOrderId","merchantOrderId","method","orderReference","status","transactionId","transactionResponseCode"]
              }
            }
            """)!.AsObject();
        if (eventName is not null)
        {
            body["event"] = eventName;
        }

        return body;
    }

    private static WebhookRequest Request(JsonObject body, string? signature = null) =>
        Fixtures.Webhook(body, signature ?? Fixtures.SignWebhook(body["data"]!.AsObject(), Fixtures.SigningKey));

    private static async Task<WebhookParseResult> VerifyAsync(JsonObject body, string? signature = null)
    {
        using var harness = new KashierHarness(o => o.PaymentApiKey = Fixtures.SigningKey);
        var verifier = harness.Provider.GetServices<IWebhookVerifier>().Single(v => v.Provider == PaymentProviderId.Kashier);
        return await verifier.VerifyAndParseAsync(Request(body, signature), Token);
    }

    private static async Task<PaymentEvent> SingleEventAsync(JsonObject body)
    {
        var result = await VerifyAsync(body);
        result.Kind.ShouldBe(WebhookParseKind.Verified, result.Reason);
        return result.Events.ShouldHaveSingleItem();
    }

    private sealed class DedupingHandler(HashSet<string> seen) : IPaymentEventHandler
    {
        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(seen.Add(paymentEvent.EventId) ? WebhookHandlingOutcome.Processed : WebhookHandlingOutcome.AlreadyProcessed);
    }
}
