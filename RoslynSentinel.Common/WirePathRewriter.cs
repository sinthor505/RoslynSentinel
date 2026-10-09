using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynSentinel.Common;

/// <summary>
/// Pure helpers that rewrite absolute file paths under the solution root to root-relative
/// forward-slash form inside a tool response (JSON text), to cut the per-row path prefix from
/// every response. No MCP types: the response filter that calls this lives in Server.Basic.
/// See docs/current/plans/plan_wire_relative_paths_backstop_filter.md for the rules.
/// </summary>
public static class WirePathRewriter
{
    /// <summary>
    /// Property names whose string values are human-readable messages: every occurrence of
    /// <c>&lt;root&gt;\</c> or <c>&lt;root&gt;/</c> inside them is removed (substring rule).
    /// </summary>
    public static readonly IReadOnlySet<string> MessageKeys = new[]
    {
        "statusMessage", "message", "detail", "warningDetails",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Property names whose values are code or text a model may copy back verbatim (for example into
    /// <c>ReplaceSnippet.oldContent</c>). Values under these keys, at any depth, are never rewritten.
    /// Object keys under them are still rewritten.
    /// </summary>
    public static readonly IReadOnlySet<string> VerbatimKeys = new[]
    {
        "source", "preview", "contextSnippet", "codeSnippet", "lineText", "text", "content", "diff",
        "oldContent", "newContent", "newText", "changedContent", "beforeContent", "afterContent",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Property names whose whole value (keys and values, any depth) is left absolute, because it is
    /// fed back to something that does not resolve against the solution root or points outside the
    /// workspace. The <c>largeResult</c> subtree is also kept absolute (see <see cref="LargeResultKey"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> KeepAbsoluteKeys = new[]
    {
        "solutionPath", "solutionRoot", "serverBinaryPath", "binaryPath", "baseRepoDir",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Name of the offload pointer subtree that always stays absolute.</summary>
    private const string LargeResultKey = "largeResult";

    /// <summary>Name of the property stamped as the first property of a rewritten response.</summary>
    private const string SolutionRootKey = "solutionRoot";

    /// <summary>
    /// Whole-value rule: when <paramref name="value"/> is entirely a path under <paramref name="root"/>
    /// (case-insensitive, a <c>\</c> or <c>/</c> required right after the root, no CR/LF) returns true
    /// and the root-relative path with <c>/</c> separators. The exact root alone, a sibling that merely
    /// shares the prefix, a path outside the root, and anything that would need a <c>..</c> segment are
    /// not rewritten.
    /// </summary>
    /// <param name="value">Candidate string.</param>
    /// <param name="root">Solution root; trailing <c>\</c> and <c>/</c> are ignored.</param>
    /// <param name="relative">The relative path when true; otherwise <paramref name="value"/>.</param>
    public static bool TryRelativize(string value, string root, out string relative)
    {
        relative = value;
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(root))
            return false;

        var trimmedRoot = root.TrimEnd('\\', '/');
        if (trimmedRoot.Length == 0)
            return false;

        if (value.Length <= trimmedRoot.Length + 1)
            return false;

        if (value.IndexOfAny(['\r', '\n']) >= 0)
            return false;

        if (!value.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        var separator = value[trimmedRoot.Length];
        if (separator != '\\' && separator != '/')
            return false;

        var rest = value.Substring(trimmedRoot.Length + 1).Replace('\\', '/');
        if (rest.Length == 0 || rest[0] == '/' || HasDotDotSegment(rest))
            return false;

        relative = rest;
        return true;
    }

    /// <summary>
    /// Substring rule: removes every case-insensitive occurrence of <c>&lt;root&gt;\</c> or
    /// <c>&lt;root&gt;/</c> from <paramref name="text"/> (an occurrence followed by a <c>..</c> segment
    /// is kept, so a <c>..</c> is never exposed).
    /// </summary>
    /// <param name="text">Message text.</param>
    /// <param name="root">Solution root; trailing <c>\</c> and <c>/</c> are ignored.</param>
    /// <param name="replacements">Number of prefixes removed.</param>
    public static string RelativizeInText(string text, string root, out int replacements)
    {
        replacements = 0;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(root))
            return text;

        var trimmedRoot = root.TrimEnd('\\', '/');
        if (trimmedRoot.Length == 0)
            return text;

        StringBuilder? sb = null;
        var copiedUpTo = 0;
        var searchFrom = 0;

        while (searchFrom < text.Length)
        {
            var index = text.IndexOf(trimmedRoot, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;

            var afterRoot = index + trimmedRoot.Length;
            var isUnderRoot = afterRoot < text.Length
                && (text[afterRoot] == '\\' || text[afterRoot] == '/')
                && !StartsWithDotDotSegment(text, afterRoot + 1);

            if (!isUnderRoot)
            {
                searchFrom = index + 1;
                continue;
            }

            sb ??= new StringBuilder(text.Length);
            sb.Append(text, copiedUpTo, index - copiedUpTo);
            copiedUpTo = afterRoot + 1;
            searchFrom = copiedUpTo;
            replacements++;
        }

        if (sb is null)
            return text;

        sb.Append(text, copiedUpTo, text.Length - copiedUpTo);
        return sb.ToString();
    }

    /// <summary>
    /// Rewrites paths under <paramref name="root"/> in a JSON-object response text. Returns null when
    /// <paramref name="text"/> is not a JSON object, does not parse, or nothing was rewritten (the
    /// caller then keeps the original text byte-for-byte). Otherwise builds a new tree: whole-value
    /// paths and object keys are made root-relative, message-key values lose the root prefix, verbatim
    /// values and keep-absolute subtrees (including <c>largeResult</c>) are untouched, and
    /// <c>"solutionRoot"</c> is inserted as the first property of the root object. Serialized with
    /// <see cref="SharedJsonOptions.Compact"/>.
    /// </summary>
    /// <param name="text">Response text (the first text block of a tool result).</param>
    /// <param name="root">Solution root.</param>
    /// <param name="rewrites">Number of values and keys rewritten.</param>
    public static string? RewriteJsonText(string text, string root, out int rewrites)
    {
        rewrites = 0;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(root))
            return null;

        if (!text.AsSpan().TrimStart().StartsWith("{"))
            return null;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        if (parsed is not JsonObject original)
            return null;

        var context = new RewriteContext(root);
        var rewritten = Rewrite(original, null, false, context) as JsonObject;
        if (rewritten is null || context.Aborted || context.Count == 0)
            return null;

        if (!rewritten.ContainsKey(SolutionRootKey))
            rewritten.Insert(0, SolutionRootKey, JsonValue.Create(root));

        rewrites = context.Count;
        return rewritten.ToJsonString(SharedJsonOptions.Compact);
    }

    private sealed class RewriteContext(string root)
    {
        public string Root { get; } = root;
        public int Count { get; set; }

        /// <summary>Set when rewriting would collide two object keys; the whole rewrite is dropped.</summary>
        public bool Aborted { get; set; }
    }

    private static JsonNode? Rewrite(JsonNode? node, string? parentKey, bool verbatim, RewriteContext context)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
                return RewriteObject(obj, verbatim, context);

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var element in array)
                    result.Add(Rewrite(element, parentKey, verbatim, context));
                return result;
            }

            case JsonValue value:
                return RewriteValue(value, parentKey, verbatim, context);

            default:
                return node.DeepClone();
        }
    }

    private static JsonObject RewriteObject(JsonObject obj, bool verbatim, RewriteContext context)
    {
        var result = new JsonObject();
        foreach (var (key, child) in obj)
        {
            var isKeepAbsolute = KeepAbsoluteKeys.Contains(key)
                || string.Equals(key, LargeResultKey, StringComparison.OrdinalIgnoreCase);

            var newKey = key;
            if (!isKeepAbsolute && TryRelativize(key, context.Root, out var relativeKey))
            {
                newKey = relativeKey;
                context.Count++;
            }

            if (result.ContainsKey(newKey))
            {
                // Two keys collapsed to the same relative path (or collided with a literal one).
                context.Aborted = true;
                return result;
            }

            JsonNode? newChild = isKeepAbsolute
                ? child?.DeepClone()
                : Rewrite(child, key, verbatim || VerbatimKeys.Contains(key), context);

            result.Add(newKey, newChild);
        }

        return result;
    }

    private static JsonNode? RewriteValue(JsonValue value, string? parentKey, bool verbatim, RewriteContext context)
    {
        if (verbatim || !value.TryGetValue<string>(out var s) || s is null)
            return value.DeepClone();

        if (TryRelativize(s, context.Root, out var relative))
        {
            context.Count++;
            return JsonValue.Create(relative);
        }

        if (parentKey is not null && MessageKeys.Contains(parentKey))
        {
            var replaced = RelativizeInText(s, context.Root, out var n);
            if (n > 0)
            {
                context.Count += n;
                return JsonValue.Create(replaced);
            }
        }

        return value.DeepClone();
    }

    private static bool HasDotDotSegment(string relativeWithForwardSlashes)
    {
        foreach (var segment in relativeWithForwardSlashes.Split('/'))
        {
            if (segment == "..")
                return true;
        }

        return false;
    }

    private static bool StartsWithDotDotSegment(string text, int start)
    {
        if (start + 1 >= text.Length || text[start] != '.' || text[start + 1] != '.')
            return false;

        return start + 2 >= text.Length || text[start + 2] == '\\' || text[start + 2] == '/';
    }
}
