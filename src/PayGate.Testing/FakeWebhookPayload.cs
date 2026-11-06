namespace PayGate.Testing;

using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using PayGate.Providers;
using PayGate.Webhooks;

/// <summary>The body of a fake webhook.</summary>
internal sealed record FakeWebhookPayload
{
    public required string EventId { get; init; }

    public required string Type { get; init; }

    public required string Status { get; init; }

    public required string RawStatus { get; init; }

    public required string ProviderPaymentId { get; init; }

    public required string MerchantReference { get; init; }

    public string? ProviderTransactionId { get; init; }

    public required string Currency { get; init; }

    public required long AmountMinor { get; init; }

    public long? RefundMinor { get; init; }

    public long? RefundedTotalMinor { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = [];

    public static FakeWebhookPayload From(FakePayment payment, PaymentEventType? type)
    {
        var eventType = type ?? TypeOf(payment.Status);
        bool isRefund = eventType is PaymentEventType.RefundSucceeded or PaymentEventType.RefundFailed;
        long? refundedTotal = isRefund ? payment.Refunded.ToMinorUnits() : null;

        // Built from the facts only, so that delivering the same change again gives the same id.
        string identity = string.Join('|', payment.Id, eventType, payment.Status, refundedTotal?.ToString(CultureInfo.InvariantCulture));

        return new FakeWebhookPayload
        {
            EventId = $"fake_evt_{Signatures.BodyHash(Encoding.UTF8.GetBytes(identity))}",
            Type = eventType.ToString(),
            Status = payment.Status.ToString(),
            RawStatus = payment.RawStatus,
            ProviderPaymentId = payment.Id,
            MerchantReference = payment.Request.MerchantReference,
            ProviderTransactionId = payment.TransactionId,
            Currency = payment.Request.Amount.Currency.Code,
            AmountMinor = payment.Request.Amount.ToMinorUnits(),
            RefundMinor = isRefund ? payment.LastRefund?.ToMinorUnits() : null,
            RefundedTotalMinor = refundedTotal,
            OccurredAt = payment.UpdatedAt,
            Metadata = new Dictionary<string, string>(payment.Request.Metadata, StringComparer.Ordinal),
        };
    }

    private static PaymentEventType TypeOf(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending or PaymentStatus.Processing or PaymentStatus.RequiresAction => PaymentEventType.PaymentPending,
        PaymentStatus.Authorized => PaymentEventType.PaymentAuthorized,
        PaymentStatus.Succeeded => PaymentEventType.PaymentSucceeded,
        PaymentStatus.Failed => PaymentEventType.PaymentFailed,
        PaymentStatus.Canceled => PaymentEventType.PaymentCanceled,
        PaymentStatus.Expired => PaymentEventType.PaymentExpired,
        PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded => PaymentEventType.RefundSucceeded,
        _ => PaymentEventType.Other,
    };
}

/// <summary>Source-generated JSON for <see cref="FakeWebhookPayload"/>.</summary>
[JsonSerializable(typeof(FakeWebhookPayload))]
internal sealed partial class FakeWebhookJson : JsonSerializerContext;
