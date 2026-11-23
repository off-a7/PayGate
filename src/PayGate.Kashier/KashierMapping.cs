namespace PayGate.Kashier;

using System.Globalization;
using System.Net;
using PayGate.Kashier.Transport;

/// <summary>Kashier's statuses, methods and errors in PayGate's terms (docs/providers/kashier.md).</summary>
internal static class KashierMapping
{
    /// <summary>
    /// Maps a session or order status. Unknown values map to <see cref="PaymentStatus.Unknown"/>, never to a guess: the raw
    /// value is kept on the result for the app to see.
    /// </summary>
    public static PaymentStatus Status(string? raw) => raw?.Trim().ToUpperInvariant() switch
    {
        "CREATED" or "OPENED" or "INITIATED" or "PENDING" or "AUTHENTICATION_IN_PROGRESS" => PaymentStatus.Pending,
        "AUTHORIZED" => PaymentStatus.Authorized,
        "PAID" or "CAPTURED" or "SUCCESS" => PaymentStatus.Succeeded,
        "FAILED" or "FAILURE" => PaymentStatus.Failed,
        "EXPIRED" => PaymentStatus.Expired,
        "VOIDED" or "CANCELLED" or "CANCELED" => PaymentStatus.Canceled,
        "PARTIALLY_REFUNDED" => PaymentStatus.PartiallyRefunded,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Unknown,
    };

    public static PaymentMethodType Method(string? method) => method?.Trim().ToUpperInvariant() switch
    {
        "CARD" => PaymentMethodType.Card,
        "WALLET" => PaymentMethodType.Wallet,
        "INSTAPAY" => PaymentMethodType.InstaPay,
        "BASATA" or "CASH" => PaymentMethodType.CashReference,
        _ => PaymentMethodType.Other,
    };

    /// <summary>Kashier's name for a method in <c>allowedMethods</c>, or <see langword="null"/> when Kashier can't offer it here.</summary>
    public static string? AllowedMethod(PaymentMethodType method) => method switch
    {
        PaymentMethodType.Card => "card",
        PaymentMethodType.Wallet => "wallet",
        PaymentMethodType.InstaPay => "instapay",
        _ => null,
    };

    /// <summary>Kashier writes <c>NA</c> where a value doesn't exist yet.</summary>
    public static string? Value(string? raw) =>
        string.IsNullOrWhiteSpace(raw) || raw.Equals("NA", StringComparison.OrdinalIgnoreCase) ? null : raw;

    public static string? Last4(string? maskedCard)
    {
        if (string.IsNullOrEmpty(maskedCard) || maskedCard.Length < 4)
        {
            return null;
        }

        string tail = maskedCard[^4..];
        return tail.All(char.IsAsciiDigit) ? tail : null;
    }

    /// <summary>The newest declined step of a session, as a failure reason.</summary>
    public static PaymentFailureReason? Failure(KashierSessionPayment payment)
    {
        var declined = payment.History.FirstOrDefault(h => Status(h.Status) == PaymentStatus.Failed && h.TransactionResponseMessage is not null);
        return declined is null
            ? null
            : new PaymentFailureReason
            {
                Code = PaymentErrorCode.Declined,
                ProviderCode = Value(declined.TransactionResponseCode),
                Message = declined.TransactionResponseMessage?.En,
                MessageAr = declined.TransactionResponseMessage?.Ar,
            };
    }

    /// <summary>
    /// Turns a 4xx Kashier answered with into an error. A 401 means different things by call: on a refund, cancel or capture
    /// the session was just read with the same key, so the key works and the 401 is Kashier refusing the operation (an
    /// over-refund answers 401). Anywhere else it means the keys are wrong.
    /// </summary>
    public static PaymentError Error(string call, HttpStatusCode status, KashierError? error, bool afterSuccessfulRead)
    {
        int code = (int)status;
        string? providerMessage = error?.Message ?? error?.Cause;
        string providerCode = error?.Code ?? code.ToString(CultureInfo.InvariantCulture);
        string detail = providerMessage is null ? $"{call} was refused with {code}." : $"{call} was refused with {code}: {providerMessage}";

        if (error?.Code == "ERR_ORD_02")
        {
            return PaymentError.DuplicateReference($"{detail} The merchant reference was already used; use a new one per attempt.", error.Code) with
            {
                ProviderMessage = providerMessage,
            };
        }

        var errorCode = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when afterSuccessfulRead => PaymentErrorCode.Validation,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PaymentErrorCode.Authentication,
            HttpStatusCode.NotFound => PaymentErrorCode.NotFound,
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity => PaymentErrorCode.Validation,
            _ => PaymentErrorCode.ProviderError,
        };

        string message = errorCode == PaymentErrorCode.Authentication
            ? $"{detail} Check the SecretKey and PaymentApiKey, and that test keys go with Environment = Sandbox."
            : detail;
        return new PaymentError { Code = errorCode, Message = message, ProviderCode = providerCode, ProviderMessage = providerMessage };
    }
}
