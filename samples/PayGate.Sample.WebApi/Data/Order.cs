namespace PayGate.Sample.WebApi.Data;

/// <summary>What the customer buys. Owned by the app, never by PayGate.</summary>
public sealed class Order
{
    public int Id { get; set; }

    public required string Description { get; set; }

    /// <summary>The total in minor units (piasters for EGP), so no rounding happens in the database.</summary>
    public long AmountMinor { get; set; }

    public required string Currency { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.AwaitingPayment;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>The attempt that paid. A second attempt that also succeeds does not pay the order again.</summary>
    public int? PaidAttemptId { get; set; }

    /// <summary>Set once the goods were handed over. Fulfillment runs at most once per order because of it.</summary>
    public DateTimeOffset? FulfilledAt { get; set; }

    public List<PaymentAttempt> Attempts { get; } = [];

    public Money Amount => Money.FromMinorUnits(AmountMinor, PayGate.Currency.FromCode(Currency));
}

public enum OrderStatus
{
    AwaitingPayment = 0,

    Paid,

    Refunded,
}
