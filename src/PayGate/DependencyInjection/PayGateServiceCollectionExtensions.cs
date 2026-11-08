namespace Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;
using PayGate;
using PayGate.Webhooks;

/// <summary>Adds PayGate to a service collection.</summary>
public static class PayGateServiceCollectionExtensions
{
    /// <summary>
    /// Adds the PayGate core services: the gateway resolver, the webhook processor, the default credentials provider and the system clock.
    /// Chain provider registrations onto the returned builder, such as <c>services.AddPayGate().AddKashier(...)</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder for adding providers and the event handler.</returns>
    /// <remarks>Safe to call more than once: the core services are registered only the first time.</remarks>
    public static PayGateBuilder AddPayGate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPaymentGatewayResolver, PaymentGatewayResolver>();
        services.TryAddSingleton<IWebhookProcessor, WebhookProcessor>();
        services.TryAdd(ServiceDescriptor.Singleton(typeof(IPaymentCredentialsProvider<>), typeof(OptionsCredentialsProvider<>)));

        return new PayGateBuilder(services);
    }
}
