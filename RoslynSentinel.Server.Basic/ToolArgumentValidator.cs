using System.Diagnostics;

namespace RoslynSentinel.Server.Basic;

public static class ToolArgumentValidator
{
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Cache of tool name → (all declared parameter names, required parameter names), read from
    /// each tool's emitted JSON input schema. Cached because the schema is fixed for the process
    /// lifetime and this runs on every single tool call.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (System.Collections.Generic.HashSet<string> All, System.Collections.Generic.List<string> Required)> SchemaCache = new(StringComparer.Ordinal);
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Reads the declared and required parameter names for <paramref name="toolName"/> out of the
    /// tool's emitted JSON input schema. Reads the schema that is actually emitted to clients
    /// rather than reflecting over the C# signature, because this repo has a history of the two
    /// disagreeing — validating against the signature would reject calls that match what the model
    /// was actually shown, which is the exact failure mode this validator exists to prevent.
    /// Returns <see langword="null"/> when the tool or its schema cannot be resolved, in which case
    /// the caller must skip validation rather than guess.
    /// </summary>
    private static (System.Collections.Generic.HashSet<string> All, System.Collections.Generic.List<string> Required)? TryGetSchemaParameters(
        ModelContextProtocol.Server.McpServer? server, string? toolName)
    {
        if (server is null || string.IsNullOrEmpty(toolName))
            return null;

        if (SchemaCache.TryGetValue(toolName, out var cached))
            return cached;

        try
        {
            var tools = server.ServerOptions?.ToolCollection;
            if (tools is null)
                return null;

            ModelContextProtocol.Server.McpServerTool? tool = null;
            foreach (var candidate in tools)
            {
                if (string.Equals(candidate.ProtocolTool.Name, toolName, StringComparison.Ordinal))
                {
                    tool = candidate;
                    break;
                }
            }

            if (tool is null)
                return null;

            var schema = tool.ProtocolTool.InputSchema;
            if (schema.ValueKind != System.Text.Json.JsonValueKind.Object)
                return null;

            var all = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                    all.Add(property.Name);
            }

            var required = new System.Collections.Generic.List<string>();
            if (schema.TryGetProperty("required", out var requiredArray) &&
                requiredArray.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in requiredArray.EnumerateArray())
                {
                    if (entry.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var name = entry.GetString();
                        if (!string.IsNullOrEmpty(name))
                            required.Add(name);
                    }
                }
            }

            // A schema declaring no properties at all is far more likely to mean "schema emission
            // failed" than "this tool genuinely takes nothing", and treating it as the latter would
            // reject every argument the caller passes. Don't cache or validate against it.
            if (all.Count == 0)
                return null;

