namespace PayGate.Kashier.Tests;

public sealed class KashierAccountOptionsTests
{
    [Fact]
    public void Complete_sandbox_settings_are_valid()
    {
        KashierAccountOptionsValidator.Check(Valid()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(nameof(KashierAccountOptions.MerchantId), "MerchantId is required.")]
    [InlineData(nameof(KashierAccountOptions.SecretKey), "SecretKey is required.")]
    [InlineData(nameof(KashierAccountOptions.PaymentApiKey), "PaymentApiKey is required.")]
    public void Each_required_key_is_reported_when_missing(string setting, string expected)
    {
        var options = Valid();
        typeof(KashierAccountOptions).GetProperty(setting)!.SetValue(options, " ");

        KashierAccountOptionsValidator.Check(options).ShouldBe([expected]);
    }

    [Fact]
    public void The_transfer_key_is_optional()
    {
        var options = Valid();
        options.TransferApiKey = null;

        KashierAccountOptionsValidator.Check(options).ShouldBeEmpty();
    }

    [Fact]
    public void A_merchant_id_must_look_like_one()
    {
        var options = Valid();
        options.MerchantId = "44733-715";

        KashierAccountOptionsValidator.Check(options).ShouldBe(["MerchantId must look like MID-1234-567."]);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("EN")]
    [InlineData("")]
    public void Display_must_be_english_or_arabic(string display)
    {
        var options = Valid();
        options.Display = display;

        KashierAccountOptionsValidator.Check(options).ShouldBe(["Display must be 'en' or 'ar'."]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Failure_attempts_must_be_between_one_and_ten(int attempts)
    {
        var options = Valid();
        options.MaxFailureAttempts = attempts;

        KashierAccountOptionsValidator.Check(options).ShouldBe(["MaxFailureAttempts must be between 1 and 10."]);
    }

    [Fact]
    public void The_session_lifetime_must_be_positive()
    {
        var options = Valid();
        options.SessionLifetime = TimeSpan.Zero;

        KashierAccountOptionsValidator.Check(options).ShouldBe(["SessionLifetime must be positive."]);
    }

    [Theory]
    [InlineData(PaymentEnvironment.Sandbox, "https://api.kashier.io/", "live host api.kashier.io, but Environment is Sandbox")]
    [InlineData(PaymentEnvironment.Live, "https://test-api.kashier.io/", "test host test-api.kashier.io, but Environment is Live")]
    public void A_kashier_host_must_match_the_environment(PaymentEnvironment environment, string host, string expected)
    {
        var options = Valid();
        options.Environment = environment;
        options.BaseUrlOverride = new Uri(host);

        KashierAccountOptionsValidator.Check(options).ShouldHaveSingleItem().ShouldContain(expected);
    }

    [Fact]
    public void The_fep_host_is_checked_too()
    {
        var options = Valid();
        options.FepBaseUrlOverride = new Uri("https://fep.kashier.io/");

        KashierAccountOptionsValidator.Check(options).ShouldHaveSingleItem().ShouldStartWith("FepBaseUrlOverride is the live host");
    }

    [Fact]
    public void A_kashier_host_must_use_https()
    {
        var options = Valid();
        options.BaseUrlOverride = new Uri("http://test-api.kashier.io/");

        KashierAccountOptionsValidator.Check(options).ShouldBe(["BaseUrlOverride must use https for a Kashier host."]);
    }

    [Theory]
    [InlineData(PaymentEnvironment.Sandbox)]
    [InlineData(PaymentEnvironment.Live)]
    public void Any_other_host_is_allowed_for_mock_servers(PaymentEnvironment environment)
    {
        var options = Valid();
        options.Environment = environment;
        options.BaseUrlOverride = new Uri("http://localhost:9091/kashier");

        KashierAccountOptionsValidator.Check(options).ShouldBeEmpty();
    }

    [Fact]
    public void The_hosts_follow_the_environment()
    {
        var sandbox = Valid();
        var live = Valid();
        live.Environment = PaymentEnvironment.Live;

        sandbox.ApiBaseUrl.ShouldBe(new Uri("https://test-api.kashier.io/"));
        sandbox.FepBaseUrl.ShouldBe(new Uri("https://test-fep.kashier.io/"));
        live.ApiBaseUrl.ShouldBe(new Uri("https://api.kashier.io/"));
        live.FepBaseUrl.ShouldBe(new Uri("https://fep.kashier.io/"));
    }

    [Fact]
    public void The_base_url_override_replaces_both_hosts_and_the_fep_override_only_fep()
    {
        var options = Valid();
        options.BaseUrlOverride = new Uri("http://localhost:9091/kashier");

        options.ApiBaseUrl.ShouldBe(new Uri("http://localhost:9091/kashier/"));
        options.FepBaseUrl.ShouldBe(new Uri("http://localhost:9091/kashier/"));

        options.FepBaseUrlOverride = new Uri("http://localhost:9092/");
        options.ApiBaseUrl.ShouldBe(new Uri("http://localhost:9091/kashier/"));
        options.FepBaseUrl.ShouldBe(new Uri("http://localhost:9092/"));
    }

    [Fact]
    public void ToString_shows_the_merchant_but_never_a_key()
    {
        var options = Valid();
        options.TransferApiKey = "transfer-key-value";

        string text = options.ToString();

        text.ShouldContain("MerchantId = MID-1234-567");
        text.ShouldContain("SecretKey = ***");
        foreach (string secret in new[] { "secret-key-value", "payment-api-key-value", "transfer-key-value" })
        {
            text.ShouldNotContain(secret);
        }
    }

    [Fact]
    public void ToString_shows_a_missing_key_as_not_set()
    {
        new KashierAccountOptions().ToString().ShouldContain("PaymentApiKey = (not set)");
    }

    internal static KashierAccountOptions Valid() => new()
    {
        MerchantId = "MID-1234-567",
        SecretKey = "secret-key-value",
        PaymentApiKey = "payment-api-key-value",
    };
}
