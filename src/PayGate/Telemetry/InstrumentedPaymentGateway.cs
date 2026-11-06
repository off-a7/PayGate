namespace PayGate;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

/// <summary>Adds traces, metrics and logs to every gateway, so that provider packages do not have to.</summary>
internal sealed partial class InstrumentedPaymentGateway(
    PaymentAccount account,
    IPaymentGateway inner,
    TimeProvider timeProvider,
    ILogger<InstrumentedPaymentGateway> logger) : IPaymentGateway
{
    private const string _success = "Success";
    private const string _canceled = "Canceled";
    private const string _exception = "Exception";

    public PaymentAccount Account => inner.Account;

    public PaymentCapabilities Capabilities => inner.Capabilities;

    public Task<PaymentResult<PaymentSession>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken = default) =>
        RunAsync("create", token => inner.CreatePaymentAsync(request, token), cancellationToken);

    public Task<PaymentResult<PaymentDetails>> GetPaymentAsync(PaymentLookup lookup, CancellationToken cancellationToken = default) =>
        RunAsync("get", token => inner.GetPaymentAsync(lookup, token), cancellationToken);

    public Task<PaymentResult<RefundResult>> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
        RunAsync("refund", token => inner.RefundAsync(request, token), cancellationToken);

    public bool TryGetFeature<TFeature>([NotNullWhen(true)] out TFeature? feature)
        where TFeature : class => inner.TryGetFeature(out feature);

    private async Task<PaymentResult<T>> RunAsync<T>(
        string operation,
        Func<CancellationToken, Task<PaymentResult<T>>> call,
        CancellationToken cancellationToken)
    {
        using var activity = PayGateDiagnostics.StartActivity($"paygate.gateway.{operation}", account);
        activity?.SetTag(PayGateDiagnostics.OperationTag, operation);

        long started = timeProvider.GetTimestamp();
        string outcome = _exception;
        try
        {
            var result = await call(cancellationToken).ConfigureAwait(false);
            outcome = result.IsSuccess ? _success : result.Error.Code.ToString();
            if (result.IsFailure)
            {
                Report(operation, result.Error.Code);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = _canceled;
            throw;
        }
        finally
        {
            activity?.SetTag(PayGateDiagnostics.OutcomeTag, outcome);
            if (outcome is not (_success or _canceled))
            {
                activity?.SetStatus(ActivityStatusCode.Error, outcome);
            }

            var tags = PayGateDiagnostics.Tags(account);
            tags.Add(PayGateDiagnostics.OperationTag, operation);
            tags.Add(PayGateDiagnostics.OutcomeTag, outcome);
            PayGateDiagnostics.OperationDuration.Record(timeProvider.GetElapsedTime(started).TotalSeconds, tags);
        }
    }

    private void Report(string operation, PaymentErrorCode code)
    {
        if (code != PaymentErrorCode.OutcomeUnknown)
        {
            LogFailed(operation, account, code);
            return;
        }

        LogOutcomeUnknown(operation, account);
        var tags = PayGateDiagnostics.Tags(account);
        tags.Add(PayGateDiagnostics.OperationTag, operation);
        PayGateDiagnostics.OutcomeUnknown.Add(1, tags);
    }

    [LoggerMessage(EventId = 1200, EventName = "GatewayOperationFailed", Level = LogLevel.Warning,
        Message = "Gateway operation {Operation} for {Account} failed with {Code}")]
    private partial void LogFailed(string operation, PaymentAccount account, PaymentErrorCode code);

    [LoggerMessage(EventId = 1201, EventName = "GatewayOutcomeUnknown", Level = LogLevel.Error,
        Message = "Gateway operation {Operation} for {Account} may or may not have reached the provider. Reconcile it")]
    private partial void LogOutcomeUnknown(string operation, PaymentAccount account);
}
