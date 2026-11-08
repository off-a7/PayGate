namespace PayGate;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PayGate.Webhooks;

public sealed partial class PayGateBuilder
{
    /// <summary>
    /// Registers a provider's webhook verifier, for provider packages. Verifiers are singletons, one per provider. Registering
    /// the same type again, as a package does for each account, has no effect.
    /// </summary>
    /// <typeparam name="TVerifier">The provider's verifier.</typeparam>
    /// <returns>This builder.</returns>
    public PayGateBuilder AddWebhookVerifier<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TVerifier>()
        where TVerifier : class, IWebhookVerifier
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebhookVerifier, TVerifier>());
        return this;
    }

    /// <summary>
    /// Registers a provider's ack policy, for provider packages whose provider expects something other than the default
    /// <see cref="WebhookAckPolicy"/>. Registering the same type again has no effect.
    /// </summary>
    /// <typeparam name="TPolicy">The provider's policy.</typeparam>
    /// <returns>This builder.</returns>
    public PayGateBuilder AddWebhookAckPolicy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>()
        where TPolicy : WebhookAckPolicy
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<WebhookAckPolicy, TPolicy>());
        return this;
    }

    /// <summary>Changes how one account's webhooks are processed.</summary>
    /// <param name="account">The account.</param>
    /// <param name="configure">Changes the options.</param>
    /// <returns>This builder.</returns>
    public PayGateBuilder ConfigureWebhooks(PaymentAccount account, Action<WebhookOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(account.ToString(), configure);
        return this;
    }
}
