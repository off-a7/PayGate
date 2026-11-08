namespace PayGate.AspNetCore.Tests;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PayGate.Testing;
using PayGate.Webhooks;

public sealed class WebhookEndpointTests
{
    private const string _minimalPrefix = "/webhooks";
    private const string _controllerPrefix = "/controller-webhooks";
    private static readonly PaymentProviderId _recorder = new("recorder");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_raw_body_reaches_the_verifier_byte_for_byte_Async()
    {
        await using var host = await TestHost.StartAsync();
        byte[] body = Encoding.UTF8.GetBytes("{ \"z\" : 1,\r\n  \"a\":[ 2 ,3 ], \"name\": \"دفع\" }  \n");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/recorder/second?hmac=abc&hmac=def")
        {
            Content = new ByteArrayContent(body),
        };
        request.Headers.Add("X-Signature", "sig-value");

        using var response = await host.Client.SendAsync(request, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = host.Recorder.Requests.ShouldHaveSingleItem();
        received.Body.ToArray().ShouldBe(body);
        received.Account.ShouldBe(new PaymentAccount(_recorder, "second"));
        received.Method.ShouldBe("POST");
        received.GetHeader("x-signature").ShouldBe("sig-value");
        received.Query["HMAC"].ShouldBe(["abc", "def"]);
        received.ReceivedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task A_provider_that_calls_back_with_get_is_accepted_Async()
    {
        await using var host = await TestHost.StartAsync();

        using var response = await host.Client.GetAsync(new Uri("/webhooks/recorder?id=42", UriKind.Relative), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = host.Recorder.Requests.ShouldHaveSingleItem();
        received.Method.ShouldBe("GET");
        received.Body.Length.ShouldBe(0);
        received.GetQuery("id").ShouldBe("42");
    }

    [Theory]
    [InlineData(_minimalPrefix)]
    [InlineData(_controllerPrefix)]
    public async Task A_paid_webhook_is_applied_once_Async(string prefix)
    {
        await using var host = await TestHost.StartAsync();
        var webhook = await TestHost.PaidWebhookAsync(host.Fake);

        using var first = await host.PostAsync($"{prefix}/fake", webhook);
        using var replay = await host.PostAsync($"{prefix}/fake", webhook);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadAsByteArrayAsync(Token)).ShouldBeEmpty();
        host.Inbox.Events.ShouldHaveSingleItem().Type.ShouldBe(PaymentEventType.PaymentSucceeded);
    }

    [Theory]
    [InlineData(_minimalPrefix)]
    [InlineData(_controllerPrefix)]
    public async Task A_tampered_webhook_is_rejected_before_the_handler_Async(string prefix)
    {
        await using var host = await TestHost.StartAsync();
        var webhook = FakeWebhookBuilder.Tampered(await TestHost.PaidWebhookAsync(host.Fake));

        using var response = await host.PostAsync($"{prefix}/fake", webhook);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        host.Inbox.Events.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(_minimalPrefix)]
    [InlineData(_controllerPrefix)]
    public async Task A_body_over_the_limit_is_rejected_before_verifying_Async(string prefix)
    {
        await using var host = await TestHost.StartAsync(maxBodyBytes: 64);

        using var declared = await host.Client.PostAsync(new Uri($"{prefix}/recorder", UriKind.Relative), new ByteArrayContent(new byte[65]), Token);
        using var streamed = await host.Client.PostAsync(new Uri($"{prefix}/recorder", UriKind.Relative), new UnknownLengthContent(new byte[65]), Token);

        declared.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        streamed.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        host.Recorder.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_body_at_the_limit_is_accepted_Async()
    {
        await using var host = await TestHost.StartAsync(maxBodyBytes: 64);

        using var response = await host.Client.PostAsync(new Uri("/webhooks/recorder", UriKind.Relative), new UnknownLengthContent(new byte[64]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Recorder.Requests.ShouldHaveSingleItem().Body.Length.ShouldBe(64);
    }

    [Theory]
    [InlineData(_minimalPrefix)]
    [InlineData(_controllerPrefix)]
    public async Task Each_account_is_verified_with_its_own_secret_Async(string prefix)
    {
        await using var host = await TestHost.StartAsync();
        var webhook = await TestHost.PaidWebhookAsync(host.SecondFake);

        using var wrongAccount = await host.PostAsync($"{prefix}/fake", webhook);
        using var rightAccount = await host.PostAsync($"{prefix}/fake/second", webhook);

        wrongAccount.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        rightAccount.StatusCode.ShouldBe(HttpStatusCode.OK);
        host.Inbox.Events.ShouldHaveSingleItem().Account.Name.ShouldBe("second");
    }

    [Theory]
    [InlineData("/webhooks/fake/nobody")]
    [InlineData("/webhooks/not-registered")]
    [InlineData("/webhooks/bad!provider")]
    [InlineData("/controller-webhooks/fake/nobody")]
    [InlineData("/controller-webhooks/bad!provider")]
    public async Task An_account_that_is_not_registered_is_not_found_Async(string path)
    {
        await using var host = await TestHost.StartAsync();

        using var response = await host.Client.PostAsync(new Uri(path, UriKind.Relative), new ByteArrayContent([]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(_minimalPrefix)]
    [InlineData(_controllerPrefix)]
    public async Task A_failing_handler_answers_500_so_that_the_provider_retries_Async(string prefix)
    {
        await using var host = await TestHost.StartAsync();
        host.Inbox.Fail = true;

        using var response = await host.PostAsync($"{prefix}/fake", await TestHost.PaidWebhookAsync(host.Fake));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("database");
    }

    [Theory]
    [InlineData(_minimalPrefix)]
    [InlineData(_controllerPrefix)]
    public async Task A_providers_ack_policy_sets_the_status_and_body_Async(string prefix)
    {
        await using var host = await TestHost.StartAsync(builder => builder.AddWebhookAckPolicy<ConflictOnDuplicatePolicy>());
        var webhook = await TestHost.PaidWebhookAsync(host.Fake);
        (await host.PostAsync($"{prefix}/fake", webhook)).Dispose();

        using var replay = await host.PostAsync($"{prefix}/fake", webhook);

        replay.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await replay.Content.ReadAsStringAsync(Token)).ShouldBe("duplicate");
        replay.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("text/plain");
    }

    [Fact]
    public async Task The_prefix_can_be_changed_Async()
    {
        await using var host = await TestHost.StartAsync(prefix: "/payments/hooks");

        using var moved = await host.Client.PostAsync(new Uri("/payments/hooks/recorder", UriKind.Relative), new ByteArrayContent([]), Token);
        using var old = await host.Client.PostAsync(new Uri("/webhooks/recorder", UriKind.Relative), new ByteArrayContent([]), Token);

        moved.StatusCode.ShouldBe(HttpStatusCode.OK);
        old.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Mapping_without_registering_the_endpoint_explains_the_fix_Async()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddPayGate();
        await using var app = builder.Build();

        Should.Throw<InvalidOperationException>(() => app.MapPayGateWebhooks()).Message.ShouldContain("AddWebhookEndpoints()");
    }

    [Fact]
    public async Task A_limit_that_is_not_positive_stops_the_app_from_starting_Async()
    {
        await Should.ThrowAsync<OptionsValidationException>(async () =>
        {
            await using var host = await TestHost.StartAsync(maxBodyBytes: 0);
        });
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestHost(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public Inbox Inbox => _app.Services.GetRequiredService<Inbox>();

        public RequestRecorder Recorder => _app.Services.GetRequiredService<RequestRecorder>();

        public FakePaymentGateway Fake => _app.Services.GetRequiredService<FakePaymentGateway>();

        public FakePaymentGateway SecondFake => _app.Services.GetRequiredKeyedService<FakePaymentGateway>("fake:second");

        public static async Task<TestHost> StartAsync(
            Action<PayGateBuilder>? configure = null,
            int? maxBodyBytes = null,
            string prefix = _minimalPrefix)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<Inbox>();
            builder.Services.AddSingleton<RequestRecorder>();
            builder.Services.AddControllers().AddApplicationPart(typeof(WebhookEndpointTests).Assembly);

            var recorder = Substitute.For<IPaymentGateway>();
            var paygate = builder.Services.AddPayGate()
                .AddWebhookEndpoints(options =>
                {
                    if (maxBodyBytes is { } limit)
                    {
                        options.MaxBodyBytes = limit;
                    }
                })
                .AddEventHandler<InboxHandler>()
                .AddPayGateFake()
                .AddPayGateFake(options => options.WebhookSecret = "second-secret", accountName: "second")
                .AddGateway(new PaymentAccount(_recorder), _ => recorder)
                .AddGateway(new PaymentAccount(_recorder, "second"), _ => recorder)
                .AddWebhookVerifier<RecordingVerifier>();
            configure?.Invoke(paygate);

            var app = builder.Build();
            app.MapPayGateWebhooks(prefix);
            app.MapControllers();
            await app.StartAsync(Token);
            return new TestHost(app);
        }

        public static async Task<WebhookRequest> PaidWebhookAsync(FakePaymentGateway gateway)
        {
            var session = await gateway.CreatePaymentAsync(
                new CreatePaymentRequest { MerchantReference = $"order-{Guid.NewGuid():N}", Amount = new Money(100m, Currency.Egp) },
                Token);
            gateway.Succeed(session.Value.ProviderPaymentId);
            return new FakeWebhookBuilder(gateway).ForPayment(session.Value.ProviderPaymentId);
        }

        public Task<HttpResponseMessage> PostAsync(string path, WebhookRequest webhook)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(webhook.Body.ToArray()) };
            request.Headers.Add(FakeWebhookBuilder.SignatureHeader, webhook.GetHeader(FakeWebhookBuilder.SignatureHeader));
            return Client.SendAsync(request, Token);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Posts a body without a Content-Length, as a chunked request would.</summary>
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ConflictOnDuplicatePolicy() : WebhookAckPolicy(FakePaymentGateway.ProviderId)
    {
        public override WebhookAck GetAck(WebhookProcessingStatus status) =>
            status == WebhookProcessingStatus.AlreadyProcessed ? new WebhookAck(409, "duplicate") : base.GetAck(status);
    }
}

public sealed class RequestRecorder
{
    public List<WebhookRequest> Requests { get; } = [];
}

public sealed class Inbox
{
    public List<PaymentEvent> Events { get; } = [];

    public bool Fail { get; set; }
}

public sealed class RecordingVerifier(RequestRecorder recorder) : IWebhookVerifier
{
    public PaymentProviderId Provider { get; } = new("recorder");

    public ValueTask<WebhookParseResult> VerifyAndParseAsync(WebhookRequest request, CancellationToken cancellationToken = default)
    {
        recorder.Requests.Add(request);
        return ValueTask.FromResult(WebhookParseResult.Ignored("recorded"));
    }
}

/// <summary>Dedupes on the event id, as the docs ask apps to.</summary>
public sealed class InboxHandler(Inbox inbox) : IPaymentEventHandler
{
    public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default)
    {
        if (inbox.Fail)
        {
            throw new InvalidOperationException("The database is down.");
        }

        if (inbox.Events.Exists(e => e.EventId == paymentEvent.EventId))
        {
            return ValueTask.FromResult(WebhookHandlingOutcome.AlreadyProcessed);
        }

        inbox.Events.Add(paymentEvent);
        return ValueTask.FromResult(WebhookHandlingOutcome.Processed);
    }
}

/// <summary>A controller written the way the consuming apps write theirs.</summary>
[Route("controller-webhooks/{provider}/{account?}")]
public sealed class WebhooksController(IPayGateWebhookEndpoint webhooks) : ControllerBase
{
    [HttpPost]
    public Task<IResult> Receive() => webhooks.HandleAsync(HttpContext, HttpContext.RequestAborted);
}
