namespace PayGate.Testing;

using System.Text;

/// <summary>The way a <see cref="FakePaymentGateway"/> asks the customer to pay.</summary>
public enum FakeFlow
{
    /// <summary>Returns a <see cref="RedirectAction"/>, like a hosted checkout page.</summary>
    Redirect = 0,

    /// <summary>Returns a <see cref="ClientSdkAction"/>, like a provider's web or mobile SDK.</summary>
    ClientSdk,

    /// <summary>Returns a <see cref="DisplayReferenceAction"/>, like a code paid later at a kiosk.</summary>
    DisplayReference,
}

/// <summary>Settings of a <see cref="FakePaymentGateway"/>.</summary>
public sealed class FakeGatewayOptions : PaymentAccountOptions
{
    /// <summary>The default value of <see cref="WebhookSecret"/>.</summary>
    public const string DefaultWebhookSecret = "fake-webhook-secret";

    /// <summary>The way the fake asks the customer to pay. Defaults to <see cref="FakeFlow.Redirect"/>.</summary>
    public FakeFlow Flow { get; set; } = FakeFlow.Redirect;

    /// <summary>
    /// Whether the fake honours idempotency keys. When set, an identical replay returns the original session and a timed-out
    /// write is retried with the same key.
    /// </summary>
    public bool IdempotentWrites { get; set; }

    /// <summary>Whether payments can be looked up by merchant reference. Defaults to <see langword="true"/>.</summary>
    public bool LookupByMerchantReference { get; set; } = true;

    /// <summary>The key webhooks are signed with.</summary>
    public string WebhookSecret { get; set; } = DefaultWebhookSecret;

    /// <summary>
    /// Lets the fake run in the Production environment, where it would otherwise stop the app from starting. A fake gateway
    /// reports payments that never happened, so set this only for a deliberate demo.
    /// </summary>
    public bool AllowInProduction { get; set; }

    /// <inheritdoc/>
    protected override void AppendMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(", Flow = ").Append(Flow)
            .Append(", IdempotentWrites = ").Append(IdempotentWrites)
            .Append(", LookupByMerchantReference = ").Append(LookupByMerchantReference)
            .Append(", WebhookSecret = ").Append(Redact(WebhookSecret))
            .Append(", AllowInProduction = ").Append(AllowInProduction);
    }
}
