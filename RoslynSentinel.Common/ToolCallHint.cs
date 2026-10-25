using System.Globalization;
using System.Threading;

namespace RoslynSentinel.Common;

/// <summary>
/// Builds complete, schema-valid MCP tool-call text for hints such as "Next: ..." so a model can paste the call
/// as written. Lookups go through a <see cref="ToolSignatureCatalog"/>. Nothing here throws at runtime: a bad hint
/// must not turn a tool failure into a crash.
/// </summary>
public static class ToolCallHint
{
    private const string ReasonPlaceholder = "\"<why you call this>\"";

    private static readonly object FallbackLock = new();

    /// <summary>Explicit override set through the Current setter; null means no override.</summary>
    private static ToolSignatureCatalog? _override;

    /// <summary>Cached fallback. Only a catalog holding at least one tool is cached, so an early call is rebuilt later.</summary>
    private static ToolSignatureCatalog? _fallback;

    /// <summary>
    /// The catalog used by the overloads that take no catalog argument. An assigned value is an explicit override;
    /// assigning null clears it. Without an override the fallback applies: a catalog built from the loaded
    /// [McpServerTool] methods by <see cref="ToolSignatureCatalogReflection.FromLoadedAssemblies"/>, cached once it
    /// holds at least one tool. Live catalog wiring comes in a later slice; until then, and in unit tests, the fallback
    /// applies. Thread-safe; reads on the hot path do not allocate once the fallback is cached.
    /// </summary>
    public static ToolSignatureCatalog Current
    {
        get
        {
            var explicitCatalog = Volatile.Read(ref _override);
            if (explicitCatalog is not null)
            {
                return explicitCatalog;
            }

            return Volatile.Read(ref _fallback) ?? BuildFallback();
        }
        set => Volatile.Write(ref _override, value);
    }

    private static ToolSignatureCatalog BuildFallback()
    {
        lock (FallbackLock)
        {
            if (_fallback is not null)
            {
                return _fallback;
            }

            var built = ToolSignatureCatalogReflection.FromLoadedAssemblies();
            if (built.ToolNames.Any())
            {
                Volatile.Write(ref _fallback, built);
            }

            return built;
        }
    }

