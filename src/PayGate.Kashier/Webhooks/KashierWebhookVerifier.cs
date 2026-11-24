namespace PayGate.Kashier.Webhooks;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PayGate.Providers;
using PayGate.Webhooks;

/// <summary>
/// Verifies Kashier's payment webhooks and turns them into <see cref="PaymentEvent"/>s (docs/providers/kashier.md, Webhooks).
/// </summary>
/// <remarks>
/// Kashier signs fields of <c>data</c> only. The top-level <c>event</c> (pay, authorize, refund…) is <b>not signed</b>, so a
/// recorded webhook can be replayed with another event name and still verify. That is why <c>AddKashier</c> turns
/// <see cref="WebhookOptions.ConfirmWithProvider"/> on: the status the app acts on then comes from Kashier itself.
/// </remarks>
internal sealed partial class KashierWebhookVerifier(
    IPaymentCredentialsProvider<KashierAccountOptions> credentials,
    ILogger<KashierWebhookVerifier>? logger = null) : IWebhookVerifier
{
    private readonly ILogger _logger = logger ?? NullLogger<KashierWebhookVerifier>.Instance;

    public PaymentProviderId Provider => PaymentProviderId.Kashier;

    public async ValueTask<WebhookParseResult> VerifyAndParseAsync(WebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? signature = request.GetHeader(KashierSignature.WebhookHeader);
        if (string.IsNullOrWhiteSpace(signature))
        {
            return WebhookParseResult.InvalidSignature($"The {KashierSignature.WebhookHeader} header is missing.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            return WebhookParseResult.Malformed("The body is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return WebhookParseResult.Malformed("The body has no data object.");
            }

            if (!KashierSignature.TryBuildWebhookString(data, out string signed, out string? problem))
            {
                return WebhookParseResult.InvalidSignature(problem!);
            }

            var options = await credentials.GetCredentialsAsync(request.Account, cancellationToken).ConfigureAwait(false);
            if (!KashierSignature.Matches(options.PaymentApiKey, signed, signature))
            {
                return WebhookParseResult.InvalidSignature("The signature does not match.");
            }

            return Parse(request, root, data);
        }
    }

    /// <summary>
    /// What an event and its status report. <c>event</c> says what happened and <c>status</c> whether it worked, so a refund
    /// reporting SUCCESS never reads as a payment. <see langword="null"/> means nothing changed that an app acts on.
    /// </summary>
    internal static (PaymentEventType Type, PaymentStatus Status)? Classify(string eventName, string status) =>
        (eventName, KashierMapping.Status(status)) switch
        {
            // The status goes through the same table as lookups, so FAILED (the redirect's spelling) reads as FAILURE.
            ("pay" or "capture", PaymentStatus.Succeeded) => (PaymentEventType.PaymentSucceeded, PaymentStatus.Succeeded),
            ("pay" or "authorize", PaymentStatus.Failed) => (PaymentEventType.PaymentFailed, PaymentStatus.Failed),
            ("pay" or "authorize", PaymentStatus.Pending) => (PaymentEventType.PaymentPending, PaymentStatus.Pending),
            ("pay", PaymentStatus.Canceled) => (PaymentEventType.PaymentCanceled, PaymentStatus.Canceled),
            ("authorize", PaymentStatus.Succeeded or PaymentStatus.Authorized) => (PaymentEventType.PaymentAuthorized, PaymentStatus.Authorized),
            ("void" or "reversal", PaymentStatus.Succeeded) => (PaymentEventType.PaymentCanceled, PaymentStatus.Canceled),
            ("reject", PaymentStatus.Succeeded or PaymentStatus.Failed) => (PaymentEventType.PaymentFailed, PaymentStatus.Failed),

            // The webhook carries only this refund, so the status is the least it can be. ConfirmWithProvider replaces it with
            // Kashier's own, which tells a full refund from a partial one.
            ("refund" or "partial_refund", PaymentStatus.Succeeded) => (PaymentEventType.RefundSucceeded, PaymentStatus.PartiallyRefunded),

            // A refund that failed leaves the payment as it was, which the webhook doesn't say.
            ("refund" or "partial_refund", PaymentStatus.Failed) => (PaymentEventType.RefundFailed, PaymentStatus.Unknown),
            _ => null,
        };

    /// <summary>A stable id: Kashier has none, and each operation has its own transaction id.</summary>
    internal static string EventId(string transactionId, string eventName, string status)
    {
        string identity = string.Join('|', transactionId, eventName, status);
        return $"kashier_evt_{Signatures.BodyHash(Encoding.UTF8.GetBytes(identity))}";
    }

    private static string? Text(JsonElement element, string name) =>
        WireFormat.TryGetRawProperty(element, name, out string? value) ? KashierMapping.Value(value) : null;

    private static bool IsKnownEvent(string eventName) =>
        eventName is "pay" or "authorize" or "capture" or "void" or "reversal" or "reject" or "refund" or "partial_refund";

    private static ImmutableDictionary<string, string> Metadata(JsonElement data)
    {
        if (!data.TryGetProperty("metaData", out var metaData) || metaData.ValueKind != JsonValueKind.Object)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        // The app's values come back as strings. Kashier adds its own keys, some of them objects that hold personal data.
        var values = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var property in metaData.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                values[property.Name] = property.Value.GetString()!;
            }
        }

        return values.ToImmutable();
    }

    private WebhookParseResult Parse(WebhookRequest request, JsonElement root, JsonElement data)
    {
        string eventName = (Text(root, "event") ?? string.Empty).Trim().ToLowerInvariant();
        string status = (Text(data, "status") ?? string.Empty).Trim().ToUpperInvariant();

        if (eventName == "idempotency")
        {
            return WebhookParseResult.Ignored("Kashier replayed an event it already sent (idempotency).");
        }

        if (Classify(eventName, status) is not { } kind)
        {
            if (!IsKnownEvent(eventName) || KashierMapping.Status(status) == PaymentStatus.Unknown)
            {
                LogUnknownEvent(request.Account.ToString(), eventName, status);
            }

            return WebhookParseResult.Ignored($"Kashier event '{eventName}' with status '{status}' changes nothing PayGate reports.");
        }

        string? transactionId = Text(data, "transactionId");
        string? merchantOrderId = Text(data, "merchantOrderId");
        if (transactionId is null || merchantOrderId is null)
        {
            return WebhookParseResult.Malformed("The webhook has no transactionId or merchantOrderId.");
        }

        if (!Currency.TryFromCode(Text(data, "currency"), out var currency)
            || !decimal.TryParse(Text(data, "amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount)
            || amount < 0)
        {
            return WebhookParseResult.Malformed("The webhook has no usable amount and currency.");
        }

        var money = Money.Round(amount, currency);
        bool isRefund = kind.Type is PaymentEventType.RefundSucceeded or PaymentEventType.RefundFailed;
        var occurredAt = DateTimeOffset.TryParse(Text(data, "creationDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
            ? created
            : (DateTimeOffset?)null;

        return WebhookParseResult.Verified([new PaymentEvent
        {
            EventId = EventId(transactionId, eventName, status),
            Account = request.Account,
            Type = kind.Type,
            MerchantReference = merchantOrderId,

            // The webhook names the Kashier order and transaction, not the session that ProviderPaymentId stands for. Confirming
            // with the provider looks the payment up by merchant reference and fills it in.
            ProviderPaymentId = null,
            ProviderTransactionId = transactionId,
            Status = kind.Status,
            RawStatus = $"{eventName}/{status}",
            Amount = isRefund ? null : money,
            RefundAmount = isRefund ? money : null,
            OccurredAt = occurredAt ?? request.ReceivedAt,
            OccurredAtIsReceiptTime = occurredAt is null,
            Metadata = Metadata(data),

            // Kashier's body carries the Payment API Key (merchantDetails) and card tokens, so it is never passed on.
            RawPayload = null,
        }]);
    }

    [LoggerMessage(EventId = 3101, Level = LogLevel.Warning,
        Message = "Kashier account {Account} sent a webhook PayGate has no mapping for: event {EventName}, status {RawStatus}. It is acknowledged and ignored.")]
    private partial void LogUnknownEvent(string account, string eventName, string rawStatus);
}
