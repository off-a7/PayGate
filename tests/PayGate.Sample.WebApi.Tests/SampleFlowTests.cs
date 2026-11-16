namespace PayGate.Sample.WebApi.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using PayGate.Sample.WebApi.Data;
using PayGate.Sample.WebApi.Payments;

public sealed class SampleFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_order_is_paid_fulfilled_once_and_refunded_in_two_steps_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync(1000m);

        // Create: the customer presses "Pay" and is sent to the provider.
        var checkout = await app.CheckoutPaidLaterAsync(order.Id);
        checkout.Reference.ShouldBe($"ORD-{order.Id}-R1");
        checkout.NextAction.ShouldBe(NextActionKind.Redirect);
        checkout.RedirectUrl.ShouldNotBeNull();
        string paymentId = (await app.AttemptAsync(order.Id, checkout.Reference)).ProviderPaymentId.ShouldNotBeNull();

        // Webhook: the provider says it is paid. The order is fulfilled once.
        app.Fake.Succeed(paymentId);
        using (var paid = await app.DeliverWebhookAsync(paymentId))
        {
            paid.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var afterPayment = await app.GetOrderAsync(order.Id);
        afterPayment.Status.ShouldBe(OrderStatus.Paid);
        afterPayment.FulfilledAt.ShouldNotBeNull();
        app.Fulfillment.CountFor(order.Id).ShouldBe(1);

        // Duplicate: the same webhook again changes nothing.
        using (var duplicate = await app.DeliverWebhookAsync(paymentId))
        {
            duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        app.Fulfillment.CountFor(order.Id).ShouldBe(1);
        await app.WithDatabaseAsync(async db => (await db.WebhookInbox.CountAsync(Token)).ShouldBe(1));

        // Partial refund, then its webhook: the total is not counted twice.
        using (var partial = await app.RefundAsync(order.Id, 300m))
        {
            partial.StatusCode.ShouldBe(HttpStatusCode.OK);
            var refund = (await partial.Content.ReadFromJsonAsync<RefundResponse>(SampleApp.Json, Token))!;
            refund.RefundReference.ShouldBe($"{checkout.Reference}-F1");
            refund.Refunded.ShouldBe(300m);
            refund.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        }

        (await app.DeliverWebhookAsync(paymentId)).Dispose();
        var afterPartial = await app.AttemptAsync(order.Id, checkout.Reference);
        afterPartial.Refunded.ShouldBe(300m);
        afterPartial.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        (await app.GetOrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);

        // Full refund of what is left.
        using (var full = await app.RefundAsync(order.Id))
        {
            full.StatusCode.ShouldBe(HttpStatusCode.OK);
            var refund = (await full.Content.ReadFromJsonAsync<RefundResponse>(SampleApp.Json, Token))!;
            refund.RefundReference.ShouldBe($"{checkout.Reference}-F2");
            refund.Refunded.ShouldBe(1000m);
        }

        (await app.DeliverWebhookAsync(paymentId)).Dispose();
        var afterFull = await app.GetOrderAsync(order.Id);
        afterFull.Status.ShouldBe(OrderStatus.Refunded);
        afterFull.Attempts.ShouldHaveSingleItem().Status.ShouldBe(PaymentStatus.Refunded);
        afterFull.Attempts[0].Refunded.ShouldBe(1000m);
        app.Fulfillment.CountFor(order.Id).ShouldBe(1);

        using var nothingLeft = await app.RefundAsync(order.Id);
        nothingLeft.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Each_press_of_pay_is_a_new_attempt_with_its_own_reference_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync();

        var first = await app.CheckoutPaidLaterAsync(order.Id);
        string firstId = (await app.AttemptAsync(order.Id, first.Reference)).ProviderPaymentId!;
        app.Fake.Fail(firstId);
        (await app.DeliverWebhookAsync(firstId)).Dispose();

        var second = await app.CheckoutPaidLaterAsync(order.Id);
        string secondId = (await app.AttemptAsync(order.Id, second.Reference)).ProviderPaymentId!;
        app.Fake.Succeed(secondId);
        (await app.DeliverWebhookAsync(secondId)).Dispose();

        second.Reference.ShouldBe($"ORD-{order.Id}-R2");
        var paid = await app.GetOrderAsync(order.Id);
        paid.Status.ShouldBe(OrderStatus.Paid);
        paid.Attempts.Select(a => a.Status).ShouldBe([PaymentStatus.Failed, PaymentStatus.Succeeded]);

        using var third = await app.CheckoutAsync(order.Id);
        third.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_webhook_whose_amount_differs_from_the_attempt_changes_nothing_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync(1000m);
        var checkout = await app.CheckoutPaidLaterAsync(order.Id);
        string paymentId = (await app.AttemptAsync(order.Id, checkout.Reference)).ProviderPaymentId!;

        // As if the attempt had been made for a different total than what the provider charged.
        await app.WithDatabaseAsync(db => db.PaymentAttempts
            .Where(a => a.MerchantReference == checkout.Reference)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.AmountMinor, 50_000), Token));
        app.Fake.Succeed(paymentId);

        using var response = await app.DeliverWebhookAsync(paymentId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var unpaid = await app.GetOrderAsync(order.Id);
        unpaid.Status.ShouldBe(OrderStatus.AwaitingPayment);
        unpaid.Attempts.ShouldHaveSingleItem().LastError.ShouldNotBeNull().ShouldContain("1000.00 EGP");
        app.Fulfillment.CountFor(order.Id).ShouldBe(0);
        await app.WithDatabaseAsync(async db => (await db.WebhookInbox.SingleAsync(Token)).Error.ShouldNotBeNull());
    }

    [Fact]
    public async Task A_failure_while_handling_rolls_back_so_that_the_retry_fulfills_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync();
        var checkout = await app.CheckoutPaidLaterAsync(order.Id);
        string paymentId = (await app.AttemptAsync(order.Id, checkout.Reference)).ProviderPaymentId!;
        app.Fake.Succeed(paymentId);
        app.Fulfillment.FailNext = true;

        using var failed = await app.DeliverWebhookAsync(paymentId);

        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await app.GetOrderAsync(order.Id)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        await app.WithDatabaseAsync(async db => (await db.WebhookInbox.CountAsync(Token)).ShouldBe(0));

        using var retried = await app.DeliverWebhookAsync(paymentId);

        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.GetOrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
        app.Fulfillment.CountFor(order.Id).ShouldBe(1);
    }

    [Fact]
    public async Task The_return_page_asks_the_provider_before_showing_paid_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync();
        var checkout = await app.CheckoutPaidLaterAsync(order.Id);
        string paymentId = (await app.AttemptAsync(order.Id, checkout.Reference)).ProviderPaymentId!;

        var beforePaying = await app.Client.GetFromJsonAsync<ReturnResponse>($"/payments/return?reference={checkout.Reference}&status=SUCCESS", SampleApp.Json, Token);
        beforePaying!.Status.ShouldBe(PaymentStatus.Pending);

        app.Fake.Succeed(paymentId);
        var afterPaying = await app.Client.GetFromJsonAsync<ReturnResponse>($"/payments/return?reference={checkout.Reference}", SampleApp.Json, Token);

        afterPaying!.Status.ShouldBe(PaymentStatus.Succeeded);
        afterPaying.OrderStatus.ShouldBe(OrderStatus.Paid);
        app.Fulfillment.CountFor(order.Id).ShouldBe(1);

        // The webhook arriving afterwards is handled, but fulfills nothing again.
        using var late = await app.DeliverWebhookAsync(paymentId);
        late.StatusCode.ShouldBe(HttpStatusCode.OK);
        app.Fulfillment.CountFor(order.Id).ShouldBe(1);
    }

    [Fact]
    public async Task The_return_page_sends_the_customer_on_only_to_allowed_addresses_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync();

        using var refused = await app.CheckoutAsync(order.Id, next: "https://evil.example.com/");
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using var accepted = await app.CheckoutAsync(order.Id, next: "https://shop.example.com/account/orders");
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        string reference = (await accepted.Content.ReadFromJsonAsync<CheckoutResponse>(SampleApp.Json, Token))!.Reference;

        using var local = await app.Client.GetAsync(new Uri($"/payments/return?reference={reference}&next=/orders/{order.Id}", UriKind.Relative), Token);
        local.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        local.Headers.Location!.OriginalString.ShouldBe($"/orders/{order.Id}");

        using var edited = await app.Client.GetAsync(new Uri($"/payments/return?reference={reference}&next=https://evil.example.com/", UriKind.Relative), Token);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void The_webhook_url_given_to_the_provider_names_the_provider_and_account()
    {
        var options = new SamplePaymentOptions { PublicBaseUrl = new Uri("https://shop.example.com/") };
        OrderEndpoints.WebhookUrl(options, new PaymentAccount(FakeProvider)).ShouldBe(new Uri("https://shop.example.com/webhooks/fake"));
        OrderEndpoints.WebhookUrl(options, new PaymentAccount(FakeProvider, "second")).ShouldBe(new Uri("https://shop.example.com/webhooks/fake/second"));
    }

    [Fact]
    public async Task Reconciliation_finds_payments_whose_webhook_never_came_Async()
    {
        await using var app = new SampleApp();
        var paidOrder = await app.CreateOrderAsync();
        var paid = await app.CheckoutPaidLaterAsync(paidOrder.Id);
        var expiredOrder = await app.CreateOrderAsync();
        var expired = await app.CheckoutPaidLaterAsync(expiredOrder.Id);
        app.Fake.Succeed((await app.AttemptAsync(paidOrder.Id, paid.Reference)).ProviderPaymentId!);
        app.Fake.Expire((await app.AttemptAsync(expiredOrder.Id, expired.Reference)).ProviderPaymentId!);

        int changed = await app.ReconcileAsync();

        changed.ShouldBe(2);
        (await app.GetOrderAsync(paidOrder.Id)).Status.ShouldBe(OrderStatus.Paid);
        app.Fulfillment.CountFor(paidOrder.Id).ShouldBe(1);
        (await app.AttemptAsync(expiredOrder.Id, expired.Reference)).Status.ShouldBe(PaymentStatus.Expired);
        (await app.GetOrderAsync(expiredOrder.Id)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await app.ReconcileAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_checkout_that_timed_out_is_found_later_by_its_reference_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync();
        app.Fake.TimeOutNextWrite();

        using var timedOut = await app.CheckoutAsync(order.Id);

        timedOut.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var unknown = (await app.GetOrderAsync(order.Id)).Attempts.ShouldHaveSingleItem();
        unknown.ProviderPaymentId.ShouldBeNull();

        (await app.ReconcileAsync()).ShouldBe(1);
        string paymentId = (await app.AttemptAsync(order.Id, unknown.Reference)).ProviderPaymentId.ShouldNotBeNull();

        app.Fake.Succeed(paymentId);
        await app.ReconcileAsync();
        (await app.GetOrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    public async Task The_dev_endpoints_play_the_customer_on_the_fake_Async()
    {
        await using var app = new SampleApp();
        var order = await app.CreateOrderAsync();
        var checkout = await app.CheckoutPaidLaterAsync(order.Id);
        string paymentId = (await app.AttemptAsync(order.Id, checkout.Reference)).ProviderPaymentId!;

        using var succeeded = await app.Client.PostAsync(new Uri($"/dev/fake/payments/{paymentId}/succeed", UriKind.Relative), null, Token);
        using var again = await app.Client.PostAsync(new Uri($"/dev/fake/payments/{paymentId}/webhook", UriKind.Relative), null, Token);
        using var invalid = await app.Client.PostAsync(new Uri($"/dev/fake/payments/{paymentId}/fail", UriKind.Relative), null, Token);

        (await succeeded.Content.ReadAsStringAsync(Token)).ShouldContain("\"Processed\"");
        (await again.Content.ReadAsStringAsync(Token)).ShouldContain("\"AlreadyProcessed\"");
        invalid.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await app.GetOrderAsync(order.Id)).Status.ShouldBe(OrderStatus.Paid);
    }

    private static PaymentProviderId FakeProvider => PayGate.Testing.FakePaymentGateway.ProviderId;
}
