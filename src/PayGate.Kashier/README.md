# PayGate.Kashier

Kashier provider for PayGate: hosted payment sessions, status lookup, refunds and voids, webhook and redirect verification, and Kashier payouts.

> **Preview.** Payments, lookups, refunds and voids work. Webhook and redirect verification, and payouts, are being added; see the [PayGate repository](https://github.com/off-a7/PayGate) and the [Kashier notes](https://github.com/off-a7/PayGate/blob/main/docs/providers/kashier.md).

```csharp
builder.Services.AddPayGate()
    .AddKashier(o => builder.Configuration.GetSection("Payments:Kashier").Bind(o));
```

```json
{
  "Payments": {
    "Kashier": {
      "Environment": "Sandbox",
      "MerchantId": "MID-1234-567"
    }
  }
}
```

Keep `SecretKey` and `PaymentApiKey` (and `TransferApiKey`, for payouts) out of `appsettings.json`: use user secrets, environment variables or a key vault. The app refuses to start when a key is missing, or when a Kashier host doesn't match `Environment`.

```csharp
var kashier = resolver.GetGateway(new PaymentAccount(PaymentProviderId.Kashier));
var session = await kashier.CreatePaymentAsync(new CreatePaymentRequest
{
    MerchantReference = attempt.Reference,          // a new one for every payment attempt
    Amount = new Money(250m, Currency.Egp),
    ReturnUrl = new Uri("https://shop.example/payments/return"),
    NotificationUrl = new Uri("https://shop.example/webhooks/kashier"),
});
// Redirect the customer to ((RedirectAction)session.Value.NextAction).Url and keep session.Value.ProviderPaymentId.
```

Kashier refuses `localhost` return URLs, so use a tunnel while developing.

Requires .NET 10 (`net10.0`).
