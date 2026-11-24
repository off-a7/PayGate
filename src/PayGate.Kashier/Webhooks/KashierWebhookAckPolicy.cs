namespace PayGate.Kashier.Webhooks;

using PayGate.Webhooks;

/// <summary>
/// Kashier treats 200 and 409 as delivered and retries anything else for hours. A webhook the app already has is answered
/// 409, so Kashier's dashboard shows it as a duplicate rather than a fresh delivery.
/// </summary>
internal sealed class KashierWebhookAckPolicy() : WebhookAckPolicy(PaymentProviderId.Kashier)
{
    public override WebhookAck GetAck(WebhookProcessingStatus status) =>
        status == WebhookProcessingStatus.AlreadyProcessed ? new WebhookAck(409) : base.GetAck(status);
}
