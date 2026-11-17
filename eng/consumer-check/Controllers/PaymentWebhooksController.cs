namespace ConsumerCheck.Controllers;

using Microsoft.AspNetCore.Mvc;
using PayGate.AspNetCore;

/// <summary>Receives webhooks the way manga-store does: from a controller, never with [FromBody].</summary>
[ApiController]
[Route("payments/webhooks/{provider}/{account?}")]
public sealed class PaymentWebhooksController(IPayGateWebhookEndpoint webhooks, IPayGateReturnCallbacks returns) : ControllerBase
{
    [HttpPost]
    public Task<IResult> ReceiveAsync() => webhooks.HandleAsync(HttpContext, HttpContext.RequestAborted);

    [HttpGet("~/payments/return")]
    public IActionResult Return([FromQuery] string? next) =>
        returns.TryGetSafeReturnUrl(next, out var url) ? Redirect(url.ToString()) : Ok();
}
