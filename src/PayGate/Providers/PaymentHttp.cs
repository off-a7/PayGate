namespace PayGate.Providers;

using System.Net;
using Polly.Timeout;

/// <summary>
/// Sends provider requests through an account's HTTP client and turns transport failures into <see cref="PaymentError"/>s,
/// so that a write that may have reached the provider is never reported as a plain failure.
/// </summary>
public static class PaymentHttp
{
    /// <summary>The header that makes a write safe to retry, for providers that honour it (Stripe).</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Returns the name of an account's HTTP client, for <see cref="IHttpClientFactory.CreateClient(string)"/>.</summary>
    /// <param name="account">The account.</param>
    /// <returns>The client name, for example <c>PayGate:kashier:secondary</c>.</returns>
    public static string ClientName(PaymentAccount account) => $"PayGate:{account}";

    /// <summary>
    /// Whether a request may be sent more than once: a read (<c>GET</c>, <c>HEAD</c>, <c>OPTIONS</c>) or a write that carries an
    /// <see cref="IdempotencyKeyHeader"/>. Only these are ever retried.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when sending the request twice cannot charge or refund twice.</returns>
    public static bool IsRetryable(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Method == HttpMethod.Get
            || request.Method == HttpMethod.Head
            || request.Method == HttpMethod.Options
            || request.Headers.Contains(IdempotencyKeyHeader);
    }

    /// <summary>Sends a request and maps transport-level outcomes to <see cref="PaymentError"/>s.</summary>
    /// <param name="client">The account's client, from <see cref="ClientName"/>.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the call. Cancellation by the caller is thrown, never mapped to an error.</param>
    /// <returns>
    /// The response, which the caller disposes, for every status the provider answered with deliberately (2xx, 3xx and 4xx
    /// other than 408 and 429): the provider package maps those bodies itself. Otherwise an error:
    /// <list type="bullet">
    /// <item>429: <see cref="PaymentErrorCode.RateLimited"/>; 408 and 503: <see cref="PaymentErrorCode.ProviderUnavailable"/>. The request was not processed.</item>
    /// <item>Another 5xx, a timeout or a broken connection: <see cref="PaymentErrorCode.ProviderUnavailable"/> for a retryable
    /// request, but <see cref="PaymentErrorCode.OutcomeUnknown"/> for a write, which may have been processed.</item>
    /// <item>A connection that could not be opened: <see cref="PaymentErrorCode.ProviderUnavailable"/>, because nothing was sent.</item>
    /// </list>
    /// </returns>
    public static async Task<PaymentResult<HttpResponseMessage>> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutRejectedException or OperationCanceledException or IOException)
        {
            return TransportFailure(request, exception);
        }

        var error = StatusFailure(request, response.StatusCode);
        if (error is null)
        {
            return PaymentResult.Success(response);
        }

        response.Dispose();
        return PaymentResult.Failure<HttpResponseMessage>(error);
    }

    private static PaymentResult<HttpResponseMessage> TransportFailure(HttpRequestMessage request, Exception exception)
    {
        string call = Describe(request);
        bool notSent = exception is HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError,
        };

        if (notSent || IsRetryable(request))
        {
            return PaymentResult.Failure<HttpResponseMessage>(new PaymentError
            {
                Code = PaymentErrorCode.ProviderUnavailable,
                Message = notSent
                    ? $"{call} could not connect to the provider ({exception.GetType().Name}). Nothing was sent."
                    : $"{call} failed in transport ({exception.GetType().Name}).",
                IsRetryable = true,
            });
        }

        return PaymentResult.Failure<HttpResponseMessage>(PaymentError.OutcomeUnknown(
            $"{call} timed out or failed in transport ({exception.GetType().Name}) after it may have reached the provider. " +
            "Look the payment up before trying again."));
    }

    private static PaymentError? StatusFailure(HttpRequestMessage request, HttpStatusCode status)
    {
        int code = (int)status;
        string call = Describe(request);
        string providerCode = code.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (status == HttpStatusCode.TooManyRequests)
        {
            return new PaymentError
            {
                Code = PaymentErrorCode.RateLimited,
                Message = $"{call} was rate-limited by the provider.",
                ProviderCode = providerCode,
                IsRetryable = true,
            };
        }

        bool notProcessed = status is HttpStatusCode.RequestTimeout or HttpStatusCode.ServiceUnavailable;
        if (notProcessed || (code >= 500 && IsRetryable(request)))
        {
            return new PaymentError
            {
                Code = PaymentErrorCode.ProviderUnavailable,
                Message = $"{call} returned {code}.",
                ProviderCode = providerCode,
                IsRetryable = true,
            };
        }

        if (code >= 500)
        {
            return PaymentError.OutcomeUnknown(
                $"{call} returned {code} after it may have been processed. Look the payment up before trying again.") with
            {
                ProviderCode = providerCode,
            };
        }

        return null;
    }

    private static string Describe(HttpRequestMessage request) => $"{request.Method} {request.RequestUri?.AbsolutePath}";
}
