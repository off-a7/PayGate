namespace PayGate.AspNetCore;

using Microsoft.Extensions.Primitives;

/// <summary>Copies headers and query strings into the shapes the PayGate models take.</summary>
internal static class RequestValues
{
    public static Dictionary<string, IReadOnlyList<string>> Copy(IEnumerable<KeyValuePair<string, StringValues>> values)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            copy[key] = [.. value.OfType<string>()];
        }

        return copy;
    }
}
