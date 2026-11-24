namespace PayGate.Kashier.Tests;

using Microsoft.Extensions.DependencyInjection;
using PayGate.Webhooks;

/// <summary>The customer's browser coming back from Kashier, against the recorded redirects (tests/fixtures/kashier/returns).</summary>
public sealed class KashierReturnTests
{
    [Theory]
    [InlineData("success", PaymentStatus.Succeeded, "PG-20261122212627-3721")]
    [InlineData("failed", PaymentStatus.Failed)]
    [InlineData("authorized", PaymentStatus.Authorized)]
    public async Task Every_recorded_redirect_verifies_and_claims_its_status_Async(string name, PaymentStatus status, string? reference = null)
    {
        var result = await ParseAsync(Fixtures.Return(name));

        result.Verification.ShouldBe(ReturnCallbackVerification.Valid);
        result.IndicatedStatus.ShouldBe(status);
        result.MerchantReference.ShouldNotBeNull();
        if (reference is not null)
        {
            result.MerchantReference.ShouldBe(reference);
        }

        result.ProviderPaymentId.ShouldBeNull("the redirect names the order, not the session");
        result.Account.ShouldBe(KashierHarness.Account);
    }

    [Theory]
    [InlineData("paymentStatus", "SUCCESS")]
    [InlineData("amount", "5000")]
    [InlineData("merchantOrderId", "ORD-SOMEONE-ELSE")]
    [InlineData("transactionId", "TX-00000000999")]
    public async Task A_changed_signed_value_is_invalid_and_claims_nothing_Async(string field, string value)
    {
        var result = await ParseAsync(Fixtures.Return("failed", q => q[field] = value));

        result.Verification.ShouldBe(ReturnCallbackVerification.Invalid);
        result.IndicatedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task Parameters_outside_the_list_are_not_signed_Async()
    {
        // The app's own return URL may carry its parameters, and Kashier adds mode.
        var result = await ParseAsync(Fixtures.Return("success", q =>
        {
            q["mode"] = "live";
            q["ref"] = "ORD-42";
        }));

        result.Verification.ShouldBe(ReturnCallbackVerification.Valid);
    }

    [Fact]
    public async Task A_missing_field_is_signed_as_null_Async()
    {
        var query = Fixtures.Load("returns/success.json")["query"]!.AsArray()
            .ToDictionary(p => p![0]!.GetValue<string>(), p => p![1]!.GetValue<string>());
        query.Remove("cardDataToken");
        query.Remove("maskedCard");
        query["signature"] = Fixtures.SignReturn(query, Fixtures.SigningKey);

        var result = await ParseAsync(Fixtures.Return(query));

        result.Verification.ShouldBe(ReturnCallbackVerification.Valid);
        result.IndicatedStatus.ShouldBe(PaymentStatus.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000")]
    public async Task A_missing_or_unreadable_signature_is_invalid_Async(string? signature)
    {
        var result = await ParseAsync(Fixtures.Return("success", q =>
        {
            if (signature is null)
            {
                q.Remove("signature");
            }
            else
            {
                q["signature"] = signature;
            }
        }));

        result.Verification.ShouldBe(ReturnCallbackVerification.Invalid);
    }

    [Fact]
    public async Task A_signature_made_with_another_key_is_invalid_Async()
    {
        var result = await ParseAsync(Fixtures.Return("success", q => q["signature"] = Fixtures.SignReturn(q, "some-other-key")));

        result.Verification.ShouldBe(ReturnCallbackVerification.Invalid);
    }

    [Fact]
    public async Task A_redirect_without_a_merchant_reference_is_invalid_even_when_signed_Async()
    {
        var result = await ParseAsync(Fixtures.Return("success", q =>
        {
            q.Remove("merchantOrderId");
            q["signature"] = Fixtures.SignReturn(q, Fixtures.SigningKey);
        }));

        result.Verification.ShouldBe(ReturnCallbackVerification.Invalid);
        result.IndicatedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task Query_names_are_read_whatever_their_case_Async()
    {
        var query = Fixtures.Load("returns/success.json")["query"]!.AsArray()
            .ToDictionary(p => p![0]!.GetValue<string>().ToUpperInvariant(), p => p![1]!.GetValue<string>());

        var result = await ParseAsync(Fixtures.Return(query));

        result.Verification.ShouldBe(ReturnCallbackVerification.Valid);
    }

    private static async Task<ReturnCallbackResult> ParseAsync(ReturnCallbackRequest request)
    {
        using var harness = new KashierHarness(o => o.PaymentApiKey = Fixtures.SigningKey);
        var parser = harness.Provider.GetServices<IPaymentReturnParser>().Single(p => p.Provider == PaymentProviderId.Kashier);
        return await parser.ParseAsync(request, TestContext.Current.CancellationToken);
    }
}
