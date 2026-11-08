namespace PayGate.Tests.Core;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PayGate.Webhooks;

/// <summary>Each test uses its own account, because the listeners see the telemetry of tests running in parallel.</summary>
public sealed class TelemetryTests
{
    private const string _secretReference = "customer-order-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_names_apps_subscribe_to_are_stable()
    {
        PayGateTelemetry.ActivitySourceName.ShouldBe("PayGate");
        PayGateTelemetry.MeterName.ShouldBe("PayGate");
    }

    [Fact]
    public async Task A_gateway_call_is_traced_and_measured_Async()
    {
        using var capture = new Capture("traced");
        var inner = Gateway();
        inner.GetPaymentAsync(Arg.Any<PaymentLookup>(), Arg.Any<CancellationToken>()).Returns(Details());
        using var provider = Build(capture.Account, inner);

        await Resolve(provider, capture.Account).GetPaymentAsync(PaymentLookup.ByMerchantReference(_secretReference), Token);

        var activity = capture.Activities.ShouldHaveSingleItem();
        activity.OperationName.ShouldBe("paygate.gateway.get");
        activity.GetTagItem("paygate.provider").ShouldBe("kashier");
        activity.GetTagItem("paygate.operation").ShouldBe("get");
        activity.GetTagItem("paygate.outcome").ShouldBe("Success");
        activity.Status.ShouldBe(ActivityStatusCode.Unset);

        var measurement = capture.Measurements.ShouldHaveSingleItem();
        measurement.Instrument.ShouldBe("paygate.gateway.operation.duration");
        measurement.Tags["paygate.outcome"].ShouldBe("Success");
    }

    [Fact]
    public async Task Tags_never_carry_payment_data_Async()
    {
        using var capture = new Capture("private");
        var inner = Gateway();
        inner.GetPaymentAsync(Arg.Any<PaymentLookup>(), Arg.Any<CancellationToken>()).Returns(Details());
        using var provider = Build(capture.Account, inner);

        await Resolve(provider, capture.Account).GetPaymentAsync(PaymentLookup.ByMerchantReference(_secretReference), Token);

        capture.Activities.SelectMany(a => a.Tags).ShouldNotContain(tag => tag.Value != null && tag.Value.Contains(_secretReference));
        capture.Measurements.SelectMany(m => m.Tags.Values).ShouldNotContain(_secretReference);
    }

    [Fact]
    public async Task An_outcome_unknown_write_is_counted_and_marks_the_span_as_failed_Async()
    {
        using var capture = new Capture("unknown");
        var inner = Gateway();
        inner.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure<RefundResult>(PaymentError.OutcomeUnknown("timed out")));
        using var provider = Build(capture.Account, inner);

        await Resolve(provider, capture.Account).RefundAsync(new RefundRequest { ProviderPaymentId = "pay-1" }, Token);

