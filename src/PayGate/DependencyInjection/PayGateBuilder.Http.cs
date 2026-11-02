namespace PayGate;

using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayGate.Providers;
using Polly;

public sealed partial class PayGateBuilder
{
    private const string _resiliencePipelineName = "paygate";

    private static readonly ProductInfoHeaderValue _userAgent = new("PayGate", LibraryVersion());

    /// <summary>
    /// Registers the HTTP client of one account, for provider packages. The client:
    /// <list type="bullet">
    /// <item>never retries a write (create, refund, cancel) unless it carries an <c>Idempotency-Key</c> header;</item>
    /// <item>retries reads on transient failures, with exponential back-off and jitter;</item>
    /// <item>applies the per-attempt and total timeouts of <see cref="PaymentHttpOptions"/>;</item>
    /// <item>logs method, host, path, status and duration only, never headers, query strings or bodies;</item>
    /// <item>sends a <c>PayGate/{version}</c> User-Agent.</item>
    /// </list>
    /// Send through it with <see cref="PaymentHttp.SendAsync"/> so that transport failures map to the right <see cref="PaymentError"/>.
    /// </summary>
    /// <param name="account">The account. The client is named <see cref="PaymentHttp.ClientName"/>.</param>
    /// <param name="configureClient">Sets the base address and any provider headers.</param>
    /// <returns>The client builder, for provider-specific handler settings such as pinning HTTP/1.1.</returns>
    public IHttpClientBuilder AddProviderHttpClient(PaymentAccount account, Action<IServiceProvider, HttpClient>? configureClient = null)
    {
        string name = PaymentHttp.ClientName(account);

        Services.AddOptions<PaymentHttpOptions>(name)
            .Validate(o => o.ReadAttemptTimeout > TimeSpan.Zero && o.WriteAttemptTimeout > TimeSpan.Zero, "Attempt timeouts must be positive.")
            .Validate(o => o.TotalTimeout >= o.ReadAttemptTimeout && o.TotalTimeout >= o.WriteAttemptTimeout, "TotalTimeout must cover each attempt timeout.")
            .Validate(o => o.MaxReadRetries is >= 0 and <= 10, "MaxReadRetries must be between 0 and 10.")
            .Validate(o => o.RetryBaseDelay >= TimeSpan.Zero, "RetryBaseDelay must not be negative.")
            .ValidateOnStart();

        var builder = Services.AddHttpClient(name, (provider, client) =>
            {
                // The resilience pipeline owns every timeout.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.Add(_userAgent);
                configureClient?.Invoke(provider, client);
            })
            .RemoveAllLoggers();

        builder.AddResilienceHandler(_resiliencePipelineName, (pipeline, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptionsMonitor<PaymentHttpOptions>>().Get(name);
            ConfigurePipeline(pipeline, options);
        });

        // Added after the resilience handler, so it sits inside it and logs every attempt.
        builder.AddHttpMessageHandler(provider => new PaymentHttpLoggingHandler(
            name,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<PaymentHttpLoggingHandler>>()));

        return builder;
    }

    /// <summary>
    /// Customizes one account's HTTP client, for example to add a proxy or a handler of the app's own. Handlers added here run
    /// inside PayGate's resilience and logging handlers.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="configure">Configures the client builder.</param>
    /// <returns>This builder.</returns>
    public PayGateBuilder ConfigureHttpClient(PaymentAccount account, Action<IHttpClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Services.AddHttpClient(PaymentHttp.ClientName(account)));
        return this;
    }

    /// <summary>Changes one account's HTTP timeouts and read retries.</summary>
    /// <param name="account">The account.</param>
    /// <param name="configure">Changes the options.</param>
    /// <returns>This builder.</returns>
    public PayGateBuilder ConfigureHttpOptions(PaymentAccount account, Action<PaymentHttpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(PaymentHttp.ClientName(account), configure);
        return this;
    }

    private static void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, PaymentHttpOptions options)
    {
        pipeline.AddTimeout(options.TotalTimeout);

        if (options.MaxReadRetries > 0)
        {
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxReadRetries,
                Delay = options.RetryBaseDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Context.GetRequestMessage() is { } request
                    && PaymentHttp.IsRetryable(request)
                    && HttpClientResiliencePredicates.IsTransient(args.Outcome)),
            });
        }

        pipeline.AddTimeout(new HttpTimeoutStrategyOptions
        {
            TimeoutGenerator = args => ValueTask.FromResult(
                args.Context.GetRequestMessage() is { } request && PaymentHttp.IsRetryable(request)
                    ? options.ReadAttemptTimeout
                    : options.WriteAttemptTimeout),
        });
    }

    private static string LibraryVersion()
    {
        string? version = typeof(PayGateBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return "0.0.0";
        }

        int metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }
}