    /// <summary>
    /// Renders a complete call, <c>Tool(p1: v1, p2: v2)</c>, in schema order. A parameter appears when the caller supplied
    /// a valid value or the schema marks it required (then a placeholder). Unknown tools render as the bare name; a null or
    /// empty tool name renders as an empty string. Invalid or unknown pairs are dropped.
    /// </summary>
    public static string Call(ToolSignatureCatalog catalog, string toolName, params (string Name, string Value)[] known)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            return string.Empty;
        }

        if (catalog is null || !catalog.TryGetParameters(toolName, out var parameters))
        {
            return toolName;
        }

        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in known ?? Array.Empty<(string Name, string Value)>())
        {
            var spec = FindSpec(parameters, pair.Name);
            if (spec is not null && TryRender(spec, pair.Value, out var rendered))
            {
                supplied.TryAdd(spec.Name, rendered);
            }
        }

        var parts = new List<string>();
        foreach (var spec in parameters)
        {
            if (supplied.TryGetValue(spec.Name, out var rendered))
            {
                parts.Add($"{spec.Name}: {rendered}");
            }
            else if (spec.Required)
            {
                parts.Add($"{spec.Name}: {Placeholder(spec)}");
            }
        }

        return $"{toolName}({string.Join(", ", parts)})";
    }

    /// <summary>Same as <see cref="Call(ToolSignatureCatalog, string, (string Name, string Value)[])"/> using <see cref="Current"/>.</summary>
    public static string Call(string toolName, params (string Name, string Value)[] known) => Call(Current, toolName, known);

    /// <summary>
    /// Returns <c>Next: call message</c>, with the call built as in <see cref="Call(ToolSignatureCatalog, string, (string Name, string Value)[])"/>.
    /// An empty message leaves no trailing space.
    /// </summary>
    public static string Next(ToolSignatureCatalog catalog, string toolName, string message, params (string Name, string Value)[] known)
    {
        var call = Call(catalog, toolName, known);
        return string.Join(" ", new[] { "Next:", call, message }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>Same as <see cref="Next(ToolSignatureCatalog, string, string, (string Name, string Value)[])"/> using <see cref="Current"/>.</summary>
    public static string Next(string toolName, string message, params (string Name, string Value)[] known) => Next(Current, toolName, message, known);

    /// <summary>
    /// Returns human-readable problems with a call: unknown tool, unknown or duplicate parameter, and an invalid value.
    /// Omitting a required parameter is valid: Call fills it with a placeholder. Empty when the call is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(ToolSignatureCatalog catalog, string toolName, params (string Name, string Value)[] known)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(toolName))
        {
            problems.Add("Tool name is empty.");
            return problems;
        }

        if (catalog is null || !catalog.TryGetParameters(toolName, out var parameters))
        {
            problems.Add($"Unknown tool '{toolName}'.");
            return problems;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in known ?? Array.Empty<(string Name, string Value)>())
        {
            var name = pair.Name ?? string.Empty;
            var spec = FindSpec(parameters, name);
            if (spec is null)
            {
                problems.Add($"Unknown parameter '{name}' for tool '{toolName}'.");
            }
            else if (!seen.Add(name))
            {
                problems.Add($"Duplicate parameter '{name}' for tool '{toolName}'.");
            }
            else if (!TryRender(spec, pair.Value, out _))
            {
                problems.Add(DescribeInvalid(spec, pair.Value));
            }
        }

        return problems;
    }

    /// <summary>Same as <see cref="Validate(ToolSignatureCatalog, string, (string Name, string Value)[])"/> using <see cref="Current"/>.</summary>
    public static IReadOnlyList<string> Validate(string toolName, params (string Name, string Value)[] known) => Validate(Current, toolName, known);

    private static ToolParameterSpec? FindSpec(IReadOnlyList<ToolParameterSpec> parameters, string? name) =>
        name is null ? null : parameters.FirstOrDefault(parameter => parameter.Name == name);

    private static bool TryRender(ToolParameterSpec spec, string? value, out string rendered)
    {
        rendered = string.Empty;
        if (value is null)
        {
            return false;
        }

        if (spec.EnumValues is { } allowed)
        {
            if (!allowed.Contains(value, StringComparer.Ordinal))
            {
                return false;
            }

            rendered = value;
            return true;
        }

        switch (spec.JsonType)
        {
            case "string":
                rendered = "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                return true;
            case "integer":
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }

                rendered = value;
                return true;
            case "number":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    return false;
                }

                rendered = value;
                return true;
            case "boolean":
                if (!bool.TryParse(value, out var flag))
                {
                    return false;
                }

                rendered = flag ? "true" : "false";
                return true;
            default:
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                rendered = value;
                return true;
        }
    }

    private static string Placeholder(ToolParameterSpec spec)
    {
        if (spec.Name == "reason")
        {
            return ReasonPlaceholder;
        }

        if (spec.EnumValues is { Count: > 0 } allowed)
        {
            return "<" + string.Join("|", allowed) + ">";
        }

        return spec.JsonType switch
        {
            "string" => "\"<" + spec.Name + ">\"",
            "boolean" => "<true|false>",
            _ => "<" + spec.Name + ">",
        };
    }

    private static string DescribeInvalid(ToolParameterSpec spec, string? value)
    {
        if (spec.EnumValues is { } allowed)
        {
            return $"Invalid value '{value}' for '{spec.Name}'; allowed: {string.Join(", ", allowed)}.";
        }

        return spec.JsonType switch
        {
            "integer" => $"'{spec.Name}' expects an integer, got '{value}'.",
            "number" => $"'{spec.Name}' expects a number, got '{value}'.",
            "boolean" => $"'{spec.Name}' expects true or false, got '{value}'.",
            _ => $"'{spec.Name}' has no usable value.",
        };
    }
}
