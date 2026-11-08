namespace PayGate.AspNetCore;

using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using PayGate.Webhooks;

/// <summary>The default <see cref="IPayGateWebhookEndpoint"/>.</summary>
internal sealed class PayGateWebhookEndpoint(
    IWebhookProcessor processor,
    IOptions<PayGateWebhookEndpointOptions> options,
    TimeProvider timeProvider) : IPayGateWebhookEndpoint
{
    public const string ProviderRouteValue = "provider";
    public const string AccountRouteValue = "account";

    private const int _chunkSize = 16 * 1024;

    public Task<IResult> HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryReadAccount(context, out var account)
            ? HandleAsync(context, account, cancellationToken)
            : Task.FromResult(Results.StatusCode(StatusCodes.Status404NotFound));
    }

    public async Task<IResult> HandleAsync(HttpContext context, PaymentAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.Request;
        byte[]? body = await ReadBodyAsync(request, options.Value.MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var webhook = new WebhookRequest
        {
            Account = account,
            Method = request.Method,
            Headers = Copy(request.Headers),
            Query = Copy(request.Query),
            Body = body,
            ReceivedAt = timeProvider.GetUtcNow(),
        };

        var result = await processor.ProcessAsync(webhook, cancellationToken).ConfigureAwait(false);
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

    private static Dictionary<string, IReadOnlyList<string>> Copy(IEnumerable<KeyValuePair<string, StringValues>> values)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            copy[key] = [.. value.OfType<string>()];
        }

        return copy;
    }
}
