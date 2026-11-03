using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using BulkNode = (string Path, System.Text.Json.Nodes.JsonNode Node, System.Text.Json.Nodes.JsonNode Parent, string? Key, int Index, int Count);

namespace RoslynSentinel.Common;

/// <summary>
/// Pure pager for a tool result's wire envelope (plan_wire_result_marshaller.md Step 3; not wired into any tool yet).
/// <see cref="Fit(JsonNode, int, JsonSerializerOptions, string?, IReadOnlyCollection{string})"/> takes the
/// envelope as a <see cref="JsonNode"/>, a budget in characters and the wire <see cref="JsonSerializerOptions"/>, and
/// returns a fitted copy plus one <see cref="OmittedPart"/> per cut. The input is never modified.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pinned</b> (never cut): everything outside <c>successData</c> and <c>errorData</c> (top-level scalars,
/// <c>statusMessage</c>, <c>warningDetails</c>, <c>listSummary</c>, <c>findings</c>, stamp keys), and
/// <c>errorData.errorCode</c> / <c>errorData.message</c>.
/// <b>Bulk</b>: every array and string under <c>successData</c> and <c>errorData</c>, at any depth (the data node
/// itself counts when it is an array or string).
/// </para>
/// <para>
/// <b>Order of work.</b> Nodes named <see cref="NeverInlineAttribute"/> are always moved out first. While the
/// envelope is over budget the declared records node (<see cref="ResultRecordsAttribute"/>) is cut, else the bulk node with
/// the longest serialized form. An array keeps a prefix of elements, a string keeps a prefix of whole lines
/// (line terminators kept), found by binary search for the longest prefix that fits, and never fewer than one element
/// or line. When every cuttable node is at its minimum and the envelope is still over budget, the largest remaining
/// bulk node is replaced by an empty prefix (empty array or string) and recorded with <c>Returned = 0</c>. A
/// result whose pinned part alone exceeds the budget is returned as small as the rules allow.
/// </para>
/// <para>
/// Measurement is the length of <c>ToJsonString(wireOptions)</c> of the whole envelope. Paths use wire names, dots
/// for properties and <c>[i]</c> for array elements (<c>successData.regexResults</c>, <c>successData.sites[3].matches</c>).
/// </para>
/// </remarks>
public static class ResultPager
{
    /// <summary><see cref="OmittedPart.Unit"/> for array elements.</summary>
    public const string UnitItems = "items";

    /// <summary><see cref="OmittedPart.Unit"/> for string lines.</summary>
    public const string UnitLines = "lines";

    /// <summary><see cref="OmittedPart.Unit"/> for a node of another kind moved out whole (serialized characters).</summary>
    public const string UnitChars = "chars";

    private const string SuccessKey = "successData";
    private const string ErrorKey = "errorData";

    private static readonly ConcurrentDictionary<(Type Type, JsonNamingPolicy? Policy), (string? RecordsPath, IReadOnlyList<string> NeverInlinePaths)> HintCache = new();

    /// <summary>
    /// Resolves the hints declared on <paramref name="successDataType"/>'s public properties: the one
    /// <see cref="ResultRecordsAttribute"/> property and every <see cref="NeverInlineAttribute"/> property, as wire
    /// paths under <c>successData</c> (<c>[JsonPropertyName]</c>, else the options' naming policy, else the C# name).
    /// Cached per type and naming policy. A null type or one without hints gives (null, empty).
    /// </summary>
    public static (string? RecordsPath, IReadOnlyList<string> NeverInlinePaths) ResolveHints(Type? successDataType, JsonSerializerOptions wireOptions)
    {
        ArgumentNullException.ThrowIfNull(wireOptions);
        if (successDataType is null)
        {
            return (null, []);
        }

        return HintCache.GetOrAdd((successDataType, wireOptions.PropertyNamingPolicy), static key => ReflectHints(key.Type, key.Policy));
    }

