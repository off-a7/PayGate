namespace PayGate.Sample.WebApi.Payments;

using System.Text;
using Microsoft.EntityFrameworkCore;
using PayGate.Providers;
using PayGate.Sample.WebApi.Data;
using PayGate.Webhooks;

/// <summary>
/// The app's half of a webhook. PayGate has already checked the signature and parsed the event;
/// this records it in the inbox, checks it against the attempt, applies it and fulfills the order, in one
/// transaction. Throwing makes PayGate answer 500, so the provider delivers the webhook again.
/// </summary>
public sealed partial class PaymentWebhookHandler(
    SampleDbContext db,
    PaymentUpdater updater,
    TimeProvider timeProvider,
    ILogger<PaymentWebhookHandler> logger) : IPaymentEventHandler
{
    public async ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentEvent);
        string provider = paymentEvent.Account.Provider.Value;

        // 1. Dedupe. The unique index below is what really guarantees it; this check only saves the work.
        if (await db.WebhookInbox.AnyAsync(e => e.Provider == provider && e.EventId == paymentEvent.EventId, cancellationToken))
        {
            return WebhookHandlingOutcome.AlreadyProcessed;
        }

        var now = timeProvider.GetUtcNow();
        var entry = new WebhookInboxEntry
        {
            Provider = provider,
            Account = paymentEvent.Account.ToString(),
            EventId = paymentEvent.EventId,
            EventType = paymentEvent.Type.ToString(),
            MerchantReference = paymentEvent.MerchantReference,
            BodyHash = paymentEvent.RawPayload is null ? null : Signatures.BodyHash(Encoding.UTF8.GetBytes(paymentEvent.RawPayload)),
            ReceivedAt = now,
            ProcessedAt = now,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.WebhookInbox.Add(entry);

        var outcome = WebhookHandlingOutcome.Processed;
        var attempt = await FindAttemptAsync(paymentEvent, cancellationToken);
        if (attempt is null)
        {
            // Not ours, or from another app on the same provider account. Answer 200 so it is not retried forever.
            entry.Error = "No payment attempt matches the event.";
            LogNoAttempt(paymentEvent.EventId, paymentEvent.Account);
            outcome = WebhookHandlingOutcome.Ignored;
        }
        else
        {
            // 2 and 3. Check the amount and currency, then move the status by Core's rules.
            // 4. Fulfill when the order became paid, inside the same transaction.
            var applied = await updater.ApplyAsync(attempt, PaymentObservation.FromEvent(paymentEvent), cancellationToken);
            if (applied == ApplyOutcome.Rejected)
            {
                entry.Error = attempt.LastError;
                outcome = WebhookHandlingOutcome.Ignored;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SampleDbContext.IsUniqueViolation(exception))
        {
            // The same event was being handled at the same time and the other delivery won.
            return WebhookHandlingOutcome.AlreadyProcessed;
        }

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    private Task<PaymentAttempt?> FindAttemptAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken)
    {
        // Only attempts made on the account the webhook was verified for: another account's secret did not sign it.
        string account = paymentEvent.Account.ToString();
        var attempts = db.PaymentAttempts.Include(a => a.Order).Where(a => a.Account == account);
        if (paymentEvent.MerchantReference is { } reference)
        {
            return attempts.FirstOrDefaultAsync(a => a.MerchantReference == reference, cancellationToken);
        }

        return paymentEvent.ProviderPaymentId is { } providerPaymentId
            ? attempts.FirstOrDefaultAsync(a => a.ProviderPaymentId == providerPaymentId, cancellationToken)
            : Task.FromResult<PaymentAttempt?>(null);
    }

    [LoggerMessage(EventId = 3200, Level = LogLevel.Warning, Message = "Event {EventId} for {Account} matches no payment attempt")]
    private partial void LogNoAttempt(string eventId, PaymentAccount account);
}
