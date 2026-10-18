using System.Diagnostics;

namespace RoslynSentinel.Server.Basic;

public static class ToolArgumentValidator
{
    /// <summary>
    /// Cache of tool name -> (all declared parameter names, required parameter names, each
    /// declared parameter's own schema node for type/enum checks, and a case-insensitive ->
    /// canonical-name lookup used only for the case-normalization pass in
    /// <see cref="NormalizeParameterCase"/>). Cached because the schema is fixed for the process
    /// lifetime and this runs on every single tool call.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (System.Collections.Generic.HashSet<string> All, System.Collections.Generic.List<string> Required, System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> Properties, System.Collections.Generic.Dictionary<string, string> CaseInsensitiveLookup)> SchemaCache = new(StringComparer.Ordinal);
    /// <summary>
    /// Reads the declared and required parameter names, plus each declared parameter's own schema
    /// node (for type/enum checks), out of the tool's emitted JSON input schema. Reads the schema
    /// that is actually emitted to clients rather than reflecting over the C# signature, because
    /// this repo has a history of the two disagreeing -> validating against the signature would
    /// reject calls that match what the model was actually shown, which is the exact failure mode
    /// this validator exists to prevent. Returns <see langword="null"/> when the tool or its
    /// schema cannot be resolved, in which case the caller must skip validation rather than guess.
    /// </summary>
    private static (System.Collections.Generic.HashSet<string> All, System.Collections.Generic.List<string> Required, System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> Properties, System.Collections.Generic.Dictionary<string, string> CaseInsensitiveLookup)? TryGetSchemaParameters(
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
            var propertySchemas = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
            if (schema.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    all.Add(property.Name);
                    propertySchemas[property.Name] = property.Value;
                }
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

            // Case-insensitive -> canonical lookup, used only to normalize a case-only mismatch
            // (e.g. "filepath" -> "filePath") before Validate ever runs, so a caller that gets the
            // name right but the case wrong doesn't burn a round-trip on it. Built once here rather
            // than lowering on every call, since the schema is fixed for the process lifetime.
            // A declared name is deliberately left OUT of the lookup - not overwritten - when two
            // declared parameters collide case-insensitively (e.g. a tool that declared both "Id"
            // and "id"), because normalizing to either one would silently discard the model's
            // actual choice between two real, distinct parameters. That situation should fail the
            // same way an unrecognised parameter always has, not be guessed away.
            var caseInsensitiveLookup = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var caseInsensitiveCollisions = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in all)
            {
                if (caseInsensitiveCollisions.Contains(name))
                    continue;

                if (caseInsensitiveLookup.ContainsKey(name))
                {
                    caseInsensitiveLookup.Remove(name);
                    caseInsensitiveCollisions.Add(name);
                    continue;
                }

                caseInsensitiveLookup[name] = name;
            }

            var result = (All: all, Required: required, Properties: propertySchemas, CaseInsensitiveLookup: caseInsensitiveLookup);
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

