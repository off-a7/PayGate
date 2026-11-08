namespace PayGate.Webhooks;

/// <summary>How one account's webhooks are processed. Set with <see cref="PayGateBuilder.ConfigureWebhooks"/>.</summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Re-reads each payment from the provider before dispatching its event. This defends against out-of-order delivery and
    /// weak signing schemes. Off by default.
    /// </summary>
    /// <remarks>
    /// The event's <see cref="PaymentEvent.Status"/>, <see cref="PaymentEvent.RawStatus"/> and <see cref="PaymentEvent.Amount"/>
    /// then come from the provider, and a refund event's <see cref="PaymentEvent.RefundedTotal"/> too.
    /// <see cref="PaymentEvent.Type"/> stays what the webhook reported, so a handler must act on the status.
    /// <see cref="PaymentEvent.RefundAmount"/> stays what the webhook reported: the provider's totals cannot give a delta.
    /// When the payment cannot be confirmed, the event is not dispatched and the webhook fails, so that the provider retries.
    /// </remarks>
    public bool ConfirmWithProvider { get; set; }
}
