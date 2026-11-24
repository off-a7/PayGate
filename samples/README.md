# PayGate samples

## PayGate.Sample.WebApi

A small shop API that takes payments through PayGate. It runs on the **fake gateway** by default, and on the Kashier sandbox by changing configuration. It shows the part every app has to write itself: its own tables, deduping webhooks, checking amounts, fulfilling once, refunds and reconciliation.

### Run it

```powershell
dotnet run --project samples/PayGate.Sample.WebApi
# listens on http://localhost:5080 and creates paygate-sample.db next to it
```

Then play a whole payment with curl:

```bash
B=http://localhost:5080

# 1. An order, then "Pay". The answer says where to send the customer.
curl -X POST $B/orders -H 'content-type: application/json' -d '{"description":"Manga box","amount":250.50}'
curl -X POST $B/orders/1/checkout -H 'content-type: application/json' -d '{"next":"/orders/1"}'
curl $B/orders/1                                   # attempt ORD-1-R1, note its providerPaymentId

# 2. Play the customer and the provider (Development only, fake only).
curl -X POST $B/dev/fake/payments/fake_pay_1/succeed   # pays it and delivers the signed webhook
curl -X POST $B/dev/fake/payments/fake_pay_1/webhook   # the same webhook again → AlreadyProcessed

# 3. The customer comes back; the page asks the provider, then redirects to next.
curl -i "$B/payments/return?reference=ORD-1-R1&next=/orders/1"

# 4. Refund part, then the rest.
curl -X POST $B/orders/1/refunds -H 'content-type: application/json' -d '{"amount":50}'
curl -X POST $B/orders/1/refunds -H 'content-type: application/json' -d '{}'
```

The dev endpoints also offer `/fail` and `/expire`. Delete `paygate-sample.db` to start over.

### Run it on the Kashier sandbox

The same app takes real sandbox payments when `Payments:Account` is `kashier`. Kashier refuses `localhost`, so the app needs a public HTTPS address, such as an ngrok or dev tunnel to port 5080.

```powershell
cd samples/PayGate.Sample.WebApi
dotnet user-secrets set "Payments:Kashier:MerchantId" "MID-..."
dotnet user-secrets set "Payments:Kashier:SecretKey" "..."
dotnet user-secrets set "Payments:Kashier:PaymentApiKey" "..."
dotnet user-secrets set "Payments:Account" "kashier"
dotnet user-secrets set "Payments:PublicBaseUrl" "https://<your-tunnel>/"
dotnet run
```

Checkout then answers with Kashier's payment page. Kashier sends the webhook to `/webhooks/kashier` and the customer back to `/payments/return`. The `/dev/fake` endpoints are not mapped, and refunds go to Kashier.

### Endpoints

| Endpoint | What it does |
|---|---|
| `POST /orders` | Creates an order awaiting payment |
| `GET /orders/{id}` | The order with every payment attempt |
| `POST /orders/{id}/checkout` | A **new attempt** per press (`ORD-{id}-R1`, `-R2`, ...), and the next action: redirect URL, payment code or client secret |
| `GET /payments/return?reference=&next=` | Asks the provider, applies the answer, and redirects to `next` only when it is safe |
| `POST /orders/{id}/refunds` | Partial (`amount`) or full (empty body). Each refund gets its own reference (`-F1`, `-F2`) |
| `POST /webhooks/fake` | PayGate's webhook endpoint (`MapPayGateWebhooks`) |
| `POST /dev/fake/payments/{id}/succeed` · `fail` · `expire` · `webhook` | Development only: changes the fake payment and delivers its webhook |

### App code and library code

| Part | Who owns it | Where |
|---|---|---|
| Signature check, parsing, ack codes, body limit, logs | **PayGate** | `PayGate`, `PayGate.AspNetCore` |
| Calls to the provider (`CreatePaymentAsync`, `GetPaymentAsync`, `RefundAsync`) | **PayGate** | provider package (the fake here) |
| Tables: `Orders`, `PaymentAttempts`, `WebhookInbox` | **the app** | [`Data/`](PayGate.Sample.WebApi/Data/) |
| Dedupe on `(provider, EventId)` with a unique index | **the app** | [`PaymentWebhookHandler`](PayGate.Sample.WebApi/Payments/PaymentWebhookHandler.cs) |
| Amount check, status moves (`CanTransitionTo`), refund totals, fulfill once | **the app** | [`PaymentUpdater`](PayGate.Sample.WebApi/Payments/PaymentUpdater.cs) |
| Checkout, return page, refunds | **the app** | [`OrderEndpoints`](PayGate.Sample.WebApi/Payments/OrderEndpoints.cs) |
| Reconciliation of lost webhooks | **the app** | [`PaymentReconciler`](PayGate.Sample.WebApi/Payments/PaymentReconciler.cs) |
| Which provider to use | **configuration** | `Payments:Account` in `appsettings.json` |

Every status the app learns (webhook, return page, refund, reconciliation) goes through `PaymentUpdater`, so they cannot disagree. The full contract is in [docs/webhooks.md](../docs/webhooks.md).

### Settings

| Setting | Default | Meaning |
|---|---|---|
| `Payments:Account` | `fake` | The PayGate account checkouts use: `fake` or `kashier` |
| `Payments:PublicBaseUrl` | `http://localhost:5080/` | Base of the return and webhook URLs. Use your tunnel's address to receive real webhooks |
| `Payments:ConfirmWithProvider` | `true` | Ask the provider about each webhook's payment before handling it |
| `Payments:AllowedReturnUrls` | empty | Absolute addresses `next` may point to, besides local paths |
| `Payments:Reconciliation:*` | every 5 min, older than 15 min, up to 2 days | The reconciliation job |
| `Payments:Fake:Flow` | `Redirect` | `Redirect`, `ClientSdk` or `DisplayReference` |

The fake refuses to start in the Production environment.

### What is left out on purpose

- **Migrations.** The app calls `EnsureCreated` on start. A real app uses EF Core migrations.
- **Concurrency tokens.** Two refunds pressed at the same moment can both reach the provider. A real app adds a row version or takes an idempotency key from the client.
- **Authentication.** The shop endpoints are open. Only the webhook endpoint is meant to stay anonymous.

### Tests

[`tests/PayGate.Sample.WebApi.Tests`](../tests/PayGate.Sample.WebApi.Tests/) runs the app in memory and drives it over HTTP: create → webhook → fulfilled once → duplicate ignored → partial refund → full refund, plus amount mismatch, rollback on failure, the return page, reconciliation and a checkout that timed out.
