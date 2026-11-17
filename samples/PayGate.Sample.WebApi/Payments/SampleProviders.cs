namespace PayGate.Sample.WebApi.Payments;

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

        // Phase 3 adds Kashier here, read from Payments:Kashier.
        throw new InvalidOperationException($"The sample cannot use provider '{account.Provider}' yet. Set Payments:Account to 'fake'.");
    }
}
