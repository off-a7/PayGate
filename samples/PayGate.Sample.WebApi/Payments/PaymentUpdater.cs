namespace PayGate.Sample.WebApi.Payments;

using PayGate.Sample.WebApi.Data;
using PayGate.Sample.WebApi.Fulfillment;

/// <summary>What applying an observation did.</summary>
public enum ApplyOutcome
{
    /// <summary>Nothing new: a replay, or a status the attempt is already past.</summary>
    Unchanged = 0,

    Changed,

    /// <summary>The observation contradicts the attempt, for example a different amount. Nothing was applied.</summary>
    Rejected,
}

/// <summary>
/// The one place that changes an attempt and its order. The webhook handler, the return page, the refund
/// endpoint and the reconciliation job all go through it, so they cannot disagree. It does not save:
/// the caller saves and commits, so that the change and the inbox row land together.
/// </summary>
public sealed partial class PaymentUpdater(IOrderFulfillment fulfillment, TimeProvider timeProvider, ILogger<PaymentUpdater> logger)
{
    /// <summary>Applies what was learned. <paramref name="attempt"/> must have its order loaded.</summary>
    public async Task<ApplyOutcome> ApplyAsync(PaymentAttempt attempt, PaymentObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(observation);

        string? problem = Contradiction(attempt, observation);
        if (problem is not null)
        {
            attempt.LastError = problem;
            LogRejected(attempt.MerchantReference, observation.Source, problem);
            return ApplyOutcome.Rejected;
        }

        var now = timeProvider.GetUtcNow();
        var from = attempt.Status;
        bool changed = false;

        if (attempt.ProviderPaymentId is null && observation.ProviderPaymentId is not null)
        {
            attempt.ProviderPaymentId = observation.ProviderPaymentId;
            changed = true;
        }

        if (attempt.ProviderTransactionId is null && observation.ProviderTransactionId is not null)
        {
            attempt.ProviderTransactionId = observation.ProviderTransactionId;
            changed = true;
        }

        // A total only grows, so an old or repeated report cannot shrink it or count a refund twice.
        if (observation.RefundedTotal is { } refunded && refunded.ToMinorUnits() > attempt.RefundedMinor)
        {
            attempt.RefundedMinor = refunded.ToMinorUnits();
            changed = true;
        }

        // Core's transition rules: terminal states stick, so a late "pending" cannot undo a payment.
        if (from.CanTransitionTo(observation.Status))
        {
            attempt.Status = observation.Status;
            changed = true;
            LogMoved(attempt.MerchantReference, from, observation.Status, observation.Source);
        }

        if (changed)
        {
            attempt.UpdatedAt = now;
            attempt.LastError = null;
        }

        await UpdateOrderAsync(attempt, now, cancellationToken);
        return changed ? ApplyOutcome.Changed : ApplyOutcome.Unchanged;
    }

    private static string? Contradiction(PaymentAttempt attempt, PaymentObservation observation)
    {
        // Without this check a customer could pay 1 EGP against a 1000 EGP order, if a provider allowed it.
        if (observation.Amount is { } amount && amount != attempt.Amount)
        {
            return $"The provider reports {amount}, but the attempt is for {attempt.Amount}.";
        }

        if (observation.RefundedTotal is { } refunded && refunded.Currency != attempt.Amount.Currency)
        {
            return $"The provider reports a refund in {refunded.Currency}, but the attempt is in {attempt.Currency}.";
        }

        if (attempt.ProviderPaymentId is not null
            && observation.ProviderPaymentId is not null
            && !string.Equals(attempt.ProviderPaymentId, observation.ProviderPaymentId, StringComparison.Ordinal))
        {
            return "The provider's payment id differs from the attempt's.";
        }

        return null;
    }

    private static bool HasBeenPaid(PaymentStatus status) =>
        status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded;

    private async Task UpdateOrderAsync(PaymentAttempt attempt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var order = attempt.Order;
        if (!HasBeenPaid(attempt.Status))
        {
            return;
        }

        if (order.PaidAttemptId is null)
        {
            order.PaidAttemptId = attempt.Id;
            order.PaidAt = now;
            order.Status = OrderStatus.Paid;
        }
        else if (order.PaidAttemptId != attempt.Id)
        {
            // The customer paid twice, for example from two tabs. Only a person can sort this out.
            LogPaidTwice(order.Id, attempt.MerchantReference);
            return;
        }

        if (attempt.Status == PaymentStatus.Refunded)
        {
            order.Status = OrderStatus.Refunded;
        }

        // Fulfill once per order. If saving fails afterwards, the provider retries the webhook and
        // fulfillment runs again, which is why it must be idempotent on the order id.
        if (order.FulfilledAt is null && attempt.Status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded)
        {
            await fulfillment.FulfillAsync(order, cancellationToken);
            order.FulfilledAt = now;
        }
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Attempt {Reference} moved from {From} to {To} ({Source})")]
    private partial void LogMoved(string reference, PaymentStatus from, PaymentStatus to, string source);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Attempt {Reference} was not changed ({Source}): {Problem}")]
    private partial void LogRejected(string reference, string source, string problem);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning,
        Message = "Order {OrderId} was already paid by another attempt, and {Reference} was paid too. It needs a refund.")]
    private partial void LogPaidTwice(int orderId, string reference);
}
