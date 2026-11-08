namespace PayGate.AspNetCore;

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PayGate.Webhooks;

/// <summary>The default <see cref="IPayGateReturnCallbacks"/>.</summary>
internal sealed class PayGateReturnCallbacks : IPayGateReturnCallbacks
{
    private readonly Dictionary<PaymentProviderId, IPaymentReturnParser> _parsers = [];
    private readonly IOptions<PayGateWebhookEndpointOptions> _options;

    public PayGateReturnCallbacks(IEnumerable<IPaymentReturnParser> parsers, IOptions<PayGateWebhookEndpointOptions> options)
    {
        _options = options;

        // Last registration wins, so an app can replace a provider's parser.
        foreach (var parser in parsers)
        {
            _parsers[parser.Provider] = parser;
        }
    }

    public ValueTask<ReturnCallbackResult> ParseAsync(HttpRequest request, PaymentAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_parsers.TryGetValue(account.Provider, out var parser))
        {
            throw new InvalidOperationException($"No return parser is registered for provider '{account.Provider}'.");
        }

        var callback = new ReturnCallbackRequest { Account = account, Query = RequestValues.Copy(request.Query) };
        return parser.ParseAsync(callback, cancellationToken);
    }

    public bool TryGetSafeReturnUrl(string? candidate, [NotNullWhen(true)] out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (IsLocalPath(candidate))
        {
            url = new Uri(candidate, UriKind.Relative);
            return true;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            return false;
        }

        foreach (var allowed in _options.Value.AllowedReturnUrls)
        {
            if (IsUnder(absolute, allowed))
            {
                url = absolute;
                return true;
            }
        }

        return false;
    }

    /// <summary>A path on this site: one leading slash, and no second slash or backslash that browsers read as another host.</summary>
    private static bool IsLocalPath(string candidate) =>
        candidate[0] == '/'
        && (candidate.Length == 1 || (candidate[1] != '/' && candidate[1] != '\\'))
        && !candidate.Contains('\\', StringComparison.Ordinal)
        && !candidate.Any(char.IsControl);

    /// <summary>Same scheme, host and port, and a path under the allowed path, segment by segment.</summary>
    private static bool IsUnder(Uri candidate, Uri allowed)
    {
        if (!string.Equals(candidate.Scheme, allowed.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.IdnHost, allowed.IdnHost, StringComparison.OrdinalIgnoreCase)
            || candidate.Port != allowed.Port
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        string allowedPath = allowed.AbsolutePath.EndsWith('/') ? allowed.AbsolutePath : allowed.AbsolutePath + "/";
        string candidatePath = candidate.AbsolutePath.EndsWith('/') ? candidate.AbsolutePath : candidate.AbsolutePath + "/";
        return candidatePath.StartsWith(allowedPath, StringComparison.Ordinal);
    }
}
