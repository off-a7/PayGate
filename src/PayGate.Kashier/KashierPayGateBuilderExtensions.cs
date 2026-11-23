namespace PayGate.Kashier;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayGate.Kashier.Transport;

/// <summary>Registers Kashier accounts.</summary>
public static class KashierPayGateBuilderExtensions
{
    /// <summary>
    /// Registers a Kashier account as <c>kashier</c>, or <c>kashier:name</c>, with its payment gateway. Its settings are
    /// validated when the app starts: the keys must be set and the hosts must match the environment. Resolve the gateway with
    /// <see cref="IPaymentGatewayResolver"/>; it also offers <see cref="IPaymentCancelFeature"/> to void a payment.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">
    /// Sets the account, for example <c>o =&gt; configuration.GetSection("Payments:Kashier").Bind(o)</c>. Keep the keys in
    /// user secrets, environment variables or a key vault.
    /// </param>
    /// <param name="accountName">The account name, for a second Kashier merchant.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddPayGate()
    ///     .AddKashier(o =&gt; builder.Configuration.GetSection("Payments:Kashier").Bind(o));
    /// </code>
    /// </example>
    public static PayGateBuilder AddKashier(
        this PayGateBuilder builder,
        Action<KashierAccountOptions> configure,
        string accountName = PaymentAccount.DefaultName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var account = new PaymentAccount(PaymentProviderId.Kashier, accountName);
        string key = account.ToString();

        builder.AddAccountOptions(account, configure);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<KashierAccountOptions>, KashierAccountOptionsValidator>());

        // Every request carries an absolute URL to the API or FEP host, so one client serves both. HTTP/1.1 is pinned per
        // request; the handler allows TLS renegotiation and recycles connections so DNS changes are picked up.
        builder.AddProviderHttpClient(account)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                SslOptions = { AllowRenegotiation = true },
            });

        builder.Services.AddKeyedSingleton(key, (services, _) => new KashierApiClient(
            account,
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<IPaymentCredentialsProvider<KashierAccountOptions>>()));

        return builder.AddGateway(account, services => new KashierPaymentGateway(
            services.GetRequiredKeyedService<KashierApiClient>(key),
            services.GetRequiredService<IPaymentCredentialsProvider<KashierAccountOptions>>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetService<ILogger<KashierPaymentGateway>>() ?? NullLogger<KashierPaymentGateway>.Instance));
    }
}
