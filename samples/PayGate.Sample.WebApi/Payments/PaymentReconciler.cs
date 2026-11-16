namespace PayGate.Sample.WebApi.Payments;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayGate.Sample.WebApi.Data;

/// <summary>
/// Asks the provider about attempts that are still open after a while. Webhooks get lost: the app was
/// down, the tunnel closed, or the provider gave up retrying. This is what catches those payments.
/// </summary>
public sealed partial class PaymentReconciler(
    SampleDbContext db,
    PaymentUpdater updater,
    IPaymentGatewayResolver gateways,
    IOptions<SamplePaymentOptions> options,
    TimeProvider timeProvider,
    ILogger<PaymentReconciler> logger)
{
    /// <summary>Statuses that can still change on the provider's side without the app doing anything.</summary>
    private static readonly PaymentStatus[] _open =
    [
        PaymentStatus.Unknown,
        PaymentStatus.Pending,
        PaymentStatus.RequiresAction,
        PaymentStatus.Processing,
        PaymentStatus.Authorized,
    ];

    /// <summary>Runs one pass and returns how many attempts changed.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Reconciliation;
        var now = timeProvider.GetUtcNow();
        var youngest = now - settings.PendingOlderThan;
        var oldest = now - settings.StopAfter;

        var due = await db.PaymentAttempts
            .Include(a => a.Order)
            .Where(a => _open.Contains(a.Status) && a.CreatedAt <= youngest && a.CreatedAt >= oldest)
            .OrderBy(a => a.CreatedAt)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        int changed = 0;
        foreach (var attempt in due)
        {
            if (await ReconcileAsync(attempt, cancellationToken) == ApplyOutcome.Changed)
            {
                changed++;
            }
        }

        return changed;
    }

    private async Task<ApplyOutcome> ReconcileAsync(PaymentAttempt attempt, CancellationToken cancellationToken)
    {
        if (!gateways.TryGetGateway(attempt.PaymentAccount, out var gateway))
        {
            LogSkipped(attempt.MerchantReference, "its account is no longer registered");
            return ApplyOutcome.Unchanged;
        }

        // An attempt whose creation timed out has no provider id: find it by our own reference instead.
        var lookup = attempt.ProviderPaymentId is { } id
            ? PaymentLookup.ByProviderPaymentId(id)
            : gateway.Capabilities.LookupByMerchantReference ? PaymentLookup.ByMerchantReference(attempt.MerchantReference) : null;
        if (lookup is null)
        {
            LogSkipped(attempt.MerchantReference, "the provider cannot look it up by merchant reference");
            return ApplyOutcome.Unchanged;
        }

        var result = await gateway.GetPaymentAsync(lookup, cancellationToken);
        PaymentObservation observation;
        if (result.IsSuccess)
        {
            observation = PaymentObservation.FromDetails(result.Value, "reconciliation");
        }
        else if (result.Error.Code == PaymentErrorCode.NotFound && attempt.ProviderPaymentId is null)
        {
            // Creating it timed out and the provider never got it. The customer can press "Pay" again.
            observation = new PaymentObservation { Status = PaymentStatus.Failed, Source = "reconciliation" };
        }
        else
        {
            LogLookupFailed(attempt.MerchantReference, result.Error.Code);
            return ApplyOutcome.Unchanged;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var outcome = await updater.ApplyAsync(attempt, observation, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    [LoggerMessage(EventId = 3300, Level = LogLevel.Information, Message = "Reconciliation skipped {Reference}: {Reason}")]
    private partial void LogSkipped(string reference, string reason);

    [LoggerMessage(EventId = 3303, Level = LogLevel.Warning, Message = "Reconciliation could not look up {Reference}: {Code}")]
    private partial void LogLookupFailed(string reference, PaymentErrorCode code);
}

/// <summary>Runs <see cref="PaymentReconciler"/> on a timer, each pass in its own scope.</summary>
public sealed partial class ReconciliationService(
    IServiceScopeFactory scopes,
    IOptions<SamplePaymentOptions> options,
    TimeProvider timeProvider,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Reconciliation;
        if (!settings.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(settings.Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                int changed = await scope.ServiceProvider.GetRequiredService<PaymentReconciler>().RunOnceAsync(stoppingToken);
                if (changed > 0)
                {
                    LogPass(changed);
                }
            }
#pragma warning disable CA1031 // One failed pass must not stop the job; the next tick tries again.
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                LogFailed(exception);
            }
        }
    }

    [LoggerMessage(EventId = 3301, Level = LogLevel.Information, Message = "Reconciliation changed {Count} payment attempts")]
    private partial void LogPass(int count);

    [LoggerMessage(EventId = 3302, Level = LogLevel.Error, Message = "A reconciliation pass failed")]
    private partial void LogFailed(Exception exception);
}
