// Grows with the public API: each phase adds calls here so the packages are exercised under
// strict consumer settings, not just referenced.
using PayGate;
using PayGate.AspNetCore;
using PayGate.Kashier;
using PayGate.Providers;
using PayGate.Testing;
using PayGate.Webhooks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddPayGate()
    .AddEventHandler<ConsumerEventHandler>()
    .AddWebhookEndpoints(options => options.MaxBodyBytes = 128 * 1024)
    .AddPayGateFake(options => options.Flow = FakeFlow.DisplayReference)
    .AddKashier(options => builder.Configuration.GetSection("Payments:Kashier").Bind(options))
    .ConfigureWebhooks(new PaymentAccount(FakePaymentGateway.ProviderId), options => options.ConfirmWithProvider = true);

var app = builder.Build();
app.MapPayGateWebhooks().AllowAnonymous();
app.MapControllers();

app.MapGet("/", (IPaymentGatewayResolver resolver) =>
{
    var amount = new Money(150m, Currency.Egp);
    bool hasKashier = resolver.TryGetGateway(new PaymentAccount(PaymentProviderId.Kashier), out _);
    return $"PayGate consumer check: {WireFormat.ToDecimalString(amount)}, kashier registered: {hasKashier}";
});

app.MapPost("/kashier", async (IPaymentGatewayResolver resolver, CancellationToken cancellationToken) =>
{
    var kashier = resolver.GetGateway(new PaymentAccount(PaymentProviderId.Kashier));
    var created = await kashier.CreatePaymentAsync(
        new CreatePaymentRequest
        {
            MerchantReference = "consumer-check-kashier",
            Amount = new Money(150m, Currency.Egp),
            ReturnUrl = new Uri("https://shop.example/payments/return"),
            ProviderOptions = new KashierPaymentOptions { Display = "ar", MaxFailureAttempts = 1 },
        },
        cancellationToken);
    if (created.IsFailure)
    {
        return Results.Problem(created.Error.Message);
    }

    var details = await kashier.GetPaymentAsync(PaymentLookup.ByProviderPaymentId(created.Value.ProviderPaymentId), cancellationToken);
    string? orderId = details.IsSuccess ? details.Value.GetProviderData<KashierPaymentData>()?.OrderId : null;
    bool canCancel = kashier.TryGetFeature<IPaymentCancelFeature>(out _);
    return Results.Ok(new { orderId, canCancel });
});

app.MapGet("/kashier/return", async (HttpRequest request, IPayGateReturnCallbacks returns, CancellationToken cancellationToken) =>
{
    var claim = await returns.ParseAsync(request, new PaymentAccount(PaymentProviderId.Kashier), cancellationToken);
    return Results.Ok(new { claim.Verification, claim.MerchantReference, claim.IndicatedStatus });
});

app.MapPost("/fake", async (FakePaymentGateway fake, IWebhookProcessor processor, CancellationToken cancellationToken) =>
{
    var request = new CreatePaymentRequest { MerchantReference = "consumer-check", Amount = new Money(150m, Currency.Egp) };
    var session = await fake.CreatePaymentAsync(request, cancellationToken);
    fake.Succeed(session.Value.ProviderPaymentId);

    var webhook = new FakeWebhookBuilder(fake).ForPayment(session.Value.ProviderPaymentId);
    var result = await processor.ProcessAsync(webhook, cancellationToken);
    return Results.StatusCode(result.Ack.StatusCode);
});

await app.RunAsync();

internal sealed class ConsumerEventHandler : IPaymentEventHandler
{
    public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(WebhookHandlingOutcome.Processed);
}
