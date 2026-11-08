namespace PayGate.AspNetCore;

/// <summary>
/// How webhook and return requests are handled. Set with <see cref="PayGateBuilderAspNetCoreExtensions.AddWebhookEndpoints"/>.
/// The route prefix is an argument of <c>MapPayGateWebhooks</c>, and confirming events with the provider is set per account
/// with <see cref="PayGateBuilder.ConfigureWebhooks"/>.
/// </summary>
public sealed class PayGateWebhookEndpointOptions
{
    /// <summary>The default of <see cref="MaxBodyBytes"/>: 256 KB, far above any provider's webhook.</summary>
    public const int DefaultMaxBodyBytes = 256 * 1024;

    /// <summary>The largest body accepted. A larger one is answered with 413 before anything else runs.</summary>
    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    /// <summary>Replaces <see cref="MaxBodyBytes"/> for one provider, keyed by its id such as <c>kashier</c>.</summary>
    public IDictionary<string, int> MaxBodyBytesByProvider { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The absolute URLs <see cref="IPayGateReturnCallbacks.TryGetSafeReturnUrl"/> allows, each with everything under its path,
    /// such as <c>https://shop.example.com/</c> or <c>mansa://payment/</c>. Local paths are always allowed.
    /// </summary>
    public IList<Uri> AllowedReturnUrls { get; } = [];

    /// <summary>The body limit for a provider.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>Its override, or <see cref="MaxBodyBytes"/>.</returns>
    public int MaxBodyBytesFor(PaymentProviderId provider) =>
        MaxBodyBytesByProvider.TryGetValue(provider.Value, out int limit) ? limit : MaxBodyBytes;
}
