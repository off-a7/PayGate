# Receiving webhooks

A webhook is the provider telling your app that a payment changed: it succeeded, failed, expired or was refunded. PayGate receives it, checks the signature against the exact bytes, parses it into `PaymentEvent`s and hands each one to your `IPaymentEventHandler`. **Your app stores the events and decides what they mean.** PayGate keeps no state.

```
provider ──POST /webhooks/{provider}/{account?}──► PayGate.AspNetCore ──► IWebhookProcessor ──► your IPaymentEventHandler
                                                   reads the raw body      verifies, parses,      dedupes, checks the amount,
                                                   (413 over the limit)    confirms (optional)    moves the status, fulfills
          ◄──────────── 200 / 401 / 400 / 404 / 500 from the provider's ack policy ─────────────────┘
```

The [sample app](../samples/README.md) does every step below with real tables and tests.

## 1. Register and map the endpoint

```csharp
builder.Services.AddPayGate()
    .AddPayGateFake()                              // a provider package; Kashier's comes in 0.3.0-preview
    .AddEventHandler<PaymentWebhookHandler>()      // your code, resolved in a new scope per event
    .AddWebhookEndpoints(options =>
    {
        options.MaxBodyBytes = 128 * 1024;         // 256 KB by default; over it → 413, before verifying
        options.MaxBodyBytesByProvider["kashier"] = 64 * 1024;
    })
    .ConfigureWebhooks(new PaymentAccount(PaymentProviderId.Kashier),
        options => options.ConfirmWithProvider = true);   // ask the provider before dispatching

var app = builder.Build();
app.MapPayGateWebhooks()                           // POST and GET /webhooks/{provider}/{account?}
   .AllowAnonymous();                              // the signature authenticates a webhook, not a login
```

- **Routes.** `/webhooks/kashier` is the `default` account. `/webhooks/kashier/secondary` is the account named `secondary`, verified with that account's secret. Change the prefix with `MapPayGateWebhooks("/payments/hooks")`.
- **The returned group.** `MapPayGateWebhooks` returns a `RouteGroupBuilder`, so you can add `AllowAnonymous()`, rate limiting, tags or a host filter. If your app has a global authorization policy, `AllowAnonymous()` is required, or every webhook gets 401 from your own middleware.
- **`ConfirmWithProvider`.** When on, PayGate looks the payment up with `GetPaymentAsync` before your handler runs, and passes on the provider's current status and amount. It costs one call per event and protects you from acting on a stale message.

### From an MVC controller

Apps that use controllers only inject `IPayGateWebhookEndpoint`. It behaves exactly like the mapped endpoint.

```csharp
[ApiController]
[Route("payments/webhooks/{provider}/{account?}")]
public sealed class PaymentWebhooksController(IPayGateWebhookEndpoint webhooks) : ControllerBase
{
    [HttpPost]
    public Task<IResult> ReceiveAsync() => webhooks.HandleAsync(HttpContext, HttpContext.RequestAborted);
}
```

- **Never bind the body** with `[FromBody]` or read it in a filter or middleware first. The signature covers the exact bytes. Re-serialized JSON, with different whitespace or key order, fails verification.
- The route values must be named `provider` and `account`. A route without them can call `HandleAsync(HttpContext, PaymentAccount, CancellationToken)` with the account it knows.

## 2. Give the provider a public URL

The provider calls your app from the internet, so the webhook URL must be:

- **public**: `localhost` and private addresses are unreachable from the provider;
- **HTTPS** with a valid certificate: most providers refuse plain HTTP or self-signed certificates;
- **stable**: providers retry for a while, then give up. A URL that changes every run loses webhooks.

Set it in the provider's dashboard, or per payment through `CreatePaymentRequest.NotificationUrl` when `gateway.Capabilities.PerRequestNotificationUrl` is true.

### Testing on your machine with a tunnel

A tunnel gives your local app a public HTTPS address. With [dev tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/):

```powershell
devtunnel user login
devtunnel host -p 5080 --allow-anonymous
# → https://abc123-5080.euw.devtunnels.ms  → use https://abc123-5080.euw.devtunnels.ms/webhooks/kashier
```

With ngrok: `ngrok http 5080`. Then set the tunnel address as the webhook URL, and as the base of your return URL.

Keep the tunnel open while you test. When it closes, the provider's retries fail, and only reconciliation (section 5) catches those payments.

## 3. The dedupe contract

**Providers deliver the same webhook more than once.** They retry on timeouts, on 5xx and sometimes for no visible reason. Your handler will see the same event again, possibly at the same moment on two servers.

PayGate gives every event a stable `PaymentEvent.EventId`: the same change delivered twice has the same id. Your app **must**:

1. **Store `EventId` in a table with a unique index** on `(provider, EventId)`. The index is the guarantee: a check in code alone races.
2. **Return `AlreadyProcessed`** when the id is already there, without changing anything.
3. **Insert the inbox row in the same transaction** as the state change. If the change fails, the row is rolled back too, and the retry is handled normally.