        capture.Activities.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        capture.Measurements.Single(m => m.Instrument == "paygate.gateway.outcome_unknown").Tags["paygate.operation"].ShouldBe("refund");
        capture.Measurements.Single(m => m.Instrument == "paygate.gateway.operation.duration").Tags["paygate.outcome"].ShouldBe("OutcomeUnknown");
    }

    [Fact]
    public async Task A_gateway_exception_is_recorded_and_rethrown_Async()
    {
        using var capture = new Capture("exception");
        var inner = Gateway();
        inner.CreatePaymentAsync(Arg.Any<CreatePaymentRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));
        using var provider = Build(capture.Account, inner);

        await Should.ThrowAsync<InvalidOperationException>(() => Resolve(provider, capture.Account).CreatePaymentAsync(
            new CreatePaymentRequest { MerchantReference = "a", Amount = new Money(1m, Currency.Egp) }, Token));

        capture.Measurements.ShouldHaveSingleItem().Tags["paygate.outcome"].ShouldBe("Exception");
    }

    [Fact]
    public async Task A_cancelled_call_is_not_marked_as_failed_Async()
    {
        using var capture = new Capture("canceled");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var inner = Gateway();
        inner.GetPaymentAsync(Arg.Any<PaymentLookup>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cancellation.Token));
        using var provider = Build(capture.Account, inner);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Resolve(provider, capture.Account).GetPaymentAsync(PaymentLookup.ByProviderPaymentId("pay-1"), cancellation.Token));

        capture.Activities.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Unset);
        capture.Measurements.ShouldHaveSingleItem().Tags["paygate.outcome"].ShouldBe("Canceled");
    }

    [Fact]
    public async Task A_rejected_webhook_counts_as_a_verification_failure_Async()
    {
        using var capture = new Capture("forged");
        using var provider = Build(capture.Account, Gateway(), WebhookParseResult.InvalidSignature("bad"));

        await Process(provider, capture.Account);

        capture.Measurements.Count(m => m.Instrument == "paygate.webhook.verification_failures").ShouldBe(1);
        capture.Measurements.Single(m => m.Instrument == "paygate.webhook.duration").Tags["paygate.outcome"].ShouldBe("InvalidSignature");
        capture.Activities.ShouldHaveSingleItem().OperationName.ShouldBe("paygate.webhook.verify");
    }

    [Fact]
    public async Task A_dispatched_webhook_has_a_verify_and_a_dispatch_span_Async()
    {
        using var capture = new Capture("dispatched");
        var paymentEvent = new PaymentEvent
        {
            EventId = "evt-1",
            Account = capture.Account,
            Type = PaymentEventType.PaymentSucceeded,
            Status = PaymentStatus.Succeeded,
            OccurredAt = DateTimeOffset.UnixEpoch,
        };
        using var provider = Build(capture.Account, Gateway(), WebhookParseResult.Verified([paymentEvent]));

        await Process(provider, capture.Account);

        capture.Activities.Select(a => a.OperationName).ShouldBe(["paygate.webhook.verify", "paygate.webhook.dispatch"], ignoreOrder: true);
        capture.Activities.Single(a => a.OperationName == "paygate.webhook.dispatch").GetTagItem("paygate.outcome").ShouldBe("Processed");
    }

    [Fact]
    public async Task A_failing_handler_marks_the_dispatch_span_as_failed_Async()
    {
        using var capture = new Capture("failing");
        var paymentEvent = new PaymentEvent
        {
            EventId = "evt-1",
            Account = capture.Account,
            Type = PaymentEventType.PaymentSucceeded,
            Status = PaymentStatus.Succeeded,
            OccurredAt = DateTimeOffset.UnixEpoch,
        };
        using var provider = Build(capture.Account, Gateway(), WebhookParseResult.Verified([paymentEvent]), handler: new ThrowingHandler());

        var result = await Process(provider, capture.Account);

        result.Status.ShouldBe(WebhookProcessingStatus.Failed);
        var span = capture.Activities.Single(a => a.OperationName == "paygate.webhook.dispatch");
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe(nameof(InvalidOperationException));
    }

    private static IPaymentGateway Resolve(ServiceProvider provider, PaymentAccount account) =>
        provider.GetRequiredService<IPaymentGatewayResolver>().GetGateway(account);

    private static Task<WebhookProcessingResult> Process(ServiceProvider provider, PaymentAccount account) =>
        provider.GetRequiredService<IWebhookProcessor>().ProcessAsync(
            new WebhookRequest { Account = account, Method = "POST", ReceivedAt = DateTimeOffset.UnixEpoch },
            Token);

    private static ServiceProvider Build(
        PaymentAccount account,
        IPaymentGateway gateway,
        WebhookParseResult? parsed = null,
        IPaymentEventHandler? handler = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ParsedWebhook(parsed ?? WebhookParseResult.Ignored("none")));
        services.AddSingleton(handler ?? new AcceptingHandler());
        services.AddPayGate().AddGateway(account, _ => gateway).AddWebhookVerifier<StubVerifier>();
        return services.BuildServiceProvider();
    }

    private static IPaymentGateway Gateway()
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.Capabilities.Returns(new PaymentCapabilities());
        return gateway;
    }

    private static PaymentResult<PaymentDetails> Details() => PaymentResult.Success(new PaymentDetails
    {
        ProviderPaymentId = "pay-1",
        Status = PaymentStatus.Pending,
        RawStatus = "PENDING",
        Amount = new Money(1m, Currency.Egp),
        RefundedAmount = Money.Zero(Currency.Egp),
    });

    private sealed record ParsedWebhook(WebhookParseResult Result);

    private sealed record Measurement(string Instrument, Dictionary<string, string?> Tags);

    private sealed class StubVerifier(ParsedWebhook parsed) : IWebhookVerifier
    {
        public PaymentProviderId Provider => PaymentProviderId.Kashier;

        public ValueTask<WebhookParseResult> VerifyAndParseAsync(WebhookRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(parsed.Result);
    }

    private sealed class AcceptingHandler : IPaymentEventHandler
    {
        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(WebhookHandlingOutcome.Processed);
    }

    private sealed class ThrowingHandler : IPaymentEventHandler
    {
        public ValueTask<WebhookHandlingOutcome> HandleAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    /// <summary>Records the spans and measurements of one account.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _activities = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        public Capture(string accountName)
        {
            Account = new PaymentAccount(PaymentProviderId.Kashier, accountName);

            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PayGateTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (Equals(activity.GetTagItem("paygate.account"), Account.Name))
                    {
                        _activities.Enqueue(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == PayGateTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
            _meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
            _meterListener.Start();
        }

        public PaymentAccount Account { get; }

        public IReadOnlyList<Activity> Activities => [.. _activities];

        public IReadOnlyList<Measurement> Measurements => [.. _measurements];

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }

        private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                values[tag.Key] = tag.Value?.ToString();
            }

            if (values.GetValueOrDefault("paygate.account") == Account.Name)
            {
                _measurements.Enqueue(new Measurement(instrument.Name, values));
            }
        }
    }
}
