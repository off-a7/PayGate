# PayGate.Kashier

Kashier provider for PayGate: hosted payment sessions, status lookup, refunds and voids, webhook and redirect verification, and Kashier payouts.

> **Preview.** So far this package registers and validates Kashier accounts. Payments, webhooks and payouts are being added in phases; see the [PayGate repository](https://github.com/off-a7/PayGate) and the [Kashier notes](https://github.com/off-a7/PayGate/blob/main/docs/providers/kashier.md).

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

Requires .NET 10 (`net10.0`).
