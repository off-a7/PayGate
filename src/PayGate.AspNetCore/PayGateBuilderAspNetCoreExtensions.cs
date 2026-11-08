namespace PayGate.AspNetCore;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>Registers PayGate's ASP.NET Core services.</summary>
public static class PayGateBuilderAspNetCoreExtensions
{
    /// <summary>
    /// Registers <see cref="IPayGateWebhookEndpoint"/>, which <see cref="Microsoft.AspNetCore.Builder.PayGateEndpointRouteBuilderExtensions.MapPayGateWebhooks"/>
    /// and MVC controllers use to receive webhooks.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Changes how webhook requests are read.</param>
    /// <returns>The builder.</returns>
    public static PayGateBuilder AddWebhookEndpoints(this PayGateBuilder builder, Action<PayGateWebhookEndpointOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<PayGateWebhookEndpointOptions>()
            .Validate(o => o.MaxBodyBytes > 0, "MaxBodyBytes must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.TryAddSingleton<IPayGateWebhookEndpoint, PayGateWebhookEndpoint>();
        return builder;
    }
}
