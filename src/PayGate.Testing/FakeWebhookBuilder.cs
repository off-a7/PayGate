namespace PayGate.Testing;

using System.Text;
using System.Text.Json;
using PayGate.Providers;
using PayGate.Webhooks;

/// <summary>
/// Builds webhooks about the payments of a <see cref="FakePaymentGateway"/>, signed the way its verifier expects. Pass them to
/// <see cref="IWebhookProcessor"/> to test an app's handler.
/// </summary>
/// <param name="gateway">The gateway whose payments the webhooks report.</param>
public sealed class FakeWebhookBuilder(FakePaymentGateway gateway)
{
    /// <summary>The header that carries the signature: lowercase hex HMAC-SHA256 of the raw body.</summary>
    public const string SignatureHeader = "X-Fake-Signature";

    private readonly FakePaymentGateway _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    /// <summary>Builds a webhook reporting a payment's current state. Building it twice gives the same event id, like a provider's retry.</summary>
    /// <param name="providerPaymentId">The payment.</param>
    /// <param name="type">What the event reports. Defaults to what the payment's status implies.</param>
    /// <returns>The signed webhook.</returns>
    /// <exception cref="InvalidOperationException">The payment does not exist.</exception>
    public WebhookRequest ForPayment(string providerPaymentId, PaymentEventType? type = null) =>
        Signed(JsonSerializer.SerializeToUtf8Bytes(_gateway.Payload(providerPaymentId, type), FakeWebhookJson.Default.FakeWebhookPayload));

    /// <summary>Builds a correctly signed webhook whose body cannot be read.</summary>
    /// <returns>The signed webhook.</returns>
    public WebhookRequest Malformed() => Signed(Encoding.UTF8.GetBytes("{ not json"));

    /// <summary>Returns a copy of a webhook with one body byte changed and the signature kept.</summary>
    /// <param name="webhook">The webhook.</param>
    /// <returns>The tampered webhook.</returns>
    public static WebhookRequest Tampered(WebhookRequest webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        byte[] body = webhook.Body.ToArray();
        body[^1] ^= 1;
        return webhook with { Body = body };
    }

    private WebhookRequest Signed(byte[] body)
    {
        byte[] signature = Signatures.HmacSha256(Encoding.UTF8.GetBytes(_gateway.Options.WebhookSecret), body);
        return new WebhookRequest
        {
            Account = _gateway.Account,
            Method = "POST",
            Headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [SignatureHeader] = [Convert.ToHexStringLower(signature)],
            },
            Body = body,
            ReceivedAt = _gateway.Now,
        };
    }
}
