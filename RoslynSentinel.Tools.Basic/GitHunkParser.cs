using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Tools.Basic;

/// <summary>One zero-context hunk. <c>RawText</c> is the "@@ ..." line through the last body line, line endings untouched.</summary>
internal sealed record GitHunk(
    int Index, int OldStart, int OldCount, int NewStart, int NewCount, string Header, string RawText);

/// <summary>One file's parsed <c>git diff -U0 --no-renames</c> output. <c>Fingerprint</c> covers the whole input text.</summary>
internal sealed record GitFileDiff(
    string HeaderText, IReadOnlyList<GitHunk> Hunks, bool IsBinary, bool IsNewFile, bool IsDeletedFile,
    bool IsRenameOrModeOnly, string Fingerprint);

/// <summary>
/// Outcome of selecting new-file lines of one diff: <c>WholeHunks</c> are indexes of hunks wholly inside the range,
/// <c>SubHunks</c> are hunks cut down to the in-range lines (each keeps the <see cref="GitHunk.Index"/> of the hunk it
/// was cut from), <c>Unsupported</c> are hunks that overlap the range but cannot be cut.
/// </summary>
internal sealed record GitLineSelectionResult(
    IReadOnlyList<int> WholeHunks, IReadOnlyList<GitHunk> SubHunks, IReadOnlyList<GitHunk> Unsupported);