            var result = (All: all, Required: required);
            SchemaCache[toolName] = result;
            return result;
        }
        catch (Exception ex)
        {
            // Validation is a guardrail, never a gate: if the schema can't be read, the call must
            // still go through to the tool exactly as it did before.
            Debug.WriteLine($"ToolArgumentValidator schema lookup failed for '{toolName}': {ex}");
            return null;
        }
    }
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Nearest declared parameter to <paramref name="unknown"/>, or null when nothing is close
    /// enough to suggest. Turns "that parameter doesn't exist" into "you meant this one", which is
    /// the difference between an error the caller can act on immediately and one that costs a
    /// round-trip of guessing.
    /// </summary>
    private static string? SuggestClosest(string unknown, System.Collections.Generic.IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;

        foreach (var candidate in candidates)
        {
            // A case-only or substring difference (paths/Paths, file/files) is the most common
            // near-miss and always worth suggesting outright.
            if (string.Equals(candidate, unknown, StringComparison.OrdinalIgnoreCase))
                return candidate;

            var distance = LevenshteinDistance(unknown, candidate);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        // Scale the tolerance with name length so short names don't match everything: at most a
        // third of the name may differ, and never more than 3 edits.
        var tolerance = Math.Min(3, Math.Max(1, unknown.Length / 3));
        return bestDistance <= tolerance ? best : null;
    }

    /// <summary>Standard iterative two-row Levenshtein edit distance.</summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;
        if (b.Length == 0)
            return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var substitutionCost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }// Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Per-parameter recovery hint appended to a missing-required-parameter error: an example
    /// value, and the tool that discovers a real one where such a tool exists. Rejecting a call
    /// without naming a way to obtain the missing value just relocates the guessing.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> ParameterHints = new(StringComparer.Ordinal)
    {
        ["solutionPath"] = "the absolute path to a .slnx/.sln/.csproj file, e.g. \"C:\\\\repos\\\\MyApp\\\\MyApp.slnx\". Call ListWorkspaceSolutions to discover the solutions available on this host.",
        ["filepath"] = "a repo-relative or absolute path to a file in the loaded solution, e.g. \"src/Orders/OrderService.cs\". Call ListAll or ListSolutionItems to list the files in the solution.",
        ["filePath"] = "a repo-relative or absolute path to a file in the loaded solution, e.g. \"src/Orders/OrderService.cs\". Call ListAll or ListSolutionItems to list the files in the solution.",
        ["docCommentId"] = "a documentation comment ID, e.g. \"M:MyApp.Orders.OrderService.Total(System.Int32)\". Call LocateSymbol to obtain the exact ID for a symbol — do not hand-write one.",
        ["reason"] = "a short phrase (at least 10 characters, containing a space) saying why you are calling this tool right now, e.g. \"checking the working tree before staging\".",
        ["operation"] = "one of the operation names listed in this tool's schema enum. Re-read the tool's parameter schema and pick one of them verbatim.",
        ["typeName"] = "the name of a type in the loaded solution, e.g. \"OrderService\". Call ListAll(kind: types) or LocateSymbol to find the exact name.",
        ["memberName"] = "the name of a member on the target type, e.g. \"CalculateTotal\". Call Member(operation: view) or GetFileOutline to list a container's members.",
        ["containerName"] = "the name of the type that holds the member, e.g. \"OrderService\". Call GetFileOutline to see the types declared in a file.",
        ["oldContent"] = "the exact text to replace, copied verbatim from a prior ReadFile/GetMethodSource result — not retyped from memory.",
        ["newContent"] = "the replacement text.",
        ["message"] = "the commit message describing what the change does.",
    };
    // Added by AddMember (expected - used for diagnostics)
    /// <summary>
    /// Checks a tool call's arguments against the tool's emitted input schema BEFORE dispatch, and
    /// returns an actionable error message when the call cannot succeed as written — or
    /// <see langword="null"/> to let the call proceed.
    /// <para>
    /// Two dispatch-layer defects make this necessary, and neither is fixable per-tool:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// An <b>unknown argument is silently discarded.</b> Argument binding is a pull model —
    /// <c>AIFunctionFactory</c> looks up each declared parameter by name in the arguments
    /// dictionary and never inspects what is left over — so a misspelled or misapplied parameter
    /// name is simply never read. The tool then runs on its defaults and returns
    /// <c>success:true</c> with the wrong result. That silent-wrong-behaviour class is the hardest
    /// of all for a weak model to recover from: there is no error to react to. Confirmed live —
    /// <c>Git(operation:"stage", paths:"…")</c> quietly staged tracked files only, because
    /// <c>stage</c> reads <c>files</c> and <c>paths</c> belonged to <c>diff</c>.
    /// </description></item>
    /// <item><description>
    /// A <b>missing required argument throws a raw framework exception.</b>
    /// <c>AIFunctionFactory</c> raises "The arguments dictionary is missing a value for the
    /// required parameter 'x'. (Parameter 'arguments')" — dispatch-layer vocabulary describing an
    /// internal data structure the caller never sees, with no example value and no route to a real
    /// one, in violation of the never-leak-raw-exceptions convention in CLAUDE.md.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both are caught here rather than in each tool because the fault is in the shared dispatch
    /// path: a per-tool fix would have to be repeated on every tool and re-applied to every tool
    /// added later, which is precisely the forgotten-call-site failure mode this repo keeps hitting.
    /// </para>
    /// </summary>
    /// <returns>An error message to return to the caller, or null when the arguments are valid.</returns>
    public static string? Validate(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        var schema = TryGetSchemaParameters(server, toolName);
        if (schema is null)
            return null;

        var (declared, required) = schema.Value;

        // 1. Unknown arguments. Reported first and in full: if the caller both misspelled a
        //    parameter and omitted a required one, the misspelling is usually the cause of both.
        if (arguments is not null && arguments.Count > 0)
        {
            var unknown = new System.Collections.Generic.List<string>();
            foreach (var argument in arguments)
            {
                if (!declared.Contains(argument.Key))
                    unknown.Add(argument.Key);
            }

            if (unknown.Count > 0)
            {
                var builder = new System.Text.StringBuilder();
                builder.Append(unknown.Count == 1 ? "Unknown parameter " : "Unknown parameters ");
                builder.Append(string.Join(", ", unknown.Select(u => $"'{u}'")));
                builder.Append(" for tool '").Append(toolName).Append("'. ");

                foreach (var name in unknown)
                {
                    var suggestion = SuggestClosest(name, declared);
                    if (suggestion is not null)
                        builder.Append($"Did you mean '{suggestion}' instead of '{name}'? ");
                }

                builder.Append("This tool accepts only: ")
                       .Append(string.Join(", ", declared.OrderBy(d => d, StringComparer.Ordinal)))
                       .Append(". Nothing was executed — the call was rejected before running, because an unrecognised parameter would otherwise be ignored and the tool would run on its defaults and report success with the wrong result.");

                return builder.ToString();
            }
        }

        // 2. Missing required arguments.
        var missing = new System.Collections.Generic.List<string>();
        foreach (var name in required)
        {
            if (arguments is null || !arguments.ContainsKey(name))
                missing.Add(name);
        }

        if (missing.Count > 0)
        {
            var builder = new System.Text.StringBuilder();
            builder.Append(missing.Count == 1 ? "Missing required parameter " : "Missing required parameters ");
            builder.Append(string.Join(", ", missing.Select(missingName => $"'{missingName}'")));
            builder.Append(" for tool '").Append(toolName).Append("'. ");

            foreach (var name in missing)
            {
                builder.Append($"Pass {name}: ");
                builder.Append(ParameterHints.TryGetValue(name, out var hint)
                    ? hint
                    : "see this parameter's description in the tool's schema for its accepted values.");
                builder.Append(' ');
            }

            builder.Append("Nothing was executed.");
            return builder.ToString();
        }

        return null;
    }
}
