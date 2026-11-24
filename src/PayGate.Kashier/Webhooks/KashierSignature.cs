namespace PayGate.Kashier.Webhooks;

using System.Text;
using System.Text.Json;
using PayGate.Providers;

/// <summary>
/// The strings Kashier signs, as recorded in tests/fixtures/kashier and written up in docs/providers/kashier.md. Both are
/// HMAC-SHA256 in lowercase hex, keyed with the Payment API Key.
/// </summary>
internal static class KashierSignature
{
    /// <summary>The header a webhook's signature arrives in.</summary>
    public const string WebhookHeader = "x-kashier-signature";

    /// <summary>The query parameter a redirect's signature arrives in.</summary>
    public const string ReturnParameter = "signature";

    /// <summary>
    /// The fields PayGate decides on, which every recorded webhook signs. A webhook that leaves one of them out of
    /// <c>signatureKeys</c> is refused, because that field could then be changed without breaking the signature.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredWebhookKeys =
    [
        "amount", "currency", "merchantOrderId", "status", "transactionId",
    ];

    /// <summary>The redirect's signed fields, in the order Kashier signs them.</summary>
    public static readonly IReadOnlyList<string> ReturnFields =
    [
        "paymentStatus", "cardDataToken", "maskedCard", "merchantOrderId", "orderId", "cardBrand", "orderReference",
        "transactionId", "amount", "currency",
    ];

    /// <summary>
    /// Builds the string a webhook signs from its <c>data</c> object: the keys of <c>data.signatureKeys</c>, sorted ordinally,
    /// each written <c>key=value</c> with the value RFC 3986-encoded, joined with <c>&amp;</c>. Values are taken as the raw JSON
    /// text, so <c>113.34</c> stays <c>113.34</c>. It fails closed: a missing list, a listed key that is absent or
    /// <c>null</c>, or a list without <see cref="RequiredWebhookKeys"/> gives no string at all.
    /// </summary>
    /// <param name="data">The webhook's <c>data</c> object.</param>
    /// <param name="signed">The string to sign.</param>
    /// <param name="problem">Why no string was built. Names a field, never a value.</param>
    /// <returns><see langword="true"/> when the string was built.</returns>
    public static bool TryBuildWebhookString(JsonElement data, out string signed, out string? problem)
    {
        signed = string.Empty;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("signatureKeys", out var list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() == 0)
        {
            problem = "The webhook has no data.signatureKeys.";
            return false;
        }

        var keys = new List<string>(list.GetArrayLength());
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString()))
            {
                problem = "data.signatureKeys holds something other than field names.";
                return false;
            }

            keys.Add(item.GetString()!);
        }

        foreach (string required in RequiredWebhookKeys)
        {
            if (!keys.Contains(required, StringComparer.Ordinal))
            {
                problem = $"data.signatureKeys does not sign '{required}'.";
                return false;
            }
        }

        keys.Sort(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (string key in keys)
        {
            if (!WireFormat.TryGetRawProperty(data, key, out string? value) || value is null)
            {
                problem = $"The signed field '{key}' is missing or null.";
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(key).Append('=').Append(WireFormat.PercentEncode(value));
        }

        signed = builder.ToString();
        problem = null;
        return true;
    }

    /// <summary>
    /// Builds the string a redirect signs: <see cref="ReturnFields"/> in that order, each written <c>key=value</c> with the
    /// decoded value, joined with <c>&amp;</c>. A missing field is signed as the literal <c>null</c>.
    /// </summary>
    /// <param name="get">Reads a decoded query value, or <see langword="null"/> when it is absent.</param>
    /// <returns>The string to sign.</returns>
    public static string BuildReturnString(Func<string, string?> get)
    {
        var builder = new StringBuilder();
        foreach (string field in ReturnFields)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(field).Append('=').Append(get(field) ?? "null");
        }

        return builder.ToString();
    }

    /// <summary>Whether a hex signature is the HMAC-SHA256 of a string under the key. Fixed time; <see langword="false"/> for an empty key.</summary>
    public static bool Matches(string? key, string signed, string? signatureHex)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(signatureHex))
        {
            return false;
        }

        byte[] expected = Signatures.HmacSha256(key, signed);
        return Signatures.FixedTimeEqualsHex(expected, signatureHex.Trim());
    }
}
