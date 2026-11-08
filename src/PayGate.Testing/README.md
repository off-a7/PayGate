# PayGate.Testing

Test helpers for applications using PayGate: a fake payment gateway and signed webhook builders. Independent of any test framework.

> **Preview.** The API may change before 1.0. See the [PayGate repository](https://github.com/off-a7/PayGate).

```csharp
services.AddPayGate()
    .AddPayGateFake(options => options.Flow = FakeFlow.Redirect)
    .AddEventHandler<MyEventHandler>();

// In a test: resolve the fake, drive a payment, and deliver its webhook.
var fake = provider.GetRequiredService<FakePaymentGateway>();
var session = await fake.CreatePaymentAsync(request);
fake.Succeed(session.Value.ProviderPaymentId);

var webhook = new FakeWebhookBuilder(fake).ForPayment(session.Value.ProviderPaymentId);
var result = await provider.GetRequiredService<IWebhookProcessor>().ProcessAsync(webhook);
```

- **Flows:** `Redirect`, `ClientSdk` and `DisplayReference`.
- **Outcomes:** `Succeed`, `Fail`, `Expire`, `ReportUnknownStatus`, partial refunds through `RefundAsync`, and `TimeOutNextWrite` for `OutcomeUnknown`.
- **Production:** the app fails to start in the Production environment unless `AllowInProduction` is set.

Requires .NET 10 (`net10.0`).
