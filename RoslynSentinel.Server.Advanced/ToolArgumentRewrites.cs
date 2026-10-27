using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Basic;

public static class ToolArgumentRewrites
{
    /// <summary>
    /// Drops undeclared arguments that are safe to ignore (benign-ignore table) and returns one note per
    /// removed key, or <see langword="null"/> when nothing changed. Pure apart from mutating
    /// <paramref name="arguments"/>; never throws. A rule is a no-op when its name is itself in
    /// <paramref name="declared"/> (declared wins) or when its predicate fails (the key stays for
    /// <see cref="ToolArgumentValidator.Validate"/>). Run after the case-normalization and alias passes.
    /// </summary>
    /// <param name="toolName">Tool being called; compared case-insensitively.</param>
    /// <param name="arguments">Call arguments, mutated in place.</param>
    /// <param name="declared">Declared parameter names (<c>All</c> from <c>TryGetSchemaParameters</c>).</param>
    /// <param name="properties">Per-parameter schema nodes (<c>Properties</c> from <c>TryGetSchemaParameters</c>); used by later steps.</param>
    /// <param name="solutionRoot">Solution root directory, used to make Search path values relative; null disables absolute-path mapping.</param>
    public static IReadOnlyList<string>? Apply(
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        IReadOnlySet<string> declared,
        IReadOnlyDictionary<string, JsonElement> properties,
        string? solutionRoot)
    {
        try
        {
            if (arguments is null || arguments.Count == 0 || string.IsNullOrEmpty(toolName))
                return null;

            List<string>? notes = null;
            foreach (var rule in IgnoreRules)
            {
                if (!string.Equals(rule.Tool, toolName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (declared.Contains(rule.Name))
                    continue;
                if (!arguments.TryGetValue(rule.Name, out var value))
                    continue;
                if (!rule.Predicate(arguments, value))
                    continue;
                if (!Remove(arguments, rule.Name))
                    continue;

                (notes ??= new List<string>()).Add(
                    $"Note: '{rule.Name}' is not a parameter of {rule.Tool} and was ignored ({rule.Reason}). Omit it in future calls.");
            }

            // Value-translating rewrites run after the ignore table; each bails out (keys untouched) on any doubt.
            if (string.Equals(toolName, "Search", StringComparison.OrdinalIgnoreCase))
            {
                var pathNote = RewriteSearchPath(arguments, declared, solutionRoot);
                if (pathNote is not null)
                    (notes ??= new List<string>()).Add(pathNote);

                // Runs after the path rule so a path-derived (or explicit) fileGlob wins and filePath stays.
                var filePathNote = RewriteSearchFilePath(arguments, declared, solutionRoot);
                if (filePathNote is not null)
                    (notes ??= new List<string>()).Add(filePathNote);
            }

            var rewriteNote = toolName.ToLowerInvariant() switch
            {
                "search" => RewriteSearchHeadLimit(arguments, declared),
                "readfile" => RewriteReadFileRange(arguments, declared),
                "runtest" => RewriteRunTestProject(arguments, declared, properties),
                "getdiagnostics" => RewriteGetDiagnosticsFile(arguments, declared, properties),
                "build" => RewriteBuildLevel(arguments, declared, properties),
                _ => null,
            };
            if (rewriteNote is not null)
                (notes ??= new List<string>()).Add(rewriteNote);

            return notes;
        }
        catch (Exception)
        {
            // A rewrite is a convenience, never a gate: on any failure leave the call to Validate.
            return null;
        }
    }

    /// <summary>
    /// Maps a Search <c>path</c> value to a <c>fileGlob</c> relative to the solution root, or returns
    /// <see langword="null"/> when it cannot be mapped unambiguously (the key is then left for Validate).
    /// Pure string work (no file-system access) following the plan's "Path-to-fileGlob rules" 1-7; never throws.
    /// </summary>
    public static string? PathToFileGlob(string value, string? solutionRoot)
    {
        try
        {
            // Rule 1: trim, empty -> null, forward slashes only.
            var path = value?.Trim();
            if (string.IsNullOrEmpty(path))
                return null;
            path = path.Replace('\\', '/');

            var root = string.IsNullOrWhiteSpace(solutionRoot)
                ? null
                : solutionRoot.Trim().Replace('\\', '/').TrimEnd('/');

            // Rule 2: parent segments, home, character classes, UNC, and a rooted path without a drive letter
            // (unless the root itself is rooted that way) cannot be mapped.
            if (path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith('~') || path.Contains('['))
                return null;
            foreach (var segment in path.Split('/'))
            {
                if (segment == "..")
                    return null;
            }

            var hasDriveLetter = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
            if (path.StartsWith('/') && !(root is not null && root.StartsWith('/')))
                return null;

            // Rule 3: an absolute path must lie strictly inside the solution root; strip the root prefix.
            if (hasDriveLetter || path.StartsWith('/'))
            {
                if (string.IsNullOrEmpty(root))
                    return null;

                var trimmed = path.TrimEnd('/');
                if (string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    return null;
                path = path[(root.Length + 1)..];
            }

            // Rule 4: strip leading "./" and trailing "/"; nothing left (or just ".") means no restriction.
            path = path.TrimEnd('/');
            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path[2..];
            if (path.Length == 0 || path == ".")
                return null;

            // Rule 5: already a glob.
            if (path.IndexOfAny(GlobChars) >= 0)
                return path;

            // Rule 6: a .cs file (a single segment stays a bare file name that matches in any folder).
            if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return path;

            // Rule 7: a directory.
            return path + "/**";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One benign-ignore row: when <see cref="Tool"/> receives an undeclared <see cref="Name"/> whose value
    /// satisfies <see cref="Predicate"/> (arguments, value), the key is dropped because ignoring it cannot
    /// change which results come back. <see cref="Reason"/> is quoted in the note.
    /// </summary>
    internal sealed record IgnoreRule(
        string Tool,
        string Name,
        Func<IDictionary<string, JsonElement>, JsonElement, bool> Predicate,
        string Reason);

    /// <summary>True for JSON <c>true</c> or the string "true" (case-insensitive).</summary>
    private static bool IsTrue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True ||
        (value.ValueKind == JsonValueKind.String &&
         string.Equals(value.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the value is a string equal (case-insensitive) to one of <paramref name="accepted"/>.</summary>
    private static bool IsOneOf(JsonElement value, params string[] accepted)
    {
        if (value.ValueKind != JsonValueKind.String)
            return false;

        var text = value.GetString()?.Trim();
        foreach (var candidate in accepted)
        {
            if (string.Equals(text, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True when <c>arguments["mode"]</c> is the string "text" (case-insensitive).</summary>
    private static bool IsTextMode(IDictionary<string, JsonElement> arguments) =>
        arguments.TryGetValue("mode", out var mode) && IsOneOf(mode, "text");

    /// <summary>Reads a JSON number or a numeric string as an int; false for anything else.</summary>
    private static bool TryGetInt(JsonElement value, out int result)
    {
        result = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetInt32(out result);
            case JsonValueKind.String:
                return int.TryParse(value.GetString()?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads the <c>enum</c> array of a declared parameter's schema node (same shape
    /// <see cref="ToolArgumentValidator.NormalizeEnumCase"/> reads); empty when the parameter has no enum.
    /// </summary>
    private static IReadOnlyList<string> EnumMembers(IReadOnlyDictionary<string, JsonElement> properties, string name)
    {
        var members = new List<string>();
        if (!properties.TryGetValue(name, out var schema) ||
            schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("enum", out var enumNode) ||
            enumNode.ValueKind != JsonValueKind.Array)
            return members;

        foreach (var entry in enumNode.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                continue;
            var member = entry.GetString();
            if (member is not null)
                members.Add(member);
        }

        return members;
    }

    /// <summary>Reads a non-empty JSON string (trimmed); false for any other kind or an empty string.</summary>
    private static bool TryGetText(JsonElement value, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            return false;

        var trimmed = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        text = trimmed;
        return true;
    }

    /// <summary>
    /// Finds the enum member of <paramref name="parameter"/> equal to <paramref name="wanted"/> ignoring case
    /// and returns it in the schema's own spelling; null when the parameter has no such member.
    /// </summary>
    private static string? FindEnumMember(IReadOnlyDictionary<string, JsonElement> properties, string parameter, string wanted)
    {
        foreach (var member in EnumMembers(properties, parameter))
        {
            if (string.Equals(member, wanted, StringComparison.OrdinalIgnoreCase))
                return member;
        }

        return null;
    }

    /// <summary>Search <c>head_limit: N</c> (N &gt; 0) becomes <c>maxResults: N</c>; N &lt;= 0 is left for Validate.</summary>
    private static string? RewriteSearchHeadLimit(IDictionary<string, JsonElement> arguments, IReadOnlySet<string> declared)
    {
        if (declared.Contains("head_limit") || !declared.Contains("maxResults") || arguments.ContainsKey("maxResults"))
            return null;
        if (!arguments.TryGetValue("head_limit", out var raw) || !TryGetInt(raw, out var limit) || limit <= 0)
            return null;

        Remove(arguments, "head_limit");
        arguments["maxResults"] = JsonSerializer.SerializeToElement(limit);
        return $"Note: 'head_limit' is not a parameter of Search; it was treated as maxResults: {limit}. Use {ToolCallHint.Call("Search", ("maxResults", limit.ToString()))} in future calls.";
    }

    /// <summary>
    /// Search (mode text) <c>path: P</c> becomes <c>fileGlob: G</c> via <see cref="PathToFileGlob"/>. Acts only when
    /// <c>path</c> is a string and not itself declared, and <c>fileGlob</c> is declared and absent; an unmappable
    /// path stays for Validate.
    /// </summary>
    private static string? RewriteSearchPath(IDictionary<string, JsonElement> arguments, IReadOnlySet<string> declared, string? solutionRoot)
    {
        if (declared.Contains("path") || !declared.Contains("fileGlob") || arguments.ContainsKey("fileGlob"))
            return null;
        if (!IsTextMode(arguments))
            return null;
        if (!arguments.TryGetValue("path", out var raw) || raw.ValueKind != JsonValueKind.String)
            return null;

        var glob = PathToFileGlob(raw.GetString() ?? string.Empty, solutionRoot);
        if (glob is null)
            return null;

        Remove(arguments, "path");
        arguments["fileGlob"] = JsonSerializer.SerializeToElement(glob);
        var note = $"Note: 'path' is not a parameter of Search; it was treated as fileGlob: \"{glob}\" (text mode restricts files with fileGlob). Use fileGlob in future calls.";
        if (glob.IndexOf('/') < 0 && glob.IndexOfAny(GlobChars) < 0)
            note += $" This matches every file named {glob} in the solution.";
        return note;
    }

    /// <summary>
    /// Search (mode text) <c>filePath: P</c> becomes <c>fileGlob: G</c> via <see cref="PathToFileGlob"/>: in text mode
    /// filePath only pins symbol resolution, so a text search would silently widen to the whole solution. Unlike the
    /// <c>path</c> rule, filePath IS a declared parameter, so this rule is keyed on mode text, not on "undeclared".
    /// Runs after <see cref="RewriteSearchPath"/>: acts only while <c>fileGlob</c> is declared and still absent
    /// (so an explicit or path-derived fileGlob wins and filePath stays); an unmappable value leaves the call unchanged.
    /// </summary>
    private static string? RewriteSearchFilePath(IDictionary<string, JsonElement> arguments, IReadOnlySet<string> declared, string? solutionRoot)
    {
        if (!declared.Contains("fileGlob") || arguments.ContainsKey("fileGlob"))
            return null;
        if (!IsTextMode(arguments))
            return null;
        if (!arguments.TryGetValue("filePath", out var raw) || raw.ValueKind != JsonValueKind.String)
            return null;

        var glob = PathToFileGlob(raw.GetString() ?? string.Empty, solutionRoot);
        if (glob is null)
            return null;

        Remove(arguments, "filePath");
        arguments["fileGlob"] = JsonSerializer.SerializeToElement(glob);
        var note = $"Note: 'filePath' only pins symbol resolution and is ignored by Search in text mode; it was treated as fileGlob: \"{glob}\". Use fileGlob to restrict a text search.";
        if (glob.IndexOf('/') < 0 && glob.IndexOfAny(GlobChars) < 0)
            note += $" This matches every file named {glob} in the solution.";
        return note;
    }

    /// <summary>
    /// ReadFile <c>offset: A</c> / <c>limit: B</c> become <c>startLine: A</c> / <c>endLine: A+B-1</c> (limit alone:
    /// <c>endLine: B</c>; offset alone: <c>startLine: A</c>). Skipped when startLine, endLine or memberName is supplied.
    /// </summary>
    private static string? RewriteReadFileRange(IDictionary<string, JsonElement> arguments, IReadOnlySet<string> declared)
    {
        if (declared.Contains("offset") || declared.Contains("limit"))
            return null;
        if (arguments.ContainsKey("startLine") || arguments.ContainsKey("endLine") || arguments.ContainsKey("memberName"))
            return null;

        var hasOffset = arguments.TryGetValue("offset", out var offsetRaw);
        var hasLimit = arguments.TryGetValue("limit", out var limitRaw);
        if (!hasOffset && !hasLimit)
            return null;

        var offset = 0;
        var limit = 0;
        if (hasOffset && (!TryGetInt(offsetRaw, out offset) || offset < 1))
            return null;
        if (hasLimit && (!TryGetInt(limitRaw, out limit) || limit < 1))
            return null;
        if ((hasOffset && !declared.Contains("startLine")) || (hasLimit && !declared.Contains("endLine")))
            return null;

        var end = 0;
        if (hasLimit)
        {
            var computed = hasOffset ? (long)offset + limit - 1 : limit;
            if (computed > int.MaxValue)
                return null;
            end = (int)computed;
        }

        var targets = new List<string>();
        var callArgs = new List<(string Name, string Value)>();
        if (hasOffset)
        {
            Remove(arguments, "offset");
            arguments["startLine"] = JsonSerializer.SerializeToElement(offset);
            targets.Add($"startLine: {offset}");
            callArgs.Add(("startLine", offset.ToString()));
        }

        if (hasLimit)
        {
            Remove(arguments, "limit");
            arguments["endLine"] = JsonSerializer.SerializeToElement(end);
            targets.Add($"endLine: {end}");
            callArgs.Add(("endLine", end.ToString()));
        }

        var sources = hasOffset && hasLimit
            ? "'offset' and 'limit' are not parameters"
            : hasOffset ? "'offset' is not a parameter" : "'limit' is not a parameter";
        var joined = string.Join(", ", targets);
        return $"Note: {sources} of ReadFile; they were treated as {joined}. Use {ToolCallHint.Call("ReadFile", callArgs.ToArray())} in future calls.";
    }

    /// <summary>
    /// RunTest <c>project: X</c> or <c>projectName: X</c> becomes <c>scope: "project", scopeName: X</c>, only when
    /// neither scope nor scopeName is supplied and the scope enum has a "project" member.
    /// </summary>
    private static string? RewriteRunTestProject(
        IDictionary<string, JsonElement> arguments,
        IReadOnlySet<string> declared,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!declared.Contains("scope") || !declared.Contains("scopeName"))
            return null;
        if (arguments.ContainsKey("scope") || arguments.ContainsKey("scopeName"))
            return null;

        var sources = new[] { "project", "projectName" };
        foreach (var source in sources)
        {
            if (declared.Contains(source) || !arguments.TryGetValue(source, out var raw) || !TryGetText(raw, out var project))
                continue;

            var scope = FindEnumMember(properties, "scope", "project");
            if (scope is null)
                return null;

            Remove(arguments, source);
            arguments["scope"] = JsonSerializer.SerializeToElement(scope);
            arguments["scopeName"] = JsonSerializer.SerializeToElement(project);
            return $"Note: '{source}' is not a parameter of RunTest; it was treated as scope: \"{scope}\", scopeName: \"{project}\". Use {ToolCallHint.Call("RunTest", ("scope", scope), ("scopeName", project))} in future calls.";
        }

        return null;
    }

    /// <summary>
    /// GetDiagnostics <c>filePath: X</c> becomes <c>scope: "file", scopeName: X</c>, only when filePath is not
    /// itself declared, neither scope nor scopeName is supplied and the scope enum has a "file" member.
    /// </summary>
    private static string? RewriteGetDiagnosticsFile(
        IDictionary<string, JsonElement> arguments,
        IReadOnlySet<string> declared,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (declared.Contains("filePath") || !declared.Contains("scope") || !declared.Contains("scopeName"))
            return null;
        if (arguments.ContainsKey("scope") || arguments.ContainsKey("scopeName"))
            return null;
        if (!arguments.TryGetValue("filePath", out var raw) || !TryGetText(raw, out var file))
            return null;

        var scope = FindEnumMember(properties, "scope", "file");
        if (scope is null)
            return null;

        Remove(arguments, "filePath");
        arguments["scope"] = JsonSerializer.SerializeToElement(scope);
        arguments["scopeName"] = JsonSerializer.SerializeToElement(file);
        return $"Note: 'filePath' is not a parameter of GetDiagnostics; it was treated as scope: \"{scope}\", scopeName: \"{file}\". Use {ToolCallHint.Call("GetDiagnostics", ("scope", scope), ("scopeName", file))} in future calls.";
    }

    /// <summary>
    /// Build <c>fullBuild: true</c> becomes <c>level: "fullBuild"</c> (false is left for Validate), and
    /// <c>mode: M</c> where M is a member of the level enum becomes <c>level: M</c>. Only when level is
    /// declared and not already supplied; the enum member is written in the schema's spelling.
    /// </summary>
    private static string? RewriteBuildLevel(
        IDictionary<string, JsonElement> arguments,
        IReadOnlySet<string> declared,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        if (!declared.Contains("level") || arguments.ContainsKey("level"))
            return null;

        if (!declared.Contains("fullBuild") && arguments.TryGetValue("fullBuild", out var fullBuild) && IsTrue(fullBuild))
        {
            var level = FindEnumMember(properties, "level", "fullBuild");
            if (level is not null)
            {
                Remove(arguments, "fullBuild");
                arguments["level"] = JsonSerializer.SerializeToElement(level);
                return $"Note: 'fullBuild' is not a parameter of Build; it was treated as level: \"{level}\". Use {ToolCallHint.Call("Build", ("level", level))} in future calls.";
            }
        }

        if (!declared.Contains("mode") && arguments.TryGetValue("mode", out var mode) && TryGetText(mode, out var wanted))
        {
            var level = FindEnumMember(properties, "level", wanted);
            if (level is not null)
            {
                Remove(arguments, "mode");
                arguments["level"] = JsonSerializer.SerializeToElement(level);
                return $"Note: 'mode' is not a parameter of Build; it was treated as level: \"{level}\". Use {ToolCallHint.Call("Build", ("level", level))} in future calls.";
            }
        }

        return null;
    }

    /// <summary>
    /// Removes <paramref name="name"/> from <paramref name="arguments"/>. Keys are compared exactly (ordinal),
    /// the same way <see cref="ToolArgumentValidator"/> compares them against the declared set; case-only
    /// mismatches against declared names are repaired earlier by the case-normalization pass.
    /// </summary>
    private static bool Remove(IDictionary<string, JsonElement> arguments, string name) =>
        arguments.Remove(name);

    /// <summary>
    /// Moves the value at <paramref name="from"/> to <paramref name="to"/>; false (nothing changed) when
    /// <paramref name="from"/> is absent or <paramref name="to"/> is already supplied.
    /// </summary>
    private static bool Rename(IDictionary<string, JsonElement> arguments, string from, string to)
    {
        if (!arguments.TryGetValue(from, out var value) || arguments.ContainsKey(to))
            return false;

        arguments.Remove(from);
        arguments[to] = value;
        return true;
    }

    /// <summary>Characters that mark a Search path value as already being a glob (plan rule 5).</summary>
    private static readonly char[] GlobChars = ['*', '?', '{'];

    /// <summary>
    /// Benign-ignore table (plan_unknown_parameter_rejection_reduction.md, "Benign-ignore list"). A row removes
    /// an undeclared key only when ignoring it cannot change which results come back; when the predicate fails
    /// the key stays for <see cref="ToolArgumentValidator.Validate"/> to reject.
    /// </summary>
    private static readonly IgnoreRule[] IgnoreRules =
    [
        new("Search", "output_mode", (_, v) => IsOneOf(v, "content", "files_with_matches", "count"), "Search always returns every match with file, line and preview"),
        new("Search", "outputMode", (_, v) => IsOneOf(v, "content", "files_with_matches", "count"), "Search always returns every match with file, line and preview"),
        new("Search", "useRegex", (a, v) => IsTrue(v) && IsTextMode(a), "text mode already evaluates literal and regex matches in one pass"),
        new("Search", "isRegex", (a, v) => IsTrue(v) && IsTextMode(a), "text mode already evaluates literal and regex matches in one pass"),
        new("Search", "regex", (a, v) => IsTrue(v) && IsTextMode(a), "text mode already evaluates literal and regex matches in one pass"),
        new("Search", "searchMode", (a, v) => IsOneOf(v, "regex") && IsTextMode(a), "text mode already evaluates literal and regex matches in one pass"),
        new("Search", "-n", (_, v) => IsTrue(v), "every match already has a line number"),
        new("Search", "type", (_, v) => IsOneOf(v, "cs", "csharp"), "Search only looks at C# files"),
        new("ReadFile", "allow_large", (_, v) => IsTrue(v), "there is no size gate to open; a large result is returned as a resultId"),
        new("GetMethodSource", "includeDocComments", (_, v) => IsTrue(v), "doc comments are already included in the returned source"),
    ];
}
