namespace PayGate.Webhooks;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>The default <see cref="IWebhookProcessor"/>.</summary>
internal sealed partial class WebhookProcessor : IWebhookProcessor
{
    private readonly IPaymentGatewayResolver _gateways;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WebhookOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookProcessor> _logger;
    private readonly Dictionary<PaymentProviderId, IWebhookVerifier> _verifiers = [];
    private readonly Dictionary<PaymentProviderId, WebhookAckPolicy> _ackPolicies = [];

    /// <remarks>The logger is optional because PayGate does not require the app to have added logging.</remarks>
    public WebhookProcessor(
        IPaymentGatewayResolver gateways,
        IEnumerable<IWebhookVerifier> verifiers,
        IEnumerable<WebhookAckPolicy> ackPolicies,
        IServiceScopeFactory scopes,
        IOptionsMonitor<WebhookOptions> options,
        TimeProvider timeProvider,
        ILogger<WebhookProcessor>? logger = null)
    {
        _gateways = gateways;
        _scopes = scopes;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<WebhookProcessor>.Instance;

        // Last registration wins, so an app can replace a provider's verifier or policy.
        foreach (var verifier in verifiers)
        {
            _verifiers[verifier.Provider] = verifier;
        }

        foreach (var policy in ackPolicies)
        {
            _ackPolicies[policy.Provider] = policy;
        }
    }

    public async Task<WebhookProcessingResult> ProcessAsync(WebhookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        long started = _timeProvider.GetTimestamp();
        var account = request.Account;
        var completed = new List<string>();

        (var status, string? reason) = await RunAsync(request, completed, cancellationToken).ConfigureAwait(false);

        var tags = PayGateDiagnostics.Tags(account);
        tags.Add(PayGateDiagnostics.OutcomeTag, status.ToString());
        PayGateDiagnostics.WebhookDuration.Record(_timeProvider.GetElapsedTime(started).TotalSeconds, tags);

        var policy = _ackPolicies.GetValueOrDefault(account.Provider) ?? new WebhookAckPolicy(account.Provider);
        return new WebhookProcessingResult(status, policy.GetAck(status), completed, reason);
    }

    private static WebhookProcessingStatus Combine(WebhookProcessingStatus status, WebhookHandlingOutcome outcome) => outcome switch
    {
        WebhookHandlingOutcome.Processed => WebhookProcessingStatus.Processed,
        WebhookHandlingOutcome.AlreadyProcessed when status != WebhookProcessingStatus.Processed => WebhookProcessingStatus.AlreadyProcessed,
        _ => status,
    };

    private static async Task<(PaymentEvent? Confirmed, string? Failure)> ConfirmAsync(
        IPaymentGateway gateway,
        PaymentEvent paymentEvent,
        CancellationToken cancellationToken)
    {
        var lookup = LookupFor(gateway, paymentEvent);
        if (lookup is null)
        {
            return (null, "The event has no id the provider can be asked about.");
        }

        var result = await gateway.GetPaymentAsync(lookup, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return (null, $"Confirming with the provider failed with {result.Error.Code}.");
        }

        var details = result.Value;
        if (Differs(paymentEvent.ProviderPaymentId, details.ProviderPaymentId)
            || Differs(paymentEvent.MerchantReference, details.MerchantReference))
        {
            return (null, "The provider's payment does not match the event.");
        }

        if (details.Status == PaymentStatus.Unknown)
        {
            return (null, "The provider reported a status PayGate does not know.");
        }

        bool isRefund = paymentEvent.Type is PaymentEventType.RefundSucceeded or PaymentEventType.RefundFailed;
        return (paymentEvent with
        {
            Status = details.Status,
            RawStatus = details.RawStatus,
            Amount = details.Amount,
            RefundedTotal = isRefund ? details.RefundedAmount : paymentEvent.RefundedTotal,
            ProviderPaymentId = details.ProviderPaymentId,
            ProviderTransactionId = paymentEvent.ProviderTransactionId ?? details.ProviderTransactionId,
            MerchantReference = paymentEvent.MerchantReference ?? details.MerchantReference,
        }, null);
    }

    private static PaymentLookup? LookupFor(IPaymentGateway gateway, PaymentEvent paymentEvent)
    {
        if (!string.IsNullOrWhiteSpace(paymentEvent.ProviderPaymentId))
        {
            return PaymentLookup.ByProviderPaymentId(paymentEvent.ProviderPaymentId);
        }

        return gateway.Capabilities.LookupByMerchantReference && !string.IsNullOrWhiteSpace(paymentEvent.MerchantReference)
            ? PaymentLookup.ByMerchantReference(paymentEvent.MerchantReference)
            : null;
    }

    private static bool Differs(string? fromEvent, string? fromProvider) =>
        fromEvent is not null && fromProvider is not null && !string.Equals(fromEvent, fromProvider, StringComparison.Ordinal);

