using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>One model listed by a llama-server host. Loaded is null when the host reports no state.</summary>
public sealed record LlamaServerModel(string Id, bool? Loaded, int? ContextLength);

/// <summary>Parses llama-server's /v1/models body and matches requested model ids against it.</summary>
public static class LlamaServerModels
{
    /// <summary>Parses the data[] array. Never throws; an invalid body gives an empty list.</summary>
    public static IReadOnlyList<LlamaServerModel> Parse(string? modelsJson)
    {
        var models = new List<LlamaServerModel>();
        if (string.IsNullOrWhiteSpace(modelsJson))
        {
            return models;
        }

        try
        {
            using var document = JsonDocument.Parse(modelsJson);
            if (!TryGetProperty(document.RootElement, "data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return models;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryGetProperty(item, "id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString();
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                models.Add(new LlamaServerModel(id, ReadLoaded(item), ReadContextLength(item)));
            }
        }
        catch (JsonException)
        {
            models.Clear();
        }

        return models;
    }

    /// <summary>The entry whose Loaded is true and whose id matches the requested model.</summary>
    public static LlamaServerModel? FindLoaded(IReadOnlyList<LlamaServerModel> models, string requestedModel) =>
        models.FirstOrDefault(m => m.Loaded == true && IdsMatch(requestedModel, m.Id));

    /// <summary>The entry whose id matches the requested model, in any state.</summary>
    public static LlamaServerModel? FindListed(IReadOnlyList<LlamaServerModel> models, string requestedModel) =>
        models.FirstOrDefault(m => IdsMatch(requestedModel, m.Id));

    /// <summary>Case-insensitive comparison of the text after the last '/' of each id.</summary>
    public static bool IdsMatch(string requested, string listedId) =>
        string.Equals(ShortId(requested), ShortId(listedId), StringComparison.OrdinalIgnoreCase);

    private static bool? ReadLoaded(JsonElement item)
    {
        if (TryGetProperty(item, "loaded", out var loaded)
            && (loaded.ValueKind == JsonValueKind.True || loaded.ValueKind == JsonValueKind.False))
        {
            return loaded.GetBoolean();
        }

        if (TryGetProperty(item, "status", out var status)
            && TryGetProperty(status, "value", out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            return string.Equals(value.GetString(), "loaded", StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static int? ReadContextLength(JsonElement item)
    {
        if (TryGetProperty(item, "context_length", out var length)
            && length.ValueKind == JsonValueKind.Number
            && length.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static string ShortId(string id)
    {
        var slash = id.LastIndexOf('/');
        return slash < 0 ? id : id.Substring(slash + 1);
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
