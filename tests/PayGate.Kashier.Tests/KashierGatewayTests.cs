namespace PayGate.Kashier.Tests;

using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

/// <summary>The gateway an app resolves, against Kashier's recorded answers (tests/fixtures/kashier/api).</summary>
public sealed class KashierGatewayTests
{
    private const string _noOrders = """{"status":"SUCCESS","message":"Orders retrieved successfully","data":[]}""";
    private static readonly DateTimeOffset _now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- create ----

    [Fact]
    public async Task A_payment_becomes_a_kashier_session_and_a_redirect_Async()
    {
        using var harness = Harness();
        harness.Handler.Respond(200, _noOrders).RespondWithFixture("create-session");

        var result = await harness.Gateway.CreatePaymentAsync(Request(), Token);

        var session = result.Value;
        session.ProviderPaymentId.ShouldBe("6b035e0df1523df5868a61eb");
        session.Status.ShouldBe(PaymentStatus.Pending);
        session.NextAction.ShouldBeOfType<RedirectAction>().Url.ShouldBe(new Uri("https://payments.kashier.io/session/6b035e0df1523df5868a61eb?mode=test"));
        session.RawResponse.ShouldBeNull("Kashier echoes the Payment API Key in this body");
    }

    [Fact]
    public async Task The_session_body_carries_the_request_in_kashiers_terms_Async()
    {
        using var harness = Harness();
        harness.Handler.Respond(200, _noOrders).RespondWithFixture("create-session");

        await harness.Gateway.CreatePaymentAsync(Request() with
        {
            Locale = "ar-EG",
            AllowedPaymentMethods = new HashSet<PaymentMethodType> { PaymentMethodType.Wallet, PaymentMethodType.Card },
        }, Token);

        var body = SentBody(harness, 1);
        body["merchantId"]!.GetValue<string>().ShouldBe("MID-1234-567");
        body["order"]!.GetValue<string>().ShouldBe("ORD-42");
        body["amount"]!.GetValue<string>().ShouldBe("113.34");
        body["currency"]!.GetValue<string>().ShouldBe("EGP");
        body["merchantRedirect"]!.GetValue<string>().ShouldBe("https://shop.example/payments/return?ref=ORD-42");
        body["serverWebhook"]!.GetValue<string>().ShouldBe("https://shop.example/webhooks/kashier");
        body["display"]!.GetValue<string>().ShouldBe("ar");
        body["allowedMethods"]!.GetValue<string>().ShouldBe("card,wallet");
        body["maxFailureAttempts"]!.GetValue<int>().ShouldBe(3);
        body["expireAt"]!.GetValue<string>().ShouldBe("2026-11-23T08:30:00.000Z");
        body["description"]!.GetValue<string>().ShouldBe("Order 42");
        body["metaData"]!["orderId"]!.GetValue<string>().ShouldBe("42");
        body["customer"]!["email"]!.GetValue<string>().ShouldBe("mona@example.com");
        body.ContainsKey("failureRedirect").ShouldBeFalse();
    }

    [Fact]
    public async Task Kashier_options_override_the_account_defaults_Async()
    {
        using var harness = Harness(o => o.AllowedMethods = "card");
        harness.Handler.Respond(200, _noOrders).RespondWithFixture("create-session");

        await harness.Gateway.CreatePaymentAsync(Request() with
        {
            ExpiresAt = _now.AddHours(2),
            ProviderOptions = new KashierPaymentOptions { Display = "en", MaxFailureAttempts = 1, FailureRedirect = true, Enable3DS = false },
        }, Token);

        var body = SentBody(harness, 1);
        body["display"]!.GetValue<string>().ShouldBe("en");
        body["allowedMethods"]!.GetValue<string>().ShouldBe("card");
        body["maxFailureAttempts"]!.GetValue<int>().ShouldBe(1);
        body["failureRedirect"]!.GetValue<bool>().ShouldBeTrue();
        body["enable3DS"]!.GetValue<bool>().ShouldBeFalse();
        body["expireAt"]!.GetValue<string>().ShouldBe("2026-11-23T10:00:00.000Z");
    }