    private static (string? RecordsPath, IReadOnlyList<string> NeverInlinePaths) ReflectHints(Type type, JsonNamingPolicy? policy)
    {
        string? recordsPath = null;
        var neverInline = new List<string>();

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            bool records = property.GetCustomAttribute<ResultRecordsAttribute>() is not null;
            bool never = property.GetCustomAttribute<NeverInlineAttribute>() is not null;
            if (!records && !never)
            {
                continue;
            }

            string wireName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? policy?.ConvertName(property.Name)
                ?? property.Name;
            string path = SuccessKey + "." + wireName;

            if (records)
            {
                recordsPath ??= path;
            }

            if (never)
            {
                neverInline.Add(path);
            }
        }

        return (recordsPath, neverInline);
    }

    /// <summary>
    /// Fits <paramref name="envelope"/> to <paramref name="budgetChars"/> using the hints declared on
    /// <paramref name="successDataType"/> (the runtime type of the result's success data; null for none).
    /// </summary>
    public static (JsonNode Inline, IReadOnlyList<OmittedPart> Omitted) Fit(
        JsonNode envelope, int budgetChars, JsonSerializerOptions wireOptions, Type? successDataType)
    {
        var (recordsPath, neverInlinePaths) = ResolveHints(successDataType, wireOptions);
        return Fit(envelope, budgetChars, wireOptions, recordsPath, neverInlinePaths);
    }

    /// <summary>
    /// Fits <paramref name="envelope"/> to <paramref name="budgetChars"/>. <paramref name="recordsPath"/> is the declared
    /// records path (null when not declared); <paramref name="neverInlinePaths"/> are moved out unconditionally.
    /// </summary>
    public static (JsonNode Inline, IReadOnlyList<OmittedPart> Omitted) Fit(
        JsonNode envelope,
        int budgetChars,
        JsonSerializerOptions wireOptions,
        string? recordsPath,
        IReadOnlyCollection<string> neverInlinePaths)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(wireOptions);
        ArgumentNullException.ThrowIfNull(neverInlinePaths);

        JsonNode root = envelope.DeepClone();
        var omitted = new List<OmittedPart>();
        if (root is not JsonObject rootObject)
        {
            return (root, omitted);
        }

        foreach (string path in neverInlinePaths)
        {
            MoveOut(rootObject, path, omitted);
        }

        // Phase 1: cut. A node with one element or line left is not cuttable any more.
        while (Measure(root, wireOptions) > budgetChars)
        {
            var candidates = EnumerateBulk(rootObject).Where(c => c.Count > 1).ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            BulkNode chosen = candidates.FirstOrDefault(c => recordsPath is not null && c.Path == recordsPath);
            bool declared = chosen.Node is not null;
            if (!declared)
            {
                chosen = LargestOf(candidates, wireOptions);
            }

            CutToFit(root, chosen, budgetChars, wireOptions, omitted, inferred: !declared);
        }

        // Phase 2: whatever is still over budget is a single element or line that is too big on its own.
        while (Measure(root, wireOptions) > budgetChars)
        {
            var candidates = EnumerateBulk(rootObject).Where(c => c.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            BulkNode chosen = LargestOf(candidates, wireOptions);
            ReplaceWithEmpty(chosen, omitted, inferred: recordsPath is null || chosen.Path != recordsPath);
        }

        return (root, omitted);
    }

    /// <summary>
    /// Selects the node an <see cref="OmittedPart.Path"/> names: property names separated by dots and <c>[i]</c> array
    /// indexes (<c>successData.regexResults</c>, <c>successData.groups[0].items</c>). Null when any step is missing or of the
    /// wrong kind. The empty path selects <paramref name="root"/>. The same syntax <see cref="Fit(JsonNode, int, JsonSerializerOptions, string?, IReadOnlyCollection{string})"/> writes.
    /// </summary>
    public static JsonNode? SelectNode(JsonNode root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        JsonNode? current = root;
        int i = 0;
        while (i < path.Length && current is not null)
        {
            if (path[i] == '.')
            {
                i++;
            }
            else if (path[i] == '[')
            {
                int close = path.IndexOf(']', i);
                if (close < 0 ||
                    !int.TryParse(path.AsSpan(i + 1, close - i - 1), out int index) ||
                    current is not JsonArray array ||
                    index < 0 ||
                    index >= array.Count)
                {
                    return null;
                }

                current = array[index];
                i = close + 1;
            }
            else
            {
                int end = i;
                while (end < path.Length && path[end] != '.' && path[end] != '[')
                {
                    end++;
                }

                current = current is JsonObject obj && obj.TryGetPropertyValue(path[i..end], out JsonNode? child) ? child : null;
                i = end;
            }
        }

        return current;
    }

    /// <summary>
    /// Paths of the bulk nodes under <c>successData</c>/<c>errorData</c> that can be paged (arrays with more than one element,
    /// strings with more than one line), largest serialized form first, at most <paramref name="max"/>. Used when a stored
    /// envelope carries no omitted list, and to tell a caller which paths exist when theirs does not.
    /// </summary>
    public static IReadOnlyList<string> PageablePaths(JsonNode envelope, int max)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope is not JsonObject root)
        {
            return [];
        }

        return EnumerateBulk(root)
            .Where(c => c.Count > 1)
            .OrderByDescending(c => c.Node.ToJsonString().Length)
            .Take(Math.Max(0, max))
            .Select(c => c.Path)
            .ToList();
    }

    private static int Measure(JsonNode root, JsonSerializerOptions options) => root.ToJsonString(options).Length;

    private static BulkNode LargestOf(List<BulkNode> candidates, JsonSerializerOptions options)
    {
        BulkNode best = candidates[0];
        int bestLength = best.Node.ToJsonString(options).Length;
        for (int i = 1; i < candidates.Count; i++)
        {
            int length = candidates[i].Node.ToJsonString(options).Length;
            if (length > bestLength)
            {
                best = candidates[i];
                bestLength = length;
            }
        }

        return best;
    }

    // ---- enumeration ----

    private static IEnumerable<BulkNode> EnumerateBulk(JsonObject root)
    {
        foreach (string key in new[] { SuccessKey, ErrorKey })
        {
            if (root[key] is { } data)
            {
                foreach (BulkNode node in Walk(data, key, root, key, -1, isErrorDataRoot: key == ErrorKey))
                {
                    yield return node;
                }
            }
        }
    }

    private static IEnumerable<BulkNode> Walk(JsonNode node, string path, JsonNode parent, string? key, int index, bool isErrorDataRoot)
    {
        switch (node)
        {
            case JsonArray array:
                yield return (path, array, parent, key, index, array.Count);
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child)
                    {
                        foreach (BulkNode nested in Walk(child, path + "[" + i + "]", array, null, i, isErrorDataRoot: false))
                        {
                            yield return nested;
                        }
                    }
                }

                break;

            case JsonValue value when value.TryGetValue(out string? text):
                yield return (path, value, parent, key, index, CountLines(text));
                break;

            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj.ToList())
                {
                    if (property.Value is not { } child ||
                        (isErrorDataRoot && (property.Key == "errorCode" || property.Key == "message")))
                    {
                        continue;
                    }

                    foreach (BulkNode nested in Walk(child, path + "." + property.Key, obj, property.Key, -1, isErrorDataRoot: false))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    // ---- lines ----

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int count = 0;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return text[^1] == '\n' ? count : count + 1;
    }

    /// <summary>
    /// Whole lines with their terminators, so concatenating a prefix is an exact prefix of the original. The line
    /// definition behind <see cref="UnitLines"/>: a final piece without a terminator counts as a line; empty text has none.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            int end = newline < 0 ? text.Length : newline + 1;
            lines.Add(text.Substring(start, end - start));
            start = end;
        }

        return lines;
    }

    // ---- cutting ----

    private static void SetChild(JsonNode parent, string? key, int index, JsonNode? replacement)
    {
        if (parent is JsonObject obj)
        {
            obj[key!] = replacement;
        }
        else if (parent is JsonArray array)
        {
            array[index] = replacement;
        }
    }

    private static void CutToFit(JsonNode root, BulkNode node, int budget, JsonSerializerOptions options, List<OmittedPart> omitted, bool inferred)
    {
        int originalTotal = omitted.FirstOrDefault(p => p.Path == node.Path)?.Total ?? node.Count;
        int current = node.Count;

        Action<int> setKeep;
        string unit;
        if (node.Node is JsonArray array)
        {
            unit = UnitItems;
            List<JsonNode?> all = array.ToList();
            setKeep = keep =>
            {
                while (array.Count > keep)
                {
                    array.RemoveAt(array.Count - 1);
                }

                while (array.Count < keep)
                {
                    array.Add(all[array.Count]);
                }
            };
        }
        else
        {
            unit = UnitLines;
            List<string> lines = SplitLines(node.Node.GetValue<string>());
            setKeep = keep => SetChild(node.Parent, node.Key, node.Index, JsonValue.Create(string.Concat(lines.Take(keep))));
        }

        // Longest prefix of 1..current-1 that fits; if even one element or line does not fit, keep one.
        int best = 1;
        setKeep(1);
        if (Measure(root, options) <= budget)
        {
            int low = 2;
            int high = current - 1;
            while (low <= high)
            {
                int mid = low + ((high - low) / 2);
                setKeep(mid);
                if (Measure(root, options) <= budget)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
        }

        setKeep(best);

        if (unit == UnitItems)
        {
            RemoveDescendants(omitted, node.Path + "[", firstKeptIndexExclusive: best);
        }

        Upsert(omitted, new OmittedPart(node.Path, best, originalTotal, unit, inferred));
    }

    private static void ReplaceWithEmpty(BulkNode node, List<OmittedPart> omitted, bool inferred)
    {
        int originalTotal = omitted.FirstOrDefault(p => p.Path == node.Path)?.Total ?? node.Count;
        string unit;
        if (node.Node is JsonArray array)
        {
            unit = UnitItems;
            array.Clear();
        }
        else
        {
            unit = UnitLines;
            SetChild(node.Parent, node.Key, node.Index, JsonValue.Create(string.Empty));
        }

        RemoveDescendants(omitted, node.Path + "[", firstKeptIndexExclusive: 0);
        RemoveDescendants(omitted, node.Path + ".", firstKeptIndexExclusive: 0);
        Upsert(omitted, new OmittedPart(node.Path, 0, originalTotal, unit, inferred));
    }

    // A NeverInline node: always empty inline. Arrays and strings keep their shape (empty); any other kind is removed.
    private static void MoveOut(JsonObject root, string path, List<OmittedPart> omitted)
    {
        string[] segments = path.Split('.');
        JsonObject current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject next)
            {
                return;
            }

            current = next;
        }

        string leaf = segments[^1];
        if (current[leaf] is not { } node)
        {
            return;
        }

        switch (node)
        {
            case JsonArray array:
                if (array.Count > 0)
                {
                    int count = array.Count;
                    array.Clear();
                    Upsert(omitted, new OmittedPart(path, 0, count, UnitItems, Inferred: false));
                }

                break;

            case JsonValue value when value.TryGetValue(out string? text):
                if (text.Length > 0)
                {
                    current[leaf] = JsonValue.Create(string.Empty);
                    Upsert(omitted, new OmittedPart(path, 0, CountLines(text), UnitLines, Inferred: false));
                }

                break;

            default:
                int length = node.ToJsonString().Length;
                current.Remove(leaf);
                Upsert(omitted, new OmittedPart(path, 0, length, UnitChars, Inferred: false));
                break;
        }
    }

    private static void Upsert(List<OmittedPart> omitted, OmittedPart part)
    {
        int existing = omitted.FindIndex(p => p.Path == part.Path);
        if (existing >= 0)
        {
            omitted[existing] = part;
        }
        else
        {
            omitted.Add(part);
        }
    }

    // Drops parts recorded inside elements that a later cut removed. For prefix "path[" only indexes at or past the
    // kept count are stale; for any other prefix everything below it is.
    private static void RemoveDescendants(List<OmittedPart> omitted, string prefix, int firstKeptIndexExclusive)
    {
        omitted.RemoveAll(p =>
        {
            if (!p.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (!prefix.EndsWith('['))
            {
                return true;
            }

            int close = p.Path.IndexOf(']', prefix.Length);
            return close > prefix.Length &&
                int.TryParse(p.Path.AsSpan(prefix.Length, close - prefix.Length), out int index) &&
                index >= firstKeptIndexExclusive;
        });
    }
}