/// <summary>
/// Pure parser for one file's zero-context unified diff, used to stage selected hunks. Works on a
/// Latin-1-decoded string (1 char == 1 byte) so patches round-trip byte-exact: the caller decodes
/// with <c>Encoding.Latin1.GetString(bytes)</c> and writes a built patch back with
/// <c>Encoding.Latin1.GetBytes</c>. Lines are split on '\n' only; '\r' is never normalized.
/// </summary>
internal static class GitHunkParser
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@ -(?<os>\d+)(?:,(?<oc>\d+))? \+(?<ns>\d+)(?:,(?<nc>\d+))? @@(?<h>.*)$",
        RegexOptions.CultureInvariant);

    public static GitFileDiff Parse(string latin1Diff)
    {
        latin1Diff ??= string.Empty;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(latin1Diff)))
            .ToLowerInvariant()[..12];

        var hunkStarts = new List<(int Start, int OldStart, int OldCount, int NewStart, int NewCount, string Header)>();
        var headerLines = new List<string>();
        foreach (var (start, length) in EnumerateLines(latin1Diff))
        {
            var line = LineContent(latin1Diff, start, length);
            var m = HunkHeaderRegex.Match(line);
            if (m.Success
                && int.TryParse(m.Groups["os"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var os)
                && int.TryParse(m.Groups["ns"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ns))
            {
                var oc = ParseCount(m.Groups["oc"]);
                var nc = ParseCount(m.Groups["nc"]);
                hunkStarts.Add((start, os, oc, ns, nc, m.Groups["h"].Value.Trim()));
            }
            else if (hunkStarts.Count == 0)
            {
                headerLines.Add(line);
            }
        }

        var headerEnd = hunkStarts.Count > 0 ? hunkStarts[0].Start : latin1Diff.Length;
        var headerText = latin1Diff[..headerEnd];

        var hunks = new List<GitHunk>(hunkStarts.Count);
        for (var i = 0; i < hunkStarts.Count; i++)
        {
            var h = hunkStarts[i];
            var end = i + 1 < hunkStarts.Count ? hunkStarts[i + 1].Start : latin1Diff.Length;
            hunks.Add(new GitHunk(i + 1, h.OldStart, h.OldCount, h.NewStart, h.NewCount, h.Header,
                latin1Diff[h.Start..end]));
        }

        var isBinary = headerLines.Any(l => l.StartsWith("Binary files ", StringComparison.Ordinal)
                                            || l.StartsWith("GIT binary patch", StringComparison.Ordinal));
        var isNew = headerLines.Any(l => l.StartsWith("new file mode", StringComparison.Ordinal));
        var isDeleted = headerLines.Any(l => l.StartsWith("deleted file mode", StringComparison.Ordinal));
        var isRenameOrMode = hunks.Count == 0
                             || headerLines.Any(l => l.StartsWith("rename from", StringComparison.Ordinal)
                                                     || l.StartsWith("old mode", StringComparison.Ordinal));

        return new GitFileDiff(headerText, hunks, isBinary, isNew, isDeleted, isRenameOrMode, fingerprint);
    }

    /// <summary>The header text followed by the raw text of the selected hunks in ascending order, all verbatim.</summary>
    public static string BuildPatch(GitFileDiff diff, IReadOnlyCollection<int> indexes) =>
        BuildPatchFrom(diff, indexes.Distinct().OrderBy(i => i).Select(i => HunkAt(diff, i, nameof(indexes))));

    /// <summary>
    /// Like <see cref="BuildPatch(GitFileDiff, IReadOnlyCollection{int})"/> for a line selection: whole hunks verbatim plus
    /// sub-hunks, in ascending hunk order. Each sub-hunk's own new-line count feeds the position delta of the hunks after
    /// it. <see cref="GitLineSelectionResult.Unsupported"/> hunks are ignored; the caller decides whether to refuse them.
    /// </summary>
    public static string BuildPatch(GitFileDiff diff, GitLineSelectionResult selection)
    {
        var hunks = selection.WholeHunks.Distinct().Select(i => HunkAt(diff, i, nameof(selection)))
            .Concat(selection.SubHunks)
            .OrderBy(h => h.Index);
        return BuildPatchFrom(diff, hunks);
    }

    private static GitHunk HunkAt(GitFileDiff diff, int index, string paramName)
    {
        if (index < 1 || index > diff.Hunks.Count)
            throw new ArgumentOutOfRangeException(paramName, index,
                $"Hunk index must be between 1 and {diff.Hunks.Count}.");
        return diff.Hunks[index - 1];
    }

    private static string BuildPatchFrom(GitFileDiff diff, IEnumerable<GitHunk> hunksInOrder)
    {
        var sb = new StringBuilder(diff.HeaderText);
        var delta = 0;
        foreach (var hunk in hunksInOrder)
        {
            sb.Append(WithNewStart(hunk, delta));
            delta += hunk.NewCount - hunk.OldCount;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits the new-file line range [from, to] into whole hunks, sub-hunks and unsupported hunks. A hunk wholly inside
    /// the range is whole (same rule as <see cref="SelectByNewLineRange"/>). A hunk that only overlaps the range is cut
    /// to its in-range '+' lines: a pure insertion keeps just the run; a mixed hunk ('-' lines then '+' lines) takes its
    /// '-' lines only when the range covers its first '+' line (see SliceHunk). Hunks whose body is not exactly that
    /// shape, or that cannot be cut safely, are returned in <see cref="GitLineSelectionResult.Unsupported"/>.
    /// </summary>
    public static GitLineSelectionResult SelectLines(GitFileDiff diff, int from, int to)
    {
        var whole = SelectByNewLineRange(diff, from, to, out var straddling);
        var subs = new List<GitHunk>();
        var unsupported = new List<GitHunk>();
        foreach (var hunk in straddling)
        {
            var sub = SliceHunk(hunk, from, to);
            if (sub != null)
                subs.Add(sub);
            else
                unsupported.Add(hunk);
        }
        return new GitLineSelectionResult(whole, subs, unsupported);
    }

    // Cuts a hunk shaped '-'* [marker] '+'* [marker] to the '+' lines whose new line number lies in [from, to]. Lines are copied
    // byte-verbatim (their own line endings). A "\ No newline at end of file" marker belongs to the side of the line it follows, so
    // the new-side marker is kept only when the hunk's LAST '+' line is kept. Returns null (the hunk is refused) when the body is
    // not that shape, its line counts disagree with the header, or the cut cannot be expressed safely.
    //
    // '-' lines have no new-file line number, so the range selects '-' lines by one deterministic rule: the '-' block is taken (deleted
    // in the staged patch) if and only if the range includes the hunk's FIRST '+' line (new line NewStart). That gives two shapes:
    //  (a) '-' block taken: the sub-hunk keeps all '-' lines and a PREFIX of the '+' lines (a longer cut would be a whole hunk).
    //  (b) '-' block not taken: the '-' lines stay unstaged (they become context) and the kept '+' run becomes a PURE INSERTION placed
    //      after the last old line of the unselected block. Like `git add -p` splitting a hunk: an unselected '-' turns into context.
    private static GitHunk? SliceHunk(GitHunk hunk, int from, int to)
    {
        var text = hunk.RawText;
        var lines = EnumerateLines(text).ToList();
        if (lines.Count < 2)
            return null;
        var m = HunkHeaderRegex.Match(LineContent(text, lines[0].Start, lines[0].Length));
        if (!m.Success)
            return null;

        var minus = new List<(int Start, int Length)>();
        var plus = new List<(int Start, int Length)>();
        (int Start, int Length)? oldMarker = null;
        (int Start, int Length)? newMarker = null;
        for (var i = 1; i < lines.Count; i++)
        {
            var firstChar = text[lines[i].Start];
            if (firstChar == '-' && plus.Count == 0 && oldMarker == null)
                minus.Add(lines[i]);
            else if (firstChar == '+' && newMarker == null)
                plus.Add(lines[i]);
            else if (firstChar == '\\' && plus.Count == 0 && minus.Count > 0 && oldMarker == null)
                oldMarker = lines[i];
            else if (firstChar == '\\' && plus.Count > 0 && i == lines.Count - 1)
                newMarker = lines[i];
            else
                return null;
        }
        if (minus.Count != hunk.OldCount || plus.Count != hunk.NewCount)
            return null;

        var firstKept = Math.Max(from - hunk.NewStart, 0);
        var lastKept = Math.Min(to - hunk.NewStart, plus.Count - 1);
        if (firstKept > lastKept)
            return null;

        var keptCount = lastKept - firstKept + 1;
        var newStart = hunk.NewStart + firstKept;
        var takeMinus = minus.Count > 0 && firstKept == 0;
        // Shape (b) inserts after an old line; if that line has no trailing newline (old-side marker), the inserted text would be glued
        // onto it, so that cut is refused rather than staged corrupt.
        if (minus.Count > 0 && !takeMinus && oldMarker != null)
            return null;

        var oldStart = minus.Count == 0 || takeMinus ? hunk.OldStart : hunk.OldStart + minus.Count - 1;
        var oldCount = takeMinus ? minus.Count : 0;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"@@ -{oldStart},{oldCount} +{newStart},{keptCount} @@{m.Groups["h"].Value}\n");
        if (takeMinus)
        {
            foreach (var (start, length) in minus)
                sb.Append(text, start, length);
            if (oldMarker is { } om)
                sb.Append(text, om.Start, om.Length);
        }
        for (var i = firstKept; i <= lastKept; i++)
            sb.Append(text, plus[i].Start, plus[i].Length);
        if (lastKept == plus.Count - 1 && newMarker is { } nm)
            sb.Append(text, nm.Start, nm.Length);

        return new GitHunk(hunk.Index, oldStart, oldCount, newStart, keptCount, hunk.Header, sb.ToString());
    }

    // Rewrites only the new-start number of a hunk's first line, so the body stays byte-exact.
    // The header position must match the partial patch: a hunk with no old lines (pure insertion or
    // pure deletion) has nothing else to position it, and git reads its position from the header.
    private static string WithNewStart(GitHunk hunk, int delta)
    {
        var text = hunk.RawText;
        var lineEnd = text.IndexOf('\n');
        var firstLine = lineEnd < 0 ? text : text[..lineEnd];
        var m = HunkHeaderRegex.Match(firstLine);
        if (!m.Success)
            return text;

        var newStart = hunk.OldCount == 0 ? hunk.OldStart + 1 + delta
            : hunk.NewCount == 0 ? hunk.OldStart - 1 + delta
            : hunk.OldStart + delta;
        var ns = m.Groups["ns"];
        return text[..ns.Index] + newStart.ToString(CultureInfo.InvariantCulture) + text[(ns.Index + ns.Length)..];
    }

    /// <summary>Parses hunk ids given as CSV ("1,3") or a JSON array ("[1,3]" / "[\"1\",\"3\"]"); duplicates collapse, result ascending.</summary>
    public static bool TryParseHunkIds(string? csvOrJson, int hunkCount, out List<int> ids, out string error)
    {
        ids = [];
        error = string.Empty;
        var rangeText = hunkCount > 0 ? $"Valid hunk ids are 1 to {hunkCount}" : "The file has no hunks to choose from";

        var tokens = DelimitedListParser.ParseStringOrJsonArrayToList(csvOrJson, out var listError);
        if (listError != null)
        {
            // A JSON array of numbers ([1,3]) is a natural thing to send; the shared parser only takes strings.
            try
            {
                tokens = JsonSerializer.Deserialize<int[]>(csvOrJson!.Trim())?
                    .Select(n => n.ToString(CultureInfo.InvariantCulture)).ToArray();
            }
            catch (JsonException)
            {
                tokens = null;
            }
            if (tokens == null)
            {
                error = $"{listError} {rangeText}.";
                return false;
            }
        }

        if (tokens == null || tokens.Length == 0)
        {
            error = $"No hunk ids given. {rangeText}, e.g. \"1,3\".";
            return false;
        }

        var seen = new SortedSet<int>();
        foreach (var token in tokens)
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                error = $"'{token}' is not a hunk id (a whole number). {rangeText}.";
                return false;
            }
            if (id < 1 || id > hunkCount)
            {
                error = $"Hunk id {id} is out of range. {rangeText}.";
                return false;
            }
            seen.Add(id);
        }

        ids = [.. seen];
        return true;
    }

    /// <summary>Parses "52" or "40-80" (new-file line numbers, 1-based, from &lt;= to).</summary>
    public static bool TryParseLineRange(string? text, out int from, out int to, out string error)
    {
        from = 0;
        to = 0;
        error = string.Empty;
        const string Format = "Use a single new-file line number like \"52\" or a range like \"40-80\".";

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = $"lineRange is empty. {Format}";
            return false;
        }

        string left, right;
        var dash = trimmed.IndexOf('-');
        if (dash < 0)
        {
            left = right = trimmed;
        }
        else
        {
            left = trimmed[..dash].Trim();
            right = trimmed[(dash + 1)..].Trim();
        }

        if (!int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var f)
            || !int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
        {
            error = $"'{trimmed}' is not a valid line range. {Format}";
            return false;
        }
        if (f < 1 || t < 1)
        {
            error = $"'{trimmed}' is not a valid line range: line numbers start at 1. {Format}";
            return false;
        }
        if (f > t)
        {
            error = $"'{trimmed}' is reversed: the first line must be <= the last line (e.g. \"{t}-{f}\").";
            return false;
        }

        from = f;
        to = t;
        return true;
    }

    /// <summary>
    /// Indexes of hunks lying wholly inside new-file lines [from, to]. A hunk occupies
    /// [NewStart, NewStart + Max(NewCount,1) - 1]; a pure deletion (NewCount == 0) occupies the single
    /// position NewStart, git's "line before the deletion" (0 for a deletion at the top of the file is
    /// treated as line 1). Hunks that overlap the range without being inside it go to <paramref name="straddling"/>.
    /// </summary>
    public static List<int> SelectByNewLineRange(GitFileDiff diff, int from, int to, out List<GitHunk> straddling)
    {
        var selected = new List<int>();
        straddling = [];
        foreach (var hunk in diff.Hunks)
        {
            var start = Math.Max(hunk.NewStart, 1);
            var end = start + Math.Max(hunk.NewCount, 1) - 1;
            if (start >= from && end <= to)
                selected.Add(hunk.Index);
            else if (start <= to && end >= from)
                straddling.Add(hunk);
        }
        return selected;
    }

    /// <summary>
    /// The changed lines (+/-) of a hunk, one per line, decoded as UTF-8 from the Latin-1 text. Each line is
    /// cut to <paramref name="maxCharsPerLine"/> characters and the list to <paramref name="maxLines"/> lines;
    /// either cut is marked with "...".
    /// </summary>
    public static string PreviewLines(GitHunk hunk, int maxLines, int maxCharsPerLine)
    {
        maxLines = Math.Max(maxLines, 0);
        maxCharsPerLine = Math.Max(maxCharsPerLine, 1);

        var result = new List<string>();
        var more = false;
        var first = true;
        foreach (var (start, length) in EnumerateLines(hunk.RawText))
        {
            if (first)
            {
                first = false; // the "@@ ..." line
                continue;
            }
            var line = LineContent(hunk.RawText, start, length);
            if (line.Length == 0 || (line[0] != '+' && line[0] != '-'))
                continue;
            if (result.Count >= maxLines)
            {
                more = true;
                break;
            }
            var decoded = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(line));
            if (decoded.Length > maxCharsPerLine)
            {
                var cut = maxCharsPerLine;
                if (char.IsLowSurrogate(decoded[cut]))
                    cut--;
                decoded = decoded[..cut] + "...";
            }
            result.Add(decoded);
        }
        if (more)
            result.Add("...");
        return string.Join('\n', result);
    }

    private static int ParseCount(Group g) =>
        !g.Success ? 1 : int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;

    /// <summary>Yields (start, length) of each line including its '\n'; splits on '\n' only.</summary>
    private static IEnumerable<(int Start, int Length)> EnumerateLines(string text)
    {
        var pos = 0;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl + 1;
            yield return (pos, end - pos);
            pos = end;
        }
    }

    /// <summary>The line without its '\n' and without a trailing '\r'.</summary>
    private static string LineContent(string text, int start, int length)
    {
        var end = start + length;
        if (end > start && text[end - 1] == '\n')
            end--;
        if (end > start && text[end - 1] == '\r')
            end--;
        return text[start..end];
    }
}
