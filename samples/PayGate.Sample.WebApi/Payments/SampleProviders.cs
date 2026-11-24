namespace PayGate.Sample.WebApi.Payments;

using PayGate.Kashier;
using PayGate.Testing;

/// <summary>Registers the provider named by <c>Payments:Account</c>. The rest of the app does not know which one it is.</summary>
public static class SampleProviders
{
    public static PayGateBuilder AddConfiguredProvider(this PayGateBuilder paygate, PaymentAccount account, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(paygate);
        ArgumentNullException.ThrowIfNull(configuration);

        if (account.Provider == FakePaymentGateway.ProviderId)
        {
            // Payments:Fake holds the fake's options, such as Flow. It refuses to start in Production.
            return paygate.AddPayGateFake(options => configuration.GetSection("Payments:Fake").Bind(options), account.Name);
        }

        if (account.Provider == PaymentProviderId.Kashier)
        {
            // Payments:Kashier holds the merchant id and environment; the keys come from user secrets or the environment.
            return paygate.AddKashier(options => configuration.GetSection("Payments:Kashier").Bind(options), account.Name);
        }

        throw new InvalidOperationException($"The sample cannot use provider '{account.Provider}'. Set Payments:Account to 'fake' or 'kashier'.");
    }
}
