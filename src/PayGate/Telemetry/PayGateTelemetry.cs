namespace PayGate;

/// <summary>
/// The names to subscribe to in OpenTelemetry, such as <c>tracing.AddSource(PayGateTelemetry.ActivitySourceName)</c> and
/// <c>metrics.AddMeter(PayGateTelemetry.MeterName)</c>. Tags carry the provider, account, operation and outcome only, never
/// amounts, customer details, references or secrets.
/// </summary>
public static class PayGateTelemetry
{
    /// <summary>The name of PayGate's <see cref="System.Diagnostics.ActivitySource"/>.</summary>
    public const string ActivitySourceName = "PayGate";

    /// <summary>The name of PayGate's <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string MeterName = "PayGate";
}
