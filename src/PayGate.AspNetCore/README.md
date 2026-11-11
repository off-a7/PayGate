# PayGate.AspNetCore

ASP.NET Core integration for PayGate: webhook endpoints for minimal APIs and MVC controllers, raw-body capture and return-callback helpers.

> **Preview.** The API may change before 1.0. See the [PayGate repository](https://github.com/off-a7/PayGate).

```csharp
builder.Services.AddPayGate()
    .AddWebhookEndpoints(options =>
    {
        options.MaxBodyBytes = 128 * 1024;                              // 256 KB by default
        options.AllowedReturnUrls.Add(new Uri("https://shop.example.com/"));
    })
    .AddEventHandler<MyEventHandler>();

var app = builder.Build();
app.MapPayGateWebhooks()            // POST and GET /webhooks/{provider}/{account?}
   .AllowAnonymous();               // the signature authenticates a webhook
```

In an MVC controller, inject `IPayGateWebhookEndpoint` instead. Never bind the body with `[FromBody]`: signatures cover the exact bytes.

```csharp
[Route("webhooks/{provider}/{account?}")]
public sealed class WebhooksController(IPayGateWebhookEndpoint webhooks) : ControllerBase
{
    [HttpPost]
    public Task<IResult> Receive() => webhooks.HandleAsync(HttpContext, HttpContext.RequestAborted);
}
```

Requires .NET 10 (`net10.0`).

Each webhook is logged once (event id 2000, or 2001 when it is not accepted) with the account, outcome, status code, event ids and duration, never the payload.

## Return callbacks

A return from the payment page is never proof of payment. Read it, check where to send the customer, and confirm the payment yourself:

```csharp
app.MapGet("/payments/return/{provider}", async (string provider, HttpRequest request,
    IPayGateReturnCallbacks returns, IPaymentGatewayResolver gateways, CancellationToken ct) =>
{
    var account = new PaymentAccount(new PaymentProviderId(provider));
    var claim = await returns.ParseAsync(request, account, ct);          // untrusted
    // ... look the payment up with gateways.GetGateway(account).GetPaymentAsync(...)

    return returns.TryGetSafeReturnUrl(request.Query["returnUrl"], out var next)
        ? Results.Redirect(next.ToString())
        : Results.Redirect("/orders");
});
```

`TryGetSafeReturnUrl` allows local paths such as `/orders/42`, and absolute URLs only under `AllowedReturnUrls`.
