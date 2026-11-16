namespace PayGate.Sample.WebApi.Fulfillment;

using PayGate.Sample.WebApi.Data;

/// <summary>
/// Hands the goods over. Called at least once per paid order, so an implementation must be idempotent
/// on the order id: a retry after a crash calls it again for the same order.
/// </summary>
public interface IOrderFulfillment
{
    Task FulfillAsync(Order order, CancellationToken cancellationToken);
}

/// <summary>The sample's stub: it only writes a log line.</summary>
public sealed partial class LoggingOrderFulfillment(ILogger<LoggingOrderFulfillment> logger) : IOrderFulfillment
{
    public Task FulfillAsync(Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        LogFulfilled(order.Id, order.Amount);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "Order {OrderId} fulfilled for {Amount}")]
    private partial void LogFulfilled(int orderId, Money amount);
}
