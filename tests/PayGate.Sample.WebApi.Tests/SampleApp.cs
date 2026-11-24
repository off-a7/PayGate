namespace PayGate.Sample.WebApi.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PayGate.Sample.WebApi.Data;
using PayGate.Sample.WebApi.Fulfillment;
using PayGate.Sample.WebApi.Payments;
using PayGate.Testing;
using PayGate.Webhooks;

/// <summary>The sample app in memory, on its own SQLite file, with the background job turned off.</summary>
internal sealed class SampleApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _database = Path.Combine(Path.GetTempPath(), $"paygate-sample-{Guid.NewGuid():N}.db");

    public CountingFulfillment Fulfillment { get; } = new();

    public FakePaymentGateway Fake => Services.GetRequiredService<FakePaymentGateway>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public HttpClient Client => field ??= CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async Task<OrderResponse> CreateOrderAsync(decimal amount = 1000m)
    {
        using var response = await Client.PostAsJsonAsync("/orders", new CreateOrderRequest("Manga box set", amount), Json, Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<OrderResponse>(Json, Token))!;
    }

    public Task<HttpResponseMessage> CheckoutAsync(int orderId, string? next = null) =>
        Client.PostAsJsonAsync($"/orders/{orderId}/checkout", new CheckoutRequest(next), Json, Token);

    public async Task<CheckoutResponse> CheckoutPaidLaterAsync(int orderId)
    {
        using var response = await CheckoutAsync(orderId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CheckoutResponse>(Json, Token))!;
    }

    public async Task<OrderResponse> GetOrderAsync(int orderId) =>
        (await Client.GetFromJsonAsync<OrderResponse>($"/orders/{orderId}", Json, Token))!;

    public async Task<AttemptResponse> AttemptAsync(int orderId, string reference) =>
        (await GetOrderAsync(orderId)).Attempts.Single(a => a.Reference == reference);

    /// <summary>Delivers the fake payment's current state to <c>/webhooks/fake</c>, the way a provider would.</summary>
    public Task<HttpResponseMessage> DeliverWebhookAsync(string providerPaymentId) => PostAsync(new FakeWebhookBuilder(Fake).ForPayment(providerPaymentId));

    public Task<HttpResponseMessage> PostAsync(WebhookRequest webhook)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/fake") { Content = new ByteArrayContent(webhook.Body.ToArray()) };
        request.Headers.Add(FakeWebhookBuilder.SignatureHeader, webhook.GetHeader(FakeWebhookBuilder.SignatureHeader));
        return Client.SendAsync(request, Token);
    }

    public Task<HttpResponseMessage> RefundAsync(int orderId, decimal? amount = null) =>
        Client.PostAsJsonAsync($"/orders/{orderId}/refunds", new RefundOrderRequest(amount, "Customer asked"), Json, Token);

    public async Task<int> ReconcileAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentReconciler>().RunOnceAsync(Token);
    }

    public async Task WithDatabaseAsync(Func<SampleDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<SampleDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_database);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Sample", $"Data Source={_database};Pooling=False");

        // Set here so that a developer's user secrets (Payments:Account = kashier for a sandbox run) never reach Kashier.
        builder.UseSetting("Payments:Account", "fake");
        builder.UseSetting("Payments:Reconciliation:Enabled", "false");
        builder.UseSetting("Payments:Reconciliation:PendingOlderThan", "00:00:00");
        builder.UseSetting("Payments:PublicBaseUrl", "https://shop.example.com/");
        builder.UseSetting("Payments:AllowedReturnUrls:0", "https://shop.example.com/account/");
        builder.ConfigureTestServices(services => services.AddSingleton<IOrderFulfillment>(Fulfillment));
    }
}

/// <summary>Counts hand-overs per order, and can fail the next one to show that a failure rolls everything back.</summary>
internal sealed class CountingFulfillment : IOrderFulfillment
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _counts = new();

    public bool FailNext { get; set; }

    public int CountFor(int orderId) => _counts.GetValueOrDefault(orderId);

    public Task FulfillAsync(Order order, CancellationToken cancellationToken)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("The warehouse is down.");
        }

        _counts.AddOrUpdate(order.Id, 1, (_, count) => count + 1);
        return Task.CompletedTask;
    }
}
