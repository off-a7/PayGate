namespace PayGate;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PayGate.Webhooks;

/// <summary>
/// Configures PayGate. Returned by <see cref="PayGateServiceCollectionExtensions.AddPayGate"/>; provider packages add their own
/// extension methods to it, such as <c>AddKashier(...)</c>.
/// </summary>
public sealed partial class PayGateBuilder
{
    internal PayGateBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Registers the app's handler for verified payment events. It is resolved in a fresh scope for each event, so it may
    /// depend on scoped services such as a <c>DbContext</c>. Calling this again replaces the previous handler.
    /// </summary>
    /// <typeparam name="THandler">The app's handler.</typeparam>
    /// <returns>This builder.</returns>
    public PayGateBuilder AddEventHandler<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>()
        where THandler : class, IPaymentEventHandler
    {
        Services.Replace(ServiceDescriptor.Scoped<IPaymentEventHandler, THandler>());
        return this;
    }

    /// <summary>
    /// Registers the gateway for one account. Used by provider packages. The gateway is resolvable through
    /// <see cref="IPaymentGatewayResolver"/> and as a keyed <see cref="IPaymentGateway"/> under the account's string form
    /// (<c>kashier</c>, <c>kashier:secondary</c>).
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="factory">Creates the gateway. Gateways are stateless and created on each resolution; they must not depend on scoped services.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">A gateway is already registered for the account.</exception>
    public PayGateBuilder AddGateway(PaymentAccount account, Func<IServiceProvider, IPaymentGateway> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (account.Provider.Value.Length == 0)
        {
            throw new ArgumentException("A provider is required.", nameof(account));
        }

        foreach (var descriptor in Services)
        {
            if (descriptor.ServiceType == typeof(GatewayRegistration)
                && descriptor.ImplementationInstance is GatewayRegistration existing
                && existing.Account == account)
            {
                throw new InvalidOperationException($"A payment gateway is already registered for '{account}'. Give the second account its own name.");
            }
        }

        Services.AddSingleton(new GatewayRegistration(account, factory));
        Services.AddKeyedTransient<IPaymentGateway>(account.ToString(), (provider, _) => factory(provider));
        return this;
    }

    /// <summary>
    /// Registers the named options of one account, validated when the app starts. Used by provider packages, which add their
    /// own <see cref="IValidateOptions{TOptions}"/> for the rules of their credentials.
    /// </summary>
    /// <typeparam name="TOptions">The provider's account options type.</typeparam>
    /// <param name="account">The account. Its string form is the options name.</param>
    /// <param name="configure">Sets the options, for example from configuration or code.</param>
    /// <returns>The options builder, for further configuration or validation.</returns>
    public OptionsBuilder<TOptions> AddAccountOptions<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        PaymentAccount account,
        Action<TOptions> configure)
        where TOptions : PaymentAccountOptions
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Services.AddOptions<TOptions>(account.ToString())
            .Configure(configure)
            .ValidateOnStart();
    }
}
