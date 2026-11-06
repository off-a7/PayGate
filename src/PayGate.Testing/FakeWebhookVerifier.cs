namespace PayGate.Testing;

using System.Text;
using System.Text.Json;
using PayGate.Providers;
using PayGate.Webhooks;

/// <summary>Verifies the webhooks of <see cref="FakeWebhookBuilder"/>, the way a real provider package does.</summary>
internal sealed class FakeWebhookVerifier(IPaymentCredentialsProvider<FakeGatewayOptions> credentials) : IWebhookVerifier
{
    public PaymentProviderId Provider => FakePaymentGateway.ProviderId;

    public async ValueTask<WebhookParseResult> VerifyAndParseAsync(WebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = await credentials.GetCredentialsAsync(request.Account, cancellationToken).ConfigureAwait(false);
        byte[] expected = Signatures.HmacSha256(Encoding.UTF8.GetBytes(options.WebhookSecret), request.Body.Span);
        if (!Signatures.FixedTimeEqualsHex(expected, request.GetHeader(FakeWebhookBuilder.SignatureHeader)))
        {
            return WebhookParseResult.InvalidSignature("The signature is missing or does not match.");
        }

        FakeWebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(request.Body.Span, FakeWebhookJson.Default.FakeWebhookPayload);
        }
        catch (JsonException)
        {
            return WebhookParseResult.Malformed("The body is not a fake webhook.");
        }

        if (payload is null
            || !Enum.TryParse<PaymentEventType>(payload.Type, out var type)
            || !Enum.TryParse<PaymentStatus>(payload.Status, out var status)
            || !Currency.TryFromCode(payload.Currency, out var currency))
        {
            return WebhookParseResult.Malformed("The body is not a fake webhook.");
        }

        if (type == PaymentEventType.Other)
        {
            return WebhookParseResult.Ignored("The fake reported a status with no event.");
        }

        return WebhookParseResult.Verified([new PaymentEvent
        {
            EventId = payload.EventId,
            Account = request.Account,
            Type = type,
            MerchantReference = payload.MerchantReference,
            ProviderPaymentId = payload.ProviderPaymentId,
            ProviderTransactionId = payload.ProviderTransactionId,
            Status = status,
            RawStatus = payload.RawStatus,
            Amount = Money.FromMinorUnits(payload.AmountMinor, currency),
            RefundAmount = payload.RefundMinor is { } refund ? Money.FromMinorUnits(refund, currency) : null,
            RefundedTotal = payload.RefundedTotalMinor is { } total ? Money.FromMinorUnits(total, currency) : null,
            OccurredAt = payload.OccurredAt,
            Metadata = payload.Metadata,
            RawPayload = Encoding.UTF8.GetString(request.Body.Span),
        }]);
    }
}
