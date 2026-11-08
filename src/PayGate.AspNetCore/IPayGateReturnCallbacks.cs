namespace PayGate.AspNetCore;

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using PayGate.Webhooks;

/// <summary>
/// Reads the customer's browser returning from the provider's payment page, for the app's own return endpoint.
/// </summary>
/// <remarks>
/// A return is never proof of payment: anyone can type the URL. Show the customer that the payment is being confirmed, call
/// <see cref="IPaymentGateway.GetPaymentAsync"/>, and fulfil only from verified webhooks or lookups.
/// </remarks>
public interface IPayGateReturnCallbacks
{
    /// <summary>Verifies the query signature, where the provider signs one, and reads what the return claims.</summary>
    /// <param name="request">The return request.</param>
    /// <param name="account">The account the return is for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the return claims. Untrusted, even when its signature is valid.</returns>
    /// <exception cref="InvalidOperationException">The account's provider registered no return parser.</exception>
    ValueTask<ReturnCallbackResult> ParseAsync(HttpRequest request, PaymentAccount account, CancellationToken cancellationToken);

    /// <summary>
    /// Checks a URL the app is about to redirect to, such as a <c>returnUrl</c> query parameter, so that the app cannot be used
    /// to send customers to another site. A local path such as <c>/orders/42</c> is allowed; an absolute URL only when it is under
    /// one of <see cref="PayGateWebhookEndpointOptions.AllowedReturnUrls"/>.
    /// </summary>
    /// <param name="candidate">The URL to check.</param>
    /// <param name="url">The URL to redirect to, when allowed.</param>
    /// <returns><see langword="true"/> when the URL is safe to redirect to.</returns>
    bool TryGetSafeReturnUrl(string? candidate, [NotNullWhen(true)] out Uri? url);
}
