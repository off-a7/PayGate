namespace PayGate.Kashier.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayGate.Webhooks;

/// <summary>Reads the recorded Kashier traffic in tests/fixtures/kashier, copied next to the test assembly.</summary>
internal static class Fixtures
{
    /// <summary>The key every webhook and return fixture was re-signed with.</summary>
    public const string SigningKey = "playground-fixture-key-not-a-secret";

    public static JsonObject Load(string relativePath)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "kashier", relativePath);
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    /// <summary>The body Kashier answered an API call with, exactly as recorded.</summary>
    public static string ApiResponse(string name) => Load($"api/{name}.json")["response"]!.ToJsonString();

    /// <summary>A recorded session lookup with its status changed, for statuses the sandbox didn't produce on demand.</summary>
    public static string SessionWithStatus(string name, string status)
    {
        var response = Load($"api/{name}.json")["response"]!.AsObject();
        response["data"]!["status"] = status;
        return response.ToJsonString();
    }

    /// <summary>The HTTP status Kashier answered an API call with.</summary>
    public static int ApiStatus(string name) => Load($"api/{name}.json")["status"]!.GetValue<int>();

    /// <summary>The body of a recorded webhook, as Kashier posted it.</summary>
    public static JsonObject WebhookBody(string name) => Load($"webhooks/{name}.json")["body"]!.AsObject();

    /// <summary>A recorded webhook as PayGate receives it, with its recorded signature.</summary>
    public static WebhookRequest Webhook(string name) =>
        Webhook(WebhookBody(name), Load($"webhooks/{name}.json")["headers"]!["x-kashier-signature"]!.GetValue<string>());

    /// <summary>A recorded webhook body changed by a test, signed again with <see cref="SigningKey"/> unless told otherwise.</summary>
    public static WebhookRequest Webhook(string name, Action<JsonObject> change, bool resign = true)
    {
        var body = WebhookBody(name);
        change(body);
        string signature = resign
            ? SignWebhook(body["data"]!.AsObject(), SigningKey)
            : Load($"webhooks/{name}.json")["headers"]!["x-kashier-signature"]!.GetValue<string>();
        return Webhook(body, signature);
    }

    public static WebhookRequest Webhook(JsonObject body, string? signature, string header = "x-kashier-signature") => new()
    {
        Account = KashierHarness.Account,
        Method = "POST",
        Headers = signature is null
            ? new Dictionary<string, IReadOnlyList<string>>()
            : new Dictionary<string, IReadOnlyList<string>> { [header] = [signature] },
        Body = Encoding.UTF8.GetBytes(body.ToJsonString()),
        ReceivedAt = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero),
    };

    /// <summary>
    /// Kashier's webhook scheme, written out again here so the tests don't check the library against itself: the sorted
    /// <c>signatureKeys</c>, values as raw JSON text, only the values RFC 3986-encoded.
    /// </summary>
    public static string SignWebhook(JsonObject data, string key)
    {
        var keys = data["signatureKeys"]!.AsArray().Select(k => k!.GetValue<string>()).Order(StringComparer.Ordinal);
        string signed = string.Join('&', keys.Select(k => $"{k}={Uri.EscapeDataString(RawText(data[k]!))}"));
        return Hmac(key, signed);
    }

    /// <summary>A recorded browser redirect as PayGate receives it.</summary>
    public static ReturnCallbackRequest Return(string name, Action<Dictionary<string, string>>? change = null)
    {
        var query = Load($"returns/{name}.json")["query"]!.AsArray()
            .ToDictionary(p => p![0]!.GetValue<string>(), p => p![1]!.GetValue<string>());
        change?.Invoke(query);
        return Return(query);
    }

    public static ReturnCallbackRequest Return(Dictionary<string, string> query) => new()
    {
        Account = KashierHarness.Account,
        Query = query.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[p.Value]),
    };

    /// <summary>Kashier's redirect scheme, written out again: a fixed list in a fixed order, decoded values, missing ones as <c>null</c>.</summary>
    public static string SignReturn(IReadOnlyDictionary<string, string> query, string key)
    {
        string[] fields = ["paymentStatus", "cardDataToken", "maskedCard", "merchantOrderId", "orderId", "cardBrand", "orderReference", "transactionId", "amount", "currency"];
        return Hmac(key, string.Join('&', fields.Select(f => $"{f}={query.GetValueOrDefault(f) ?? "null"}")));
    }

    public static string Hmac(string key, string data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data)));

    private static string RawText(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
}
