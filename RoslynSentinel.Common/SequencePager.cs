using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

/// <summary>
/// Pure sequence pager for a tool result's wire envelope, used by <see cref="ToolResultMarshaller"/> for the first page and
/// by <c>GetLargeResult</c> for later ones (docs/current/references/reference_tool_response_pipeline.md;
/// design: proposal_sequence_pager.md). Every pageable result is one ordered sequence of
/// units (array elements, or lines of the one large string) and every unit fits under the size cap.
/// <see cref="ResolveSequence"/> picks the sequence, <see cref="BuildPage"/> builds one page of it. The input envelope
/// is never modified.
/// </summary>
/// <remarks>
/// <para>
/// This pager does not replace <see cref="ResultPager.Fit(JsonNode, int, JsonSerializerOptions, Type?)"/>: a result
/// with no array and no large string directly under <c>successData</c> (dictionaries of strings, nested shapes,
/// scalar-only data) resolves to <see cref="SequenceKind.None"/> and the caller keeps using <c>Fit</c> for it.
/// </para>
/// <para>
/// Measurement is the length of <c>ToJsonString(wireOptions)</c>; the page size is the size of the envelope with the
/// sequence emptied plus the sum of the units' own serialized sizes (plus one comma per extra array element). That sum
/// is exact for compact <c>wireOptions</c> (the marshaller's); with indented options it underestimates.
/// </para>
/// </remarks>
public static class SequencePager
{
    /// <summary>
    /// Default <see cref="SequenceDescriptor.MaxUnitChars"/>: a line longer than this is split into chunks of this size.
    /// A fixed value, not the page budget, so the unit order stays the same for every caller that pages the same result.
    /// </summary>
    public const int DefaultMaxUnitChars = 4000;

    private const string SuccessKey = "successData";

    /// <summary>
    /// Picks the pageable sequence of <paramref name="envelope"/> (Decided 1 and 6), first match wins:
    /// <list type="number">
    /// <item><description><paramref name="hints"/>: the arrays its paths resolve to, in declared order (a path that does not
    /// resolve to an array is ignored and logged at debug level; when none resolves the next rule applies).</description></item>
    /// <item><description>The <see cref="ResultRecordsAttribute"/> list on <paramref name="successDataType"/>, one list, when it
    /// resolves to a non-empty array (an empty declared list falls through, so a regex search whose declared literal list is
    /// empty still pages its other list).</description></item>
    /// <item><description>The default rule. A bare-array <c>successData</c> is one list; a string <c>successData</c> is its lines.
    /// For an object, EMPTY arrays are ignored, then the longest string directly under <c>successData</c> is compared with the
    /// combined serialized size of the non-empty arrays: the larger wins (a tie goes to the lists). Lists: every non-empty array
    /// directly under <c>successData</c>, in document order. <see cref="SequenceKind.Lines"/>: that longest string. A diff or
    /// source text that is the tool's main output therefore pages by lines even beside empty or small lists (Decided 9).</description></item>
    /// <item><description>Otherwise <see cref="SequenceDescriptor.None"/>: the caller keeps using
    /// <see cref="ResultPager.Fit(JsonNode, int, JsonSerializerOptions, Type?)"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="envelope">The full wire envelope.</param>
    /// <param name="hints">The envelope's <c>Paging</c> hints, or null.</param>
    /// <param name="successDataType">The runtime type of the success data (for <see cref="ResultRecordsAttribute"/>), or null.</param>
    /// <param name="wireOptions">The wire serializer options (property naming policy for attribute and hint names).</param>
    /// <param name="logger">Receives the debug line for an ignored hint path; may be null.</param>
    /// <param name="maxUnitChars">Stored in the descriptor for Lines: the chunk size for an overlong line.</param>
    public static SequenceDescriptor ResolveSequence(
        JsonObject envelope,
        PagingHints? hints,
        Type? successDataType,
        JsonSerializerOptions wireOptions,
        ILogger? logger = null,
        int maxUnitChars = DefaultMaxUnitChars)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(wireOptions);
        if (maxUnitChars < 1)
        {
            maxUnitChars = DefaultMaxUnitChars;
        }

        if (envelope[SuccessKey] is not { } data)
        {
            return SequenceDescriptor.None;
        }