    [Fact]
    public async Task A_reference_that_already_has_a_paid_order_is_refused_before_creating_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("search-orders-retried-session");

        var result = await harness.Gateway.CreatePaymentAsync(Request() with { MerchantReference = "PG-20261122212816-7711" }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.DuplicateReference);
        result.Error.Message.ShouldContain("CAPTURED");
        harness.Handler.Requests.ShouldHaveSingleItem().Method.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public async Task A_reference_whose_only_orders_failed_can_be_paid_again_Async()
    {
        using var harness = Harness();
        harness.Handler
            .Respond(200, """{"status":"SUCCESS","data":[{"orderId":"o-1","merchantOrderId":"ORD-42","status":"FAILED","sessionId":"s-1"}]}""")
            .RespondWithFixture("create-session");

        var result = await harness.Gateway.CreatePaymentAsync(Request(), Token);

        result.IsSuccess.ShouldBeTrue();
        harness.Handler.Requests.Select(r => r.Method).ShouldBe([HttpMethod.Get, HttpMethod.Post]);
    }

    [Fact]
    public async Task The_reference_check_can_be_turned_off_Async()
    {
        using var harness = Harness(o => o.CheckReferenceBeforeCreate = false);
        harness.Handler.RespondWithFixture("create-session");

        await harness.Gateway.CreatePaymentAsync(Request(), Token);

        harness.Handler.Requests.ShouldHaveSingleItem().Method.ShouldBe(HttpMethod.Post);
    }

    [Theory]
    [InlineData("no-return-url", "Kashier needs a ReturnUrl")]
    [InlineData("localhost", "refuses localhost return URLs")]
    [InlineData("zero", "more than zero")]
    [InlineData("expired", "ExpiresAt is in the past")]
    [InlineData("other-provider", "not Kashier")]
    public async Task Invalid_requests_are_refused_before_anything_is_sent_Async(string problem, string expected)
    {
        using var harness = Harness();
        var request = problem switch
        {
            "no-return-url" => Request() with { ReturnUrl = null },
            "localhost" => Request() with { ReturnUrl = new Uri("http://localhost:5080/payments/return") },
            "zero" => Request() with { Amount = Money.Zero(Currency.Egp) },
            "expired" => Request() with { ExpiresAt = _now.AddMinutes(-1) },
            _ => Request() with { ProviderOptions = new OtherOptions() },
        };

        var result = await harness.Gateway.CreatePaymentAsync(request, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.Validation);
        result.Error.Message.ShouldContain(expected);
        harness.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_method_kashier_cant_offer_is_not_supported_Async()
    {
        using var harness = Harness();

        var result = await harness.Gateway.CreatePaymentAsync(
            Request() with { AllowedPaymentMethods = new HashSet<PaymentMethodType> { PaymentMethodType.Bnpl } }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.NotSupported);
        harness.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_session_keeps_kashiers_message_Async()
    {
        using var harness = Harness();
        harness.Handler.Respond(200, _noOrders).RespondWithFixture("create-session-localhost-redirect-400");

        var result = await harness.Gateway.CreatePaymentAsync(Request(), Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.Validation);
        result.Error.ProviderMessage.ShouldBe("\"merchantRedirect\" must be a valid URL");
    }

    [Theory]
    [InlineData(401, """{"error":"Authorization error","message":"Unauthorized"}""", PaymentErrorCode.Authentication)]
    [InlineData(400, """{"message":"Duplicate order","code":"ERR_ORD_02"}""", PaymentErrorCode.DuplicateReference)]
    [InlineData(404, """{"message":"Not found"}""", PaymentErrorCode.NotFound)]
    [InlineData(418, "", PaymentErrorCode.ProviderError)]
    public async Task Kashier_refusals_on_create_map_to_payment_errors_Async(int status, string body, PaymentErrorCode expected)
    {
        using var harness = Harness(o => o.CheckReferenceBeforeCreate = false);
        harness.Handler.Respond(status, body);

        var result = await harness.Gateway.CreatePaymentAsync(Request(), Token);

        result.Error!.Code.ShouldBe(expected);
    }

    [Fact]
    public async Task A_create_that_times_out_is_sent_once_and_reported_as_outcome_unknown_Async()
    {
        using var harness = Harness(o => o.CheckReferenceBeforeCreate = false);
        harness.Handler.Hang();

        var result = await harness.Gateway.CreatePaymentAsync(Request(), Token);

        harness.Handler.Requests.Count.ShouldBe(1);
        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
    }

    // ---- lookup ----

    [Theory]
    [InlineData("session-paid", PaymentStatus.Succeeded, "PAID", 60, 60, 0)]
    [InlineData("session-partially-refunded", PaymentStatus.PartiallyRefunded, "PARTIALLY_REFUNDED", 60, 60, 10)]
    [InlineData("session-refunded", PaymentStatus.Refunded, "REFUNDED", 113.34, 113.34, 113.34)]
    [InlineData("session-voided", PaymentStatus.Canceled, "VOIDED", 50, 0, 0)]
    [InlineData("session-unpaid-expired", PaymentStatus.Expired, "EXPIRED", 113.34, 0, 0)]
    [InlineData("session-failed-expired", PaymentStatus.Expired, "EXPIRED", 75.5, 0, 0)]
    [InlineData("session-wallet-paid", PaymentStatus.Succeeded, "PAID", 40, 40, 0)]
    public async Task Each_recorded_session_outcome_maps_to_a_status_and_amounts_Async(
        string fixture, PaymentStatus status, string raw, decimal amount, decimal captured, decimal refunded)
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture(fixture);

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("session-1"), Token)).Value;

