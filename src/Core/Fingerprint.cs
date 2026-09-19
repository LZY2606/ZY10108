using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SourceMapChains.Core;

public static class Fingerprint
{
    public static string OfText(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Deterministic digest of any serializable value via canonical JSON.</summary>
    public static string OfCanonical<T>(T value)
    {
        var canonical = CanonicalJson.Serialize(value);
        return OfText(canonical);
    }
}

/// <summary>Canonical JSON: sorted object keys, no whitespace, stable number rendering.</summary>
public static class CanonicalJson
{
    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions.Default);
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(System.Text.Json.Nodes.JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case System.Text.Json.Nodes.JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case System.Text.Json.Nodes.JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                break;
            case System.Text.Json.Nodes.JsonValue val:
                sb.Append(val.ToJsonString());
                break;
        }
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
