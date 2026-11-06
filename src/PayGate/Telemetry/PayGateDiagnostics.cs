namespace PayGate;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>PayGate's activity source and instruments.</summary>
internal static class PayGateDiagnostics
{
    public const string ProviderTag = "paygate.provider";
    public const string AccountTag = "paygate.account";
    public const string OperationTag = "paygate.operation";
    public const string OutcomeTag = "paygate.outcome";

    private static readonly ActivitySource _source = new(PayGateTelemetry.ActivitySourceName);
    private static readonly Meter _meter = new(PayGateTelemetry.MeterName);

    /// <summary>How long gateway operations take. Its count is the number of operations.</summary>
    public static Histogram<double> OperationDuration { get; } = _meter.CreateHistogram<double>(
        "paygate.gateway.operation.duration", "s", "How long gateway operations take, by provider, account, operation and outcome.");

    /// <summary>Writes whose outcome is unknown, which an operator should reconcile.</summary>
    public static Counter<long> OutcomeUnknown { get; } = _meter.CreateCounter<long>(
        "paygate.gateway.outcome_unknown", "{operation}", "Writes that may or may not have reached the provider.");

    /// <summary>How long webhooks take. Its count is the number of webhooks.</summary>
    public static Histogram<double> WebhookDuration { get; } = _meter.CreateHistogram<double>(
        "paygate.webhook.duration", "s", "How long webhooks take, by provider, account and outcome.");

    /// <summary>Webhooks with a missing or wrong signature, which may mean a forged request or a wrong secret.</summary>
    public static Counter<long> WebhookVerificationFailures { get; } = _meter.CreateCounter<long>(
        "paygate.webhook.verification_failures", "{webhook}", "Webhooks with a missing or wrong signature.");

    public static Activity? StartActivity(string name, PaymentAccount account) =>
        _source.StartActivity(name)?.SetTag(ProviderTag, account.Provider.Value).SetTag(AccountTag, account.Name);

    public static TagList Tags(PaymentAccount account) => new()
    {
        { ProviderTag, account.Provider.Value },
        { AccountTag, account.Name },
    };
}
