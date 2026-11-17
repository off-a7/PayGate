namespace PayGate.Sample.WebApi.Payments;

using PayGate.Sample.WebApi.Data;

public sealed record CreateOrderRequest(string Description, decimal Amount, string Currency = "EGP");

/// <summary>
/// <paramref name="Next"/> is where the customer goes after the return page: a local path such as
/// <c>/orders/1</c>, or an address under <c>Payments:AllowedReturnUrls</c>.
/// </summary>
public sealed record CheckoutRequest(string? Next);

/// <summary>Leave <paramref name="Amount"/> out to refund everything that is left.</summary>
public sealed record RefundOrderRequest(decimal? Amount, string? Reason);

public sealed record OrderResponse(
    int Id,
    string Description,
    decimal Amount,
    string Currency,
    OrderStatus Status,
    DateTimeOffset? PaidAt,
    DateTimeOffset? FulfilledAt,
    IReadOnlyList<AttemptResponse> Attempts)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.Description,
        order.Amount.Amount,
        order.Currency,
        order.Status,
        order.PaidAt,
        order.FulfilledAt,
        [.. order.Attempts.OrderBy(a => a.Number).Select(AttemptResponse.From)]);
}

public sealed record AttemptResponse(
    string Reference,
    string Account,
    PaymentStatus Status,
    string? ProviderPaymentId,
    decimal Refunded,
    string? LastError)
{
    public static AttemptResponse From(PaymentAttempt attempt) => new(
        attempt.MerchantReference,
        attempt.Account,
        attempt.Status,
        attempt.ProviderPaymentId,
        attempt.Refunded.Amount,
        attempt.LastError);
}

/// <summary>What the front end needs to send the customer to pay, whichever flow the provider uses.</summary>
public sealed record CheckoutResponse(
    string Reference,
    PaymentStatus Status,
    NextActionKind NextAction,
    Uri? RedirectUrl,
    string? PaymentCode,
    DateTimeOffset? ExpiresAt,
    string? ClientSecret,
    string? PublicKey)
{
    public static CheckoutResponse From(PaymentAttempt attempt, PaymentSession session) => session.NextAction switch
    {
        RedirectAction redirect => new(attempt.MerchantReference, attempt.Status, redirect.Kind, redirect.Url, null, session.ExpiresAt, null, null),
        DisplayReferenceAction code => new(attempt.MerchantReference, attempt.Status, code.Kind, null, code.Code, code.ExpiresAt ?? session.ExpiresAt, null, null),

        // The client secret is meant for the customer's browser: the provider's SDK needs it there.
        ClientSdkAction sdk => new(attempt.MerchantReference, attempt.Status, sdk.Kind, null, null, session.ExpiresAt, sdk.ClientSecret, sdk.PublicKey),
        _ => new(attempt.MerchantReference, attempt.Status, session.NextAction.Kind, null, null, session.ExpiresAt, null, null),
    };
}

public sealed record ReturnResponse(int OrderId, string Reference, PaymentStatus Status, OrderStatus OrderStatus);

public sealed record RefundResponse(string RefundReference, RefundStatus RefundStatus, decimal Refunded, PaymentStatus Status);

public sealed record PendingResponse(string Reference, string Message);
