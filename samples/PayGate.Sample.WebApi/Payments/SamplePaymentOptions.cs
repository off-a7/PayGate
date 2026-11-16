namespace PayGate.Sample.WebApi.Payments;

/// <summary>The sample's own settings, from the <c>Payments</c> section.</summary>
public sealed class SamplePaymentOptions
{
    public const string SectionName = "Payments";

    /// <summary>
    /// The PayGate account checkouts go to: <c>fake</c> now, <c>kashier</c> in Phase 3. Changing it is the
    /// only change the app needs to switch provider.
    /// </summary>
    public string Account { get; set; } = "fake";

    /// <summary>
    /// The address providers and customers reach the app on. For a provider on the internet this must be
    /// public, for example a dev tunnel while running locally.
    /// </summary>
    public Uri PublicBaseUrl { get; set; } = new("http://localhost:5080/");

    /// <summary>Where webhooks are mapped. The provider and account segments are added after it.</summary>
    public string WebhookPrefix { get; set; } = "/webhooks";

    /// <summary>
    /// Ask the provider about each webhook's payment before handling it, so that the app acts on the
    /// provider's current answer and not only on the message. One extra call per webhook.
    /// </summary>
    public bool ConfirmWithProvider { get; set; } = true;

    /// <summary>Absolute addresses the return page may send the customer on to, besides the app's own paths.</summary>
    public IList<Uri> AllowedReturnUrls { get; } = [];

    public ReconciliationOptions Reconciliation { get; } = new();
}

/// <summary>How often the app asks the provider about attempts whose webhook never came.</summary>
public sealed class ReconciliationOptions
{
    /// <summary>Turned off in tests, which run one pass at a time instead.</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Attempts younger than this are left alone: their webhook is probably on its way.</summary>
    public TimeSpan PendingOlderThan { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Attempts older than this are no longer asked about: the customer walked away. The attempt is not
    /// marked failed, because a late payment must still be able to arrive by webhook.
    /// </summary>
    public TimeSpan StopAfter { get; set; } = TimeSpan.FromDays(2);

    /// <summary>The most attempts one pass looks up, so that a backlog does not hammer the provider.</summary>
    public int BatchSize { get; set; } = 50;
}
