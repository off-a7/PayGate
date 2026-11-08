// Grows with the public API: each phase adds calls here so the packages are exercised under
// strict consumer settings, not just referenced.
using PayGate;
using PayGate.Providers;
using PayGate.Testing;
using PayGate.Webhooks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPayGate()
    .AddEventHandler<ConsumerEventHandler>()
    .AddPayGateFake(options => options.Flow = FakeFlow.DisplayReference)
    .ConfigureWebhooks(new PaymentAccount(FakePaymentGateway.ProviderId), options => options.ConfirmWithProvider = true);

var app = builder.Build();

app.MapGet("/", (IPaymentGatewayResolver resolver) =>
{
    var amount = new Money(150m, Currency.Egp);
    bool hasKashier = resolver.TryGetGateway(new PaymentAccount(PaymentProviderId.Kashier), out _);
    return $"PayGate consumer check: {WireFormat.ToDecimalString(amount)}, kashier registered: {hasKashier}";
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
