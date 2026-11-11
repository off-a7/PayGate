namespace PayGate.AspNetCore;

using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayGate.Webhooks;

/// <summary>The default <see cref="IPayGateWebhookEndpoint"/>.</summary>
internal sealed partial class PayGateWebhookEndpoint(
    IWebhookProcessor processor,
    IOptions<PayGateWebhookEndpointOptions> options,
    TimeProvider timeProvider,
    ILogger<PayGateWebhookEndpoint> logger) : IPayGateWebhookEndpoint
{
    public const string ProviderRouteValue = "provider";
    public const string AccountRouteValue = "account";

    private const int _chunkSize = 16 * 1024;
    private const string _tooLarge = "TooLarge";
    private const string _unparsableRoute = "UnparsableRoute";

    public Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (TryReadAccount(context, out var account))
        {
            return HandleAsync(context, account, cancellationToken);
        }

        // The segments are not logged: they are whatever the caller typed.
        LogRejected(_unparsableRoute, StatusCodes.Status404NotFound);
        return Task.FromResult(Results.StatusCode(StatusCodes.Status404NotFound));
    }

    public async Task<IResult> HandleAsync(HttpContext context, PaymentAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        long started = timeProvider.GetTimestamp();
        var request = context.Request;
        byte[]? body = await ReadBodyAsync(request, options.Value.MaxBodyBytesFor(account.Provider), cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            Log(account, _tooLarge, StatusCodes.Status413PayloadTooLarge, eventIds: [], started);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var webhook = new WebhookRequest
        {
            Account = account,
            Method = request.Method,
            Headers = RequestValues.Copy(request.Headers),
            Query = RequestValues.Copy(request.Query),
            Body = body,
            ReceivedAt = timeProvider.GetUtcNow(),
        };

        var result = await processor.ProcessAsync(webhook, cancellationToken).ConfigureAwait(false);
        Log(account, result.Status.ToString(), result.Ack.StatusCode, result.EventIds, started);
        return result.Ack.Body is null
            ? Results.StatusCode(result.Ack.StatusCode)
            : Results.Text(result.Ack.Body, "text/plain; charset=utf-8", statusCode: result.Ack.StatusCode);
    }

    private static bool TryReadAccount(HttpContext context, out PaymentAccount account)
    {
        account = default;
        if (context.GetRouteValue(ProviderRouteValue) is not string provider)
        {
            return false;
        }

        string name = context.GetRouteValue(AccountRouteValue) as string ?? PaymentAccount.DefaultName;
        try
        {
            account = new PaymentAccount(new PaymentProviderId(provider), name);
            return true;
        }
        catch (ArgumentException)
        {
            // A segment no account can have, such as "kashier!" or an empty name.
            return false;
        }
    }

    /// <summary>Reads the body exactly as sent. Returns <see langword="null"/> as soon as it exceeds the limit.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        int capacity = (int)Math.Min(request.ContentLength ?? _chunkSize, limit);
        using var buffer = new MemoryStream(capacity);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(_chunkSize);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    /// <summary>The one line per webhook: who sent it, what came of it, which events, and how long it took. Never the payload.</summary>
    private void Log(PaymentAccount account, string outcome, int statusCode, IReadOnlyList<string> eventIds, long started)
    {
        double elapsedMs = timeProvider.GetElapsedTime(started).TotalMilliseconds;
        string events = eventIds.Count == 0 ? "none" : string.Join(',', eventIds);
        bool accepted = outcome is nameof(WebhookProcessingStatus.Processed)
            or nameof(WebhookProcessingStatus.AlreadyProcessed)
            or nameof(WebhookProcessingStatus.Ignored);

        if (accepted)
        {
            LogReceived(account, outcome, statusCode, events, elapsedMs);
        }
        else
        {
            LogNotAccepted(account, outcome, statusCode, events, elapsedMs);
        }
    }

    [LoggerMessage(EventId = 2000, EventName = "WebhookReceived", Level = LogLevel.Information,
        Message = "Webhook for {Account} was {Outcome} and answered {StatusCode} in {ElapsedMs:0} ms; events {EventIds}")]
    private partial void LogReceived(PaymentAccount account, string outcome, int statusCode, string eventIds, double elapsedMs);

    [LoggerMessage(EventId = 2001, EventName = "WebhookNotAccepted", Level = LogLevel.Warning,
        Message = "Webhook for {Account} was {Outcome} and answered {StatusCode} in {ElapsedMs:0} ms; events {EventIds}")]
    private partial void LogNotAccepted(PaymentAccount account, string outcome, int statusCode, string eventIds, double elapsedMs);

    [LoggerMessage(EventId = 2002, EventName = "WebhookRouteRejected", Level = LogLevel.Warning,
        Message = "A webhook was {Outcome} and answered {StatusCode}: its provider or account segment is not a valid id")]
    private partial void LogRejected(string outcome, int statusCode);
}
