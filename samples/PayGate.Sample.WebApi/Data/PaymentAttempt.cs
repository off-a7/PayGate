namespace PayGate.Sample.WebApi.Data;

/// <summary>
/// One press of "Pay". Each attempt has its own merchant reference (<c>ORD-{order}-R{n}</c>), because a
/// provider refuses a reference it has seen before, even when the earlier attempt failed.
/// </summary>
public sealed class PaymentAttempt
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order Order { get; set; } = null!;

    /// <summary>The attempt's number within its order, starting at 1.</summary>
    public int Number { get; set; }

    /// <summary>Unique. Sent to the provider and used to find the attempt again.</summary>
    public required string MerchantReference { get; set; }

    /// <summary>The PayGate account, such as <c>fake</c> or <c>kashier:secondary</c>.</summary>
    public required string Account { get; set; }

    /// <summary>Null until the provider answered. Stays null when creating timed out.</summary>
    public string? ProviderPaymentId { get; set; }

    public string? ProviderTransactionId { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public long AmountMinor { get; set; }

    public required string Currency { get; set; }

    /// <summary>The refunded total as the provider last reported it. Only ever grows.</summary>
    public long RefundedMinor { get; set; }

    /// <summary>How many refunds were asked for, so that each one gets its own reference.</summary>
    public int RefundRequests { get; set; }

    /// <summary>Why the last change was refused or failed. Never a secret or a payload.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public PaymentAccount PaymentAccount => PaymentAccount.Parse(Account);

    public Money Amount => Money.FromMinorUnits(AmountMinor, PayGate.Currency.FromCode(Currency));

    public Money Refunded => Money.FromMinorUnits(RefundedMinor, PayGate.Currency.FromCode(Currency));
}
