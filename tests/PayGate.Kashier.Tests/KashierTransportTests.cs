namespace PayGate.Kashier.Tests;

using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PayGate.Kashier.Transport;

public sealed class KashierTransportTests
{
    [Fact]
    public async Task A_session_is_created_on_the_sandbox_api_host_with_both_keys_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("create-session");

        await harness.CreateSessionAsync();

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ShouldBe(new Uri("https://test-api.kashier.io/v3/payment/sessions"));
        request.Headers["Authorization"].ShouldBe("secret-key-value");
        request.Headers["api-key"].ShouldBe("payment-api-key-value");
        request.Headers["Accept"].ShouldBe("application/json");
    }

    [Fact]
    public async Task Every_call_is_pinned_to_http_1_1_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("create-session");

        await harness.CreateSessionAsync();

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Version.ShouldBe(HttpVersion.Version11);
        request.VersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionExact);
    }

    [Fact]
    public async Task Order_operations_go_to_the_fep_host_with_the_secret_key_only_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("refund-partial");

        await harness.OperateAsync(new KashierOrderOperationRequest { ApiOperation = "REFUND" });

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Put);
        request.Uri.ShouldBe(new Uri("https://test-fep.kashier.io/v3/orders/52e8cf0f-05d0-49f8-be23-fd51153b2bb6"));
        request.Headers["Authorization"].ShouldBe("secret-key-value");
        request.Headers.ContainsKey("api-key").ShouldBeFalse();
    }

    [Fact]
    public async Task A_live_account_calls_the_live_hosts_Async()
    {
        using var harness = new KashierHarness(o => o.Environment = PaymentEnvironment.Live);
        harness.Handler.Respond(200, Fixtures.ApiResponse("search-orders-retried-session")).RespondWithFixture("void");

        await harness.SearchAsync("ORD-1");
        await harness.OperateAsync(new KashierOrderOperationRequest { ApiOperation = "VOID" });

        harness.Handler.Requests.Select(r => r.Uri.Host).ShouldBe(["api.kashier.io", "fep.kashier.io"]);
    }

    [Fact]
    public async Task The_base_url_override_sends_every_call_to_a_mock_server_Async()
    {
        using var harness = new KashierHarness(o => o.BaseUrlOverride = new Uri("http://localhost:9091/kashier"));
        harness.Handler.RespondWithFixture("create-session").RespondWithFixture("void");

        await harness.CreateSessionAsync();
        await harness.OperateAsync(new KashierOrderOperationRequest { ApiOperation = "VOID" });

        harness.Handler.Requests.Select(r => r.Uri.ToString()).ShouldBe([
            "http://localhost:9091/kashier/v3/payment/sessions",
            "http://localhost:9091/kashier/v3/orders/52e8cf0f-05d0-49f8-be23-fd51153b2bb6",
        ]);
    }

    [Fact]
    public async Task The_session_body_uses_kashiers_field_names_and_leaves_out_unset_fields_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("create-session");

        await harness.CreateSessionAsync();

        var body = JsonNode.Parse(harness.Handler.Requests.ShouldHaveSingleItem().Body!)!.AsObject();
        body.Select(p => p.Key).Order(StringComparer.Ordinal).ShouldBe([
            "amount", "currency", "customer", "display", "enable3DS", "maxFailureAttempts", "merchantId", "merchantRedirect",
            "metaData", "order", "type",
        ]);
        body["amount"]!.GetValue<string>().ShouldBe("113.34");
        body["type"]!.GetValue<string>().ShouldBe("one-time");
        body["metaData"]!["orderId"]!.GetValue<string>().ShouldBe("42");
        body["customer"]!.AsObject().Select(p => p.Key).ShouldBe(["reference"]);
    }

    [Fact]
    public async Task A_recorded_session_response_is_read_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("create-session");

        var result = await harness.CreateSessionAsync();

        var session = result.Value.Body.ShouldNotBeNull();
        session.Id.ShouldBe("6b035e0df1523df5868a61eb");
        session.Status.ShouldBe("CREATED");
        session.SessionUrl.ShouldBe("https://payments.kashier.io/session/6b035e0df1523df5868a61eb?mode=test");
    }

    [Fact]
    public async Task A_recorded_inquiry_with_two_orders_for_one_reference_is_read_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("search-orders-retried-session");

        var result = await harness.SearchAsync("PG-20261122212816-7711");

        var orders = result.Value.Body.ShouldNotBeNull().Data;
        orders.Select(o => (o.OrderId, o.Status)).ShouldBe([
            ("f64e3069-7e07-4d11-8bca-6a877884b9b4", "CAPTURED"),
            ("d69bb904-be33-460b-b476-d37414b75af8", "FAILED"),
        ]);
        orders.ShouldAllBe(o => o.MerchantOrderId == "PG-20261122212816-7711");

        var captured = orders[0];
        captured.TotalCapturedAmount.ShouldBe(75.5m);
        captured.Order!.Amount.ShouldBe(75.5m);
        captured.Order.Currency.ShouldBe("EGP");
        captured.SourceOfFunds!.CardInfo!.CardBrand.ShouldBe("Mastercard");
        captured.Transactions.Select(t => (t.Operation, t.Status)).ShouldBe([("3dsecure_verify", "SUCCESS"), ("pay", "SUCCESS")]);
        orders[1].Transactions[^1].TransactionResponseMessage!.En.ShouldBe("UNSPECIFIED FAILURE");
    }

    [Fact]
    public async Task A_recorded_refund_response_carries_the_new_totals_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("refund-partial");

        var result = await harness.OperateAsync(new KashierOrderOperationRequest
        {
            ApiOperation = "REFUND",
            Transaction = new KashierOperationTransaction { Amount = 20m },
        });

        var refund = result.Value.Body.ShouldNotBeNull();
        refund.Status.ShouldBe("SUCCESS");
        refund.Response!.Status.ShouldBe("PARTIALLY_REFUNDED");
        refund.Response.Amount.ShouldBe(20m);
        refund.Response.TotalRefundedAmount.ShouldBe(20m);
        refund.Response.TotalCapturedAmount.ShouldBe(113.34m);
        refund.Response.TransactionId.ShouldBe("TX-00000000111");
        JsonNode.Parse(harness.Handler.Requests.Single().Body!)!.ToJsonString()
            .ShouldBe("""{"apiOperation":"REFUND","transaction":{"amount":20}}""");
    }

    [Fact]
    public async Task Amounts_sent_as_strings_are_read_too_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.Respond(200, """{"status":"SUCCESS","data":[{"orderId":"o-1","totalCapturedAmount":"113.34","order":{"amount":"113.34"}}]}""");

        var result = await harness.SearchAsync("ORD-1");

        var order = result.Value.Body!.Data.ShouldHaveSingleItem();
        order.TotalCapturedAmount.ShouldBe(113.34m);
        order.Order!.Amount.ShouldBe(113.34m);
    }

    [Fact]
    public async Task An_over_refund_comes_back_as_kashiers_401_body_not_as_an_error_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("refund-over-refund-401");

        var result = await harness.OperateAsync(new KashierOrderOperationRequest { ApiOperation = "REFUND" });

        var response = result.Value;
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.IsSuccess.ShouldBeFalse();
        response.Error.ShouldBe(new KashierError("Unauthorized", null, "Authorization error", null));
    }

    [Fact]
    public async Task A_rejected_session_keeps_kashiers_message_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.RespondWithFixture("create-session-localhost-redirect-400");

        var result = await harness.CreateSessionAsync();

        result.Value.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        result.Value.Error!.Message.ShouldBe("\"merchantRedirect\" must be a valid URL");
    }

    [Theory]
    [InlineData("""{"error":{"cause":"Invalid Request"},"messages":{"en":"The order has already been paid before","ar":"لقد تم دفع الطلب بالفعل"}}""", "The order has already been paid before", "لقد تم دفع الطلب بالفعل", "Invalid Request", null)]
    [InlineData("""{"message":"Duplicate order","code":"ERR_ORD_02"}""", "Duplicate order", null, null, "ERR_ORD_02")]
    [InlineData("""{"status":"FAILURE","response":{"errorCode":"ERR_ORD_02"}}""", null, null, null, "ERR_ORD_02")]
    [InlineData("not json ERR_ORD_02 at all", null, null, null, "ERR_ORD_02")]
    [InlineData("", null, null, null, null)]
    public void Each_kashier_error_shape_is_read(string body, string? message, string? messageAr, string? cause, string? code)
    {
        KashierError.Parse(body).ShouldBe(new KashierError(message, messageAr, cause, code));
    }

    [Fact]
    public async Task A_write_that_times_out_is_sent_once_and_reported_as_outcome_unknown_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.Hang();

        var result = await harness.OperateAsync(new KashierOrderOperationRequest { ApiOperation = "REFUND" });

        harness.Handler.Requests.Count.ShouldBe(1);
        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
    }

    [Fact]
    public async Task An_unreadable_success_on_a_write_is_outcome_unknown_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.Respond(200, "<html>gateway</html>");

        var result = await harness.OperateAsync(new KashierOrderOperationRequest { ApiOperation = "VOID" });

        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
        result.Error.Message.ShouldNotContain("gateway");
    }

    [Fact]
    public async Task An_unreadable_success_on_a_read_is_a_provider_error_Async()
    {
        using var harness = new KashierHarness();
        harness.Handler.Respond(200, "{\"data\":");

        var result = await harness.SearchAsync("ORD-1");

        result.Error!.Code.ShouldBe(PaymentErrorCode.ProviderError);
        result.Error.ProviderCode.ShouldBe("200");
    }

    [Fact]
    public async Task Unusable_credentials_fail_before_anything_is_sent_Async()
    {
        var credentials = new StaticCredentials(new KashierAccountOptions { MerchantId = "MID-1234-567" });
        using var harness = new KashierHarness(services: s => s.AddSingleton<IPaymentCredentialsProvider<KashierAccountOptions>>(credentials));

        var result = await harness.CreateSessionAsync();

        harness.Handler.Requests.ShouldBeEmpty();
        result.Error!.Code.ShouldBe(PaymentErrorCode.Configuration);
        result.Error.Message.ShouldContain("SecretKey is required.");
    }

    [Fact]
    public async Task Credentials_are_read_per_call_so_a_tenant_store_can_supply_them_Async()
    {
        var credentials = new StaticCredentials(new KashierAccountOptions
        {
            MerchantId = "MID-9999-999",
            SecretKey = "tenant-secret",
            PaymentApiKey = "tenant-api-key",
        });
        using var harness = new KashierHarness(services: s => s.AddSingleton<IPaymentCredentialsProvider<KashierAccountOptions>>(credentials));
        harness.Handler.RespondWithFixture("create-session");

        await harness.CreateSessionAsync();

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Headers["Authorization"].ShouldBe("tenant-secret");
        request.Headers["api-key"].ShouldBe("tenant-api-key");
    }

    private sealed class StaticCredentials(KashierAccountOptions options) : IPaymentCredentialsProvider<KashierAccountOptions>
    {
        public ValueTask<KashierAccountOptions> GetCredentialsAsync(PaymentAccount account, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(options);
    }
}
