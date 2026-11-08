# PayGate.AspNetCore

ASP.NET Core integration for PayGate: webhook endpoints for minimal APIs and MVC controllers, raw-body capture and return-callback helpers.

> **Preview.** The API may change before 1.0. See the [PayGate repository](https://github.com/off-a7/PayGate).

```csharp
builder.Services.AddPayGate()
    .AddWebhookEndpoints()          // optional: options => options.MaxBodyBytes = 128 * 1024
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
