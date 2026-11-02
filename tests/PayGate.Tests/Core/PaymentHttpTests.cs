namespace PayGate.Tests.Core;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayGate.Providers;

public sealed class PaymentHttpTests
{
    private static readonly PaymentAccount _account = new(PaymentProviderId.Kashier);

    [Fact]
    public async Task A_write_that_gets_a_server_error_is_sent_once_and_reported_as_outcome_unknown_Async()
    {
        using var harness = new Harness(handler => handler.ThenStatus(HttpStatusCode.InternalServerError));

        var result = await harness.SendAsync(HttpMethod.Post, "v3/payment/sessions");

        harness.Handler.Calls.ShouldBe(1);
        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
        result.Error.IsRetryable.ShouldBeFalse();
        result.Error.ProviderCode.ShouldBe("500");
    }

    [Fact]
    public async Task A_write_that_times_out_is_sent_once_and_reported_as_outcome_unknown_Async()
    {
        using var harness = new Harness(handler => handler.ThenHang());

        var result = await harness.SendAsync(HttpMethod.Post, "v3/payment/sessions");

        harness.Handler.Calls.ShouldBe(1);
        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
    }

    [Fact]
    public async Task A_write_whose_connection_breaks_is_sent_once_and_reported_as_outcome_unknown_Async()
    {
        using var harness = new Harness(handler => handler.ThenThrow(new HttpRequestException(HttpRequestError.ResponseEnded)));

        var result = await harness.SendAsync(HttpMethod.Put, "v3/orders/1");

        harness.Handler.Calls.ShouldBe(1);
        result.Error!.Code.ShouldBe(PaymentErrorCode.OutcomeUnknown);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    public async Task A_write_that_never_connected_is_sent_once_and_reported_as_unavailable_Async(HttpRequestError error)
    {
        using var harness = new Harness(handler => handler.ThenThrow(new HttpRequestException(error)));

        var result = await harness.SendAsync(HttpMethod.Post, "v3/payment/sessions");

        harness.Handler.Calls.ShouldBe(1);
        result.Error!.Code.ShouldBe(PaymentErrorCode.ProviderUnavailable);
        result.Error.IsRetryable.ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, PaymentErrorCode.ProviderUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout, PaymentErrorCode.ProviderUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, PaymentErrorCode.RateLimited)]
    public async Task A_write_rejected_before_processing_is_sent_once_and_is_retryable_Async(HttpStatusCode status, PaymentErrorCode expected)
    {
        using var harness = new Harness(handler => handler.ThenStatus(status));

        var result = await harness.SendAsync(HttpMethod.Post, "v3/payment/sessions");

        harness.Handler.Calls.ShouldBe(1);
        result.Error!.Code.ShouldBe(expected);
        result.Error.IsRetryable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_read_is_retried_after_a_transient_failure_Async()
    {
        using var harness = new Harness(handler => handler
            .ThenStatus(HttpStatusCode.ServiceUnavailable)
            .ThenThrow(new HttpRequestException(HttpRequestError.ResponseEnded))
            .ThenStatus(HttpStatusCode.OK));

        var result = await harness.SendAsync(HttpMethod.Get, "v3/payment/orders");

        harness.Handler.Calls.ShouldBe(3);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Dispose();
    }

    [Fact]
    public async Task A_read_that_keeps_failing_is_reported_as_unavailable_after_the_retries_Async()
    {
        using var harness = new Harness(handler => handler.ThenStatus(HttpStatusCode.BadGateway));

        var result = await harness.SendAsync(HttpMethod.Get, "v3/payment/orders");

        harness.Handler.Calls.ShouldBe(1 + 3);
        result.Error!.Code.ShouldBe(PaymentErrorCode.ProviderUnavailable);
        result.Error.IsRetryable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_read_that_hangs_is_retried_and_then_reported_as_unavailable_Async()
    {
        using var harness = new Harness(handler => handler.ThenHang());

        var result = await harness.SendAsync(HttpMethod.Get, "v3/payment/orders");

        harness.Handler.Calls.ShouldBe(1 + 3);
        result.Error!.Code.ShouldBe(PaymentErrorCode.ProviderUnavailable);
    }

    [Fact]
    public async Task Read_retries_can_be_turned_off_Async()
    {
        using var harness = new Harness(handler => handler.ThenStatus(HttpStatusCode.BadGateway), o => o.MaxReadRetries = 0);

        await harness.SendAsync(HttpMethod.Get, "v3/payment/orders");

        harness.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_write_with_an_idempotency_key_is_retried_Async()
    {
        using var harness = new Harness(handler => handler
            .ThenStatus(HttpStatusCode.InternalServerError)
            .ThenStatus(HttpStatusCode.OK));

        var result = await harness.SendAsync(HttpMethod.Post, "v1/checkout/sessions", idempotencyKey: "order-1");

        harness.Handler.Calls.ShouldBe(2);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Dispose();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task Deliberate_provider_answers_are_returned_for_the_provider_package_to_map_Async(HttpStatusCode status)
    {
        using var harness = new Harness(handler => handler.ThenStatus(status));

        var result = await harness.SendAsync(HttpMethod.Post, "v3/payment/sessions");

        harness.Handler.Calls.ShouldBe(1);
        result.IsSuccess.ShouldBeTrue();
        result.Value.StatusCode.ShouldBe(status);
        result.Value.Dispose();
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_thrown_not_mapped_Async()
    {
        using var harness = new Harness(handler => handler.ThenHang());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        Exception? caught = null;
        try
        {
            await harness.SendWithTokenAsync(HttpMethod.Post, "v3/payment/sessions", cancellation.Token);
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        caught.ShouldBeAssignableTo<OperationCanceledException>();
        harness.Handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Requests_carry_a_paygate_user_agent_Async()
    {
        using var harness = new Harness(handler => handler.ThenStatus(HttpStatusCode.OK));

        (await harness.SendAsync(HttpMethod.Get, "v3/payment/orders")).Value.Dispose();

        harness.Handler.UserAgents.ShouldHaveSingleItem().ShouldStartWith("PayGate/");
    }

    [Fact]
    public async Task Logs_contain_the_call_but_never_headers_query_or_body_Async()
    {
        using var harness = new Harness(handler => handler
            .ThenStatus(HttpStatusCode.OK)
            .ThenThrow(new HttpRequestException("https://provider.test/v3/orders?apiKey=query-secret", null, HttpStatusCode.BadGateway)));

        using (var request = Harness.Request(HttpMethod.Post, "v3/payment/sessions?apiKey=query-secret"))
        {
            request.Headers.Add("Authorization", "header-secret");
            request.Content = new StringContent("""{"card":"4111111111111111","email":"mona@example.com"}""", Encoding.UTF8, "application/json");
            (await PaymentHttp.SendAsync(harness.Client, request, TestContext.Current.CancellationToken)).Value.Dispose();
        }

        await harness.SendAsync(HttpMethod.Put, "v3/orders/1?apiKey=query-secret");

        string logs = string.Join('\n', harness.Logs.Messages);
        logs.ShouldContain("POST provider.test/v3/payment/sessions responded 200");
        logs.ShouldContain("PUT provider.test/v3/orders/1 failed");
        foreach (string secret in new[] { "query-secret", "header-secret", "4111111111111111", "mona@example.com" })
        {
            harness.Logs.Messages.Where(m => m.Contains(secret, StringComparison.Ordinal)).ShouldBeEmpty($"no log line may contain {secret}");
        }
    }

    [Fact]
    public void Invalid_http_options_fail_at_startup()
    {
        using var harness = new Harness(handler => handler.ThenStatus(HttpStatusCode.OK), o => o.TotalTimeout = TimeSpan.FromMilliseconds(1));

        Should.Throw<OptionsValidationException>(() => harness.Provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("TotalTimeout");
    }

    [Theory]
    [InlineData("GET", null, true)]
    [InlineData("HEAD", null, true)]
    [InlineData("OPTIONS", null, true)]
    [InlineData("POST", null, false)]
    [InlineData("PUT", null, false)]
    [InlineData("DELETE", null, false)]
    [InlineData("POST", "order-1", true)]
    public void Only_reads_and_idempotent_writes_are_retryable(string method, string? idempotencyKey, bool expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://provider.test/");
        if (idempotencyKey is not null)
        {
            request.Headers.Add(PaymentHttp.IdempotencyKeyHeader, idempotencyKey);
        }

        PaymentHttp.IsRetryable(request).ShouldBe(expected);
    }

    [Fact]
    public void Client_names_follow_the_account()
    {
        PaymentHttp.ClientName(new PaymentAccount(PaymentProviderId.Kashier, "secondary")).ShouldBe("PayGate:kashier:secondary");
    }

    /// <summary>An account's real HTTP client, with a scripted primary handler in place of the network and short timeouts.</summary>
    private sealed class Harness : IDisposable
    {
        private HttpClient? _client;

        public Harness(Action<ScriptedHandler> script, Action<PaymentHttpOptions>? options = null)
        {
            script(Handler);

            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            var builder = services.AddPayGate();
            builder.AddProviderHttpClient(_account, (_, client) => client.BaseAddress = new Uri("https://provider.test/"));
            builder.ConfigureHttpClient(_account, client => client.ConfigurePrimaryHttpMessageHandler(() => Handler));
            builder.ConfigureHttpOptions(_account, o =>
            {
                o.ReadAttemptTimeout = TimeSpan.FromMilliseconds(200);
                o.WriteAttemptTimeout = TimeSpan.FromMilliseconds(200);
                o.TotalTimeout = TimeSpan.FromSeconds(5);
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                options?.Invoke(o);
            });

            Provider = services.BuildServiceProvider();
        }

        public ScriptedHandler Handler { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        public ServiceProvider Provider { get; }

        /// <summary>Created on first use, so a test can check startup validation before any client exists.</summary>
        public HttpClient Client => _client ??= Provider.GetRequiredService<IHttpClientFactory>().CreateClient(PaymentHttp.ClientName(_account));

        public static HttpRequestMessage Request(HttpMethod method, string path) => new(method, path);

        /// <summary>Sends with the test's own cancellation token.</summary>
        public Task<PaymentResult<HttpResponseMessage>> SendAsync(HttpMethod method, string path, string? idempotencyKey = null) =>
            SendWithTokenAsync(method, path, TestContext.Current.CancellationToken, idempotencyKey);

        public async Task<PaymentResult<HttpResponseMessage>> SendWithTokenAsync(
            HttpMethod method,
            string path,
            CancellationToken cancellationToken,
            string? idempotencyKey = null)
        {
            using var request = Request(method, path);
            if (idempotencyKey is not null)
            {
                request.Headers.Add(PaymentHttp.IdempotencyKeyHeader, idempotencyKey);
            }

            return await PaymentHttp.SendAsync(Client, request, cancellationToken);
        }

        public void Dispose()
        {
            _client?.Dispose();
            Provider.Dispose();
        }
    }

    /// <summary>Plays back scripted outcomes in order, repeating the last one once the script runs out.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _steps = new();
        private Func<CancellationToken, Task<HttpResponseMessage>>? _last;
        private int _calls;

        public int Calls => _calls;

        public ConcurrentQueue<string> UserAgents { get; } = new();

        public ScriptedHandler ThenStatus(HttpStatusCode status) => Then(_ => Task.FromResult(new HttpResponseMessage(status)));

        public ScriptedHandler ThenThrow(Exception exception) => Then(_ => Task.FromException<HttpResponseMessage>(exception));

        public ScriptedHandler ThenHang() => Then(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            UserAgents.Enqueue(request.Headers.UserAgent.ToString());
            var step = _steps.Count > 0 ? _steps.Dequeue() : _last!;
            _last = step;
            return step(cancellationToken);
        }

        private ScriptedHandler Then(Func<CancellationToken, Task<HttpResponseMessage>> step)
        {
            _steps.Enqueue(step);
            return this;
        }
    }

    /// <summary>Keeps every formatted log message, including exception text, so tests can check nothing secret leaks.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
