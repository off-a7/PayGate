namespace Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PayGate.AspNetCore;

/// <summary>Maps PayGate's webhook endpoints.</summary>
public static class PayGateEndpointRouteBuilderExtensions
{
    /// <summary>The default route prefix.</summary>
    public const string DefaultPrefix = "/webhooks";

    /// <summary>
    /// Maps <c>POST</c> and <c>GET {prefix}/{provider}/{account?}</c>, such as <c>/webhooks/kashier</c> and
    /// <c>/webhooks/kashier/secondary</c>. Give each provider the URL of its account.
    /// </summary>
    /// <param name="endpoints">The app's endpoints.</param>
    /// <param name="prefix">The route prefix.</param>
    /// <returns>
    /// The endpoint group. The signature authenticates a webhook, so an app whose endpoints require sign-in by default must call
    /// <c>AllowAnonymous()</c> on it. Rate limiting or tags can be added here too.
    /// </returns>
    /// <exception cref="InvalidOperationException"><c>AddWebhookEndpoints()</c> was not called on <c>services.AddPayGate()</c>.</exception>
    public static RouteGroupBuilder MapPayGateWebhooks(this IEndpointRouteBuilder endpoints, string prefix = DefaultPrefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (endpoints.ServiceProvider.GetService<IPayGateWebhookEndpoint>() is null)
        {
            throw new InvalidOperationException(
                "PayGate's webhook endpoint is not registered. Call services.AddPayGate().AddWebhookEndpoints() before mapping it.");
        }

        var group = endpoints.MapGroup(prefix);
        group.MapMethods(
            $"{{{PayGateWebhookEndpoint.ProviderRouteValue}}}/{{{PayGateWebhookEndpoint.AccountRouteValue}?}}",
            [HttpMethods.Post, HttpMethods.Get],
            ReceiveAsync);
        return group;
    }

    private static async Task ReceiveAsync(HttpContext context)
    {
        var endpoint = context.RequestServices.GetRequiredService<IPayGateWebhookEndpoint>();
        var result = await endpoint.HandleAsync(context, context.RequestAborted).ConfigureAwait(false);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }
}