        if (hints?.RecordsPaths is { Count: > 0 } hinted)
        {
            var paths = new List<string>();
            foreach (string hint in hinted)
            {
                string? resolved = ResolveArrayPath(envelope, hint, wireOptions);
                if (resolved is null)
                {
                    logger?.LogDebug("Paging hint path '{HintPath}' does not resolve to an array in the result and is ignored.", hint);
                    continue;
                }

                if (!paths.Contains(resolved, StringComparer.Ordinal))
                {
                    paths.Add(resolved);
                }
            }

            // A path under another hinted array would be paged twice; the outer one wins.
            paths = paths
                .Where(p => !paths.Any(other => other != p && p.StartsWith(other + "[", StringComparison.Ordinal)))
                .ToList();
            if (paths.Count > 0)
            {
                return new SequenceDescriptor(SequenceKind.Lists, paths, hints.Order, 0);
            }
        }

        string? declared = ResultPager.ResolveHints(successDataType, wireOptions).RecordsPath;
        if (declared is not null && ResultPager.SelectNode(envelope, declared) is JsonArray { Count: > 0 })
        {
            return new SequenceDescriptor(SequenceKind.Lists, [declared], PagingOrder.Interleaved, 0);
        }

        switch (data)
        {
            case JsonArray:
                return new SequenceDescriptor(SequenceKind.Lists, [SuccessKey], PagingOrder.Interleaved, 0);

            case JsonObject obj:
                // Default rule only (hints and [ResultRecords] were handled above): empty arrays are not a sequence (an empty
                // "attributes": [] must not win over a 20 KB "source"). The larger of the longest string and the combined
                // serialized size of the non-empty arrays is what is over budget, so it is what pages; a tie goes to the lists.
                var arrays = obj.Where(p => p.Value is JsonArray { Count: > 0 }).ToList();
                string? longest = null;
                int longestLength = 0;
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    if (property.Value is JsonValue value && value.TryGetValue(out string? text) && text.Length > longestLength)
                    {
                        longest = property.Key;
                        longestLength = text.Length;
                    }
                }

                int arraysSize = arrays.Count == 0 ? 0 : arrays.Sum(p => p.Value!.ToJsonString(wireOptions).Length);
                if (arrays.Count > 0 && (longest is null || longestLength <= arraysSize))
                {
                    return new SequenceDescriptor(SequenceKind.Lists, arrays.Select(p => SuccessKey + "." + p.Key).ToList(), PagingOrder.Interleaved, 0);
                }

                return longest is null
                    ? SequenceDescriptor.None
                    : new SequenceDescriptor(SequenceKind.Lines, [SuccessKey + "." + longest], PagingOrder.Interleaved, maxUnitChars);

            case JsonValue scalar when scalar.TryGetValue(out string? whole) && whole.Length > 0:
                return new SequenceDescriptor(SequenceKind.Lines, [SuccessKey], PagingOrder.Interleaved, maxUnitChars);

