namespace PayGate.AspNetCore;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>Registers PayGate's ASP.NET Core services.</summary>
public static class PayGateBuilderAspNetCoreExtensions
{
    /// <summary>
    /// Registers <see cref="IPayGateWebhookEndpoint"/>, which <see cref="Microsoft.AspNetCore.Builder.PayGateEndpointRouteBuilderExtensions.MapPayGateWebhooks"/>
    /// and MVC controllers use to receive webhooks, and <see cref="IPayGateReturnCallbacks"/> for the app's return endpoint.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Changes how webhook and return requests are handled.</param>
    /// <returns>The builder.</returns>
    public static PayGateBuilder AddWebhookEndpoints(this PayGateBuilder builder, Action<PayGateWebhookEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<PayGateWebhookEndpointOptions>()
            .Validate(o => o.MaxBodyBytes > 0 && o.MaxBodyBytesByProvider.Values.All(limit => limit > 0), "Body limits must be positive.")
            .Validate(o => o.AllowedReturnUrls.All(url => url.IsAbsoluteUri), "AllowedReturnUrls must be absolute.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.TryAddSingleton<IPayGateWebhookEndpoint, PayGateWebhookEndpoint>();
        builder.Services.TryAddSingleton<IPayGateReturnCallbacks, PayGateReturnCallbacks>();
        return builder;
    }
}
