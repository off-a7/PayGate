namespace PayGate.Kashier.Transport;

using System.Text.Json;
using System.Text.Json.Serialization;

// Kashier's wire format, as recorded in tests/fixtures/kashier. Only the fields PayGate reads are declared; unknown
// fields are ignored and numbers may arrive as strings, so a new field or a quoted amount never breaks a read.

/// <summary><c>POST {api}/v3/payment/sessions</c>.</summary>
internal sealed class KashierSessionRequest
{
    public required string MerchantId { get; init; }

    /// <summary>The merchant reference.</summary>
    public required string Order { get; init; }

    /// <summary>A major-unit decimal string, always written with the invariant culture.</summary>
    public required string Amount { get; init; }

    public required string Currency { get; init; }

    public required string MerchantRedirect { get; init; }

    /// <summary><c>one-time</c> or <c>external</c>.</summary>
    public string Type { get; init; } = "one-time";

    public string? PaymentType { get; init; }

    public string? Display { get; init; }

    public string? AllowedMethods { get; init; }

    public int? MaxFailureAttempts { get; init; }

    /// <summary>UTC, ISO 8601 with milliseconds, such as <c>2030-01-28T17:27:32.359Z</c>.</summary>
    public string? ExpireAt { get; init; }

    public bool? FailureRedirect { get; init; }

    public string? ServerWebhook { get; init; }

    public string? Description { get; init; }

    public bool? Enable3DS { get; init; }

    public bool? ManualCapture { get; init; }

    public string? SaveCard { get; init; }

    public string? InteractionSource { get; init; }

    public KashierCustomer? Customer { get; init; }

    public IReadOnlyDictionary<string, string>? MetaData { get; init; }
}

internal sealed class KashierCustomer
{
    public string? Reference { get; init; }

    public string? Email { get; init; }
}

/// <summary>The session-create response. It also echoes the Payment API Key, which is deliberately not declared.</summary>
internal sealed class KashierSessionResponse
{
    [JsonPropertyName("_id")]
    public string? Id { get; init; }

    public string? Status { get; init; }

    public string? SessionUrl { get; init; }

    public DateTimeOffset? ExpireAt { get; init; }
}

/// <summary><c>GET {api}/v3/payment/orders?search=</c>: a partial match, newest first.</summary>
internal sealed class KashierOrdersResponse
{
    public string? Status { get; init; }

    public string? Message { get; init; }

    public IReadOnlyList<KashierOrder> Data { get; init; } = [];
}

internal sealed class KashierOrder
{
    /// <summary>The id refunds, voids and captures go to, sent as <c>kashierOrderId</c> in webhooks.</summary>
    public string? OrderId { get; init; }

    public string? MerchantOrderId { get; init; }

    public string? SessionId { get; init; }

    public string? OrderReference { get; init; }

    /// <summary><c>CAPTURED</c>, <c>AUTHORIZED</c>, <c>FAILED</c>, <c>PARTIALLY_REFUNDED</c>, <c>REFUNDED</c>, <c>CANCELLED</c>…</summary>
    public string? Status { get; init; }

    public string? Method { get; init; }

    public decimal? TotalCapturedAmount { get; init; }

    public decimal? TotalRefundedAmount { get; init; }

    public decimal? TotalAuthorizedAmount { get; init; }

    public KashierOrderAmount? Order { get; init; }

    public KashierSourceOfFunds? SourceOfFunds { get; init; }

    public IReadOnlyDictionary<string, JsonElement>? MetaData { get; init; }

    /// <summary>Every step, including 3-D Secure ones; the payment is the <c>pay</c>, <c>authorize</c> or <c>capture</c> row.</summary>
    public IReadOnlyList<KashierTransaction> Transactions { get; init; } = [];

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

internal sealed class KashierOrderAmount
{
    public decimal? Amount { get; init; }

    public string? Currency { get; init; }
}

internal sealed class KashierSourceOfFunds
{
    public KashierCardInfo? CardInfo { get; init; }
}

internal sealed class KashierCardInfo
{
    public string? MaskedCard { get; init; }

    public string? CardBrand { get; init; }
}

internal sealed class KashierTransaction
{
    public string? Operation { get; init; }

    public string? TransactionId { get; init; }

    public string? Status { get; init; }

    public decimal? Amount { get; init; }

    public string? Currency { get; init; }

    public string? TransactionResponseCode { get; init; }

    public KashierMessage? TransactionResponseMessage { get; init; }

    public DateTimeOffset? RequestDate { get; init; }

    public DateTimeOffset? ResponseDate { get; init; }
}

internal sealed class KashierMessage
{
    public string? En { get; init; }

    public string? Ar { get; init; }
}

/// <summary><c>PUT {fep}/v3/orders/{orderId}</c> for <c>CAPTURE</c>, <c>REFUND</c> or <c>VOID</c>.</summary>
internal sealed class KashierOrderOperationRequest
{
    public required string ApiOperation { get; init; }

    public string? Reason { get; init; }

    /// <summary>Omitted to act on the whole remainder.</summary>
    public KashierOperationTransaction? Transaction { get; init; }
}

internal sealed class KashierOperationTransaction
{
    public decimal? Amount { get; init; }

    public string? TargetTransactionId { get; init; }
}

/// <summary>The synchronous answer to a capture, refund or void: the outcome and the order's new totals.</summary>
internal sealed class KashierOrderOperationResponse
{
    /// <summary><c>SUCCESS</c> when the operation went through.</summary>
    public string? Status { get; init; }

    public KashierMessage? Messages { get; init; }

    public KashierOperationResult? Response { get; init; }
}

internal sealed class KashierOperationResult
{
    /// <summary>The order's status after the operation, such as <c>PARTIALLY_REFUNDED</c>.</summary>
    public string? Status { get; init; }

    public string? Operation { get; init; }

    /// <summary>The amount of this operation.</summary>
    public decimal? Amount { get; init; }

    public string? Currency { get; init; }

    public string? TransactionId { get; init; }

    public string? OrderId { get; init; }

    public string? MerchantOrderId { get; init; }

    public decimal? TotalCapturedAmount { get; init; }

    public decimal? TotalRefundedAmount { get; init; }

    public string? TransactionResponseCode { get; init; }

    public KashierMessage? TransactionResponseMessage { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KashierSessionRequest))]
[JsonSerializable(typeof(KashierSessionResponse))]
[JsonSerializable(typeof(KashierOrdersResponse))]
[JsonSerializable(typeof(KashierOrderOperationRequest))]
[JsonSerializable(typeof(KashierOrderOperationResponse))]
internal sealed partial class KashierJsonContext : JsonSerializerContext;
