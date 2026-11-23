namespace PayGate.Kashier;

/// <summary>
/// Kashier details of a payment, returned as <see cref="PaymentDetails.ProviderData"/>. Read it with
/// <c>details.GetProviderData&lt;KashierPaymentData&gt;()</c>.
/// </summary>
public sealed record KashierPaymentData
{
    /// <summary>
    /// The Kashier order the money moved on, which refunds and voids go to. <see langword="null"/> until the customer
    /// tries to pay.
    /// </summary>
    public string? OrderId { get; init; }

    /// <summary>The last operation on the order, such as <c>pay</c>, <c>refund</c> or <c>void</c>.</summary>
    public string? LastOperation { get; init; }

    /// <summary>Kashier's fees on the last operation, in the payment's currency.</summary>
    public decimal? Fees { get; init; }

    /// <summary>The VAT on those fees.</summary>
    public decimal? Vat { get; init; }

    /// <summary>What the last operation settles to the merchant: negative for a refund or void.</summary>
    public decimal? SettlementAmount { get; init; }
}
