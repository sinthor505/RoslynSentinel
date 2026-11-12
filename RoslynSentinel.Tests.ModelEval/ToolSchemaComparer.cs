using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>How a recorded tool schema differs from the live one. Higher values take precedence.</summary>
public enum SchemaDifferenceKind
{
    None,
    DescriptionOnly,
    Structural,
    Missing,
}

/// <summary>
/// Pure comparison of the tool schemas a transcript recorded with the ones the server offers now. Descriptions are
/// prose, so they are classified apart from the structure a model has to satisfy. No I/O.
/// </summary>
public static class ToolSchemaComparer
{
    private const string DescriptionKey = "description";

    // Arrays whose order carries no meaning in a JSON schema.
    private static readonly HashSet<string> UnorderedArrayKeys = new(StringComparer.Ordinal) { "required", "enum" };

    /// <summary>
    /// Compares every distinct tool named in <paramref name="toolNamesCalled"/>. Returns the highest-precedence kind
    /// found and the detail of the first tool at that kind; <see cref="SchemaDifferenceKind.None"/> has empty detail.
    /// </summary>
    public static (SchemaDifferenceKind Kind, string Detail) Compare(
        IEnumerable<string> toolNamesCalled,
        IReadOnlyDictionary<string, JsonElement>? recordedSchemas,
        IReadOnlyDictionary<string, string>? recordedDescriptions,
        IReadOnlyDictionary<string, JsonElement> liveSchemas,
        IReadOnlyDictionary<string, string> liveDescriptions,
        string? captureCommit)
    {
        string commit = captureCommit ?? "unknown";
        (SchemaDifferenceKind Kind, string Detail) worst = (SchemaDifferenceKind.None, "");
        foreach (string tool in toolNamesCalled.Distinct(StringComparer.Ordinal))
        {
            var found = CompareTool(tool, recordedSchemas, recordedDescriptions, liveSchemas, liveDescriptions, commit);
            if (found.Kind > worst.Kind)
            {
                worst = found;
            }
        }

        return worst;
    }

    private static (SchemaDifferenceKind Kind, string Detail) CompareTool(
        string tool,
        IReadOnlyDictionary<string, JsonElement>? recordedSchemas,
        IReadOnlyDictionary<string, string>? recordedDescriptions,
        IReadOnlyDictionary<string, JsonElement> liveSchemas,
        IReadOnlyDictionary<string, string> liveDescriptions,
        string commit)
    {
        if (recordedSchemas is null)
        {
            return (SchemaDifferenceKind.Missing, $"no schemas recorded (capture commit {commit})");
        }

        if (!recordedSchemas.TryGetValue(tool, out JsonElement recorded))
        {
            return (SchemaDifferenceKind.Missing, $"tool {tool} has no recorded schema (capture commit {commit})");
        }

        if (!liveSchemas.TryGetValue(tool, out JsonElement live))
        {
            return (SchemaDifferenceKind.Structural, $"tool {tool} is no longer offered (capture commit {commit})");
        }

        return CompareSchemas(tool, recorded, live, recordedDescriptions, liveDescriptions, commit);
    }

    private static (SchemaDifferenceKind Kind, string Detail) CompareSchemas(
        string tool,
        JsonElement recorded,
        JsonElement live,
        IReadOnlyDictionary<string, string>? recordedDescriptions,
        IReadOnlyDictionary<string, string> liveDescriptions,
        string commit)
    {
        string? diffPath = FirstDifference(Canonicalise(recorded, false, null), Canonicalise(live, false, null), "");
        if (diffPath is not null)
        {
            string where = diffPath.Length == 0 ? "schema root" : diffPath;
            return (SchemaDifferenceKind.Structural, $"{tool}: {where} differs (capture commit {commit})");
        }

        if (Canonicalise(recorded, true, null).ToJsonString() != Canonicalise(live, true, null).ToJsonString())
        {
            string level = RootDescription(recorded) != RootDescription(live) ? "tool-level" : "parameter-level";
            return (SchemaDifferenceKind.DescriptionOnly, $"{tool}: {level} schema description differs (capture commit {commit})");
        }

        if (ToolDescriptionDiffers(tool, recordedDescriptions, liveDescriptions))
        {
            return (SchemaDifferenceKind.DescriptionOnly, $"{tool}: tool-level description differs (capture commit {commit})");
        }

        return (SchemaDifferenceKind.None, "");
    }

    private static bool ToolDescriptionDiffers(
        string tool,
        IReadOnlyDictionary<string, string>? recordedDescriptions,
        IReadOnlyDictionary<string, string> liveDescriptions)
    {
        if (recordedDescriptions is null || !recordedDescriptions.TryGetValue(tool, out string? recorded))
        {
            return false;
        }

        liveDescriptions.TryGetValue(tool, out string? live);
        return !string.Equals(recorded.Trim(), (live ?? "").Trim(), StringComparison.Ordinal);
    }

    private static string? RootDescription(JsonElement schema)
    {
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty(DescriptionKey, out JsonElement text)
            && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
    }

    // Builds a normalised tree: object keys sorted, optionally without string-valued "description" keywords (a parameter named description keeps its object schema), and required/enum arrays sorted.
    private static JsonNode? Canonicalise(JsonElement element, bool keepDescriptions, string? key)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => CanonicaliseObject(element, keepDescriptions),
            JsonValueKind.Array => CanonicaliseArray(element, keepDescriptions, key),
            _ => JsonNode.Parse(element.GetRawText()),
        };
    }

    private static JsonObject CanonicaliseObject(JsonElement element, bool keepDescriptions)
    {
        var result = new JsonObject();
        foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!keepDescriptions && property.Name == DescriptionKey && property.Value.ValueKind == JsonValueKind.String)
            {
                continue;
            }

            result[property.Name] = Canonicalise(property.Value, keepDescriptions, property.Name);
        }

        return result;
    }

    private static JsonArray CanonicaliseArray(JsonElement element, bool keepDescriptions, string? key)
    {
        var items = element.EnumerateArray().Select(item => Canonicalise(item, keepDescriptions, null)).ToList();
        if (key is not null && UnorderedArrayKeys.Contains(key))
        {
            items = items.OrderBy(item => item?.ToJsonString(), StringComparer.Ordinal).ToList();
        }

        return new JsonArray(items.ToArray());
    }

    // Returns the JSON-pointer-like path of the first difference, or null when equal. Arrays are compared whole.
    private static string? FirstDifference(JsonNode? recorded, JsonNode? live, string path)
    {
        if (recorded is JsonObject recordedObject && live is JsonObject liveObject)
        {
            return FirstObjectDifference(recordedObject, liveObject, path);
        }

        return recorded?.ToJsonString() == live?.ToJsonString() ? null : path;
    }

    private static string? FirstObjectDifference(JsonObject recorded, JsonObject live, string path)
    {
        IEnumerable<string> names = recorded.Select(p => p.Key)
            .Concat(live.Select(p => p.Key))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (string name in names)
        {
            string? diff = FirstDifference(recorded[name], live[name], path.Length == 0 ? name : path + "." + name);
            if (diff is not null)
            {
                return diff;
            }
        }

        return null;
    }
}
