namespace PayGate.Providers;

using Microsoft.Extensions.Logging;

/// <summary>
/// Logs each provider HTTP attempt with its method, host, path, status and duration only. Never logs headers, query strings
/// or bodies, which carry keys, signatures, card data and customer details. Also strips transport exception messages, which
/// can echo the request URI, before the resilience telemetry outside this handler logs them.
/// </summary>
internal sealed partial class PaymentHttpLoggingHandler(
    string clientName,
    TimeProvider timeProvider,
    ILogger<PaymentHttpLoggingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long started = timeProvider.GetTimestamp();
        string host = request.RequestUri?.Host ?? string.Empty;
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            double elapsedMs = timeProvider.GetElapsedTime(started).TotalMilliseconds;
            LogCompleted(clientName, request.Method, host, path, (int)response.StatusCode, elapsedMs);
            return response;
        }
        catch (HttpRequestException exception)
        {
            double elapsedMs = timeProvider.GetElapsedTime(started).TotalMilliseconds;
            string errorType = exception.GetType().Name;
            LogFailed(clientName, request.Method, host, path, elapsedMs, errorType);

            // A transport exception's message can echo the request URI, query string included, and the resilience
            // telemetry above this handler logs it. Rethrow a clean one that keeps the error kind callers rely on.
            throw new HttpRequestException(
                exception.HttpRequestError,
                $"{request.Method} {host}{path} failed in transport ({exception.HttpRequestError}).",
                inner: null,
                exception.StatusCode);
        }
        catch (Exception exception)
        {
            double elapsedMs = timeProvider.GetElapsedTime(started).TotalMilliseconds;
            string errorType = exception.GetType().Name;
            LogFailed(clientName, request.Method, host, path, elapsedMs, errorType);
            throw;
        }
    }

    [LoggerMessage(EventId = 1000, EventName = "ProviderHttpCompleted", Level = LogLevel.Debug,
        Message = "{Client} {Method} {Host}{Path} responded {StatusCode} in {ElapsedMs:0} ms")]
    private partial void LogCompleted(string client, HttpMethod method, string host, string path, int statusCode, double elapsedMs);

    [LoggerMessage(EventId = 1001, EventName = "ProviderHttpFailed", Level = LogLevel.Warning,
        Message = "{Client} {Method} {Host}{Path} failed after {ElapsedMs:0} ms with {ErrorType}")]
    private partial void LogFailed(string client, HttpMethod method, string host, string path, double elapsedMs, string errorType);
}
