namespace PayGate.Kashier.Webhooks;

using PayGate.Webhooks;

/// <summary>
/// Reads the customer's browser coming back from Kashier's payment page (docs/providers/kashier.md, Redirect back to the
/// merchant). The redirect is informational and may never come, so nothing here moves an order.
/// </summary>
internal sealed class KashierReturnParser(IPaymentCredentialsProvider<KashierAccountOptions> credentials) : IPaymentReturnParser
{
    public PaymentProviderId Provider => PaymentProviderId.Kashier;

    public async ValueTask<ReturnCallbackResult> ParseAsync(ReturnCallbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? merchantOrderId = KashierMapping.Value(request.GetQuery("merchantOrderId"));
        string? signature = request.GetQuery(KashierSignature.ReturnParameter);
        string signed = KashierSignature.BuildReturnString(request.GetQuery);

        var options = await credentials.GetCredentialsAsync(request.Account, cancellationToken).ConfigureAwait(false);
        bool valid = merchantOrderId is not null && KashierSignature.Matches(options.PaymentApiKey, signed, signature);

        return new ReturnCallbackResult
        {
            Account = request.Account,
            Verification = valid ? ReturnCallbackVerification.Valid : ReturnCallbackVerification.Invalid,
            MerchantReference = merchantOrderId,

            // The query names the Kashier order, not the session that ProviderPaymentId stands for.
            ProviderPaymentId = null,

            // A tampered query claims nothing. A valid one still only indicates: the redirect says FAILED where the webhook
            // says FAILURE, and both map to Failed.
            IndicatedStatus = valid && request.GetQuery("paymentStatus") is { } status ? KashierMapping.Status(status) : null,
        };
    }
}
