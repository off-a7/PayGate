namespace PayGate.AspNetCore;

/// <summary>How webhook requests are read. Set with <see cref="PayGateBuilderAspNetCoreExtensions.AddWebhookEndpoints"/>.</summary>
public sealed class PayGateWebhookEndpointOptions
{
    /// <summary>The default of <see cref="MaxBodyBytes"/>: 256 KB, far above any provider's webhook.</summary>
    public const int DefaultMaxBodyBytes = 256 * 1024;

    /// <summary>The largest body accepted. A larger one is answered with 413 before anything else runs.</summary>
    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;
}
