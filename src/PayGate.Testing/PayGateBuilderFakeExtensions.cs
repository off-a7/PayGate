namespace PayGate.Testing;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>Registers a <see cref="FakePaymentGateway"/>.</summary>
public static class PayGateBuilderFakeExtensions
{
    /// <summary>
    /// Registers a fake gateway and its webhook verifier as the account <c>fake</c>, or <c>fake:name</c>. The app fails to start
    /// in the Production environment unless <see cref="FakeGatewayOptions.AllowInProduction"/> is set. Resolve
    /// <see cref="FakePaymentGateway"/> to drive the first fake registered, or use the account's string form as a service key.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="configure">Changes the settings.</param>
    /// <param name="accountName">The account name.</param>
    /// <returns>The builder.</returns>
    public static PayGateBuilder AddPayGateFake(
        this PayGateBuilder builder,
        Action<FakeGatewayOptions>? configure = null,
        string accountName = PaymentAccount.DefaultName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var account = new PaymentAccount(FakePaymentGateway.ProviderId, accountName);
        string key = account.ToString();

        builder.AddAccountOptions<FakeGatewayOptions>(account, options => configure?.Invoke(options))
            .Validate(options => !string.IsNullOrEmpty(options.WebhookSecret), "WebhookSecret is required.")
            .Validate<IServiceProvider>(
                (options, services) => options.AllowInProduction || !IsProduction(services),
                "The fake payment gateway reports payments that never happened, so it refuses to run in Production. " +
                "Set AllowInProduction only for a deliberate demo.");

        builder.Services.AddKeyedSingleton(key, (services, _) => new FakePaymentGateway(
            services.GetRequiredService<IOptionsMonitor<FakeGatewayOptions>>().Get(key),
            account.Name,
            services.GetRequiredService<TimeProvider>()));
        builder.Services.TryAddSingleton(services => services.GetRequiredKeyedService<FakePaymentGateway>(key));

        return builder
            .AddGateway(account, services => services.GetRequiredKeyedService<FakePaymentGateway>(key))
            .AddWebhookVerifier<FakeWebhookVerifier>();
    }

    private static bool IsProduction(IServiceProvider services)
    {
        var host = services.GetService<IHostEnvironment>();
        if (host is not null)
        {
            return host.IsProduction();
        }

        string? environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environment, Environments.Production, StringComparison.OrdinalIgnoreCase);
    }
}
