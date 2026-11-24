namespace PayGate.Testing;

using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayGate.Providers;
using PayGate.Webhooks;

/// <summary>
/// Builds Kashier payment webhooks, shaped like the ones Kashier's test environment sends and signed with the account's
/// Payment API Key, so an app can test its <see cref="IPaymentEventHandler"/> or its webhook endpoint without Kashier.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddKashier</c> confirms each webhook with Kashier before the handler runs. In a test with no Kashier to ask, either
/// turn that off for the account (<c>ConfigureWebhooks(account, o =&gt; o.ConfirmWithProvider = false)</c>) or point
/// <c>BaseUrlOverride</c> at a stub that answers the session lookup.
/// </para>
/// <para>Payout webhooks are not built yet: their signature is still to be recorded.</para>
/// </remarks>
public sealed class KashierWebhookBuilder
{
    /// <summary>The header Kashier sends the signature in.</summary>
    public const string SignatureHeader = "x-kashier-signature";

    private static readonly string[] _signedKeys =
    [
        "amount", "channel", "currency", "kashierOrderId", "merchantOrderId", "method", "orderReference", "status", "transactionId",
        "transactionResponseCode",
    ];

    private readonly string _paymentApiKey;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a builder for one Kashier account.</summary>
    /// <param name="paymentApiKey">The account's Payment API Key, as the app under test is configured with.</param>
    /// <param name="accountName">The account name, for a second Kashier merchant.</param>
    /// <param name="timeProvider">The clock for <see cref="WebhookRequest.ReceivedAt"/> and the event time. Defaults to the system clock.</param>
    public KashierWebhookBuilder(string paymentApiKey, string accountName = PaymentAccount.DefaultName, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(paymentApiKey);
        _paymentApiKey = paymentApiKey;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Account = new PaymentAccount(PaymentProviderId.Kashier, accountName);
    }

    /// <summary>The account the webhooks are for.</summary>
    public PaymentAccount Account { get; }

    /// <summary>The customer paid, and the money was captured at once (<c>pay</c> / SUCCESS).</summary>
    /// <param name="merchantReference">The payment's merchant reference.</param>
    /// <param name="amount">The amount paid.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Paid(string merchantReference, Money amount) =>
        Build(new KashierWebhook { Event = "pay", MerchantReference = merchantReference, Amount = amount });

    /// <summary>The customer's card was declined (<c>pay</c> / FAILURE, code <c>AA</c>).</summary>
    /// <param name="merchantReference">The payment's merchant reference.</param>
    /// <param name="amount">The amount tried.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Declined(string merchantReference, Money amount) =>
        Build(new KashierWebhook { Event = "pay", Status = "FAILURE", MerchantReference = merchantReference, Amount = amount });

    /// <summary>The funds are held but not captured, as with a 3-D Secure card (<c>authorize</c> / SUCCESS).</summary>
    /// <param name="merchantReference">The payment's merchant reference.</param>
    /// <param name="amount">The amount held.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Authorized(string merchantReference, Money amount) =>
        Build(new KashierWebhook { Event = "authorize", MerchantReference = merchantReference, Amount = amount });

    /// <summary>Held funds were captured (<c>capture</c> / SUCCESS).</summary>
    /// <param name="merchantReference">The payment's merchant reference.</param>
    /// <param name="amount">The amount captured.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Captured(string merchantReference, Money amount) =>
        Build(new KashierWebhook { Event = "capture", MerchantReference = merchantReference, Amount = amount });

    /// <summary>
    /// A refund succeeded (<c>refund</c> / SUCCESS). Like Kashier's, the webhook carries this refund's amount only, with no
    /// running total. Give each refund on one payment its own <see cref="KashierWebhook.TransactionId"/> through
    /// <see cref="Build"/> when two refunds have the same amount.
    /// </summary>
    /// <param name="merchantReference">The payment's merchant reference.</param>
    /// <param name="refundAmount">The amount of this refund.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Refunded(string merchantReference, Money refundAmount) =>
        Build(new KashierWebhook { Event = "refund", MerchantReference = merchantReference, Amount = refundAmount });

    /// <summary>A paid or authorized payment was voided (<c>void</c> / SUCCESS).</summary>
    /// <param name="merchantReference">The payment's merchant reference.</param>
    /// <param name="amount">The payment's amount.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Voided(string merchantReference, Money amount) =>
        Build(new KashierWebhook { Event = "void", MerchantReference = merchantReference, Amount = amount });

    /// <summary>
    /// Builds any Kashier payment webhook. Ids left unset are derived from the reference, event, status and amount, so building
    /// the same webhook twice gives the same event id, like Kashier's retry.
    /// </summary>
    /// <param name="webhook">What the webhook reports.</param>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Build(KashierWebhook webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentException.ThrowIfNullOrEmpty(webhook.Event);
        ArgumentException.ThrowIfNullOrEmpty(webhook.Status);
        ArgumentException.ThrowIfNullOrEmpty(webhook.MerchantReference);

