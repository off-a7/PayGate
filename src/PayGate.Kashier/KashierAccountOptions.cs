namespace PayGate.Kashier;

using System.Text;

/// <summary>
/// Settings and credentials of one Kashier account, registered with
/// <see cref="KashierPayGateBuilderExtensions.AddKashier"/> and validated when the app starts.
/// </summary>
/// <remarks>
/// Keep the keys out of source control: bind them from user secrets, environment variables or a key vault. Test keys only
/// work with <see cref="PaymentEnvironment.Sandbox"/> and live keys with <see cref="PaymentEnvironment.Live"/>.
/// </remarks>
public sealed class KashierAccountOptions : PaymentAccountOptions
{
    /// <summary>The payment API host of the test environment.</summary>
    public static readonly Uri SandboxApiBaseUrl = new("https://test-api.kashier.io/");

    /// <summary>The payment API host of the live environment.</summary>
    public static readonly Uri LiveApiBaseUrl = new("https://api.kashier.io/");

    /// <summary>The FEP host (refund, void, capture, transfers) of the test environment.</summary>
    public static readonly Uri SandboxFepBaseUrl = new("https://test-fep.kashier.io/");

    /// <summary>The FEP host (refund, void, capture, transfers) of the live environment.</summary>
    public static readonly Uri LiveFepBaseUrl = new("https://fep.kashier.io/");

    /// <summary>The merchant id, such as <c>MID-1234-567</c>. Required.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>The secret key. Sent as the raw <c>Authorization</c> header on every call. Required.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// The Payment API Key. Sent as the <c>api-key</c> header when creating sessions, and the key Kashier signs webhooks and
    /// browser redirects with. Required.
    /// </summary>
    public string PaymentApiKey { get; set; } = string.Empty;

    /// <summary>The key named <c>transfer</c> in the Kashier dashboard. Needed only for payouts and their webhooks.</summary>
    public string? TransferApiKey { get; set; }

    /// <summary>
    /// Replaces the FEP host only. <see cref="PaymentAccountOptions.BaseUrlOverride"/> replaces both hosts, which suits a
    /// single mock server.
    /// </summary>
    public Uri? FepBaseUrlOverride { get; set; }

    /// <summary>The payment page language: <c>en</c> or <c>ar</c>. Defaults to <c>en</c>.</summary>
    public string Display { get; set; } = "en";

    /// <summary>
    /// The payment methods the payment page offers when a request doesn't choose, as Kashier's comma-separated list such as
    /// <c>card,wallet</c>. <see langword="null"/> leaves the choice to the account's dashboard settings.
    /// </summary>
    public string? AllowedMethods { get; set; }

    /// <summary>How long a payment page stays open when a request has no expiry. Defaults to 30 minutes.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How many failed tries a customer gets on one payment page. Defaults to 3.</summary>
    public int MaxFailureAttempts { get; set; } = 3;

    /// <summary>The payment API host this account calls.</summary>
    internal Uri ApiBaseUrl => WithTrailingSlash(BaseUrlOverride ?? (Environment == PaymentEnvironment.Live ? LiveApiBaseUrl : SandboxApiBaseUrl));

    /// <summary>The FEP host this account calls.</summary>
    internal Uri FepBaseUrl => WithTrailingSlash(
        FepBaseUrlOverride ?? BaseUrlOverride ?? (Environment == PaymentEnvironment.Live ? LiveFepBaseUrl : SandboxFepBaseUrl));

    /// <inheritdoc/>
    protected override void AppendMembers(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Append(", MerchantId = ").Append(MerchantId)
            .Append(", SecretKey = ").Append(Redact(SecretKey))
            .Append(", PaymentApiKey = ").Append(Redact(PaymentApiKey))
            .Append(", TransferApiKey = ").Append(Redact(TransferApiKey));
        if (FepBaseUrlOverride is not null)
        {
            builder.Append(", FepBaseUrlOverride = ").Append(FepBaseUrlOverride);
        }

        builder.Append(", Display = ").Append(Display)
            .Append(", AllowedMethods = ").Append(AllowedMethods ?? "(dashboard)")
            .Append(", SessionLifetime = ").Append(SessionLifetime)
            .Append(", MaxFailureAttempts = ").Append(MaxFailureAttempts);
    }

    private static Uri WithTrailingSlash(Uri url) =>
        url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
}
