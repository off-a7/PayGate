namespace PayGate.Kashier.Tests;

/// <summary>A clock that always reads the same instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
