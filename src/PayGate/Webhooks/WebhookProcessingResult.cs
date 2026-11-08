namespace PayGate.Webhooks;

/// <summary>The outcome of processing a webhook, and the response to send to the provider.</summary>
public sealed class WebhookProcessingResult
{
    internal WebhookProcessingResult(WebhookProcessingStatus status, WebhookAck ack, IReadOnlyList<string> eventIds, string? reason)
    {
        Status = status;
        Ack = ack;
        EventIds = eventIds;
        Reason = reason;
    }

    /// <summary>The outcome.</summary>
    public WebhookProcessingStatus Status { get; }

    /// <summary>The response to send to the provider.</summary>
    public WebhookAck Ack { get; }

    /// <summary>The ids of the events the app's handler completed. Empty when the handler was not reached.</summary>
    public IReadOnlyList<string> EventIds { get; }

    /// <summary>Why the webhook was rejected, ignored or failed, for logs. Never contains secrets. Do not send it to the provider.</summary>
    public string? Reason { get; }
}

/// <summary>The outcome of processing a webhook.</summary>
public enum WebhookProcessingStatus
{
    /// <summary>Something went wrong on our side, such as a handler exception. The provider should retry.</summary>
    Failed = 0,

    /// <summary>At least one event was applied.</summary>
    Processed,

    /// <summary>Nothing new was applied, and at least one event was already in the app's inbox.</summary>
    AlreadyProcessed,

    /// <summary>The webhook is valid, but nothing in it was acted on.</summary>
    Ignored,

    /// <summary>The signature is missing or does not match. The handler was not called.</summary>
    InvalidSignature,

    /// <summary>The payload could not be read. The handler was not called.</summary>
    Malformed,

    /// <summary>No gateway is registered for the webhook's account. The handler was not called.</summary>
    UnknownAccount,
}