    private static async Task<WebhookParseResult> VerifyAsync(IWebhookVerifier verifier, WebhookRequest request, CancellationToken cancellationToken)
    {
        using var activity = PayGateDiagnostics.StartActivity("paygate.webhook.verify", request.Account);
        var parsed = await verifier.VerifyAndParseAsync(request, cancellationToken).ConfigureAwait(false);
        activity?.SetTag(PayGateDiagnostics.OutcomeTag, parsed.Kind.ToString());
        if (parsed.Kind is WebhookParseKind.InvalidSignature or WebhookParseKind.Malformed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, parsed.Kind.ToString());
        }

        return parsed;
    }

    private async Task<(WebhookProcessingStatus Status, string? Reason)> RunAsync(
        WebhookRequest request,
        List<string> completed,
        CancellationToken cancellationToken)
    {
        var account = request.Account;
        string? currentEventId = null;
        try
        {
            if (!_gateways.TryGetGateway(account, out var gateway))
            {
                LogRejected(account, WebhookProcessingStatus.UnknownAccount);
                return (WebhookProcessingStatus.UnknownAccount, $"No gateway is registered for '{account}'.");
            }

            if (!_verifiers.TryGetValue(account.Provider, out var verifier))
            {
                throw new InvalidOperationException($"No webhook verifier is registered for provider '{account.Provider}'.");
            }

            var parsed = await VerifyAsync(verifier, request, cancellationToken).ConfigureAwait(false);
            switch (parsed.Kind)
            {
                case WebhookParseKind.Verified:
                    break;
                case WebhookParseKind.InvalidSignature:
                    PayGateDiagnostics.WebhookVerificationFailures.Add(1, PayGateDiagnostics.Tags(account));
                    LogRejected(account, WebhookProcessingStatus.InvalidSignature);
                    return (WebhookProcessingStatus.InvalidSignature, parsed.Reason);
                case WebhookParseKind.Ignored:
                    return (WebhookProcessingStatus.Ignored, parsed.Reason);
                default:
                    LogRejected(account, WebhookProcessingStatus.Malformed);
                    return (WebhookProcessingStatus.Malformed, parsed.Reason);
            }

            bool confirm = _options.Get(account.ToString()).ConfirmWithProvider;
            var status = WebhookProcessingStatus.Ignored;
            foreach (var received in parsed.Events)
            {
                if (received.Type == PaymentEventType.Other)
                {
                    continue;
                }

                currentEventId = received.EventId;
                var paymentEvent = received;
                if (confirm)
                {
                    (var confirmed, string? failure) = await ConfirmAsync(gateway, received, cancellationToken).ConfigureAwait(false);
                    if (confirmed is null)
                    {
                        LogNotConfirmed(account, received.EventId, failure);
                        return (WebhookProcessingStatus.Failed, failure);
                    }

                    paymentEvent = confirmed;
                }

                var outcome = await DispatchAsync(paymentEvent, cancellationToken).ConfigureAwait(false);
                completed.Add(received.EventId);
                status = Combine(status, outcome);
            }

            return (status, null);
        }
#pragma warning disable CA1031 // A failure of any kind must reach the provider as a 5xx, so that it retries the webhook.
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            LogFailed(exception, account, currentEventId);
            return (WebhookProcessingStatus.Failed, "Processing the webhook failed. See the logs.");
        }
    }

    private async Task<WebhookHandlingOutcome> DispatchAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        using var activity = PayGateDiagnostics.StartActivity("paygate.webhook.dispatch", paymentEvent.Account);
        activity?.SetTag("paygate.event.type", paymentEvent.Type.ToString());

        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            try
            {
                var handler = scope.ServiceProvider.GetRequiredService<IPaymentEventHandler>();
                var outcome = await handler.HandleAsync(paymentEvent, cancellationToken).ConfigureAwait(false);
                activity?.SetTag(PayGateDiagnostics.OutcomeTag, outcome.ToString());
                return outcome;
            }
            catch (Exception exception)
            {
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                throw;
            }
        }
    }

    [LoggerMessage(EventId = 1100, EventName = "WebhookFailed", Level = LogLevel.Error,
        Message = "Processing a webhook for {Account} failed at event {EventId}")]
    private partial void LogFailed(Exception exception, PaymentAccount account, string? eventId);

    [LoggerMessage(EventId = 1101, EventName = "WebhookNotConfirmed", Level = LogLevel.Warning,
        Message = "Event {EventId} for {Account} was not dispatched: {Reason}")]
    private partial void LogNotConfirmed(PaymentAccount account, string eventId, string? reason);

    [LoggerMessage(EventId = 1102, EventName = "WebhookRejected", Level = LogLevel.Warning,
        Message = "A webhook for {Account} was rejected as {Status}")]
    private partial void LogRejected(PaymentAccount account, WebhookProcessingStatus status);
}
