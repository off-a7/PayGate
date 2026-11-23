namespace PayGate.Kashier.Transport;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PayGate.Providers;

/// <summary>Which Kashier host a call goes to.</summary>
internal enum KashierHost
{
    /// <summary>Sessions and order inquiry.</summary>
    Api,

    /// <summary>Capture, refund, void and transfers.</summary>
    Fep,
}

/// <summary>
/// Sends requests for one Kashier account through its PayGate HTTP client (no write retries, redacted logging, timeouts),
/// adds Kashier's headers and reads the JSON. It never logs or keeps a body: Kashier echoes the Payment API Key and card
/// tokens in its responses.
/// </summary>
internal sealed class KashierApiClient(
    PaymentAccount account,
    IHttpClientFactory httpClientFactory,
    IPaymentCredentialsProvider<KashierAccountOptions> credentials)
{
    public PaymentAccount Account { get; } = account;

    /// <summary>Sends a call and reads Kashier's answer.</summary>
    /// <param name="host">The host the path belongs to.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="pathAndQuery">The path, relative to the host, such as <c>v3/payment/sessions</c>.</param>
    /// <param name="body">The request body, or <see langword="null"/>.</param>
    /// <param name="bodyType">The body's JSON metadata; required with a body.</param>
    /// <param name="responseType">The 2xx body's JSON metadata.</param>
    /// <param name="includeApiKey">Whether to send the Payment API Key as <c>api-key</c> (session create only).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    /// Kashier's answer for every 2xx and 4xx (other than 408 and 429), with the body read; otherwise the transport error from
    /// <see cref="PaymentHttp.SendAsync"/>, or a <see cref="PaymentErrorCode.Configuration"/> error before anything is sent.
    /// </returns>
    public async Task<PaymentResult<KashierResponse<TResponse>>> SendAsync<TRequest, TResponse>(
        KashierHost host,
        HttpMethod method,
        string pathAndQuery,
        TRequest? body,
        JsonTypeInfo<TRequest>? bodyType,
        JsonTypeInfo<TResponse> responseType,
        bool includeApiKey,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(responseType);

        var options = await credentials.GetCredentialsAsync(Account, cancellationToken).ConfigureAwait(false);
        var problems = KashierAccountOptionsValidator.Check(options);
        if (problems.Count > 0)
        {
            return PaymentResult.Failure<KashierResponse<TResponse>>(new PaymentError
            {
                Code = PaymentErrorCode.Configuration,
                Message = $"The Kashier account '{Account}' is not usable: {string.Join(" ", problems)}",
            });
        }

        var baseUrl = host == KashierHost.Fep ? options.FepBaseUrl : options.ApiBaseUrl;
        using var request = new HttpRequestMessage(method, new Uri(baseUrl, pathAndQuery))
        {
            // Kashier's test host drops HTTP/2 connections.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        // Kashier wants the raw key, without a scheme, so it bypasses header validation.
        request.Headers.TryAddWithoutValidation("Authorization", options.SecretKey);
        if (includeApiKey)
        {
            request.Headers.TryAddWithoutValidation("api-key", options.PaymentApiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            ArgumentNullException.ThrowIfNull(bodyType);
            request.Content = JsonContent.Create(body, bodyType);
        }

        var client = httpClientFactory.CreateClient(PaymentHttp.ClientName(Account));
        var sent = await PaymentHttp.SendAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (sent.IsFailure)
        {
            return PaymentResult.Failure<KashierResponse<TResponse>>(sent.Error);
        }

        using var response = sent.Value;
        string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            return PaymentResult.Success(new KashierResponse<TResponse>(status, null, KashierError.Parse(text)));
        }

        try
        {
            var value = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize(text, responseType);
            if (value is not null)
            {
                return PaymentResult.Success(new KashierResponse<TResponse>(status, value, null));
            }
        }
        catch (JsonException)
        {
            // Reported below without the body, which may hold secrets.
        }

        string call = $"{method} {request.RequestUri?.AbsolutePath}";
        return PaymentResult.Failure<KashierResponse<TResponse>>(PaymentHttp.IsRetryable(request)
            ? new PaymentError
            {
                Code = PaymentErrorCode.ProviderError,
                Message = $"{call} returned {(int)status} with a body PayGate could not read.",
                ProviderCode = ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
            }
            : PaymentError.OutcomeUnknown(
                $"{call} returned {(int)status} with a body PayGate could not read, so whether it took effect is unknown. " +
                "Look the payment up before trying again."));
    }
}

/// <summary>Kashier's answer to a call that reached it.</summary>
/// <typeparam name="T">The 2xx body type.</typeparam>
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="Body">The 2xx body, or <see langword="null"/> for a 4xx.</param>
/// <param name="Error">The 4xx body, or <see langword="null"/> for a 2xx.</param>
internal sealed record KashierResponse<T>(HttpStatusCode StatusCode, T? Body, KashierError? Error)
    where T : class
{
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Body))]
    public bool IsSuccess => Body is not null;
}

/// <summary>
/// A 4xx body. Kashier uses several shapes: <c>{"message":…}</c>, <c>{"error":"Authorization error","message":…}</c> and
/// <c>{"error":{"cause":…},"messages":{"en":…,"ar":…}}</c>. Only these text fields are kept, never the rest of the body.
/// </summary>
/// <param name="Message">The English message.</param>
/// <param name="MessageAr">The Arabic message, when Kashier sends one.</param>
/// <param name="Cause">The <c>error</c> field: a short label or the cause.</param>
/// <param name="Code">A Kashier error code such as <c>ERR_ORD_02</c>, when one appears.</param>
internal sealed record KashierError(string? Message, string? MessageAr, string? Cause, string? Code)
{
    public static KashierError Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new KashierError(null, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new KashierError(null, null, null, FindCode(text));
            }

            string? message = String(root, "message");
            string? messageAr = null;
            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Object)
            {
                message ??= String(messages, "en");
                messageAr = String(messages, "ar");
            }

            string? cause = null;
            if (root.TryGetProperty("error", out var error))
            {
                cause = error.ValueKind switch
                {
                    JsonValueKind.String => error.GetString(),
                    JsonValueKind.Object => String(error, "cause") ?? String(error, "message"),
                    _ => null,
                };
            }

            return new KashierError(message, messageAr, cause, String(root, "code") ?? String(root, "errorCode") ?? FindCode(text));
        }
        catch (JsonException)
        {
            return new KashierError(null, null, null, FindCode(text));
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Finds an <c>ERR_…</c> code anywhere in the body, wherever Kashier puts it.</summary>
    private static string? FindCode(string text)
    {
        int start = text.IndexOf("ERR_", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        int end = start + 4;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        return text[start..end];
    }
}
