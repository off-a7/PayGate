namespace PayGate.Testing;

using System.Globalization;

/// <summary>One payment held by a <see cref="FakePaymentGateway"/>. Guarded by the gateway's lock.</summary>
internal sealed class FakePayment
{
    public FakePayment(string id, CreatePaymentRequest request, PaymentSession session, DateTimeOffset now)
    {
        Id = id;
        Request = request;
        Session = session;
        Refunded = Money.Zero(request.Amount.Currency);
        CreatedAt = now;
        SetStatus(PaymentStatus.Pending, now);
    }

    public string Id { get; }

    public CreatePaymentRequest Request { get; }

    public PaymentSession Session { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; set; }

    public PaymentStatus Status { get; set; }

    public string RawStatus { get; set; } = string.Empty;

    public string? TransactionId { get; set; }

    public Money Refunded { get; set; }

    public Money? LastRefund { get; set; }

    public PaymentFailureReason? Failure { get; set; }

    public void SetStatus(PaymentStatus status, DateTimeOffset at)
    {
        Status = status;
        RawStatus = status.ToString().ToUpper(CultureInfo.InvariantCulture);
        UpdatedAt = at;
    }
}
