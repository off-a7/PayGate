namespace PayGate.Webhooks;

/// <summary>
/// Runs a received webhook through PayGate: resolve the account, verify and parse, optionally confirm with the provider,
/// then dispatch to the app's <see cref="IPaymentEventHandler"/>. Framework-agnostic; the ASP.NET Core package calls it.
/// </summary>
public interface IWebhookProcessor
{
    /// <summary>Processes one webhook.</summary>
    /// <param name="request">The webhook as received.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The outcome and the response to send. A failure is reported here, not thrown.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<WebhookProcessingResult> ProcessAsync(WebhookRequest request, CancellationToken cancellationToken = default);
}