        details.Status.ShouldBe(status);
        details.RawStatus.ShouldBe(raw);
        details.Amount.ShouldBe(new Money(amount, Currency.Egp));
        details.CapturedAmount.ShouldBe(new Money(captured, Currency.Egp));
        details.RefundedAmount.ShouldBe(new Money(refunded, Currency.Egp));
    }

    [Fact]
    public async Task A_paid_session_reports_its_ids_card_and_kashier_data_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-paid");

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("6b03f3404c677cc9c95edec7"), Token)).Value;

        harness.Handler.Requests.Single().Uri.ShouldBe(new Uri("https://test-api.kashier.io/v3/payment/sessions/6b03f3404c677cc9c95edec7/payment"));
        details.ProviderPaymentId.ShouldBe("6b03f3404c677cc9c95edec7");
        details.ProviderTransactionId.ShouldBe("TX-00000000133");
        details.MerchantReference.ShouldBe("PG-20261123080312-2622");
        details.PaymentMethod.ShouldBe(new PaymentMethodInfo { Type = PaymentMethodType.Card, Brand = "Mastercard", Last4 = "1118" });
        details.Failure.ShouldBeNull();
        details.GetProviderData<KashierPaymentData>().ShouldBe(new KashierPaymentData
        {
            OrderId = "6e804f8f-7d53-48ad-b943-2c1e87136a03",
            LastOperation = "pay",
            Fees = 5.00m,
            Vat = 0.70m,
            SettlementAmount = 54.30m,
        });
    }

    [Fact]
    public async Task A_retried_session_points_at_the_order_that_went_through_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-retried-paid");

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("6b035e79f6ff30226e46f29b"), Token)).Value;

        details.Status.ShouldBe(PaymentStatus.Succeeded);
        details.GetProviderData<KashierPaymentData>()!.OrderId.ShouldBe("f64e3069-7e07-4d11-8bca-6a877884b9b4");
        details.Failure.ShouldBeNull("the decline before the success is history, not the outcome");
    }

    [Fact]
    public async Task A_session_that_ended_after_a_decline_says_why_in_both_languages_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-failed-expired");

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("s"), Token)).Value;

        details.Failure.ShouldBe(new PaymentFailureReason
        {
            Code = PaymentErrorCode.Declined,
            ProviderCode = "AA",
            Message = "UNSPECIFIED FAILURE",
            MessageAr = "فشل غير محدد",
        });
    }

    [Fact]
    public async Task An_unpaid_session_has_no_order_transaction_or_method_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-unpaid-expired");

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("s"), Token)).Value;

        details.ProviderTransactionId.ShouldBeNull();
        details.PaymentMethod.ShouldBeNull();
        details.GetProviderData<KashierPaymentData>()!.OrderId.ShouldBeNull();
    }

    [Fact]
    public async Task A_wallet_payment_reports_the_wallet_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-wallet-paid");

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("s"), Token)).Value;

        details.PaymentMethod!.Type.ShouldBe(PaymentMethodType.Wallet);
        details.PaymentMethod.Last4.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_session_is_not_found_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-not-found-404");

        var result = await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("000000000000000000000000"), Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.NotFound);
    }

    [Theory]
    [InlineData("AUTHORIZED", PaymentStatus.Authorized)]
    [InlineData("CREATED", PaymentStatus.Pending)]
    [InlineData("OPENED", PaymentStatus.Pending)]
    [InlineData("PENDING", PaymentStatus.Pending)]
    [InlineData("FAILED", PaymentStatus.Failed)]
    [InlineData("CANCELLED", PaymentStatus.Canceled)]
    [InlineData("SOMETHING_NEW", PaymentStatus.Unknown)]
    public async Task Statuses_the_sandbox_did_not_produce_on_demand_map_too_Async(string raw, PaymentStatus expected)
    {
        using var harness = Harness();
        harness.Handler.Respond(200, Fixtures.SessionWithStatus("session-paid", raw));

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId("s"), Token)).Value;

        details.Status.ShouldBe(expected);
        details.RawStatus.ShouldBe(raw);
    }

    [Fact]
    public async Task A_lookup_by_reference_reads_the_session_of_the_order_that_went_through_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("search-orders-retried-session").RespondWithFixture("session-retried-paid");

        var details = (await harness.Gateway.GetPaymentAsync(PaymentLookup.ByMerchantReference("PG-20261122212816-7711"), Token)).Value;

        harness.Handler.Requests.Select(r => r.Uri.PathAndQuery).ShouldBe([
            "/v3/payment/orders?search=PG-20261122212816-7711",
            "/v3/payment/sessions/6b035e79f6ff30226e46f29b/payment",
        ]);
        details.ProviderPaymentId.ShouldBe("6b035e79f6ff30226e46f29b");
        details.Status.ShouldBe(PaymentStatus.Succeeded);
    }

    [Fact]
    public async Task A_lookup_by_reference_ignores_partial_matches_Async()
    {
        using var harness = Harness();
        harness.Handler.Respond(200, """{"status":"SUCCESS","data":[{"orderId":"o-1","merchantOrderId":"ORD-420","status":"CAPTURED","sessionId":"s-1"}]}""");

        var result = await harness.Gateway.GetPaymentAsync(PaymentLookup.ByMerchantReference("ORD-42"), Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.NotFound);
        harness.Handler.Requests.Count.ShouldBe(1);
    }

    // ---- refund ----

    [Fact]
    public async Task A_partial_refund_goes_to_the_sessions_order_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-paid").RespondWithFixture("refund-partial");

        var result = await harness.Gateway.RefundAsync(new RefundRequest
        {
            ProviderPaymentId = "6b03f3404c677cc9c95edec7",
            Amount = new Money(20m, Currency.Egp),
            Reason = "damaged",
        }, Token);

        var put = harness.Handler.Requests.Last();
        put.Method.ShouldBe(HttpMethod.Put);
        put.Uri.ShouldBe(new Uri("https://test-fep.kashier.io/v3/orders/6e804f8f-7d53-48ad-b943-2c1e87136a03"));
        JsonNode.Parse(put.Body!)!.ToJsonString().ShouldBe("""{"apiOperation":"REFUND","reason":"damaged","transaction":{"amount":20}}""");

        var refund = result.Value;
        refund.Status.ShouldBe(RefundStatus.Succeeded);
        refund.ProviderRefundId.ShouldBe("TX-00000000111");
        refund.Amount.ShouldBe(new Money(20m, Currency.Egp));
        refund.RawStatus.ShouldBe("PARTIALLY_REFUNDED");
    }

    [Fact]
    public async Task A_refund_without_an_amount_refunds_what_is_left_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-partially-refunded").RespondWithFixture("refund-remainder");

        var result = await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "6b03f3404c677cc9c95edec7" }, Token);

        JsonNode.Parse(harness.Handler.Requests.Last().Body!)!.ToJsonString().ShouldBe("""{"apiOperation":"REFUND"}""");
        result.Value.Amount.ShouldBe(new Money(63.34m, Currency.Egp));
        result.Value.RawStatus.ShouldBe("REFUNDED");
    }

    [Fact]
    public async Task An_over_refund_is_a_validation_error_not_a_bad_key_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-refunded").RespondWithFixture("refund-over-refund-401");

        var result = await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "s", Amount = new Money(1m, Currency.Egp) }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.Validation);
    }

    [Fact]
    public async Task A_fully_refunded_payment_is_refused_without_calling_kashier_again_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-refunded");

        var result = await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "s" }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.Validation);
        harness.Handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_authorized_payment_must_be_cancelled_not_refunded_Async()
    {
        using var harness = Harness();
        harness.Handler.Respond(200, Fixtures.SessionWithStatus("session-paid", "AUTHORIZED"));

        var result = await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "s" }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.NotSupported);
        harness.Handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_refund_in_another_currency_is_refused_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-paid");

        var result = await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "s", Amount = new Money(5m, Currency.Usd) }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.Validation);
        harness.Handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_refund_that_times_out_is_sent_once_and_reported_as_outcome_unknown_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-paid").Hang();

        var result = await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "s", Amount = new Money(5m, Currency.Egp) }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
        harness.Handler.Requests.Count(r => r.Method == HttpMethod.Put).ShouldBe(1);
    }

    [Fact]
    public async Task A_target_transaction_is_passed_through_when_the_app_names_one_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-paid").RespondWithFixture("refund-partial");

        await harness.Gateway.RefundAsync(new RefundRequest { ProviderPaymentId = "s", ProviderTransactionId = "TX-1" }, Token);

        JsonNode.Parse(harness.Handler.Requests.Last().Body!)!.ToJsonString()
            .ShouldBe("""{"apiOperation":"REFUND","transaction":{"targetTransactionId":"TX-1"}}""");
    }

    // ---- cancel ----

    [Fact]
    public void The_gateway_offers_cancel_through_paygates_wrapper()
    {
        using var harness = Harness();

        harness.Gateway.TryGetFeature<IPaymentCancelFeature>(out var cancel).ShouldBeTrue();
        cancel.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_paid_payment_is_voided_on_its_order_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-paid").RespondWithFixture("void");
        harness.Gateway.TryGetFeature<IPaymentCancelFeature>(out var cancel).ShouldBeTrue();

        var result = await cancel.CancelAsync(new PaymentCancelRequest { ProviderPaymentId = "6b03f3404c677cc9c95edec7" }, Token);

        var put = harness.Handler.Requests.Last();
        put.Uri.ShouldBe(new Uri("https://test-fep.kashier.io/v3/orders/6e804f8f-7d53-48ad-b943-2c1e87136a03"));
        JsonNode.Parse(put.Body!)!.ToJsonString().ShouldBe("""{"apiOperation":"VOID"}""");
        result.Value.Status.ShouldBe(PaymentStatus.Canceled);
        result.Value.RawStatus.ShouldBe("CANCELLED");
    }

    [Fact]
    public async Task Cancelling_a_voided_payment_again_is_a_no_op_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-voided");
        harness.Gateway.TryGetFeature<IPaymentCancelFeature>(out var cancel).ShouldBeTrue();

        var result = await cancel.CancelAsync(new PaymentCancelRequest { ProviderPaymentId = "s" }, Token);

        result.Value.Status.ShouldBe(PaymentStatus.Canceled);
        harness.Handler.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_unpaid_session_cant_be_voided_Async()
    {
        using var harness = Harness();
        harness.Handler.RespondWithFixture("session-unpaid-expired");
        harness.Gateway.TryGetFeature<IPaymentCancelFeature>(out var cancel).ShouldBeTrue();

        var result = await cancel.CancelAsync(new PaymentCancelRequest { ProviderPaymentId = "s" }, Token);

        result.Error!.Code.ShouldBe(PaymentErrorCode.Validation);
        harness.Handler.Requests.ShouldHaveSingleItem();
    }

    // ---- registration and capabilities ----

    [Fact]
    public void The_capabilities_match_what_the_sandbox_showed()
    {
        using var harness = Harness();

        harness.Gateway.Capabilities.ShouldBe(new PaymentCapabilities
        {
            SupportedNextActions = harness.Gateway.Capabilities.SupportedNextActions,
            SupportedPaymentMethods = harness.Gateway.Capabilities.SupportedPaymentMethods,
            LookupByMerchantReference = true,
            IdempotentWrites = false,
            PartialRefund = true,
            MultipleRefunds = true,
            PaymentExpiry = true,
            PerRequestNotificationUrl = true,
            MetadataPassthrough = true,
            RejectsDuplicateReference = false,
        });
        harness.Gateway.Capabilities.SupportedNextActions.ShouldBe([NextActionKind.Redirect]);
        harness.Gateway.Capabilities.SupportedPaymentMethods.ShouldBe(
            [PaymentMethodType.Card, PaymentMethodType.Wallet, PaymentMethodType.InstaPay], ignoreOrder: true);
    }

    [Fact]
    public void A_second_registration_of_the_same_account_is_refused()
    {
        var services = new ServiceCollection();
        var builder = services.AddPayGate().AddKashier(o => o.MerchantId = "MID-1234-567");

        Should.Throw<InvalidOperationException>(() => builder.AddKashier(o => o.MerchantId = "MID-1234-567"))
            .Message.ShouldContain("already registered for 'kashier'");
    }

    private static KashierHarness Harness(Action<KashierAccountOptions>? configure = null) =>
        new(configure, services => services.AddSingleton<TimeProvider>(new FixedTimeProvider(_now)));

    private static CreatePaymentRequest Request() => new()
    {
        MerchantReference = "ORD-42",
        Amount = new Money(113.34m, Currency.Egp),
        Description = "Order 42",
        ReturnUrl = new Uri("https://shop.example/payments/return?ref=ORD-42"),
        NotificationUrl = new Uri("https://shop.example/webhooks/kashier"),
        Customer = new Customer { Email = "mona@example.com" },
        Metadata = new Dictionary<string, string> { ["orderId"] = "42" },
    };

    private static JsonObject SentBody(KashierHarness harness, int index) =>
        JsonNode.Parse(harness.Handler.Requests.ElementAt(index).Body!)!.AsObject();

    private sealed record OtherOptions : ProviderPaymentOptions
    {
        public override PaymentProviderId Provider => PaymentProviderId.Stripe;
    }
}
