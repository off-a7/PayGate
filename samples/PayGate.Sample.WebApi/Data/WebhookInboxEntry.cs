namespace PayGate.Sample.WebApi.Data;

/// <summary>
/// One row per payment event the app has handled. The unique index on (Provider, EventId) is the
/// dedupe: providers deliver the same event more than once, and the second delivery must change nothing.
/// </summary>
public sealed class WebhookInboxEntry
{
    public long Id { get; set; }

    public required string Provider { get; set; }

    public required string Account { get; set; }

    /// <summary>PayGate's stable id for the event. The same change delivered twice has the same id.</summary>
    public required string EventId { get; set; }

    public required string EventType { get; set; }

    public string? MerchantReference { get; set; }

    /// <summary>SHA-256 of the raw body, to tell a replay from a different body with the same id. Not the body itself.</summary>
    public string? BodyHash { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Why the event changed nothing, for example an amount that does not match.</summary>
    public string? Error { get; set; }
}