            default:
                return SequenceDescriptor.None;
        }
    }

    /// <summary>
    /// The number of units in <paramref name="sequence"/> within <paramref name="envelope"/>: array elements summed over
    /// the lists, or lines (plus extra chunks of overlong lines) of the string. A path that no longer resolves counts as
    /// empty. Zero for <see cref="SequenceKind.None"/>.
    /// </summary>
    public static int CountUnits(JsonObject envelope, SequenceDescriptor sequence)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(sequence);

        switch (sequence.Kind)
        {
            case SequenceKind.Lists:
                return sequence.Paths.Sum(p => (ResultPager.SelectNode(envelope, p) as JsonArray)?.Count ?? 0);

            case SequenceKind.Lines:
                return LineUnits(TextAt(envelope, sequence.Paths[0]), sequence.MaxUnitChars).Count;

            default:
                return 0;
        }
    }

    /// <summary>
    /// Builds the page of <paramref name="sequence"/> that starts at unit <paramref name="offset"/> (Decided 3 and 4).
    /// Everything outside the sequence is unchanged; each list holds only its share of the page (an empty array when it
    /// has none, each record in its original JSON), or the string holds the page's lines concatenated. Units are added
    /// in sequence order until <paramref name="limit"/> is reached or the next one would push the estimated page size
    /// over <paramref name="budgetChars"/>. At least one unit is always returned (when any remain): an oversize unit goes
    /// alone and the result carries <see cref="SequencePage.ContractViolation"/>; nothing is cut inside a unit and nothing
    /// throws. <paramref name="offset"/> at or past the end (or negative) is clamped and gives an empty page when past the
    /// end. <paramref name="limit"/> of zero or less means as many as fit. A <see cref="SequenceKind.None"/> sequence
    /// returns an unchanged copy with zero units. The page copies the whole envelope, so the cost is linear in its size.
    /// </summary>
    /// <param name="fullEnvelope">The full wire envelope; not modified.</param>
    /// <param name="sequence">From <see cref="ResolveSequence"/> on the same envelope.</param>
    /// <param name="offset">Unit offset to start at.</param>
    /// <param name="limit">Maximum units on the page; zero or less for no limit besides the budget.</param>
    /// <param name="budgetChars">Maximum serialized page length in characters.</param>
    /// <param name="wireOptions">The wire serializer options used for every measurement.</param>
    public static SequencePage BuildPage(
        JsonObject fullEnvelope,
        SequenceDescriptor sequence,
        int offset,
        int limit,
        int budgetChars,
        JsonSerializerOptions wireOptions)
    {
        ArgumentNullException.ThrowIfNull(fullEnvelope);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(wireOptions);

        return sequence.Kind switch
        {
            SequenceKind.Lists => BuildListsPage(fullEnvelope, sequence, offset, limit, budgetChars, wireOptions),
            SequenceKind.Lines => BuildLinesPage(fullEnvelope, sequence, offset, limit, budgetChars, wireOptions),
            _ => new SequencePage((JsonObject)fullEnvelope.DeepClone(), 0, 0, 0, null, false, null),
        };
    }

    private static string? FindKey(JsonObject obj, string name, JsonSerializerOptions wireOptions)
    {
        if (obj.ContainsKey(name))
        {
            return name;
        }

        string? converted = wireOptions.PropertyNamingPolicy?.ConvertName(name);
        if (converted is not null && obj.ContainsKey(converted))
        {
            return converted;
        }

        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }

    // Resolves one hint path to the canonical wire path of an array in the envelope, or null. A bare name is taken
    // relative to successData; names are matched exactly, then through the wire naming policy, then ignoring case, so
    // nameof(record.Callers) finds "callers". Paths ending in an array index are not supported (a property key is needed
    // to replace the node on the page).
    private static string? ResolveArrayPath(JsonObject envelope, string hint, JsonSerializerOptions wireOptions)
    {
        string path = hint?.Trim() ?? string.Empty;
        if (path.Length == 0 || path.EndsWith(']'))
        {
            return null;
        }

        bool rooted = path == SuccessKey
            || path.StartsWith(SuccessKey + ".", StringComparison.Ordinal)
            || path.StartsWith(SuccessKey + "[", StringComparison.Ordinal);
        string full = rooted ? path : SuccessKey + "." + path;
        if (ResultPager.SelectNode(envelope, full) is JsonArray)
        {
            return full;
        }

        if (full.Contains('['))
        {
            return null;
        }

        JsonNode? current = envelope;
        var canonical = new List<string>();
        foreach (string segment in full.Split('.'))
        {
            if (current is not JsonObject obj)
            {
                return null;
            }

            string? key = FindKey(obj, segment, wireOptions);
            if (key is null)
            {
                return null;
            }

            canonical.Add(key);
            current = obj[key];
        }

        return current is JsonArray ? string.Join('.', canonical) : null;
    }

    // Replaces the node a canonical path (as written by ResolveSequence) names; the parent must already exist.
    private static void SetAtPath(JsonObject root, string path, JsonNode? replacement)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            root[path] = replacement;
            return;
        }

        if (ResultPager.SelectNode(root, path[..dot]) is JsonObject parent)
        {
            parent[path[(dot + 1)..]] = replacement;
        }
    }

    // The text at a canonical path, or empty when the node is missing or not a string.
    private static string TextAt(JsonObject envelope, string path) =>
        ResultPager.SelectNode(envelope, path) is JsonValue value && value.TryGetValue(out string? text) ? text : string.Empty;

    // Lines with terminators (ResultPager.SplitLines); a line longer than maxUnitChars becomes chunks of that size
    // (never splitting a surrogate pair), so concatenating all units gives back the exact text.
    private static List<string> LineUnits(string text, int maxUnitChars)
    {
        var units = new List<string>();
        foreach (string line in ResultPager.SplitLines(text))
        {
            if (line.Length <= maxUnitChars)
            {
                units.Add(line);
                continue;
            }

            int start = 0;
            while (start < line.Length)
            {
                int length = Math.Min(maxUnitChars, line.Length - start);
                if (length > 1 && start + length < line.Length && char.IsHighSurrogate(line[start + length - 1]))
                {
                    length--;
                }

                units.Add(line.Substring(start, length));
                start += length;
            }
        }

        return units;
    }

    // The deterministic unit order as (list, index) pairs. Interleaved: round r takes element r of every list longer
    // than r, in declared order, so an exhausted list is skipped. Linear: list 0 in full, then list 1.
    private static IEnumerable<(int List, int Index)> EnumerateUnits(IReadOnlyList<int> lengths, PagingOrder order)
    {
        if (order == PagingOrder.Linear)
        {
            for (int list = 0; list < lengths.Count; list++)
            {
                for (int index = 0; index < lengths[list]; index++)
                {
                    yield return (list, index);
                }
            }

            yield break;
        }

        int rounds = lengths.Count == 0 ? 0 : lengths.Max();
        for (int round = 0; round < rounds; round++)
        {
            for (int list = 0; list < lengths.Count; list++)
            {
                if (lengths[list] > round)
                {
                    yield return (list, round);
                }
            }
        }
    }

    // Assembles the SequencePage. used is the estimated serialized page size; it can exceed the budget only when the
    // first unit was forced onto the page (returned == 1), or when the parts outside the sequence alone are over budget.
    private static SequencePage Finish(
        JsonObject page, int offset, int returned, int total, int used, int baseSize, int budgetChars, string? firstUnit)
    {
        int end = offset + returned;
        int? nextOffset = end < total ? end : null;
        bool violation = used > budgetChars;
        string? detail = null;
        if (violation)
        {
            detail = baseSize > budgetChars
                ? $"parts outside the sequence take {baseSize} of {budgetChars} chars"
                : $"{firstUnit} is {used - baseSize} chars serialized but only {budgetChars - baseSize} are available; returned alone";
        }

        return new SequencePage(page, offset, returned, total, nextOffset, violation, detail, baseSize > budgetChars);
    }

    private static SequencePage BuildListsPage(
    JsonObject full, SequenceDescriptor sequence, int offset, int limit, int budgetChars, JsonSerializerOptions wireOptions)
    {
        JsonObject page = (JsonObject)full.DeepClone();
        var sources = sequence.Paths.Select(p => ResultPager.SelectNode(full, p) as JsonArray).ToList();
        var lengths = sources.Select(a => a?.Count ?? 0).ToList();
        int total = lengths.Sum();

        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not null)
            {
                SetAtPath(page, sequence.Paths[i], new JsonArray());
            }
        }

        int baseSize = page.ToJsonString(wireOptions).Length;
        offset = Math.Clamp(offset, 0, total);

        // Each unit is measured once: its own JSON, plus a comma when it is not the first element of its list.
        int used = baseSize;
        int returned = 0;
        string? firstUnit = null;
        var picked = sources.Select(_ => new List<int>()).ToList();
        foreach ((int list, int index) in EnumerateUnits(lengths, sequence.Order).Skip(offset))
        {
            if (limit > 0 && returned >= limit)
            {
                break;
            }

            int size = (sources[list]![index]?.ToJsonString(wireOptions) ?? "null").Length;
            if (picked[list].Count > 0)
            {
                size++;
            }

            if (returned > 0 && used + size > budgetChars)
            {
                break;
            }

            if (returned == 0)
            {
                firstUnit = $"{sequence.Paths[list]}[{index}] (sequence unit {offset})";
            }

            used += size;
            picked[list].Add(index);
            returned++;
        }

        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i] is not { } source)
            {
                continue;
            }

            var share = new JsonArray();
            foreach (int index in picked[i])
            {
                share.Add(source[index]?.DeepClone());
            }

            SetAtPath(page, sequence.Paths[i], share);
        }

        return Finish(page, offset, returned, total, used, baseSize, budgetChars, firstUnit);
    }

    private static SequencePage BuildLinesPage(
    JsonObject full, SequenceDescriptor sequence, int offset, int limit, int budgetChars, JsonSerializerOptions wireOptions)
    {
        JsonObject page = (JsonObject)full.DeepClone();
        string path = sequence.Paths[0];
        bool hasText = ResultPager.SelectNode(full, path) is JsonValue node && node.TryGetValue(out string? _);
        var units = LineUnits(TextAt(full, path), sequence.MaxUnitChars);
        int total = units.Count;

        if (hasText)
        {
            SetAtPath(page, path, JsonValue.Create(string.Empty));
        }

        int baseSize = page.ToJsonString(wireOptions).Length;
        offset = Math.Clamp(offset, 0, total);

        // A unit's JSON is its escaped text between quotes; the page string is the units concatenated.
        int used = baseSize;
        int returned = 0;
        string? firstUnit = null;
        var text = new StringBuilder();
        for (int index = offset; index < total; index++)
        {
            if (limit > 0 && returned >= limit)
            {
                break;
            }

            int size = JsonValue.Create(units[index])!.ToJsonString(wireOptions).Length - 2;
            if (returned > 0 && used + size > budgetChars)
            {
                break;
            }

            if (returned == 0)
            {
                firstUnit = $"{path} line unit {index}";
            }

            used += size;
            text.Append(units[index]);
            returned++;
        }

        if (hasText)
        {
            SetAtPath(page, path, JsonValue.Create(text.ToString()));
        }

        return Finish(page, offset, returned, total, used, baseSize, budgetChars, firstUnit);
    }
}

