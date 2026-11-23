namespace PayGate.Kashier.Tests;

using System.Text.Json.Nodes;

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
}
