using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>One entry of the eval host table (eval-hosts.json).</summary>
public sealed record EvalHost(string Alias, string Machine, string BaseUrl, string Backend, string Suffix, bool IsDefault);

/// <summary>
/// Reads the eval host table and answers per-URL questions: suffix, backend and the host entry itself.
/// A missing file is an empty table; an invalid file throws <see cref="InvalidOperationException"/>.
/// </summary>
public static class EvalHostTable
{
    public const string BackendLmStudio = "lmstudio";
    public const string BackendLlamaServer = "llama-server";
    public const string FileName = "eval-hosts.json";

    private static readonly Lazy<IReadOnlyList<EvalHost>> DefaultTable = new(() => Load());

    public static string DefaultPath { get; } = Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Loads the table at <paramref name="path"/> (default: <see cref="DefaultPath"/>). Missing file gives an empty list.</summary>
    public static IReadOnlyList<EvalHost> Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        if (!File.Exists(file))
        {
            return Array.Empty<EvalHost>();
        }

        using var document = ParseDocument(file);
        return ParseHosts(document.RootElement, file);
    }

    /// <summary>Finds the entry whose base URL matches (trailing '/' ignored, case-insensitive).</summary>
    public static EvalHost? FindByBaseUrl(IReadOnlyList<EvalHost> table, string baseUrl)
    {
        var wanted = Normalise(baseUrl);
        return table.FirstOrDefault(h => string.Equals(Normalise(h.BaseUrl), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Finds the entry with this alias (case-insensitive, surrounding whitespace ignored); null when none matches.</summary>
    public static EvalHost? FindByAlias(IReadOnlyList<EvalHost> table, string alias)
    {
        var wanted = alias.Trim();
        return table.FirstOrDefault(h => string.Equals(h.Alias, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Suffix from the default table, else the sanitised URL.</summary>
    public static string DeriveSuffix(string baseUrl) => DeriveSuffix(DefaultTable.Value, baseUrl);

    /// <summary>Suffix from the matching entry, else the sanitised URL; empty result becomes "unknown-host".</summary>
    public static string DeriveSuffix(IReadOnlyList<EvalHost> table, string baseUrl)
    {
        var entry = FindByBaseUrl(table, baseUrl);
        return entry is not null ? entry.Suffix : SanitiseUrl(baseUrl);
    }

    /// <summary>Backend from the default table; unknown URL or no table gives <see cref="BackendLmStudio"/>.</summary>
    public static string BackendFor(string baseUrl) => BackendFor(DefaultTable.Value, baseUrl);

    /// <summary>Backend of the matching entry; unknown URL or empty table gives <see cref="BackendLmStudio"/>.</summary>
    public static string BackendFor(IReadOnlyList<EvalHost> table, string baseUrl) =>
        FindByBaseUrl(table, baseUrl)?.Backend ?? BackendLmStudio;

    private static JsonDocument ParseDocument(string file)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(file));
        }
        catch (JsonException ex)
        {
            throw Invalid(file, $"not valid JSON ({ex.Message}).");
        }
    }

    private static IReadOnlyList<EvalHost> ParseHosts(JsonElement root, string file)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "hosts", out var hosts)
            || hosts.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(file, "the top-level object needs a 'hosts' array.");
        }

        var entries = new List<EvalHost>();
        var position = 0;
        foreach (var item in hosts.EnumerateArray())
        {
            var where = $"hosts[{position++}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid(file, $"{where} is not an object.");
            }

            entries.Add(ReadEntry(item, file, where));
        }

        ValidateTable(entries, file);
        return entries;
    }

    private static EvalHost ReadEntry(JsonElement item, string file, string where)
    {
        var alias = RequireString(item, "alias", file, where);
        var machine = RequireString(item, "machine", file, where);
        var baseUrl = RequireString(item, "baseUrl", file, where);
        var backend = RequireString(item, "backend", file, where).ToLowerInvariant();
        var suffix = RequireString(item, "suffix", file, where);

        if (backend != BackendLmStudio && backend != BackendLlamaServer)
        {
            throw Invalid(file, $"{where} has backend '{backend}'; the known backends are '{BackendLmStudio}' and '{BackendLlamaServer}'.");
        }

        if (!TryGetProperty(item, "default", out var defaultElement)
            || (defaultElement.ValueKind != JsonValueKind.True && defaultElement.ValueKind != JsonValueKind.False))
        {
            throw Invalid(file, $"{where} is missing the boolean field 'default'.");
        }

        return new EvalHost(alias, machine, baseUrl, backend, suffix, defaultElement.GetBoolean());
    }

    private static void ValidateTable(IReadOnlyList<EvalHost> entries, string file)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!aliases.Add(entry.Alias))
            {
                throw Invalid(file, $"alias '{entry.Alias}' appears in more than one entry; aliases must be unique.");
            }
        }

        var machines = new HashSet<string>(entries.Select(e => e.Machine), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (machines.Contains(entry.Alias))
            {
                throw Invalid(file, $"alias '{entry.Alias}' is also a machine key; aliases and machine keys share one namespace.");
            }
        }

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!urls.Add(Normalise(entry.BaseUrl)))
            {
                throw Invalid(file, $"baseUrl '{entry.BaseUrl}' is used by more than one entry; each baseUrl maps to one alias.");
            }
        }

        foreach (var group in entries.GroupBy(e => e.Machine, StringComparer.OrdinalIgnoreCase))
        {
            var defaults = group.Count(e => e.IsDefault);
            if (defaults != 1)
            {
                throw Invalid(file, $"machine '{group.Key}' has {defaults} entries with default true; each machine has exactly one default.");
            }
        }
    }

    private static string RequireString(JsonElement item, string name, string file, string where)
    {
        if (TryGetProperty(item, name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!.Trim();
        }

        throw Invalid(file, $"{where} is missing the string field '{name}'.");
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

    private static InvalidOperationException Invalid(string file, string detail) =>
        new($"eval-hosts file '{file}': {detail}");

    private static string Normalise(string url) => url.Trim().TrimEnd('/');

    // Same rule as the former per-project host-suffix archiver helpers: ASCII letters and digits kept, others '_', trimmed.
    private static string SanitiseUrl(string baseUrl)
    {
        var sanitised = new string(baseUrl.Select(c => IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return sanitised.Length == 0 ? "unknown-host" : sanitised;
    }

    private static bool IsAsciiLetterOrDigit(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
}