    /// <summary>
    /// Empties the per-tool schema cache. Production never needs this (the emitted schema is fixed
    /// for the process lifetime); tests that rebuild tool schemas under a different
    /// <see cref="RoslynSentinel.Common.SchemaOptions"/> profile in the same process must call it, or a
    /// stale cached schema would be validated against the new one.
    /// </summary>
    public static void ClearSchemaCacheForTests() => SchemaCache.Clear();

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
        ["filePath"] = "a repo-relative or absolute path to a file in the loaded solution, e.g. \"src/Orders/OrderService.cs\". Call ListAll or ListSolutionItems to list the files in the solution.",
        ["filePath"] = "a repo-relative or absolute path to a file in the loaded solution, e.g. \"src/Orders/OrderService.cs\". Call ListAll or ListSolutionItems to list the files in the solution.",
        ["docCommentId"] = "a documentation comment ID, e.g. \"M:MyApp.Orders.OrderService.Total(System.Int32)\". Call Search(mode: symbol) to obtain the exact ID for a symbol - do not hand-write one.",
        ["reason"] = "a short phrase (at least 10 characters, containing a space) saying why you are calling this tool right now, e.g. \"checking the working tree before staging\".",
        ["operation"] = "one of the operation names listed in this tool's schema enum. Re-read the tool's parameter schema and pick one of them verbatim.",
        ["typeName"] = "the name of a type in the loaded solution, e.g. \"OrderService\". Call ListAll(kind: types) or Search(mode: symbol) to find the exact name.",
        ["memberName"] = "the name of a member on the target type, e.g. \"CalculateTotal\". Call Member(operation: view) or GetFileOutline to list a container's members.",
        ["containerName"] = "the name of the type that holds the member, e.g. \"OrderService\". Call GetFileOutline to see the types declared in a file.",
        ["oldContent"] = "the exact text to replace, copied verbatim from a prior ReadFile/GetMethodSource result - not retyped from memory.",
        ["newContent"] = "the replacement text.",
        ["message"] = "the commit message describing what the change does.",
    };

    /// <summary>
    /// Rewrites <paramref name="arguments"/> in place so that a parameter name differing from a
    /// declared parameter only by case (e.g. "filepath" vs "filePath") is renamed to the declared
    /// spelling, before <see cref="Validate"/> or the SDK's own argument binder ever sees it.
    /// <para>
    /// This is necessary, not just convenient: <c>AIFunctionArguments</c> binds by exact
    /// (ordinal) key lookup, so a case-only mismatch is otherwise indistinguishable from any other
    /// unknown parameter - the SDK would silently drop it and the tool would run on its default,
    /// or (post-<see cref="Validate"/>) the caller would be rejected and forced to retype a name it
    /// already had right except for case. <see cref="SuggestClosest"/> already treats a case-only
    /// difference as a certain match for its "Did you mean" text; this does the same rewrite for
    /// real instead of only suggesting it.
    /// </para>
    /// <para>
    /// Only case-only differences are normalized. Any other near-miss (a genuine typo, or a
    /// same-length name like the "paths"/"files" GitTool incident - see docs/current/blockers -
    /// where the caller reached for a plausible but wrong parameter) is left for
    /// <see cref="Validate"/> to reject with a suggestion, because guessing past anything more than
    /// a case difference risks silently applying the wrong parameter rather than the misspelled
    /// one.
    /// </para>
    /// <para>
    /// Two situations are deliberately left un-normalized and fall through to
    /// <see cref="Validate"/>'s ordinary error path instead:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// The tool's own schema declares two parameters that collide case-insensitively (e.g. both
    /// "Id" and "id"). <see cref="TryGetSchemaParameters"/> excludes such names from its
    /// case-insensitive lookup entirely, so neither is ever a normalization target - renaming to
    /// either one would silently discard the caller's actual choice between two distinct
    /// parameters.
    /// </description></item>
    /// <item><description>
    /// The call already supplies the canonical spelling alongside a case-only variant of it (e.g.
    /// both "filePath" and "filepath" in the same call). Renaming the variant would silently
    /// overwrite one of two values the caller explicitly passed. Left alone, the canonical key
    /// keeps its original value and the variant is reported by <see cref="Validate"/> as an
    /// ordinary unknown parameter, which is the same call.Count-preserving outcome as if
    /// normalization had never run.
    /// </description></item>
    /// </list>
    /// </summary>
    public static void NormalizeParameterCase(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return;

        var schema = TryGetSchemaParameters(server, toolName);
        if (schema is null)
            return;

        var (declared, _, _, caseInsensitiveLookup) = schema.Value;

        // Collect renames first rather than mutating while enumerating arguments.Keys.
        System.Collections.Generic.List<(string From, string To)>? renames = null;
        foreach (var key in arguments.Keys)
        {
            if (declared.Contains(key))
                continue;

            if (!caseInsensitiveLookup.TryGetValue(key, out var canonical))
                continue;

            // The canonical spelling is already present in this same call - leave both keys as
            // they are so Validate reports the variant as an unknown parameter instead of this
            // pass silently overwriting one of two values the caller explicitly supplied.
            if (arguments.ContainsKey(canonical))
                continue;

            (renames ??= new System.Collections.Generic.List<(string, string)>()).Add((key, canonical));
        }

        if (renames is null)
            return;

        foreach (var (from, to) in renames)
        {
            var value = arguments[from];
            arguments.Remove(from);
            arguments[to] = value;
        }
    }

    /// <summary>
    /// Per-tool alias -> declared-parameter map for names a model repeatedly supplies instead of
    /// the declared one. Mined from real transcripts (scripts/Get-UnknownParameterReport.ps1), and
    /// only for pairs that mean the same thing and take the same type: an alias that merely
    /// resembles the right parameter would silently run the tool with a different meaning, which is
    /// the failure the unknown-parameter rejection exists to prevent. Per-tool on purpose - the same
    /// word is a real, different parameter elsewhere (e.g. "memberName" is declared on MemberTool).
    /// Applied by <see cref="ApplyParameterAliases"/>; alias keys match case-insensitively.
    /// </summary>
    public static readonly System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyDictionary<string, string>> ParameterAliases =
        new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["GetMethodSource"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["memberName"] = "methodName",
                ["symbolName"] = "methodName",
            },
            ["RunTest"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["testFilter"] = "filter",
                ["testFilterExpression"] = "filter",
            },
            ["LoadSolution"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["path"] = "solutionPath",
            },
            ["Git"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["maxCount"] = "count",
            },
            ["Member"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["newText"] = "newMemberSource",
                ["newMemberCode"] = "newMemberSource",
                ["memberSource"] = "newMemberSource",
                ["className"] = "containerName",
            },
            ["Search"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["pattern"] = "query",
                ["filePattern"] = "fileGlob",
                ["glob"] = "fileGlob",
                ["symbolName"] = "query",
                ["name"] = "query",
                ["symbol"] = "query",
            },
            ["FindReferences"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["symbol"] = "symbolName",
            },
            ["LocateSymbol"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "symbolName",
            },
            ["WriteFile"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fileContent"] = "content",
            },
            ["ApplyUnifiedDiff"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["diff"] = "unifiedDiff",
            },
            ["UsingDirective"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["usingName"] = "namespaceName",
                ["usingNamespace"] = "namespaceName",
                ["usingText"] = "namespaceName",
            },
            ["GetLargeResult"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["maxItems"] = "limit",
                ["maxRecords"] = "limit",
                ["pageSize"] = "limit",
            },
            ["Build"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["verifyLevel"] = "level",
                ["buildVerifyLevel"] = "level",
            },
            ["ChangeAccessibility"] = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["newAccessibility"] = "accessibility",
                ["symbolName"] = "targetName",
            },
        };

    /// <summary>
    /// Repairs enum parameter values when the caller-provided string value is not an exact
    /// ordinal match but exactly one enum member matches under case-insensitive comparison,
    /// and returns one short note per repair (or <see langword="null"/> when nothing changed).
    /// When zero or two-or-more members match case-insensitively, the value is left unchanged
    /// for <see cref="Validate"/> to reject in step 3b.
    /// <para>
    /// Run before <see cref="ApplyParameterAliases"/>.
    /// </para>
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string>? NormalizeEnumCase(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0 || string.IsNullOrEmpty(toolName))
            return null;

        var schema = TryGetSchemaParameters(server, toolName);
        if (schema is null)
            return null;

        var (declared, required, propertySchemas, _) = schema.Value;

        // Collect repairs first rather than mutating while enumerating arguments.Keys.
        System.Collections.Generic.List<(string Key, string Member)>? repairs = null;
        foreach (var argument in arguments)
        {
            if (argument.Value.ValueKind != System.Text.Json.JsonValueKind.String)
                continue;

            if (!propertySchemas.TryGetValue(argument.Key, out var propertySchema))
                continue;

            if (!propertySchema.TryGetProperty("enum", out var enumNode) ||
                enumNode.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            var actualValue = argument.Value.GetString() ?? "";
            var members = new System.Collections.Generic.List<string>();
            var ordinalMatch = false;

            foreach (var entry in enumNode.EnumerateArray())
            {
                if (entry.ValueKind != System.Text.Json.JsonValueKind.String)
                    continue;
                var member = entry.GetString();
                if (member is null)
                    continue;
                members.Add(member);
                if (string.Equals(member, actualValue, StringComparison.Ordinal))
                    ordinalMatch = true;
            }

            if (ordinalMatch || members.Count == 0)
                continue;

            // Count case-insensitive matches.
            var caseInsensitiveMatches = new System.Collections.Generic.List<string>();
            foreach (var member in members)
            {
                if (string.Equals(member, actualValue, StringComparison.OrdinalIgnoreCase))
                    caseInsensitiveMatches.Add(member);
            }

            // Repair only when exactly one match exists.
            if (caseInsensitiveMatches.Count == 1)
            {
                (repairs ??= new System.Collections.Generic.List<(string, string)>())
                    .Add((argument.Key, caseInsensitiveMatches[0]));
            }
        }

        if (repairs is null)
            return null;

        var notes = new System.Collections.Generic.List<string>(repairs.Count);
        foreach (var (key, member) in repairs)
        {
            var originalValue = arguments[key].GetString() ?? "";
            arguments[key] = System.Text.Json.JsonSerializer.SerializeToElement(member);
            notes.Add($"Note: '{originalValue}' is not a valid value for parameter '{key}' of {toolName}; it was treated as '{member}' ('{originalValue}' -> '{member}'). Use '{member}' in future calls.");
        }

        return notes;
    }

    /// <summary>
    /// Rewrites <paramref name="arguments"/> in place, renaming each key listed in
    /// <see cref="ParameterAliases"/> for this tool to its declared parameter, and returns one
    /// short note per rename (or <see langword="null"/> when nothing changed) so the caller can tell
    /// the model which name is the real one.
    /// <para>
    /// A rename is skipped - leaving the key for <see cref="Validate"/> to reject as unknown - when
    /// the alias is itself a declared parameter of the tool (the declared meaning wins), when the
    /// alias target is not declared in the emitted schema (a stale table entry must not invent a
    /// parameter), or when the call already supplies the target (renaming would silently overwrite
    /// one of two values the caller passed, the same rule as <see cref="NormalizeParameterCase"/>).
    /// Run after <see cref="NormalizeParameterCase"/> and <see cref="NormalizeEnumCase"/>.
    /// </para>
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string>? ApplyParameterAliases(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0 || string.IsNullOrEmpty(toolName))
            return null;

        if (!ParameterAliases.TryGetValue(toolName, out var aliases))
            return null;

        var schema = TryGetSchemaParameters(server, toolName);
        if (schema is null)
            return null;

        var declared = schema.Value.All;

        // Collect renames first rather than mutating while enumerating arguments.Keys.
        System.Collections.Generic.List<(string From, string To)>? renames = null;
        foreach (var key in arguments.Keys)
        {
            if (declared.Contains(key) || !aliases.TryGetValue(key, out var canonical))
                continue;

            if (!declared.Contains(canonical) || arguments.ContainsKey(canonical))
                continue;

            // Two aliases of the same target in one call: the first wins, the second is left for
            // Validate to reject rather than silently overwriting the first.
            if (renames is not null && renames.Exists(r => r.To == canonical))
                continue;

            (renames ??= new System.Collections.Generic.List<(string, string)>()).Add((key, canonical));
        }

        if (renames is null)
            return null;

        var notes = new System.Collections.Generic.List<string>(renames.Count);
        foreach (var (from, to) in renames)
        {
            var value = arguments[from];
            arguments.Remove(from);
            arguments[to] = value;
            notes.Add($"Note: '{from}' is not a parameter of {toolName}; it was treated as '{to}'. Use '{to}' in future calls.");
        }

        return notes;
    }

    /// <summary>
    /// Rewrites a call that still passes the retired <c>referencesKind</c> parameter of <c>Search</c>.
    /// That parameter was folded into the mode: <c>mode: "references"</c> with <c>referencesKind</c>
    /// <c>callers</c>, <c>implementations</c> or <c>all</c> now means <c>mode: "callers"</c>,
    /// <c>mode: "implementations"</c> or <c>mode: "references"</c>. The mode is rewritten, the
    /// <c>referencesKind</c> key is removed, and one note tells the model the new form.
    /// <para>
    /// Any other combination (another mode, or an unknown kind value) is left untouched so
    /// <see cref="Validate"/> rejects the unknown parameter like it does for any other call.
    /// </para>
    /// </summary>
    public static System.Collections.Generic.IReadOnlyList<string>? ApplyLegacySearchReferencesKind(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (!string.Equals(toolName, "Search", System.StringComparison.Ordinal) || arguments is null)
            return null;

        string? kindKey = null;
        string? modeKey = null;
        foreach (var key in arguments.Keys)
        {
            if (kindKey is null && string.Equals(key, "referencesKind", System.StringComparison.OrdinalIgnoreCase))
                kindKey = key;
            else if (modeKey is null && string.Equals(key, "mode", System.StringComparison.OrdinalIgnoreCase))
                modeKey = key;
        }

        if (kindKey is null || modeKey is null)
            return null;

        var kindElement = arguments[kindKey];
        var modeElement = arguments[modeKey];
        if (kindElement.ValueKind != System.Text.Json.JsonValueKind.String
            || modeElement.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;

        var mode = modeElement.GetString();
        var kindValue = kindElement.GetString();
        if (kindValue is null || !string.Equals(mode, "references", System.StringComparison.OrdinalIgnoreCase))
            return null;

        string mapped;
        if (string.Equals(kindValue, "callers", System.StringComparison.OrdinalIgnoreCase))
            mapped = "callers";
        else if (string.Equals(kindValue, "implementations", System.StringComparison.OrdinalIgnoreCase))
            mapped = "implementations";
        else if (string.Equals(kindValue, "all", System.StringComparison.OrdinalIgnoreCase))
            mapped = "references";
        else
            return null;

        arguments.Remove(kindKey);
        arguments[modeKey] = System.Text.Json.JsonSerializer.SerializeToElement(mapped);

        return new[]
        {
            $"Note: 'referencesKind' is no longer a parameter of Search; mode 'references' with referencesKind '{kindValue}' was treated as mode '{mapped}'. Use mode '{mapped}' in future calls.",
        };
    }

    /// <summary>
    /// Suffix that marks a tool parameter as a batch-edit array (<c>batchEdits</c>,
    /// <c>modifierBatchEdits</c>, ...). The repair in <see cref="WrapFlatBatchParameters"/> only
    /// considers parameters with this suffix.
    /// </summary>
    private const string BatchEditsSuffix = "batchEdits";

    /// <summary>
    /// Repairs a call that passes one edit's fields flat (e.g. <c>filePath</c>, <c>oldContent</c>,
    /// <c>newContent</c>) to a tool whose only way to carry them is a batch-edit array: the flat keys
    /// are moved into a one-element array under that parameter, and one note per call tells the model
    /// the nested form to use next time.
    /// <para>
    /// Schema-driven, with no per-tool table: a candidate is a declared parameter whose name ends in
    /// <c>batchEdits</c>, is absent from the call, and whose emitted <c>items.properties</c> declare
    /// every key the call supplies that is not itself a declared top-level parameter. Item property
    /// names match case-insensitively and are rewritten to their declared spelling. The wrap is
    /// skipped - leaving the keys for <see cref="Validate"/> to reject as unknown - when the call
    /// already supplies the array (two values for one meaning must not be merged silently), when
    /// any undeclared key is not an item property (the call is not a flat edit), when more than one
    /// batch parameter would fit (the tool cannot tell which was meant), or when the items schema is
    /// not an inline object schema. Run after <see cref="ApplyParameterAliases"/>.
    /// </para>
    /// </summary>
    /// <returns>One note per wrapped call, or <see langword="null"/> when nothing changed.</returns>
    public static System.Collections.Generic.IReadOnlyList<string>? WrapFlatBatchParameters(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0 || string.IsNullOrEmpty(toolName))
            return null;

        var schema = TryGetSchemaParameters(server, toolName);
        if (schema is null)
            return null;

        var declared = schema.Value.All;

        // Copy first: the wrap removes keys from the dictionary being inspected.
        var flatKeys = arguments.Keys
            .Where(key => !declared.Contains(key) && !RoslynSentinel.Common.HiddenSchemaParams.IsHidden(toolName, key))
            .ToList();
        if (flatKeys.Count == 0)
            return null;

        string? batchParameter = null;
        System.Collections.Generic.Dictionary<string, string>? itemNames = null;
        foreach (var (name, propertySchema) in schema.Value.Properties)
        {
            if (!name.EndsWith(BatchEditsSuffix, StringComparison.OrdinalIgnoreCase) || arguments.ContainsKey(name))
                continue;

            var candidateItemNames = TryGetItemPropertyNames(propertySchema);
            if (candidateItemNames is null || !flatKeys.All(candidateItemNames.ContainsKey))
                continue;

            if (batchParameter is not null)
                return null;

            batchParameter = name;
            itemNames = candidateItemNames;
        }

        if (batchParameter is null || itemNames is null)
            return null;

        var item = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var key in flatKeys)
        {
            item[itemNames[key]] = arguments[key];
            arguments.Remove(key);
        }

        arguments[batchParameter] = System.Text.Json.JsonSerializer.SerializeToElement(new[] { item });

        var keyList = string.Join(", ", flatKeys.Select(key => $"'{key}'"));
        return
        [
            $"Note: {keyList} {(flatKeys.Count == 1 ? "is not a parameter" : "are not parameters")} of {toolName}; the call was treated as a single entry of '{batchParameter}'. " +
            $"Pass '{batchParameter}: [{{ ... }}]' in future calls, one object per edit."
        ];
    }

    /// <summary>
    /// Repairs a call that sends a declared object or array parameter as a JSON string (for example
    /// <c>fieldMap</c> sent as the text of a JSON object) by parsing the text into the declared shape,
    /// so the framework binder receives a real JSON value instead of throwing a JsonException.
    /// <para>
    /// Schema-driven: a candidate is an argument sent as a JSON string whose emitted property schema
    /// declares <c>type</c> as <c>object</c> or <c>array</c>, alone or with <c>null</c> (a
    /// <c>["object","null"]</c> union). A property that also declares <c>string</c>, or that has an
    /// <c>enum</c>, is never a candidate. The text is trimmed and must start with the declared opener
    /// (<c>{</c> or <c>[</c>) and parse as JSON of that same kind; otherwise the argument is left
    /// untouched and no note is added, so <see cref="Validate"/> rejects it with the expected shape.
    /// </para>
    /// </summary>
    /// <returns>One note per call listing the coerced parameters, or <see langword="null"/> when nothing changed.</returns>
    public static System.Collections.Generic.IReadOnlyList<string>? CoerceStringToStructured(
        ModelContextProtocol.Server.McpServer? server,
        string? toolName,
        System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0 || string.IsNullOrEmpty(toolName))
            return null;

        var schema = TryGetSchemaParameters(server, toolName);
        if (schema is null)
            return null;

        var propertySchemas = schema.Value.Properties;

        // Copy first: the coercion replaces entries in the dictionary being inspected.
        var candidates = arguments.Keys.ToList();
        var coerced = new System.Collections.Generic.List<(string Name, string Kind)>();
        foreach (var name in candidates)
        {
            if (arguments[name].ValueKind != System.Text.Json.JsonValueKind.String ||
                !propertySchemas.TryGetValue(name, out var propertySchema))
                continue;

            if (propertySchema.TryGetProperty("enum", out _))
                continue;

            if (!propertySchema.TryGetProperty("type", out var typeNode))
                continue;

            var declaredTypes = new System.Collections.Generic.List<string>();
            if (typeNode.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var declared = typeNode.GetString();
                if (declared is not null)
                    declaredTypes.Add(declared);
            }
            else if (typeNode.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in typeNode.EnumerateArray())
                {
                    if (entry.ValueKind == System.Text.Json.JsonValueKind.String && entry.GetString() is { } declaredEntry)
                        declaredTypes.Add(declaredEntry);
                }
            }

            if (declaredTypes.Contains("string"))
                continue;

            var nonNullTypes = declaredTypes.Where(t => t != "null").ToList();
            if (nonNullTypes.Count != 1 || !(nonNullTypes[0] == "object" || nonNullTypes[0] == "array"))
                continue;

            var kind = nonNullTypes[0];
            var text = (arguments[name].GetString() ?? "").Trim();
            var opener = kind == "object" ? '{' : '[';
            if (text.Length == 0 || text[0] != opener)
                continue;

            System.Text.Json.JsonDocument? document = null;
            try
            {
                document = System.Text.Json.JsonDocument.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var parsedMatches = kind == "object"
                    ? root.ValueKind == System.Text.Json.JsonValueKind.Object
                    : root.ValueKind == System.Text.Json.JsonValueKind.Array;
                if (!parsedMatches)
                    continue;

                arguments[name] = root.Clone();
            }

            coerced.Add((name, kind));
        }

        if (coerced.Count == 0)
            return null;

        if (coerced.Count == 1)
        {
            var (single, singleKind) = coerced[0];
            var article = singleKind == "object" ? "an object" : "an array";
            return
            [
                $"Note: '{single}' was sent as a JSON string; it was parsed as {article}. Pass a real JSON {singleKind} (not a string) in future calls."
            ];
        }

        var names = string.Join(", ", coerced.Select(c => $"'{c.Name}'"));
        return
        [
            $"Note: {names} were sent as JSON strings; they were parsed as their declared JSON shapes. Pass real JSON values (not strings) in future calls."
        ];
    }

    /// <summary>
    /// Reads the declared item property names (case-insensitive -> declared spelling) out of an
    /// array parameter's emitted schema, or <see langword="null"/> when the items are not an inline
    /// object schema with properties (e.g. a <c>$ref</c>, which this repair deliberately does not chase).
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, string>? TryGetItemPropertyNames(System.Text.Json.JsonElement arraySchema)
    {
        if (!arraySchema.TryGetProperty("items", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        if (!items.TryGetProperty("properties", out var properties) || properties.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        var names = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.EnumerateObject())
            names[property.Name] = property.Name;

        return names.Count == 0 ? null : names;
    }

    /// Checks a tool call's arguments against the tool's emitted input schema BEFORE dispatch, and
    /// returns an actionable error message when the call cannot succeed as written -> or
    /// <see langword="null"/> to let the call proceed.
    /// <para>
    /// Three dispatch-layer defects make this necessary, and none is fixable per-tool:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// An <b>unknown argument is silently discarded.</b> Argument binding is a pull model ->
    /// <c>AIFunctionFactory</c> looks up each declared parameter by name in the arguments
    /// dictionary and never inspects what is left over -> so a misspelled or misapplied parameter
    /// name is simply never read. The tool then runs on its defaults and returns
    /// <c>success:true</c> with the wrong result. That silent-wrong-behaviour class is the hardest
    /// of all for a weak model to recover from: there is no error to react to. Confirmed live ->
    /// <c>GitTool(operation:"stage", paths:"…")</c> quietly staged tracked files only, because
    /// <c>stage</c> reads <c>files</c> and <c>paths</c> belonged to <c>diff</c>.
    /// </description></item>
    /// <item><description>
    /// A <b>missing required argument throws a raw framework exception.</b>
    /// <c>AIFunctionFactory</c> raises "The arguments dictionary is missing a value for the
    /// required parameter 'x'. (Parameter 'arguments')" -> dispatch-layer vocabulary describing an
    /// internal data structure the caller never sees, with no example value and no route to a real
    /// one, in violation of the never-leak-raw-exceptions convention in CLAUDE.md.
    /// </description></item>
    /// <item><description>
    /// A <b>type-mismatched or invalid-enum argument throws a raw <c>JsonException</c>.</b> Passing
    /// an array where the schema declares a scalar <c>string</c> (e.g. <c>GitTool(operation:"stage",
    /// files:["a","b"])</c> -> the plural parameter name invites this), or a string that isn't one of
    /// the schema's declared <c>enum</c> members (e.g. <c>GitTool(operation:"show")</c>, not a real
    /// <c>GitOperation</c>), both crash during framework-level deserialization before the tool
    /// method body ever runs -> no tool-level try/catch can intercept it. Confirmed live for both
    /// shapes; see docs/current/finding_git_tool_array_param_and_invalid_operation_crash.md.
    /// </description></item>
    /// </list>
    /// <para>
    /// All three are caught here rather than in each tool because the fault is in the shared
    /// dispatch path: a per-tool fix would have to be repeated on every tool and re-applied to
    /// every tool added later, which is precisely the forgotten-call-site failure mode this repo
    /// keeps hitting.
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

        var (declared, required, propertySchemas, _) = schema.Value;

        // 1. Unknown arguments. Reported first and in full: if the caller both misspelled a
        //    parameter and omitted a required one, the misspelling is usually the cause of both.
        if (arguments is not null && arguments.Count > 0)
        {
            var unknown = new System.Collections.Generic.List<string>();
            foreach (var argument in arguments)
            {
                // A parameter the Lean schema profile stripped from the emitted schema is still bound
                // by the C# method, so it is accepted (but never advertised in the lists below).
                if (!declared.Contains(argument.Key) &&
                    !RoslynSentinel.Common.HiddenSchemaParams.IsHidden(toolName, argument.Key))
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
                       .Append(". Nothing was executed - the call was rejected before running, because an unrecognised parameter would otherwise be ignored and the tool would run on its defaults and report success with the wrong result.");

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

        // 3. Type-mismatched or invalid-enum arguments -> the two shapes that otherwise crash with a
        //    raw JsonException during framework-level deserialization, before the tool method body
        //    ever runs (so no per-tool try/catch can intercept them).
        if (arguments is not null && arguments.Count > 0)
        {
            foreach (var argument in arguments)
            {
                if (!propertySchemas.TryGetValue(argument.Key, out var propertySchema))
                    continue;

                var actualKind = argument.Value.ValueKind;

                // 3a. Declared scalar type (string/number/boolean/integer, alone or nullable via a
                //     ["type", "null"] union) but the caller passed an array or object.
                if ((actualKind == System.Text.Json.JsonValueKind.Array ||
                     actualKind == System.Text.Json.JsonValueKind.Object) &&
                    propertySchema.TryGetProperty("type", out var typeNode))
                {
                    var declaredTypes = new System.Collections.Generic.List<string>();
                    if (typeNode.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var t = typeNode.GetString();
                        if (t is not null)
                            declaredTypes.Add(t);
                    }
                    else if (typeNode.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var entry in typeNode.EnumerateArray())
                        {
                            if (entry.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var t = entry.GetString();
                                if (t is not null)
                                    declaredTypes.Add(t);
                            }
                        }
                    }

                    var actualTypeWord = actualKind == System.Text.Json.JsonValueKind.Array ? "array" : "object";
                    var declaresScalarOnly = declaredTypes.Count > 0 &&
                        declaredTypes.All(t => t is "string" or "number" or "integer" or "boolean");

                    if (declaresScalarOnly)
                    {
                        return $"Parameter '{argument.Key}' for tool '{toolName}' takes a single {string.Join(" or ", declaredTypes)} value, not an {actualTypeWord}. " +
                               $"Call this tool once per value instead of passing a {actualTypeWord} - e.g. if you have several files, call the tool separately for each one. Nothing was executed.";
                    }
                }

                // 3b. Declared enum but the caller's string value isn't one of the members. A
                //     single case-insensitive match is repaired earlier by NormalizeEnumCase.
                if (actualKind == System.Text.Json.JsonValueKind.String &&
                    propertySchema.TryGetProperty("enum", out var enumNode) &&
                    enumNode.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var members = new System.Collections.Generic.List<string>();
                    var matched = false;
                    var actualValue = argument.Value.GetString() ?? "";
                    foreach (var entry in enumNode.EnumerateArray())
                    {
                        if (entry.ValueKind != System.Text.Json.JsonValueKind.String)
                            continue;
                        var member = entry.GetString();
                        if (member is null)
                            continue;
                        members.Add(member);
                        if (string.Equals(member, actualValue, StringComparison.Ordinal))
                            matched = true;
                    }

                    if (!matched && members.Count > 0)
                    {
                        var suggestion = SuggestClosest(actualValue, members);
                        var suggestionText = suggestion is not null ? $" Did you mean '{suggestion}'?" : "";
                        return $"'{actualValue}' is not a valid value for parameter '{argument.Key}' of tool '{toolName}'.{suggestionText} Valid values are: {string.Join(", ", members)}. Nothing was executed.";
                    }
                }

                // 3c. Declared object or array (alone or with null, never also string, never an enum) but
                //     the caller sent a string that CoerceStringToStructured could not parse into that shape.
                if (actualKind == System.Text.Json.JsonValueKind.String &&
                    !propertySchema.TryGetProperty("enum", out _) &&
                    propertySchema.TryGetProperty("type", out var structuredTypeNode))
                {
                    var structuredTypes = new System.Collections.Generic.List<string>();
                    if (structuredTypeNode.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var structuredType = structuredTypeNode.GetString();
                        if (structuredType is not null)
                            structuredTypes.Add(structuredType);
                    }
                    else if (structuredTypeNode.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var structuredEntry in structuredTypeNode.EnumerateArray())
                        {
                            if (structuredEntry.ValueKind == System.Text.Json.JsonValueKind.String &&
                                structuredEntry.GetString() is { } structuredName)
                                structuredTypes.Add(structuredName);
                        }
                    }

                    var nonNullStructured = structuredTypes.Where(t => t != "null").ToList();
                    if (!structuredTypes.Contains("string") &&
                        nonNullStructured.Count == 1 &&
                        (nonNullStructured[0] == "object" || nonNullStructured[0] == "array"))
                    {
                        var structuredWord = nonNullStructured[0];
                        var exampleValue = structuredWord == "object" ? "{\"oldName\": \"newName\"}" : "[\"value1\", \"value2\"]";
                        return $"Parameter '{argument.Key}' for tool '{toolName}' takes a JSON {structuredWord}, not a string, and the string given is not valid JSON of that shape. " +
                               $"Pass it as a real JSON {structuredWord} (for example {exampleValue}), not as quoted text. Nothing was executed.";
                    }
                }
            }
        }

        return null;
    }
}