/// <summary>The kind of pageable sequence a result envelope has (<see cref="SequenceDescriptor.Kind"/>).</summary>
public enum SequenceKind
{
    /// <summary>No sequence: the caller keeps using <c>ResultPager.Fit</c> (dictionaries, nested shapes, scalar-only data).</summary>
    None,

    /// <summary>One or more arrays; a unit is one array element.</summary>
    Lists,

    /// <summary>One large string; a unit is one line (or one chunk of an overlong line).</summary>
    Lines,
}
/// <summary>
/// The resolved pageable sequence of a result envelope (<see cref="SequencePager.ResolveSequence"/>): which nodes
/// hold the units and in which order they are walked. Deterministic for a given envelope, so the same descriptor
/// can be rebuilt later from the stored result.
/// </summary>
/// <param name="Kind">The kind of sequence; <see cref="SequenceKind.None"/> means the caller keeps using <c>ResultPager.Fit</c>.</param>
/// <param name="Paths">
/// Canonical wire paths (<c>successData.callers</c>; <c>successData</c> for a bare array or string). One path for
/// <see cref="SequenceKind.Lines"/>, one or more for <see cref="SequenceKind.Lists"/>, empty for None.
/// </param>
/// <param name="Order">How the lists are walked; irrelevant for Lines and None.</param>
/// <param name="MaxUnitChars">Lines only: a line longer than this many characters is split into chunks of this size, each one unit.</param>
public sealed record SequenceDescriptor(SequenceKind Kind, IReadOnlyList<string> Paths, PagingOrder Order, int MaxUnitChars)
{
    /// <summary>The descriptor of a result that has no pageable sequence.</summary>
    public static SequenceDescriptor None { get; } = new(SequenceKind.None, [], PagingOrder.Interleaved, 0);
}
/// <summary>
/// One page of a sequence (<see cref="SequencePager.BuildPage"/>): the full envelope holding only this page's share of
/// the sequence, plus the paging fields the marshaller will place in <c>largeResult</c>.
/// </summary>
/// <param name="Page">A copy of the full envelope; each sequence list (or the sequence string) holds only this page's units. Everything outside the sequence is unchanged.</param>
/// <param name="Offset">The unit offset this page starts at (clamped to 0..Total).</param>
/// <param name="Returned">Units on this page.</param>
/// <param name="Total">Units in the whole sequence.</param>
/// <param name="NextOffset">Offset of the next page; null when this page reaches the end.</param>
/// <param name="ContractViolation">True when the page is over budget even with one unit (an oversize unit, or parts outside the sequence that are too large).</param>
/// <param name="ViolationDetail">What is oversize and where (path and unit index), for the server log; null when there is no violation.</param>
/// <param name="PartsOutsideOverBudget">True when the parts outside the sequence alone exceed the budget, so no choice of units can make the page fit; the caller then cuts with <c>ResultPager.Fit</c> instead.</param>
public sealed record SequencePage(
    JsonObject Page,
    int Offset,
    int Returned,
    int Total,
    int? NextOffset,
    bool ContractViolation,
    string? ViolationDetail,
    bool PartsOutsideOverBudget = false);
