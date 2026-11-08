namespace PayGate.AspNetCore.Tests;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayGate.Webhooks;

public sealed class ReturnCallbacksTests
{
    private static readonly PaymentAccount _account = new(new PaymentProviderId("recorder"), "second");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_query_reaches_the_providers_parser_Async()
    {
        using var provider = Build(builder => builder.AddReturnParser<EchoParser>());
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?Reference=order-1&status=Succeeded&tag=a&tag=b");

        var result = await Returns(provider).ParseAsync(context.Request, _account, Token);

        result.Account.ShouldBe(_account);
        result.MerchantReference.ShouldBe("order-1");
        result.IndicatedStatus.ShouldBe(PaymentStatus.Succeeded);
        provider.GetRequiredService<EchoParser.Seen>().Query!["TAG"].ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_provider_without_a_parser_is_a_configuration_error_Async()
    {
        using var provider = Build();

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Returns(provider).ParseAsync(new DefaultHttpContext().Request, _account, Token));

        error.Message.ShouldContain("recorder");
    }

    [Fact]
    public void Registering_a_parser_for_each_account_keeps_one()
    {
        var services = new ServiceCollection();

        services.AddPayGate().AddReturnParser<EchoParser>().AddReturnParser<EchoParser>();

        services.Count(d => d.ServiceType == typeof(IPaymentReturnParser)).ShouldBe(1);
    }

    [Theory]
    [InlineData("/orders/42")]
    [InlineData("/orders/42?tab=payment#top")]
    [InlineData("/")]
    [InlineData("https://shop.example.com/")]
    [InlineData("https://shop.example.com/orders/42?paid=1")]
    [InlineData("HTTPS://SHOP.EXAMPLE.COM/orders")]
    [InlineData("mansa://payment/result?id=7")]
    public void Local_paths_and_allowed_urls_are_safe(string candidate)
    {
        using var provider = Build();

        Returns(provider).TryGetSafeReturnUrl(candidate, out var url).ShouldBeTrue();
        url.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("orders/42")]
    [InlineData("//evil.example.com/orders")]
    [InlineData("/\\evil.example.com")]
    [InlineData("/orders\\..\\evil")]
    [InlineData("/orders\n/next")]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://shop.example.com.evil.example.com/")]
    [InlineData("https://shop.example.com@evil.example.com/")]
    [InlineData("https://user@shop.example.com/")]
    [InlineData("http://shop.example.com/")]
    [InlineData("https://shop.example.com:8443/")]
    [InlineData("mansa://payment-evil/result")]
    [InlineData("mansa://other/result")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    public void Other_urls_are_refused(string? candidate)
    {
        using var provider = Build();

        Returns(provider).TryGetSafeReturnUrl(candidate, out var url).ShouldBeFalse();
        url.ShouldBeNull();
    }

    [Fact]
    public void An_allowed_path_covers_only_what_is_under_it()
    {
        using var provider = Build(configure: options => options.AllowedReturnUrls.Add(new Uri("https://pay.example.com/app/")));
        var returns = Returns(provider);

        returns.TryGetSafeReturnUrl("https://pay.example.com/app/done", out _).ShouldBeTrue();
        returns.TryGetSafeReturnUrl("https://pay.example.com/app", out _).ShouldBeTrue();
        returns.TryGetSafeReturnUrl("https://pay.example.com/application", out _).ShouldBeFalse();
        returns.TryGetSafeReturnUrl("https://pay.example.com/other", out _).ShouldBeFalse();
    }

    [Fact]
    public void A_relative_allowed_url_is_a_configuration_error()
    {
        using var provider = Build(configure: options => options.AllowedReturnUrls.Add(new Uri("/orders", UriKind.Relative)));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<PayGateWebhookEndpointOptions>>().Value);
    }

    private static IPayGateReturnCallbacks Returns(ServiceProvider provider) => provider.GetRequiredService<IPayGateReturnCallbacks>();

    private static ServiceProvider Build(Action<PayGateBuilder>? paygate = null, Action<PayGateWebhookEndpointOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<EchoParser.Seen>();
        var builder = services.AddPayGate().AddWebhookEndpoints(options =>
        {
            options.AllowedReturnUrls.Add(new Uri("https://shop.example.com/"));
            options.AllowedReturnUrls.Add(new Uri("mansa://payment/"));
            configure?.Invoke(options);
        });
        paygate?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    private sealed class EchoParser(EchoParser.Seen seen) : IPaymentReturnParser
    {
        public PaymentProviderId Provider { get; } = new("recorder");

        public ValueTask<ReturnCallbackResult> ParseAsync(ReturnCallbackRequest request, CancellationToken cancellationToken = default)
        {
            seen.Query = request.Query;
            return ValueTask.FromResult(new ReturnCallbackResult
            {
                Account = request.Account,
                Verification = ReturnCallbackVerification.NotSigned,
                MerchantReference = request.GetQuery("reference"),
                IndicatedStatus = Enum.TryParse<PaymentStatus>(request.GetQuery("status"), out var status) ? status : null,
            });
        }

        public sealed class Seen
        {
            public IReadOnlyDictionary<string, IReadOnlyList<string>>? Query { get; set; }
        }
    }
}
