namespace PayGate;

using System.Collections.Immutable;

/// <summary>
/// What a gateway's account supports. These are upper bounds: a restriction that depends on the payment
/// method or the payment's state surfaces as <see cref="PaymentErrorCode.NotSupported"/> at call time.
/// </summary>
/// <remarks>Optional operations such as cancel are discovered with <see cref="IPaymentGateway.TryGetFeature"/>, not flags.</remarks>
public sealed record PaymentCapabilities
{
    /// <summary>The next-action kinds <see cref="IPaymentGateway.CreatePaymentAsync"/> can return.</summary>
    public IReadOnlySet<NextActionKind> SupportedNextActions { get; init; } = ImmutableHashSet<NextActionKind>.Empty;

    /// <summary>The payment methods the account can offer.</summary>
    public IReadOnlySet<PaymentMethodType> SupportedPaymentMethods { get; init; } = ImmutableHashSet<PaymentMethodType>.Empty;

    /// <summary>Whether <see cref="PaymentLookup.ByMerchantReference"/> works. Without it, store the provider payment id.</summary>
    public bool LookupByMerchantReference { get; init; }

    /// <summary>
    /// Whether the provider honours idempotency keys on writes. When set, a timed-out write is retried with the same key;
    /// otherwise it returns <see cref="PaymentErrorCode.OutcomeUnknown"/> and is never retried.
    /// </summary>
    public bool IdempotentWrites { get; init; }

    /// <summary>Whether part of a payment can be refunded.</summary>
    public bool PartialRefund { get; init; }

    /// <summary>Whether a payment can be refunded more than once.</summary>
    public bool MultipleRefunds { get; init; }

    /// <summary>Whether <see cref="CreatePaymentRequest.ExpiresAt"/> is honoured.</summary>
    public bool PaymentExpiry { get; init; }

    /// <summary>Whether <see cref="CreatePaymentRequest.NotificationUrl"/> is honoured. Otherwise the webhook URL is configured once at the provider.</summary>
    public bool PerRequestNotificationUrl { get; init; }

    /// <summary>Whether <see cref="CreatePaymentRequest.Metadata"/> is echoed back in lookups and events.</summary>
    public bool MetadataPassthrough { get; init; }

    /// <summary>
    /// Whether a create that reuses a <see cref="CreatePaymentRequest.MerchantReference"/> is always refused with
    /// <see cref="PaymentErrorCode.DuplicateReference"/>. When it is not set, a reused reference may be accepted, so use a new
    /// merchant reference for every payment attempt.
    /// </summary>
    public bool RejectsDuplicateReference { get; init; }
}
