namespace PayGate.Kashier.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PayGate.Kashier.Transport;

/// <summary>An app with one Kashier account, registered for real, whose HTTP client talks to a recording handler.</summary>
internal sealed class KashierHarness : IDisposable
{
    public KashierHarness(Action<KashierAccountOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        var builder = collection.AddPayGate().AddKashier(options =>
        {
            options.MerchantId = "MID-1234-567";
            options.SecretKey = "secret-key-value";
            options.PaymentApiKey = "payment-api-key-value";
            configure?.Invoke(options);
        });
        builder.ConfigureHttpClient(Account, client => client.ConfigurePrimaryHttpMessageHandler(() => Handler));
        builder.ConfigureHttpOptions(Account, o =>
        {
            o.ReadAttemptTimeout = TimeSpan.FromMilliseconds(300);
            o.WriteAttemptTimeout = TimeSpan.FromMilliseconds(300);
            o.TotalTimeout = TimeSpan.FromSeconds(5);
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
        });
        services?.Invoke(collection);
        Provider = collection.BuildServiceProvider();
    }

    public static PaymentAccount Account { get; } = new(PaymentProviderId.Kashier);

    public RecordingHandler Handler { get; } = new();

    public ServiceProvider Provider { get; }

    public KashierApiClient Client => Provider.GetRequiredKeyedService<KashierApiClient>(Account.ToString());

    public Task<PaymentResult<KashierResponse<KashierSessionResponse>>> CreateSessionAsync(KashierSessionRequest? request = null) =>
        Client.SendAsync(
            KashierHost.Api,
            HttpMethod.Post,
            "v3/payment/sessions",
            request ?? SessionRequest(),
            KashierJsonContext.Default.KashierSessionRequest,
            KashierJsonContext.Default.KashierSessionResponse,
            includeApiKey: true,
            TestContext.Current.CancellationToken);

    public Task<PaymentResult<KashierResponse<KashierOrdersResponse>>> SearchAsync(string reference) =>
        Client.SendAsync<KashierOrderOperationRequest, KashierOrdersResponse>(
            KashierHost.Api,
            HttpMethod.Get,
            $"v3/payment/orders?search={Uri.EscapeDataString(reference)}",
            null,
            null,
            KashierJsonContext.Default.KashierOrdersResponse,
            includeApiKey: false,
            TestContext.Current.CancellationToken);

    public Task<PaymentResult<KashierResponse<KashierOrderOperationResponse>>> OperateAsync(KashierOrderOperationRequest request) =>
        Client.SendAsync(
            KashierHost.Fep,
            HttpMethod.Put,
            "v3/orders/52e8cf0f-05d0-49f8-be23-fd51153b2bb6",
            request,
            KashierJsonContext.Default.KashierOrderOperationRequest,
            KashierJsonContext.Default.KashierOrderOperationResponse,
            includeApiKey: false,
            TestContext.Current.CancellationToken);

    public static KashierSessionRequest SessionRequest() => new()
    {
        MerchantId = "MID-1234-567",
        Order = "ORD-1",
        Amount = "113.34",
        Currency = "EGP",
        MerchantRedirect = "https://shop.example/payments/return",
        Display = "ar",
        MaxFailureAttempts = 3,
        Enable3DS = true,
        Customer = new KashierCustomer { Reference = "cust-1" },
        MetaData = new Dictionary<string, string> { ["orderId"] = "42" },
    };

    public void Dispose() => Provider.Dispose();
}

/// <summary>A request as the network would have seen it.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    Version Version,
    HttpVersionPolicy VersionPolicy,
    IReadOnlyDictionary<string, string> Headers,
    string? Body);

/// <summary>Records every request and plays back scripted answers in order, repeating the last one.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();
    private Func<CancellationToken, Task<HttpResponseMessage>> _last = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public RecordingHandler Respond(int status, string body) => Then(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    }));

    public RecordingHandler RespondWithFixture(string name) => Respond(Fixtures.ApiStatus(name), Fixtures.ApiResponse(name));

    public RecordingHandler Hang() => Then(async cancellationToken =>
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, request.Version, request.VersionPolicy, headers, body));

        var step = _steps.Count > 0 ? _steps.Dequeue() : _last;
        _last = step;
        return await step(cancellationToken);
    }

    private RecordingHandler Then(Func<CancellationToken, Task<HttpResponseMessage>> step)
    {
        _steps.Enqueue(step);
        return this;
    }
}