```csharp
public async ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent e, CancellationToken ct)
{
    string provider = e.Account.Provider.Value;
    if (await db.WebhookInbox.AnyAsync(x => x.Provider == provider && x.EventId == e.EventId, ct))
    {
        return WebhookHandlingOutcome.AlreadyProcessed;       // replay: change nothing
    }

    await using var tx = await db.Database.BeginTransactionAsync(ct);
    db.WebhookInbox.Add(new WebhookInboxEntry { Provider = provider, EventId = e.EventId, ... });
    // ... check the amount, move the status, fulfill (below)
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (IsUniqueViolation(ex))
    {
        return WebhookHandlingOutcome.AlreadyProcessed;       // a parallel delivery won
    }

    await tx.CommitAsync(ct);
    return WebhookHandlingOutcome.Processed;
}
```

Some providers want a particular answer for a duplicate (Kashier, for example, accepts 409). The provider package's ack policy turns `AlreadyProcessed` into that answer. You only return the outcome.

## 4. What the handler should do with an event

1. **Find your attempt** by `MerchantReference`, or by `ProviderPaymentId` when the provider does not echo the reference. Look only among attempts made on `e.Account`.
2. **Check the amount and currency** against what you asked for. A payment for less than the order is not a payment for the order.
3. **Use `Status`, not `Type`.** `Type` says what kind of message it was. `Status` is the payment's state at that moment, and a late "succeeded" message can arrive after a refund. Move your state only when `current.CanTransitionTo(e.Status)` is true: terminal states stick, so an old "pending" cannot undo a payment.
4. **Use totals, not deltas.** For refunds, keep `RefundedTotal` (only ever increase yours). Adding `RefundAmount` on every delivery counts a refund twice.
5. **Fulfill idempotently**, once per order: guard it with a "fulfilled at" column, and make the fulfillment itself safe to run twice. A crash after fulfilling but before committing means the provider retries and the handler runs again.

What to return:

| Return | When | Provider gets |
|---|---|---|
| `Processed` | You applied it | 200 |
| `AlreadyProcessed` | The event id was already in the inbox | 200, or the provider's duplicate code |
| `Ignored` | Not yours, or it contradicts the attempt (record why) | 200, so it is not retried forever |
| throw | Your database or a dependency failed | 500, so the provider retries |

A response body never contains the exception. Details go to your logs only.

## 5. Webhooks get lost: reconcile

A webhook can be lost for good: the app was down past the provider's retries, the tunnel closed, or the URL was wrong. Run a background job that asks the provider about attempts still open after a while:

- pick attempts in `Pending`, `RequiresAction`, `Processing`, `Authorized` or `Unknown` older than, say, 15 minutes;
- look each one up with `GetPaymentAsync(PaymentLookup.ByProviderPaymentId(...))`, or `ByMerchantReference(...)` when creating it timed out (`OutcomeUnknown`) and you never got a provider id (check `Capabilities.LookupByMerchantReference`);
- apply the result through **the same code** the webhook handler uses.

Do not mark an attempt failed just because it is old: a late payment must still be able to arrive.

## 6. The return page is not proof of payment

When the customer comes back from the payment page, the query string comes from their browser and can be edited. Use it to find the attempt, then ask the provider:

```csharp
var claim = await returns.ParseAsync(request, account, ct);          // untrusted, even when signed
var details = await gateway.GetPaymentAsync(PaymentLookup.ByProviderPaymentId(id), ct);
// apply details through the same code as the webhook, then show the result
```

If you send the customer on to a `next` address, check it with `IPayGateReturnCallbacks.TryGetSafeReturnUrl`. It allows local paths such as `/orders/42`, and absolute URLs only under `AllowedReturnUrls`, which stops open redirects.

## 7. Logs and responses

Each webhook gets one log line, never the payload or a signature:

| Event id | Level | When |
|---|---|---|
| 2000 `WebhookReceived` | Information | Processed, AlreadyProcessed or Ignored |
| 2001 `WebhookNotAccepted` | Warning | Invalid signature, malformed, unknown account, too large, handler failed |
| 2002 `WebhookRouteRejected` | Warning | The provider or account segment is not a valid id |

The fields are the account, outcome, status code, event ids and duration.

| Situation | Status |
|---|---|
| Processed, AlreadyProcessed, Ignored | 200 (or the provider's code) |
| Bad or missing signature | 401 |
| Body is not the provider's format | 400 |
| Unknown provider or account in the URL | 404 |
| Body over the limit | 413 |
| Handler threw | 500 |

## Checklist

- [ ] `AddWebhookEndpoints()` and `MapPayGateWebhooks().AllowAnonymous()` (or the controller)
- [ ] No `[FromBody]`, filter or middleware reads the webhook body first
- [ ] Public HTTPS URL set in the provider dashboard (or `NotificationUrl`)
- [ ] Inbox table with a unique index on `(provider, EventId)`, written in the same transaction as the change
- [ ] Amount and currency checked; status moved with `CanTransitionTo`; refunds tracked by total
- [ ] Fulfillment idempotent per order
- [ ] Reconciliation job for open attempts
- [ ] Return page asks the provider, and `next` is checked with `TryGetSafeReturnUrl`
