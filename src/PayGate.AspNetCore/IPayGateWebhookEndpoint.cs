namespace PayGate.AspNetCore;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Receives a webhook over HTTP: reads the raw body, runs it through <see cref="Webhooks.IWebhookProcessor"/> and returns the
/// response the provider expects. <see cref="Microsoft.AspNetCore.Builder.PayGateEndpointRouteBuilderExtensions.MapPayGateWebhooks"/> uses it; inject it
/// into an MVC controller to receive webhooks there instead.
/// </summary>
/// <remarks>
/// The body is read here, byte for byte, because signatures cover the exact bytes. In a controller, never bind the body with
/// <c>[FromBody]</c> or read it before calling this.
/// </remarks>
public interface IPayGateWebhookEndpoint
{
    /// <summary>Handles a webhook whose route has <c>{provider}</c> and an optional <c>{account}</c> segment.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The response. A controller action can return it as is.</returns>
    Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken);

    /// <summary>Handles a webhook for an account the caller already knows, for routes of another shape.</summary>
    /// <param name="context">The request.</param>
    /// <param name="account">The account the webhook is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The response. A controller action can return it as is.</returns>
    Task<IResult> HandleAsync(HttpContext context, PaymentAccount account, CancellationToken cancellationToken);
}
