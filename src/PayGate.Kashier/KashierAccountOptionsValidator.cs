namespace PayGate.Kashier;

using Microsoft.Extensions.Options;

/// <summary>Checks a Kashier account's settings when the app starts, and again whenever a call reads them.</summary>
internal sealed class KashierAccountOptionsValidator : IValidateOptions<KashierAccountOptions>
{
    private const string _kashierDomain = "kashier.io";

    public ValidateOptionsResult Validate(string? name, KashierAccountOptions options)
    {
        var failures = Check(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures.Select(f => $"Kashier account '{name}': {f}"));
    }

    /// <summary>Returns every problem with the settings; empty when they are usable.</summary>
    internal static List<string> Check(KashierAccountOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.MerchantId))
        {
            failures.Add("MerchantId is required.");
        }
        else if (!options.MerchantId.StartsWith("MID-", StringComparison.Ordinal))
        {
            failures.Add("MerchantId must look like MID-1234-567.");
        }

        if (string.IsNullOrWhiteSpace(options.SecretKey))
        {
            failures.Add("SecretKey is required.");
        }

        if (string.IsNullOrWhiteSpace(options.PaymentApiKey))
        {
            failures.Add("PaymentApiKey is required.");
        }

        if (options.Display is not ("en" or "ar"))
        {
            failures.Add("Display must be 'en' or 'ar'.");
        }

        if (options.SessionLifetime <= TimeSpan.Zero)
        {
            failures.Add("SessionLifetime must be positive.");
        }

        if (options.MaxFailureAttempts is < 1 or > 10)
        {
            failures.Add("MaxFailureAttempts must be between 1 and 10.");
        }

        CheckHost(nameof(options.BaseUrlOverride), options.BaseUrlOverride, options.Environment, failures);
        CheckHost(nameof(options.FepBaseUrlOverride), options.FepBaseUrlOverride, options.Environment, failures);
        return failures;
    }

    /// <summary>
    /// Kashier's most common setup mistake is test keys against live hosts or the reverse: the calls then fail with an
    /// unhelpful 401. A Kashier host must match the environment; any other host (a mock server) is allowed.
    /// </summary>
    private static void CheckHost(string setting, Uri? url, PaymentEnvironment environment, List<string> failures)
    {
        if (url is null)
        {
            return;
        }

        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http"))
        {
            failures.Add($"{setting} must be an absolute http or https URL.");
            return;
        }

        string host = url.Host;
        bool isKashier = host.Equals(_kashierDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + _kashierDomain, StringComparison.OrdinalIgnoreCase);
        if (!isKashier)
        {
            return;
        }

        if (url.Scheme != "https")
        {
            failures.Add($"{setting} must use https for a Kashier host.");
        }

        bool testHost = host.StartsWith("test-", StringComparison.OrdinalIgnoreCase);
        if (environment == PaymentEnvironment.Sandbox && !testHost)
        {
            failures.Add($"{setting} is the live host {host}, but Environment is Sandbox. Test keys only work on test-* hosts.");
        }
        else if (environment == PaymentEnvironment.Live && testHost)
        {
            failures.Add($"{setting} is the test host {host}, but Environment is Live. Live keys only work on live hosts.");
        }
    }
}
