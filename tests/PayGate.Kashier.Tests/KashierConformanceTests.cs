namespace PayGate.Kashier.Tests;

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PayGate.Conformance;
using PayGate.Webhooks;

/// <summary>Runs the conformance suite against the Kashier package, registered as an app registers it, on an in-memory Kashier.</summary>
public sealed class KashierConformanceTests : PaymentGatewayConformanceTests, IDisposable
{
    private readonly InMemoryKashier _kashier = new();
    private readonly KashierHarness _harness;

    public KashierConformanceTests()
    {
        _harness = new KashierHarness(o => o.PaymentApiKey = Fixtures.SigningKey, kashier: _kashier);
        Verifier = _harness.Provider.GetServices<IWebhookVerifier>().Single(v => v.Provider == PaymentProviderId.Kashier);
    }

    protected override IPaymentGateway Gateway => _harness.Gateway;

    protected override IWebhookVerifier Verifier { get; }

    protected override IReadOnlyList<string?> WriteAttemptKeys => _kashier.WriteAttempts;

    public void Dispose() => _harness.Dispose();

    protected override Task MakePaidAsync(PaymentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _kashier.Pay(session.ProviderPaymentId);
        return Task.CompletedTask;
    }

    protected override Task MakeStatusUnknownAsync(PaymentSession session, string rawStatus)
    {
        ArgumentNullException.ThrowIfNull(session);
        _kashier.ReportStatus(session.ProviderPaymentId, rawStatus);
        return Task.CompletedTask;
    }

    protected override Task TimeOutNextWriteAsync()
    {
        _kashier.HangNextWrite();
        return Task.CompletedTask;
    }

    protected override Task<WebhookRequest> SignedWebhookAsync(PaymentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Task.FromResult(_kashier.PaidWebhook(session.ProviderPaymentId, Fixtures.SigningKey));
    }

    /// <summary>Flipping the body's first byte would make it unreadable JSON. Changing a signed amount tests the signature.</summary>
    protected override WebhookRequest Tamper(WebhookRequest webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        var body = JsonNode.Parse(webhook.Body.Span)!;
        body["data"]!["amount"] = 1;
        return webhook with { Body = Encoding.UTF8.GetBytes(body.ToJsonString()) };
    }
}
