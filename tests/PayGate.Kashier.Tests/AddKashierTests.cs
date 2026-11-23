namespace PayGate.Kashier.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using PayGate.Kashier.Transport;
using PayGate.Providers;

public sealed class AddKashierTests
{
    [Fact]
    public void Valid_settings_pass_startup_validation()
    {
        using var provider = Build(o => Fill(o));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Missing_keys_stop_the_app_from_starting_and_name_the_account()
    {
        using var provider = Build(o => o.MerchantId = "MID-1234-567");

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        error.Message.ShouldContain("Kashier account 'kashier': SecretKey is required.");
        error.Message.ShouldContain("PaymentApiKey is required.");
    }

    [Fact]
    public void Live_keys_on_a_test_host_stop_the_app_from_starting()
    {
        using var provider = Build(o =>
        {
            Fill(o);
            o.Environment = PaymentEnvironment.Live;
            o.BaseUrlOverride = new Uri("https://test-api.kashier.io/");
        });

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("but Environment is Live");
    }

    [Fact]
    public void Two_merchants_are_two_accounts_with_their_own_settings_and_clients()
    {
        var services = new ServiceCollection();
        services.AddPayGate()
            .AddKashier(o => Fill(o))
            .AddKashier(o => Fill(o, "MID-2222-222"), "secondary");
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<KashierAccountOptions>>();
        options.Get("kashier").MerchantId.ShouldBe("MID-1234-567");
        options.Get("kashier:secondary").MerchantId.ShouldBe("MID-2222-222");
        provider.GetRequiredKeyedService<KashierApiClient>("kashier:secondary").Account.ShouldBe(
            new PaymentAccount(PaymentProviderId.Kashier, "secondary"));
    }

    [Fact]
    public void The_client_allows_tls_renegotiation_and_recycles_connections()
    {
        using var provider = Build(o => Fill(o));
        string name = PaymentHttp.ClientName(new PaymentAccount(PaymentProviderId.Kashier));

        var builder = provider.GetRequiredService<HttpMessageHandlerBuilder>();
        foreach (var action in provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name).HttpMessageHandlerBuilderActions)
        {
            action(builder);
        }

        var handler = builder.PrimaryHandler.ShouldBeOfType<SocketsHttpHandler>();
        handler.SslOptions.AllowRenegotiation.ShouldBeTrue();
        handler.PooledConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(2));
        handler.Dispose();
    }

    private static ServiceProvider Build(Action<KashierAccountOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddPayGate().AddKashier(configure);
        return services.BuildServiceProvider();
    }

    private static void Fill(KashierAccountOptions options, string merchantId = "MID-1234-567")
    {
        options.MerchantId = merchantId;
        options.SecretKey = "secret-key-value";
        options.PaymentApiKey = "payment-api-key-value";
    }
}
