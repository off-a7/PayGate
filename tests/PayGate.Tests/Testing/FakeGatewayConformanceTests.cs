namespace PayGate.Tests.Testing;

using Microsoft.Extensions.DependencyInjection;
using PayGate.Conformance;
using PayGate.Testing;
using PayGate.Webhooks;

/// <summary>Runs the conformance suite against the fake, through the same registration an app uses.</summary>
public abstract class FakeGatewayConformanceTests : PaymentGatewayConformanceTests, IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly FakePaymentGateway _fake;

    protected FakeGatewayConformanceTests(FakeFlow flow, bool idempotentWrites = false)
    {
        var services = new ServiceCollection();
        services.AddPayGate().AddPayGateFake(options =>
        {
            options.Flow = flow;
            options.IdempotentWrites = idempotentWrites;
        });

        _provider = services.BuildServiceProvider(validateScopes: true);
        _fake = _provider.GetRequiredService<FakePaymentGateway>();
        Gateway = _provider.GetRequiredService<IPaymentGatewayResolver>().GetGateway(_fake.Account);
        Verifier = _provider.GetRequiredService<IEnumerable<IWebhookVerifier>>().Single();
    }

    protected override IPaymentGateway Gateway { get; }

    protected override IWebhookVerifier Verifier { get; }

    protected override IReadOnlyList<string?> WriteAttemptKeys => [.. _fake.WriteAttempts.Select(attempt => attempt.IdempotencyKey)];

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _provider.Dispose();
        }
    }

    protected override Task MakePaidAsync(PaymentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _fake.Succeed(session.ProviderPaymentId);
        return Task.CompletedTask;
    }

    protected override Task MakeStatusUnknownAsync(PaymentSession session, string rawStatus)
    {
        ArgumentNullException.ThrowIfNull(session);
        _fake.ReportUnknownStatus(session.ProviderPaymentId, rawStatus);
        return Task.CompletedTask;
    }

    protected override Task TimeOutNextWriteAsync()
    {
        _fake.TimeOutNextWrite();
        return Task.CompletedTask;
    }

    protected override Task<WebhookRequest> SignedWebhookAsync(PaymentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.FromResult(new FakeWebhookBuilder(_fake).ForPayment(session.ProviderPaymentId));
    }
}

public sealed class RedirectFlowConformanceTests() : FakeGatewayConformanceTests(FakeFlow.Redirect);

public sealed class ClientSdkFlowConformanceTests() : FakeGatewayConformanceTests(FakeFlow.ClientSdk);

public sealed class DisplayReferenceFlowConformanceTests() : FakeGatewayConformanceTests(FakeFlow.DisplayReference);

public sealed class IdempotentFakeConformanceTests() : FakeGatewayConformanceTests(FakeFlow.Redirect, idempotentWrites: true);
