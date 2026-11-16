namespace PayGate.Sample.WebApi.Payments;

using PayGate.Webhooks;

/// <summary>
/// Something the app learned about a payment: from a webhook, from asking the provider on the return
/// page or in the reconciliation job, or from a refund it just made. All of them are applied the same way.
/// </summary>
public sealed record PaymentObservation
{
    public required PaymentStatus Status { get; init; }

    /// <summary>The payment's amount as the provider sees it. Checked against the attempt.</summary>
    public Money? Amount { get; init; }

    /// <summary>The refunded total so far. A total, not a delta, so seeing it twice changes nothing.</summary>
    public Money? RefundedTotal { get; init; }

    public string? ProviderPaymentId { get; init; }

    public string? ProviderTransactionId { get; init; }

    /// <summary>Where it came from, for the logs.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Reads a webhook event. The status is what counts, not the event type: a late "succeeded" event
    /// may arrive after a refund, and its status tells the truth about the payment at that moment.
    /// </summary>
    public static PaymentObservation FromEvent(PaymentEvent paymentEvent)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        return new PaymentObservation
        {
            Status = paymentEvent.Status,
            Amount = paymentEvent.Amount,
            RefundedTotal = paymentEvent.RefundedTotal,
            ProviderPaymentId = paymentEvent.ProviderPaymentId,
            ProviderTransactionId = paymentEvent.ProviderTransactionId,
            Source = "webhook",
        };
    }

    public static PaymentObservation FromDetails(PaymentDetails details, string source)
    {
        ArgumentNullException.ThrowIfNull(details);
        return new PaymentObservation
        {
            Status = details.Status,
            Amount = details.Amount,
            RefundedTotal = details.RefundedAmount,
            ProviderPaymentId = details.ProviderPaymentId,
            ProviderTransactionId = details.ProviderTransactionId,
            Source = source,
        };
    }
}
