namespace PayGate.Providers;

/// <summary>
/// Timeouts and read retries of one account's provider HTTP client. Named after the client
/// (<see cref="PaymentHttp.ClientName"/>); change them with <see cref="PayGateBuilder.ConfigureHttpOptions"/>.
/// </summary>
/// <remarks>
/// Writes (create, refund, cancel) are never retried, whatever these settings say, unless the request carries an
/// <c>Idempotency-Key</c> header.
/// </remarks>
public sealed class PaymentHttpOptions
{
    /// <summary>The time allowed for one attempt of a read or an idempotent write. Defaults to 10 seconds.</summary>
    public TimeSpan ReadAttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The time allowed for a write's single attempt. Defaults to 25 seconds.</summary>
    public TimeSpan WriteAttemptTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>The time allowed for a whole call, including read retries. Defaults to 30 seconds.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a read is retried after a transient failure. Defaults to 3; 0 disables read retries.</summary>
    public int MaxReadRetries { get; set; } = 3;

    /// <summary>The base delay between read retries. It grows exponentially, with jitter. Defaults to 500 milliseconds.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
