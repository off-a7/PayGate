namespace PayGate.Webhooks;

/// <summary>The HTTP response that acknowledges a webhook.</summary>
/// <param name="StatusCode">The HTTP status code.</param>
/// <param name="Body">The plain-text body, or <see langword="null"/> for an empty body.</param>
public sealed record WebhookAck(int StatusCode, string? Body = null);

/// <summary>
/// Maps a webhook's outcome to the response its provider expects. This default answers every accepted webhook with 200 and
/// every response with an empty body. A provider package derives from it where its provider differs, and registers the result
/// with <see cref="PayGateBuilder.AddWebhookAckPolicy"/>.
/// </summary>
public class WebhookAckPolicy
{
    /// <summary>Creates the policy of a provider.</summary>
    /// <param name="provider">The provider this policy answers for.</param>
    public WebhookAckPolicy(PaymentProviderId provider)
    {
        Provider = provider;
    }

    /// <summary>The provider this policy answers for.</summary>
    public PaymentProviderId Provider { get; }

    /// <summary>Returns the response for an outcome.</summary>
    /// <param name="status">The outcome.</param>
    /// <returns>The response. <see cref="WebhookProcessingStatus.Failed"/> must stay a 5xx so that the provider retries.</returns>
    public virtual WebhookAck GetAck(WebhookProcessingStatus status) => status switch
    {
        WebhookProcessingStatus.Processed or WebhookProcessingStatus.AlreadyProcessed or WebhookProcessingStatus.Ignored => new WebhookAck(200),
        WebhookProcessingStatus.InvalidSignature => new WebhookAck(401),
        WebhookProcessingStatus.Malformed => new WebhookAck(400),
        WebhookProcessingStatus.UnknownAccount => new WebhookAck(404),
        _ => new WebhookAck(500),
    };
}