        string amount = KashierAmount(webhook.Amount.Amount);
        string orderId = webhook.KashierOrderId ?? Derived("order", webhook.MerchantReference);
        bool failed = string.Equals(webhook.Status, "FAILURE", StringComparison.OrdinalIgnoreCase);
        var now = _timeProvider.GetUtcNow();

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = amount,
            ["channel"] = "online | e-commerce",
            ["currency"] = webhook.Amount.Currency.Code,
            ["kashierOrderId"] = orderId,
            ["merchantOrderId"] = webhook.MerchantReference,
            ["method"] = webhook.Method,
            ["orderReference"] = webhook.OrderReference ?? $"TEST-ORD-{Derived("ref", webhook.MerchantReference)[..9]}",
            ["status"] = webhook.Status,
            ["transactionId"] = webhook.TransactionId
                ?? $"TX-{Derived("tx", string.Join('|', webhook.MerchantReference, webhook.Event, webhook.Status, amount))[..12]}",
            ["transactionResponseCode"] = failed ? "AA" : "00",
        };

        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("platform", "kashier");
            json.WriteString("event", webhook.Event);
            json.WriteStartObject("data");
            foreach (string key in new[] { "merchantOrderId", "kashierOrderId", "orderReference", "transactionId", "status", "method" })
            {
                json.WriteString(key, values[key]);
            }

            json.WriteString("creationDate", (webhook.OccurredAt ?? now).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            json.WritePropertyName("amount");
            json.WriteRawValue(amount);
            json.WriteString("currency", values["currency"]);
            json.WriteStartObject("metaData");
            foreach (var (key, value) in webhook.Metadata)
            {
                json.WriteString(key, value);
            }

            json.WriteEndObject();
            json.WriteString("transactionResponseCode", values["transactionResponseCode"]);
            json.WriteStartObject("transactionResponseMessage");
            json.WriteString("en", failed ? "UNSPECIFIED FAILURE" : "Approved");
            json.WriteString("ar", failed ? "فشل غير محدد" : "تمت الموافقة");
            json.WriteEndObject();
            json.WriteString("channel", values["channel"]);
            json.WriteStartArray("signatureKeys");
            foreach (string key in _signedKeys)
            {
                json.WriteStringValue(key);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        // Kashier's scheme: the signed keys sorted, values RFC 3986-encoded as they appear in the JSON, HMAC-SHA256 hex.
        string signed = string.Join('&', _signedKeys.Order(StringComparer.Ordinal).Select(k => $"{k}={WireFormat.PercentEncode(values[k])}"));
        string signature = Convert.ToHexStringLower(Signatures.HmacSha256(_paymentApiKey, signed));

        return new WebhookRequest
        {
            Account = Account,
            Method = "POST",
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [SignatureHeader] = [signature],
                ["Content-Type"] = ["application/json"],
            },
            Body = buffer.WrittenMemory.ToArray(),
            ReceivedAt = now,
        };
    }

    /// <summary>Returns a copy of a webhook with its signed amount changed and the signature kept, as a forger would send it.</summary>
    /// <param name="webhook">A webhook from this builder.</param>
    /// <returns>The tampered webhook.</returns>
    public static WebhookRequest Tampered(WebhookRequest webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        string body = Encoding.UTF8.GetString(webhook.Body.Span);
        int start = body.IndexOf("\"amount\":", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new ArgumentException("The webhook has no amount to change.", nameof(webhook));
        }

        start += "\"amount\":".Length;
        int end = body.IndexOf(',', start);
        return webhook with { Body = Encoding.UTF8.GetBytes(string.Concat(body.AsSpan(0, start), "1", body.AsSpan(end))) };
    }

    /// <summary>A major-unit amount the way Kashier writes it: no trailing zeros (<c>50</c>, <c>75.5</c>).</summary>
    private static string KashierAmount(decimal amount) => amount.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Derived(string kind, string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{value}")));
}

/// <summary>What a webhook from <see cref="KashierWebhookBuilder"/> reports. Kashier's own names are used.</summary>
public sealed record KashierWebhook
{
    /// <summary>Kashier's event: <c>pay</c>, <c>authorize</c>, <c>capture</c>, <c>refund</c>, <c>void</c>, or any other name to test how it is handled.</summary>
    public required string Event { get; init; }

    /// <summary>Kashier's status: <c>SUCCESS</c> (the default) or <c>FAILURE</c>.</summary>
    public string Status { get; init; } = "SUCCESS";

    /// <summary>The payment's merchant reference, sent as <c>merchantOrderId</c>.</summary>
    public required string MerchantReference { get; init; }

    /// <summary>The operation's amount and currency. For a refund, the refund's amount alone.</summary>
    public required Money Amount { get; init; }

    /// <summary>The operation's transaction id, such as <c>TX-123456789</c>. Derived when unset.</summary>
    public string? TransactionId { get; init; }

    /// <summary>The Kashier order id. Derived from the merchant reference when unset.</summary>
    public string? KashierOrderId { get; init; }

    /// <summary>Kashier's order reference, such as <c>TEST-ORD-193540513</c>. Derived when unset.</summary>
    public string? OrderReference { get; init; }

    /// <summary>The payment method: <c>card</c> (the default) or <c>wallet</c>.</summary>
    public string Method { get; init; } = "card";

    /// <summary>When the operation happened, sent as <c>creationDate</c>. Defaults to the builder's clock.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    /// <summary>The metadata the app sent when creating the payment, echoed back in <c>metaData</c>.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
