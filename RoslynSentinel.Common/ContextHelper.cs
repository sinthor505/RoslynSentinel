using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Common;

/// <summary>
/// Locates a position in source code using a contextSnippet (verbatim substring) instead of line/column.
/// An AI can extract this snippet from the code it already sees, requiring zero coordinate calculation.
/// When a snippet could be ambiguous, provide lineBefore and/or lineAfter for disambiguation.
/// </summary>
public static class ContextHelper
{
    /// <summary>
    /// A snippet match's location AND real length in the source text. The length is not always
    /// <c>contextSnippet.Length</c> -> the whitespace-collapsing fallback paths in
    /// <see cref="FindAllSnippetMatchesWithLength"/> match content that is equivalent once
    /// whitespace is normalized, but whose raw span in the source can be a different length
    /// (different indentation, extra spaces, etc.). Any caller that removes/replaces a span of the
    /// real source text (as opposed to merely using <c>Start</c> as an entry point into a
    /// Roslyn node/token lookup) MUST use <c>Length</c>, not <c>contextSnippet.Length</c>.
    /// </summary>
    public readonly record struct SnippetMatch(int Start, int Length);

    /// <summary>
    /// Non-throwing variant that returns every candidate match instead of resolving to one.
    /// Empty list = not found. Single-item list = unambiguous (equivalent to FindSnippetPosition's
    /// success case). Multi-item list = ambiguous; caller decides what to do, including building
    /// its own hint from the candidates.
    /// </summary>
    public static List<int> FindAllSnippetMatches(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
        => FindAllSnippetMatchesWithLength(sourceText, contextSnippet, lineBefore, lineAfter)
            .Select(m => m.Start).ToList();

    /// <summary>
    /// Same matching behavior as <see cref="FindAllSnippetMatches"/> (literal match, then
    /// CRLF-normalized, then whitespace-collapsed single-line, then whitespace-collapsed
    /// multi-line window), but returns each match's real length in the source alongside its
    /// start offset. Use this instead of <see cref="FindAllSnippetMatches"/> whenever the caller
    /// needs to remove/replace the matched span of text -> see <see cref="SnippetMatch"/>.
    /// </summary>
    public static List<SnippetMatch> FindAllSnippetMatchesWithLength(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
        {
            return new List<SnippetMatch>();
        }

        ThrowIfMultiLine(sourceText, nameof(lineBefore), lineBefore, isAfter: false);
        ThrowIfMultiLine(sourceText, nameof(lineAfter), lineAfter, isAfter: true);

        var source = sourceText.ToString();
        var allMatches = SnippetStages.FindOrdinalStage(source, contextSnippet);

        if (allMatches.Count == 0 && contextSnippet.Contains('\n'))
        {
            allMatches = SnippetStages.FindLineEndingStage(source, contextSnippet);
        }

        if (allMatches.Count == 0)
        {
            allMatches = SnippetStages.FindCollapsedSingleLineStage(sourceText, contextSnippet, StringComparison.OrdinalIgnoreCase);
        }

        if (allMatches.Count == 0 && contextSnippet.Contains('\n'))
        {
            allMatches = SnippetStages.FindCollapsedWindowStage(sourceText, contextSnippet, StringComparison.OrdinalIgnoreCase);
        }

        if (allMatches.Count == 0)
        {
            return new List<SnippetMatch>();
        }

        return FilterByAdjacentLines(sourceText, allMatches, lineBefore, lineAfter);
    }

    /// <summary>
    /// Finds the unique character offset of contextSnippet within sourceText.
    /// Optionally, provide lineBefore/lineAfter (the single verbatim adjacent line) to disambiguate.
    /// Throws InvalidOperationException if not found or still ambiguous after disambiguation.
    /// </summary>
    public static int FindSnippetPosition(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
        => FindSnippetPositionWithLength(sourceText, contextSnippet, lineBefore, lineAfter).Start;

    /// <summary>String overload -> delegates to SourceText for consistent line handling.</summary>
    public static int FindSnippetPosition(
        string fullSource, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
        => FindSnippetPosition(SourceText.From(fullSource), contextSnippet, lineBefore, lineAfter);

    /// <summary>
    /// Same resolution/ambiguity behavior as <see cref="FindSnippetPosition(SourceText, string, string?, string?)"/>,
    /// but returns the match's real length alongside its start offset (see <see cref="SnippetMatch"/>).
    /// Use this instead of <see cref="FindSnippetPosition(SourceText, string, string?, string?)"/> whenever the
    /// caller needs to remove/replace the matched span of text rather than just locate a node/token.
    /// </summary>
    public static SnippetMatch FindSnippetPositionWithLength(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
        {
            throw new ToolNotFoundException("contextSnippet must not be empty.");
        }

        var matches = FindAllSnippetMatchesWithLength(sourceText, contextSnippet, lineBefore, lineAfter);

        if (matches.Count == 0)
        {
            throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.NoMatch, contextSnippet, sourceText,
                diagnosis: DiagnoseNoMatch(sourceText, contextSnippet), verbatimRequired: false);
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var lbTrimmed = lineBefore?.Trim();
        var laTrimmed = lineAfter?.Trim();

        return (lbTrimmed, laTrimmed) switch
        {
            (null, null) => throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.Ambiguous, contextSnippet, sourceText, matches),
            _ => throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.StillAmbiguous, contextSnippet, sourceText, matches)
        };
    }

    /// <summary>String overload -> delegates to SourceText for consistent line handling.</summary>
    public static SnippetMatch FindSnippetPositionWithLength(
        string fullSource, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
        => FindSnippetPositionWithLength(SourceText.From(fullSource), contextSnippet, lineBefore, lineAfter);

    /// <summary>
    /// Strict variant of <see cref="FindAllSnippetMatchesWithLength"/> for callers that remove/replace
    /// the matched span of raw text (as opposed to merely locating a Roslyn node/token). Only the
    /// literal-ordinal and CRLF-normalized match paths run -> never the whitespace-collapsing
    /// fallbacks -> so every returned <see cref="SnippetMatch.Length"/> is guaranteed to equal
    /// <c>contextSnippet.Length</c> (mod line-ending substitution) and the removed span is always
    /// exactly the text the caller supplied. Used by <c>ReplaceSnippet</c>, whose contract is a
    /// verbatim oldContent/newContent swap with no tolerance for the caller retyping content from
    /// memory -> unlike <see cref="FindAllSnippetMatchesWithLength"/>'s callers (diff/patch-style
    /// tools), which need the whitespace tolerance to recover from stale or approximately-quoted
    /// context.
    /// </summary>
    public static List<SnippetMatch> FindAllExactSnippetMatches(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (string.IsNullOrWhiteSpace(contextSnippet))
        {
            return new List<SnippetMatch>();
        }

        ThrowIfMultiLine(sourceText, nameof(lineBefore), lineBefore, isAfter: false);
        ThrowIfMultiLine(sourceText, nameof(lineAfter), lineAfter, isAfter: true);

        var source = sourceText.ToString();
        var allMatches = SnippetStages.FindOrdinalStage(source, contextSnippet);

        if (allMatches.Count == 0 && contextSnippet.Contains('\n'))
        {
            allMatches = SnippetStages.FindLineEndingStage(source, contextSnippet);
        }

        if (allMatches.Count == 0)
        {
            return new List<SnippetMatch>();
        }

        return FilterByAdjacentLines(sourceText, allMatches, lineBefore, lineAfter);
    }

    /// <summary>
    /// Throwing variant of <see cref="FindAllExactSnippetMatches"/> -> resolves to the unique exact
    /// match, or throws <see cref="ToolNotFoundException"/>/<see cref="ToolAmbiguousMatchException"/>
    /// with the same messages as <see cref="FindSnippetPositionWithLength"/>. Unlike that method,
    /// never falls back to whitespace-collapsed matching: a snippet that isn't present verbatim
    /// (allowing only for CRLF/LF differences) is reported as not found, prompting the caller to
    /// re-read the file and copy the exact current text rather than silently matching approximate
    /// content and risking a length mismatch on removal.
    /// A blank selector means "not supplied": a null/empty/whitespace <paramref name="withinMember"/> and a
    /// <paramref name="matchLine"/> of 0 or less are treated as null, because weak models send "" or 0
    /// for an unused parameter and that must not filter out every match.
    /// </summary>
    public static SnippetMatch FindExactSnippetPosition(
        SourceText sourceText, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        Func<int, string?>? memberNameAt = null,
        string? withinMember = null, int? matchLine = null)
    {
        const string label = "oldContent";
        if (string.IsNullOrWhiteSpace(contextSnippet))
        {
            throw new ToolNotFoundException($"{label} must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(withinMember))
        {
            withinMember = null;
        }

        if (matchLine <= 0)
        {
            matchLine = null;
        }

        if (withinMember != null && memberNameAt == null)
        {
            throw new ToolNotFoundException(
                "withinMember cannot be applied: this document has no syntax tree, so its enclosing members are unknown. " +
                "Drop withinMember and use matchLine, lineBefore or lineAfter instead.");
        }

        // withinMember/matchLine act first, on the verbatim text matches; lineBefore/lineAfter then
        // filter whatever survives. With neither selector this is exactly the original single call.
        var hasSelectors = withinMember != null || matchLine != null;
        var survivorsAfterSelectors = new List<SnippetMatch>();
        List<SnippetMatch> matches;
        if (hasSelectors)
        {
            ThrowIfMultiLine(sourceText, nameof(lineBefore), lineBefore, isAfter: false);
            ThrowIfMultiLine(sourceText, nameof(lineAfter), lineAfter, isAfter: true);
            var textMatches = FindAllExactSnippetMatches(sourceText, contextSnippet);
            var selected = FilterBySelectors(sourceText, textMatches, withinMember, matchLine, memberNameAt);
            if (textMatches.Count > 0 && selected.Count == 0)
            {
                throw BuildSelectorMismatch(sourceText, contextSnippet, textMatches, withinMember, matchLine, memberNameAt, label);
            }

            survivorsAfterSelectors = selected;
            matches = FilterByAdjacentLines(sourceText, selected, lineBefore, lineAfter);
        }
        else
        {
            matches = FindAllExactSnippetMatches(sourceText, contextSnippet, lineBefore, lineAfter);
        }

        if (matches.Count == 0)
        {
            var unfiltered = hasSelectors
                ? survivorsAfterSelectors
                : (lineBefore, lineAfter) is (null, null)
                    ? new List<SnippetMatch>()
                    : FindAllExactSnippetMatches(sourceText, contextSnippet);
            if (unfiltered.Count > 0)
            {
                // The text DID match; only lineBefore/lineAfter rejected every match, so this is not a
                // "verbatim required" error.
                throw BuildAdjacentLineMismatch(sourceText, contextSnippet, unfiltered, hasSelectors, memberNameAt, label);
            }

            throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.NoMatch, contextSnippet, sourceText,
                diagnosis: DiagnoseNoMatch(sourceText, contextSnippet, label, verbatimRequired: true), verbatimRequired: true, snippetLabel: label);
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        var lbTrimmed = lineBefore?.Trim();
        var laTrimmed = lineAfter?.Trim();

        return (lbTrimmed, laTrimmed) switch
        {
            (null, null) => throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.Ambiguous, contextSnippet, sourceText, matches, snippetLabel: label, memberNameAt: memberNameAt, selectorsSupported: true),
            _ => throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.StillAmbiguous, contextSnippet, sourceText, matches, snippetLabel: label, memberNameAt: memberNameAt, selectorsSupported: true)
        };
    }

    /// <summary>
    /// Position a match is attributed to for the withinMember/matchLine selectors: its first
    /// non-whitespace character, so a match that begins with a newline or indentation is placed on
    /// the line (and in the member) a human sees it on.
    /// </summary>
    private static int FirstNonWhitespacePosition(SourceText sourceText, SnippetMatch match)
    {
        var last = match.Start + match.Length - 1;
        var position = match.Start;
        while (position < last && char.IsWhiteSpace(sourceText[position]))
        {
            position++;
        }

        return position;
    }

    /// <summary>
    /// Keeps only matches whose first non-whitespace line equals <paramref name="matchLine"/> (1-based)
    /// and/or whose enclosing member name equals <paramref name="withinMember"/> (ordinal). Both
    /// together are an intersection. The caller guarantees <paramref name="memberNameAt"/> is set
    /// whenever <paramref name="withinMember"/> is.
    /// </summary>
    private static List<SnippetMatch> FilterBySelectors(
        SourceText sourceText, List<SnippetMatch> matches, string? withinMember, int? matchLine,
        Func<int, string?>? memberNameAt)
    {
        return matches.Where(match =>
        {
            var position = FirstNonWhitespacePosition(sourceText, match);
            if (matchLine != null && sourceText.Lines.GetLinePosition(position).Line + 1 != matchLine)
            {
                return false;
            }

            return withinMember == null
                || string.Equals(memberNameAt!(position), withinMember, StringComparison.Ordinal);
        }).ToList();
    }

    /// <summary>
    /// The text matched, but no match satisfied withinMember/matchLine. Lists the current matches
    /// (re-anchored on their first non-whitespace character, the same position the selectors use)
    /// so a stale matchLine or member name can be corrected from the message alone.
    /// </summary>
    private static ToolException BuildSelectorMismatch(
        SourceText sourceText, string contextSnippet, List<SnippetMatch> textMatches,
        string? withinMember, int? matchLine, Func<int, string?>? memberNameAt, string label)
    {
        var supplied = new List<string>();
        if (matchLine != null)
        {
            supplied.Add($"matchLine={matchLine}");
        }
        if (withinMember != null)
        {
            supplied.Add($"withinMember=\"{withinMember}\"");
        }

        var listing = DescribeAmbiguousCandidates(
            sourceText, textMatches, label, memberNameAt, ContextErrorBuilder.MaxStillAmbiguousCandidatesListed,
            selectorsSupported: true);

        var diagnosis =
            $"{label} matches {textMatches.Count} place(s) exactly, but none satisfied the selector(s) you supplied ({string.Join(", ", supplied)}). " +
            "Current matches:\n" + listing + "\n" +
            "matchLine is the 1-based line of the match's first non-whitespace character and withinMember is the name of its enclosing member. Fix or drop withinMember/matchLine.";
        return ContextErrorBuilder.Build(
            SnippetMatchOutcome.SelectorMismatch, contextSnippet, sourceText,
            diagnosis: diagnosis, snippetLabel: label);
    }

    /// <summary>
    /// The text matched, but the lineBefore/lineAfter supplied filtered out every match. Keeps the
    /// original "N place(s) ... filtered out all of them" body and lists the current matches with
    /// copyable matchLine/withinMember values so the caller can switch to a selector.
    /// </summary>
    private static ToolException BuildAdjacentLineMismatch(
        SourceText sourceText, string contextSnippet, List<SnippetMatch> textMatches,
        bool afterSelectors, Func<int, string?>? memberNameAt, string label)
    {
        var listing = DescribeAmbiguousCandidates(
            sourceText, textMatches, label, memberNameAt, ContextErrorBuilder.MaxStillAmbiguousCandidatesListed,
            selectorsSupported: true);
        var afterSelectorsText = afterSelectors ? " (after the withinMember/matchLine selector)" : string.Empty;
        var diagnosis =
            $"{label} matches {textMatches.Count} place(s) exactly{afterSelectorsText}, but the lineBefore/lineAfter you supplied filtered out all of them. " +
            "lineBefore must equal the line before the match's first line and lineAfter the line after the match's last line (compared trimmed). " +
            "Fix or drop lineBefore/lineAfter, or pass matchLine (and withinMember where listed) copied from the current matches:\n" + listing;
        return ContextErrorBuilder.Build(
            SnippetMatchOutcome.AdjacentLineMismatch, contextSnippet, sourceText,
            diagnosis: diagnosis, snippetLabel: label);
    }

    /// <summary>
    /// Non-throwing variant of <see cref="FindSnippetPosition(SourceText, string, string?, string?)"/>.
    /// Returns <c>-1</c> and sets <paramref name="error"/> to the diagnostic message when the snippet
    /// cannot be found or is ambiguous. Use this when the caller wants to fall back or surface the
    /// error as a return value rather than propagate an exception.
    /// </summary>
    public static int TryFindSnippetPosition(
        SourceText sourceText, string contextSnippet, out string? error,
        string? lineBefore = null, string? lineAfter = null)
    {
        try
        {
            error = null;
            return FindSnippetPosition(sourceText, contextSnippet, lineBefore, lineAfter);
        }
        catch (ToolException ex)
        {
            error = ex.Message;
            return -1;
        }
    }

    /// <summary>String overload of <see cref="TryFindSnippetPosition(SourceText, string, out string?, string?, string?)"/>.</summary>
    public static int TryFindSnippetPosition(
        string fullSource, string contextSnippet, out string? error,
        string? lineBefore = null, string? lineAfter = null)
        => TryFindSnippetPosition(SourceText.From(fullSource), contextSnippet, out error, lineBefore, lineAfter);

    /// <summary>
    /// Splits a (possibly CRLF/CR/LF-mixed) multi-line snippet into its own content lines, with
    /// leading/trailing blank lines trimmed off. Shared by the multi-line window fallback in
    /// <see cref="FindAllSnippetMatchesWithLength"/> and <see cref="DiagnoseNoMatch"/> -> a
    /// caller-supplied snippet very commonly starts/ends with a blank line (e.g. copying a whole
    /// statement plus its closing brace with a trailing newline), which would otherwise inflate
    /// line counts with content that was never meant to be matched.
    /// </summary>
    internal static string[] SplitSnippetIntoTrimmedContentLines(string contextSnippet)
    {
        var normalized = contextSnippet.Replace("\r\n", "\n").Replace("\r", "\n");
        var lineTexts = normalized.Split('\n')
            .SkipWhile(string.IsNullOrWhiteSpace)
            .Reverse().SkipWhile(string.IsNullOrWhiteSpace).Reverse()
            .ToArray();
        return lineTexts.Length == 0 ? normalized.Split('\n') : lineTexts;
    }

    /// <summary>
    /// Cheap nearest-neighbor heuristic for <see cref="DiagnoseNoMatch"/>: shared leading-character
    /// count minus a length-difference penalty. Deliberately NOT full Levenshtein distance (see the
    /// plan's "Rejected: full sliding-window Levenshtein" section) -> this is O(min(len)) per
    /// candidate line rather than O(len1 x len2), which matters when it runs once per filtered
    /// snippet line against every source line in the file. Higher is more similar; can be negative.
    /// </summary>
    private static int LineSimilarityScore(string trimmedSnippetLine, string trimmedSourceLine)
    {
        int sharedPrefix = 0;
        int maxPrefix = Math.Min(trimmedSnippetLine.Length, trimmedSourceLine.Length);
        while (sharedPrefix < maxPrefix && trimmedSnippetLine[sharedPrefix] == trimmedSourceLine[sharedPrefix])
        {
            sharedPrefix++;
        }
        int lengthDiff = Math.Abs(trimmedSnippetLine.Length - trimmedSourceLine.Length);
        return sharedPrefix - lengthDiff;
    }

    private const int MaxHitsPerSnippetLine = 5;

    private const int MaxAllMatchedDiagnosisChars = 2000;

    private const int BoilerplateHitThreshold = 8;

    /// <summary>
    /// "Your block is right but not contiguous / not where you think" case: every filtered
    /// snippet line matched somewhere in the file, just not in the arrangement supplied.
    /// </summary>
    private static string FormatAllMatchedDiagnosis(
        int filteredCount, List<(int SnippetLineIndex, int SourceLineNumber, string SourceLineText, bool IsWhitespaceOnly)> hits,
        string snippetLabel = "contextSnippet", bool verbatimRequired = false)
    {
        // Boilerplate filter: a snippet line with many hits (a bare "}" or "return null;") is not a
        // useful candidate. Drop such lines unless that would drop every line.
        var hitCounts = hits.GroupBy(h => h.SnippetLineIndex).ToDictionary(g => g.Key, g => g.Count());
        var keptIndexes = hitCounts.Keys.Where(k => hitCounts[k] <= BoilerplateHitThreshold).ToHashSet();
        if (keptIndexes.Count == 0)
        {
            keptIndexes = hitCounts.Keys.ToHashSet();
        }
        int droppedSnippetLines = hitCounts.Count - keptIndexes.Count;

        // Per kept snippet line, keep only the first MaxHitsPerSnippetLine hits by source line number.
        var candidateHits = hits.Where(h => keptIndexes.Contains(h.SnippetLineIndex)).ToList();
        var kept = candidateHits.GroupBy(h => h.SnippetLineIndex)
            .SelectMany(g => g.OrderBy(h => h.SourceLineNumber).Take(MaxHitsPerSnippetLine))
            .OrderBy(h => h.SnippetLineIndex).ThenBy(h => h.SourceLineNumber)
            .ToList();
        int omittedHits = candidateHits.Count - kept.Count;

        string intro;
        List<string> lines;
        if (hits.All(h => h.IsWhitespaceOnly))
        {
            // The byte-exact statement is only true for the verbatim (edit) path; a tolerant-path
            // caller must not be told it needs a byte-exact match.
            intro = $"Every line of your {snippetLabel} matches source line content exactly except for " +
                    (verbatimRequired
                        ? "whitespace (indentation/spacing) - ReplaceSnippet requires a byte-exact match. Copy "
                        : "whitespace (indentation/spacing). Copy ") +
                    "these lines directly from the file rather than retyping them:";
            lines = kept.Select(h => $"snippet line {h.SnippetLineIndex + 1} -> source line {h.SourceLineNumber}: \"{h.SourceLineText}\"").ToList();
        }
        else
        {
            intro = $"{filteredCount} of {filteredCount} lines in {snippetLabel} were located individually in " +
                    "the file, but not in the arrangement you supplied. Each is a candidate to copy back verbatim:";
            lines = kept.Select(h => $"snippet line {h.SnippetLineIndex + 1} was located at source line {h.SourceLineNumber}: \"{h.SourceLineText}\"").ToList();
        }

        // Length cap: whole candidate lines only, never a line cut mid-way.
        var shown = new List<string>();
        int length = intro.Length;
        int truncatedLines = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            int added = lines[i].Length + 1; // +1 for the joining newline
            if (length + added > MaxAllMatchedDiagnosisChars)
            {
                truncatedLines = lines.Count - i;
                break;
            }
            shown.Add(lines[i]);
            length += added;
        }
        var text = intro + "\n" + string.Join("\n", shown);

        // Notice goes last and is appended after the cap, so it may exceed the cap by its own length.
        int hiddenCandidates = omittedHits + truncatedLines;
        if (hiddenCandidates > 0 || droppedSnippetLines > 0)
        {
            text += $"\n({hiddenCandidates} more candidate line(s) not shown; {droppedSnippetLines} lines were too common to be useful - copy the exact block from ReadFile)";
        }
        return text;
    }

    /// <summary>
    /// "Wholly unrecognized, not a partial-transcription error" case: zero filtered snippet lines
    /// matched anywhere. Only suggests a nearest-neighbor line when it clears
    /// <paramref name="similarityFloor"/> -> never forces a suggestion onto unrelated content.
    /// </summary>
    private static string FormatZeroMatchedDiagnosis(
        (int SourceLineNumber, string SourceLineText, int Score)?[] nearest, int similarityFloor,
        string snippetLabel = "contextSnippet")
    {
        var best = nearest.Where(n => n != null).Select(n => n!.Value)
            .OrderByDescending(n => n.Score).Cast<(int SourceLineNumber, string SourceLineText, int Score)?>()
            .FirstOrDefault();

        if (best != null && best.Value.Score >= similarityFloor)
        {
            return $"No line in {snippetLabel} was located anywhere in the file - this is not a " +
                   "partial-transcription error, the block is wholly unrecognized. The closest line in the " +
                   $"file is line {best.Value.SourceLineNumber}: \"{best.Value.SourceLineText}\", but it is " +
                   "not a confident match - re-read the file rather than retrying this text.";
        }

        return $"No line in {snippetLabel} was located anywhere in the file, and no line in the file is " +
               $"similar enough to suggest - re-read the file and copy a fresh {snippetLabel} from its current content.";
    }

    /// <summary>
    /// Appends the "did not match" section for <see cref="FormatPartialMatchDiagnosis"/>, one
    /// entry per unmatched snippet line, with a nearest-neighbor candidate when one clears
    /// <paramref name="similarityFloor"/>.
    /// </summary>
    private static void AppendUnmatchedLines(
        List<string> parts, int[] unmatched,
        (int SourceLineNumber, string SourceLineText, int Score)?[] nearest,
        string[] snippetLines, int similarityFloor)
    {
        parts.Add("Lines that did NOT match (re-copy these verbatim from the file):");
        foreach (var i in unmatched)
        {
            var n = nearest[i];
            if (n != null && n.Value.Score >= similarityFloor)
            {
                parts.Add($"  line {i + 1} (\"{snippetLines[i]}\") did not match; closest is source line " +
                          $"{n.Value.SourceLineNumber}: \"{n.Value.SourceLineText}\"");
                parts.Add("    " + DescribeFirstDifference(snippetLines[i], n.Value.SourceLineText));
            }
            else
            {
                parts.Add($"  line {i + 1} (\"{snippetLines[i]}\") did not match, and no file line is close to it");
            }
        }
    }

    /// <summary>
    /// Character-level pointer to where a snippet line first diverges from its closest source
    /// line, so a one-character slip is visible without comparing two long strings by eye.
    /// </summary>
    private static string DescribeFirstDifference(string yours, string file)
    {
        int k = 0;
        int max = Math.Min(yours.Length, file.Length);
        while (k < max && yours[k] == file[k])
        {
            k++;
        }

        static string Excerpt(string s, int from) => from >= s.Length
            ? "(end of line)"
            : "\"" + (s.Length - from > 20 ? s.Substring(from, 20) + "..." : s.Substring(from)) + "\"";

        return $"first difference at char {k + 1} (1-based): yours {Excerpt(yours, k)} vs file {Excerpt(file, k)}";
    }

    /// <summary>
    /// The entries/entries2-shaped and tuple-generic-paren-dropping-shaped failure this design
    /// targets: some but not all filtered snippet lines matched. Names the diverging line(s) and
    /// offers a nearest-neighbor candidate for each.
    /// </summary>
    private static string FormatPartialMatchDiagnosis(
        int[] filteredIndexes, int[] matchedIndexes,
        List<(int SnippetLineIndex, int SourceLineNumber, string SourceLineText, bool IsWhitespaceOnly)> hits,
        (int SourceLineNumber, string SourceLineText, int Score)?[] nearest,
        string[] snippetLines, int similarityFloor, string snippetLabel = "contextSnippet")
    {
        var matchedSet = matchedIndexes.ToHashSet();
        var unmatched = filteredIndexes.Where(i => !matchedSet.Contains(i)).ToArray();
        const int MaxMatchedShown = 3;
        var parts = new List<string>
        {
            $"{matchedIndexes.Length} of {filteredIndexes.Length} lines in {snippetLabel} matched " +
            "somewhere in the file; the rest did not - a partial-transcription error, not a genuine " +
            "absence."
        };
        AppendUnmatchedLines(parts, unmatched, nearest, snippetLines, similarityFloor);

        // One entry per matched snippet line (first hit), whitespace-only ones first since they
        // are actionable; capped so the culprit above is not buried.
        var matchedEntries = hits.Where(h => matchedSet.Contains(h.SnippetLineIndex))
            .GroupBy(h => h.SnippetLineIndex)
            .Select(g => g.OrderBy(h => h.IsWhitespaceOnly ? 0 : 1).ThenBy(h => h.SourceLineNumber).First())
            .OrderBy(h => h.IsWhitespaceOnly ? 0 : 1).ThenBy(h => h.SnippetLineIndex)
            .ToList();
        parts.Add("Lines that matched (for orientation):");
        parts.AddRange(matchedEntries.Take(MaxMatchedShown)
            .Select(h => h.IsWhitespaceOnly
                ? $"  line {h.SnippetLineIndex + 1} -> source line {h.SourceLineNumber}: whitespace differs only - \"{h.SourceLineText}\""
                : $"  line {h.SnippetLineIndex + 1} -> source line {h.SourceLineNumber}: \"{h.SourceLineText}\""));
        if (matchedEntries.Count > MaxMatchedShown)
        {
            parts.Add($"  ... and {matchedEntries.Count - MaxMatchedShown} more matched");
        }
        return string.Join("\n", parts);
    }

    /// <summary>
    /// Dispatches <see cref="DiagnoseNoMatch"/>'s evidence to the right classification: all
    /// filtered lines matched (non-contiguous), some matched (partial-transcription error), or
    /// none matched (wholly unrecognized). A similarity floor of 2 (shared-prefix minus
    /// length-difference) avoids forcing a nearest-neighbor suggestion onto unrelated content.
    /// </summary>
    private static string FormatNoMatchDiagnosis(
        int[] filteredIndexes, int[] matchedIndexes,
        List<(int SnippetLineIndex, int SourceLineNumber, string SourceLineText, bool IsWhitespaceOnly)> hits,
        (int SourceLineNumber, string SourceLineText, int Score)?[] nearest,
        string[] snippetLines, string snippetLabel = "contextSnippet", bool verbatimRequired = false)
    {
        const int SimilarityFloor = 2;

        if (matchedIndexes.Length == filteredIndexes.Length)
        {
            return FormatAllMatchedDiagnosis(filteredIndexes.Length, hits, snippetLabel, verbatimRequired);
        }

        if (matchedIndexes.Length > 0)
        {
            return FormatPartialMatchDiagnosis(filteredIndexes, matchedIndexes, hits, nearest, snippetLines, SimilarityFloor, snippetLabel);
        }

        return FormatZeroMatchedDiagnosis(nearest, SimilarityFloor, snippetLabel);
    }

    /// <summary>
    /// One single-threaded pass over the source lines: collects every exact trimmed-text hit AND
    /// tracks the closest source line (<see cref="LineSimilarityScore"/>) per filtered snippet
    /// line in the same loop - two collections from one traversal, not two passes. Deliberately
    /// NOT Parallel.ForEach - see the plan doc's "Rejected" section.
    /// </summary>
    /// <remarks>
    /// A hit whose exact trimmed-text check misses is retried with internal whitespace runs
    /// collapsed (still <see cref="StringComparison.Ordinal"/> - case-sensitive) and, if that
    /// collapsed comparison matches, recorded as a hit with <c>IsWhitespaceOnly: true</c> instead
    /// of falling through to the nearest-neighbor heuristic. This lets the formatters tell a model
    /// "this line is right except for whitespace/indentation" apart from a genuine content miss.
    /// Deliberately case-sensitive (no OrdinalIgnoreCase): see plan doc Step 6.
    /// </remarks>
    private static (
        List<(int SnippetLineIndex, int SourceLineNumber, string SourceLineText, bool IsWhitespaceOnly)> Hits,
        (int SourceLineNumber, string SourceLineText, int Score)?[] Nearest)
        GatherNoMatchEvidence(SourceText sourceText, string[] snippetLines, int[] filteredIndexes)
    {
        var hits = new List<(int SnippetLineIndex, int SourceLineNumber, string SourceLineText, bool IsWhitespaceOnly)>();
        var nearest = new (int SourceLineNumber, string SourceLineText, int Score)?[snippetLines.Length];
        var sourceLines = sourceText.Lines;

        for (int s = 0; s < sourceLines.Count; s++)
        {
            var sourceLineText = sourceLines[s].ToString().Trim();
            if (sourceLineText.Length == 0)
            {
                continue;
            }

            foreach (var i in filteredIndexes)
            {
                var snippetLine = snippetLines[i];
                if (sourceLineText.Contains(snippetLine, StringComparison.Ordinal))
                {
                    hits.Add((i, s + 1, sourceLineText, false));
                    continue;
                }

                if (CollapseWhitespace(sourceLineText).Contains(CollapseWhitespace(snippetLine), StringComparison.Ordinal))
                {
                    hits.Add((i, s + 1, sourceLineText, true));
                    continue;
                }

                var score = LineSimilarityScore(snippetLine, sourceLineText);
                if (nearest[i] == null || score > nearest[i]!.Value.Score)
                {
                    nearest[i] = (s + 1, sourceLineText, score);
                }
            }
        }

        return (hits, nearest);
    }

    /// <summary>
    /// Collapses runs of whitespace to a single space, for the whitespace-only-hit fallback in
    /// <see cref="GatherNoMatchEvidence"/>. Case is left untouched - the fallback comparison stays
    /// Ordinal, not OrdinalIgnoreCase, so a case-only difference is never folded into this bucket.
    /// </summary>
    private static string CollapseWhitespace(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    /// <summary>
    /// Diagnoses why <paramref name="contextSnippet"/> matched nothing in
    /// <paramref name="sourceText"/>, so <see cref="ContextErrorBuilder"/> can append orienting
    /// guidance to a NoMatch error instead of a bare "not found". See
    /// docs/current/plans/plan_contexterrorbuilder_orienting_guidance.md for the full design.
    /// </summary>
    private static string DiagnoseNoMatch(
        SourceText sourceText, string contextSnippet,
        string snippetLabel = "contextSnippet", bool verbatimRequired = false)
    {
        const int MinLineLength = 4;

        var rawSnippetLines = SplitSnippetIntoTrimmedContentLines(contextSnippet);
        var snippetLines = rawSnippetLines.Select(l => l.Trim()).ToArray();
        var trimmedSource = sourceText.Lines.Select(l => l.ToString().Trim()).ToArray();
        var window = AlignSnippetWindow(trimmedSource, snippetLines);

        if (window is { IsFullMatch: true } full
            && FormatWhitespaceReport(sourceText, rawSnippetLines, full, rawSnippetLines.Length) is { } whitespaceReport)
        {
            return $"Every line of your {snippetLabel} matches the file at source lines {full.Position + 1}-" +
                   $"{full.Position + rawSnippetLines.Length} once whitespace at the ends of lines is ignored, but the " +
                   "whitespace differs" +
                   (verbatimRequired ? " - ReplaceSnippet requires a byte-exact match, so the indentation must be copied exactly:" : ":") +
                   "\n" + whitespaceReport;
        }

        var filteredIndexes = Enumerable.Range(0, snippetLines.Length)
            .Where(i => snippetLines[i].Length >= MinLineLength).ToArray();

        if (filteredIndexes.Length == 0)
        {
            return $"The {snippetLabel} has no line long enough to search for individually " +
                   "(every line is under 4 characters once trimmed) - re-read the file and copy " +
                   "a longer, more specific snippet.";
        }

        var (hits, nearest) = GatherNoMatchEvidence(sourceText, snippetLines, filteredIndexes);
        var matchedIndexes = hits.Select(h => h.SnippetLineIndex).Distinct().ToArray();
        var diagnosis = FormatNoMatchDiagnosis(filteredIndexes, matchedIndexes, hits, nearest, snippetLines, snippetLabel, verbatimRequired);
        var closestWindow = FormatClosestWindowHint(sourceText, trimmedSource, snippetLines, rawSnippetLines, window);
        return closestWindow is null ? diagnosis : diagnosis + "\n" + closestWindow;
    }

    /// <summary>
    /// Names the first divergence between the snippet and the contiguous source window that matches
    /// its longest leading run. Returns null when there is nothing useful to say: fewer than two
    /// snippet lines, no leading line matching anywhere, or a full contiguous (trimmed) match, which
    /// <see cref="DiagnoseNoMatch"/> reports as a whitespace difference instead. Comparisons are
    /// trimmed, matching LineBlockAligner's convention. Snippet lines are numbered over the same
    /// array (blank interior lines included, 1-based) as every other label in the message, and a
    /// blank source line is described as blank rather than quoted as if it were content.
    /// </summary>
    private static string? FormatClosestWindowHint(
        SourceText sourceText, string[] trimmedSource, string[] snippetLines, string[] rawSnippetLines, Alignment? window)
    {
        const int MaxQuotedChars = 120;

        if (window is not { } best || best.MatchedCount < 1 || best.IsFullMatch)
        {
            return null;
        }

        static string Quote(string text) => text.Length <= MaxQuotedChars ? text : text[..MaxQuotedChars] + "...";

        var divergeAt = best.Position + best.MatchedCount;
        var snippetLine = snippetLines[best.MatchedCount];
        var snippetPart = snippetLine.Length == 0
            ? $"snippet line {best.MatchedCount + 1} (a blank line)"
            : $"snippet line {best.MatchedCount + 1} (\"{Quote(snippetLine)}\")";
        var sourcePart = divergeAt >= trimmedSource.Length
            ? "the end of the file"
            : trimmedSource[divergeAt].Length == 0 && snippetLine.Length > 0
                ? $"source line {divergeAt + 1}, a blank line that your snippet does not include"
                : $"source line {divergeAt + 1} (\"{Quote(trimmedSource[divergeAt])}\")";
        var hint = $"Closest window: source lines {best.Position + 1}-{best.Position + best.MatchedCount} match your first " +
                   $"{best.MatchedCount} snippet line(s); {snippetPart} differs from {sourcePart}.";

        var whitespace = FormatWhitespaceReport(sourceText, rawSnippetLines, best, best.MatchedCount, compact: true);
        return whitespace is null
            ? hint
            : hint + "\nThe lines that did match also differ in whitespace:\n" + whitespace;
    }

    /// <summary>
    /// Aligns the snippet's lines (blank lines included, so numbering matches every other
    /// "snippet line N" label in a NoMatch message) against the trimmed source lines and returns
    /// the best leading-run alignment. Null for a single-line snippet. Comparisons are trimmed,
    /// matching <see cref="LineBlockAligner"/>'s convention, so a full match here can still differ
    /// from the file in leading/trailing whitespace.
    /// </summary>
    private static Alignment? AlignSnippetWindow(string[] trimmedSource, string[] trimmedSnippetLines)
    {
        if (trimmedSnippetLines.Length < 2)
        {
            return null;
        }

        var candidates = Enumerable.Range(0, trimmedSource.Length).Where(i => trimmedSource[i] == trimmedSnippetLines[0]).ToArray();
        if (candidates.Length == 0)
        {
            candidates = Enumerable.Range(0, trimmedSource.Length).ToArray();
        }

        return LineBlockAligner.FindBestPrefixAlignment(trimmedSource, trimmedSnippetLines, candidates);
    }

    /// <summary>
    /// Whether a raw snippet line can sit in the file's raw line under the exact matcher's
    /// substring semantics: the first snippet line may start mid-line (so the file line only has
    /// to END with it), the last may stop mid-line (the file line only has to START with it), and
    /// every interior line must be equal. A trimmed-equal line that fails this is a whitespace
    /// difference that makes the exact matcher miss.
    /// </summary>
    private static bool IsRawCompatible(string fileLine, string snippetLine, bool isFirst, bool isLast)
    {
        if (isFirst && isLast)
        {
            return fileLine.Contains(snippetLine, StringComparison.Ordinal);
        }

        if (isFirst)
        {
            return fileLine.EndsWith(snippetLine, StringComparison.Ordinal);
        }

        if (isLast)
        {
            return fileLine.StartsWith(snippetLine, StringComparison.Ordinal);
        }

        return string.Equals(fileLine, snippetLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// One sentence naming how a trimmed-equal snippet line differs from its file line: leading
    /// whitespace first (the common dropped-indentation case, with a tabs-vs-spaces note), else
    /// trailing whitespace. "expected" is what the file has, "got" is what the snippet has.
    /// </summary>
    private static string DescribeWhitespaceDifference(
        string fileLine, string snippetLine, int snippetLineNumber, int fileLineNumber, bool isFirst, bool isLast)
    {
        static string Leading(string line)
        {
            int i = 0;
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }
            return line[..i];
        }

        static string Trailing(string line)
        {
            int i = line.Length;
            while (i > 0 && char.IsWhiteSpace(line[i - 1]))
            {
                i--;
            }
            return line[i..];
        }

        static string Describe(string run)
        {
            if (run.Length == 0)
            {
                return "0";
            }

            int tabs = run.Count(c => c == '\t');
            int spaces = run.Count(c => c == ' ');
            int other = run.Length - tabs - spaces;
            var parts = new List<string>();
            if (tabs > 0)
            {
                parts.Add(tabs == 1 ? "1 tab" : $"{tabs} tabs");
            }
            if (spaces > 0)
            {
                parts.Add(spaces == 1 ? "1 space" : $"{spaces} spaces");
            }
            if (other > 0)
            {
                parts.Add($"{other} other whitespace character(s)");
            }
            return string.Join(" and ", parts);
        }

        var fileLead = Leading(fileLine);
        var snippetLead = Leading(snippetLine);
        var prefix = $"snippet line {snippetLineNumber} matches file line {fileLineNumber} except ";
        // The first snippet line may start mid-indentation, so a shorter leading run that the file's
        // run ends with is not a difference (see IsRawCompatible).
        var leadingDiffers = isFirst
            ? !fileLead.EndsWith(snippetLead, StringComparison.Ordinal)
            : !string.Equals(fileLead, snippetLead, StringComparison.Ordinal);
        if (leadingDiffers)
        {
            var kindNote = fileLead.Contains('\t') != snippetLead.Contains('\t') ? " (tabs vs spaces)" : "";
            return $"{prefix}leading whitespace: expected {Describe(fileLead)}, got {Describe(snippetLead)}{kindNote}";
        }

        return $"{prefix}trailing whitespace: expected {Describe(Trailing(fileLine))}, got {Describe(Trailing(snippetLine))}";
    }

    /// <summary>
    /// For the first <paramref name="comparedCount"/> lines of an aligned window (trimmed-equal by
    /// construction), reports every snippet line whose raw text cannot sit in the file's raw line
    /// (<see cref="IsRawCompatible"/>), then appends the file's actual lines for that window
    /// verbatim as a copy-ready block. Bounded: at most 5 differences, 12 block lines and 1200
    /// block characters, whole lines only. Returns null when no line differs in whitespace.
    /// </summary>
    private static string? FormatWhitespaceReport(
        SourceText sourceText, string[] rawSnippetLines, Alignment window, int comparedCount, bool compact = false)
    {
        const int MaxLinesReported = 5;
        const int MaxBlockLines = 12;
        const int MaxBlockChars = 1200;

        var lines = sourceText.Lines;
        var differences = new List<string>();
        for (int k = 0; k < comparedCount && window.Position + k < lines.Count && k < rawSnippetLines.Length; k++)
        {
            var fileLine = lines[window.Position + k].ToString();
            if (IsRawCompatible(fileLine, rawSnippetLines[k], k == 0, k == rawSnippetLines.Length - 1))
            {
                continue;
            }

            differences.Add(DescribeWhitespaceDifference(
                fileLine, rawSnippetLines[k], k + 1, window.Position + k + 1, k == 0, k == rawSnippetLines.Length - 1));
        }

        if (differences.Count == 0)
        {
            return null;
        }

        if (compact)
        {
            // Partial-match hint: the content divergence is the main message, so name one whitespace
            // difference plus a count and a pointer instead of repeating the whole report and block.
            return "  " + differences[0] + "\n  (whitespace differs on " + differences.Count +
                   $" line(s) of source lines {window.Position + 1}-{window.Position + comparedCount}; copy that text exactly from ReadFile)";
        }

        var parts = differences.Take(MaxLinesReported).Select(d => "  " + d).ToList();
        if (differences.Count > MaxLinesReported)
        {
            parts.Add($"  ... and {differences.Count - MaxLinesReported} more line(s) differ in whitespace");
        }

        int blockEnd = Math.Min(lines.Count, window.Position + comparedCount);
        var shown = new List<string>();
        int chars = 0;
        for (int i = window.Position; i < blockEnd; i++)
        {
            var text = lines[i].ToString();
            if (shown.Count >= MaxBlockLines || chars + text.Length + 1 > MaxBlockChars)
            {
                break;
            }

            shown.Add(text);
            chars += text.Length + 1;
        }

        if (shown.Count > 0)
        {
            parts.Add($"The file's actual text at source lines {window.Position + 1}-{window.Position + shown.Count}, " +
                      "verbatim (copy it exactly, including indentation):");
            parts.AddRange(shown);
            int hidden = blockEnd - window.Position - shown.Count;
            if (hidden > 0)
            {
                parts.Add($"({hidden} more line(s) not shown; copy the rest from ReadFile)");
            }
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Describes each candidate's real, verbatim adjacent line(s) so an ambiguous-match error can
    /// let the caller copy one back as lineBefore/lineAfter instead of constructing one from
    /// memory. Also flags true ties (identical before AND after) since those can't be
    /// disambiguated this way at all. When <paramref name="memberNameAt"/> returns a name for a
    /// match start offset, the match line also names its enclosing member. At most
    /// <paramref name="maxListed"/> matches are listed, followed by an "... and N more" line.
    /// When <paramref name="selectorsSupported"/> is set (only the exact ReplaceSnippet matcher, which
    /// has withinMember/matchLine), each match is attributed to its first non-whitespace character (the
    /// position the selectors compare against) and also lists a ready-to-copy matchLine=N plus
    /// withinMember="X" when X is the enclosing member of exactly one of <paramref name="allMatches"/>;
    /// the tie advice then points at those selectors. Left false, the output is unchanged.
    /// </summary>
    internal static string DescribeAmbiguousCandidates(
        SourceText sourceText, List<SnippetMatch> allMatches, string snippetLabel = "contextSnippet",
        Func<int, string?>? memberNameAt = null, int maxListed = int.MaxValue, bool selectorsSupported = false)
    {
        int AnchorOf(SnippetMatch match) => selectorsSupported ? FirstNonWhitespacePosition(sourceText, match) : match.Start;
        string? MemberOf(SnippetMatch match) => memberNameAt?.Invoke(AnchorOf(match));

        // A member name only separates a match when no other match is in a member of that name.
        var uniqueMembers = selectorsSupported && memberNameAt != null
            ? allMatches.Select(MemberOf).Where(name => !string.IsNullOrEmpty(name))
                .GroupBy(name => name!, StringComparer.Ordinal).Where(g => g.Count() == 1).Select(g => g.Key)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var descriptions = new List<(string Before, string After)>();
        var lines = new List<string>();
        var listedCount = Math.Min(allMatches.Count, maxListed);
        for (int i = 0; i < listedCount; i++)
        {
            var firstLineIndex = sourceText.Lines.GetLinePosition(allMatches[i].Start).Line;
            var lastLineIndex = sourceText.Lines.GetLinePosition(allMatches[i].Start + allMatches[i].Length - 1).Line;
            var before = firstLineIndex > 0 ? sourceText.Lines[firstLineIndex - 1].ToString().Trim() : "(start of file)";
            var after = lastLineIndex < sourceText.Lines.Count - 1
                ? sourceText.Lines[lastLineIndex + 1].ToString().Trim()
                : "(end of file)";
            descriptions.Add((before, after));
            var memberName = MemberOf(allMatches[i]);
            var inMember = string.IsNullOrEmpty(memberName) ? string.Empty : $", in {memberName}";
            var startLineIndex = selectorsSupported ? sourceText.Lines.GetLinePosition(AnchorOf(allMatches[i])).Line : firstLineIndex;
            var lineSpan = selectorsSupported && startLineIndex == lastLineIndex
                ? $"line {startLineIndex + 1}"
                : $"lines {startLineIndex + 1}-{lastLineIndex + 1}";
            var selectors = string.Empty;
            if (selectorsSupported)
            {
                var member = !string.IsNullOrEmpty(memberName) && uniqueMembers.Contains(memberName) ? $"withinMember=\"{memberName}\" " : string.Empty;
                selectors = $"{member}matchLine={startLineIndex + 1} ";
            }

            lines.Add($"Match {i + 1} ({lineSpan}{inMember}): {selectors}lineBefore=\"{before}\" lineAfter=\"{after}\"");
        }

        if (listedCount < allMatches.Count)
        {
            lines.Add($"... and {allMatches.Count - listedCount} more");
        }

        var tieGroups = descriptions.Select((d, i) => (d, i)).GroupBy(x => x.d).Where(g => g.Count() > 1);
        foreach (var tie in tieGroups)
        {
            var matchNumbers = string.Join(" and ", tie.Select(x => x.i + 1));
            lines.Add(selectorsSupported
                ? $"Matches {matchNumbers} have identical surrounding lines, so lineBefore/lineAfter cannot tell them apart. " +
                  $"Pass matchLine (and withinMember where listed) copied from the list above, or widen {snippetLabel}."
                : $"Matches {matchNumbers} have identical surrounding lines and cannot be " +
                  $"disambiguated by lineBefore/lineAfter alone; use a different tool or a longer {snippetLabel}.");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// lineBefore/lineAfter disambiguate a match against exactly one real adjacent source line ->
    /// a value containing an embedded newline can never satisfy that comparison and would
    /// otherwise silently eliminate every candidate, surfacing as a misleading "not found"
    /// instead of naming the actual problem.
    /// </summary>
    private static void ThrowIfMultiLine(SourceText sourceText, string parameterName, string? value, bool isAfter)
    {
        if (value != null && value.Contains('\n'))
        {
            throw ContextErrorBuilder.Build(
                SnippetMatchOutcome.InvalidDisambiguator, contextSnippet: value, sourceText,
                parameterName: parameterName, invalidValue: value, isAfter: isAfter);
        }
    }

    /// <summary>
    /// Shared lineBefore/lineAfter filter for FindAllSnippetMatchesWithLength and
    /// FindAllExactSnippetMatches -> previously duplicated between the two.
    /// </summary>
    private static List<SnippetMatch> FilterByAdjacentLines(
        SourceText sourceText, List<SnippetMatch> allMatches, string? lineBefore, string? lineAfter)
    {
        if (lineBefore == null && lineAfter == null)
        {
            return allMatches;
        }

        var lbTrimmed = lineBefore?.Trim();
        var laTrimmed = lineAfter?.Trim();

        return allMatches.Where(match => FilterByAdjacentLinesPredicate(
            sourceText, match, lbTrimmed, laTrimmed)).ToList();
    }

    private static bool FilterByAdjacentLinesPredicate(
        SourceText sourceText, SnippetMatch match, string? lbTrimmed, string? laTrimmed)
    {
        var firstLineIndex = sourceText.Lines.GetLinePosition(match.Start).Line;
        var lastLineIndex = sourceText.Lines.GetLinePosition(match.Start + match.Length - 1).Line;

        if (lbTrimmed != null)
        {
            if (firstLineIndex == 0)
            {
                return false;
            }

            var prevLine = sourceText.Lines[firstLineIndex - 1].ToString().Trim();
            if (!MatchLine(prevLine, lbTrimmed))
            {
                return false;
            }
        }
        if (laTrimmed != null)
        {
            if (lastLineIndex >= sourceText.Lines.Count - 1)
            {
                return false;
            }

            var nextLine = sourceText.Lines[lastLineIndex + 1].ToString().Trim();
            if (!MatchLine(nextLine, laTrimmed))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Checks if a source line contains the pattern. Falls back to a quote-normalized comparison
    /// to handle AI-provided snippets where `\"` wasn't unescaped (e.g., from JSON context).
    /// </summary>
    private static bool MatchLine(string sourceLine, string pattern)
    {
        if (sourceLine.Contains(pattern, StringComparison.Ordinal))
        {
            return true;
        }
        // Normalize JSON escape sequences and retry
        var normalized = pattern
            .Replace("\\\"", "\"")
            .Replace("\\'", "'")
            .Replace("\\\\", "\\");
        return normalized != pattern && sourceLine.Contains(normalized, StringComparison.Ordinal);
    }

    /// <summary>
    /// Prepends a <c>// Added by <toolName></c> leading-trivia comment to a freshly synthesized
    /// member declaration, on its own line above any trivia the member already carries (e.g. a
    /// blank-line separator). Intended for tools that insert a brand-new member -> a constructor,
    /// method, property, or field -> so the addition is easy to spot in a diff or code review
    /// without cross-referencing which MCP tool call produced it. Not for tools that edit an
    /// existing member in place (e.g. AddSummaryComment, ChangeAccessibility) -> only for genuinely
    /// new members.
    /// </summary>
    public static T WithAddedByComment<T>(this T member, string toolName) where T : MemberDeclarationSyntax
    {
        var comment = SyntaxFactory.Comment($"// Added by {toolName} (expected - used for diagnostics)");
        var newLeadingTrivia = member.GetLeadingTrivia()
            .Insert(0, comment)
            .Insert(1, SyntaxFactory.CarriageReturnLineFeed);
        return (T)member.WithLeadingTrivia(newLeadingTrivia);
    }

    /// <summary>
    /// After <see cref="FindSnippetPosition"/> returns a position, that position may land on a
    /// modifier keyword (e.g., "public") rather than the declared identifier.
    /// This helper scans the snippet span for identifier tokens and returns the position of the
    /// <em>last</em> one (the declared name, not the return type).
    /// Required before calling <c>SymbolFinder.FindSymbolAtPositionAsync</c>, which returns null
    /// when the cursor is on a non-identifier token.
    /// </summary>
    public static int AdvanceToLastIdentifier(SyntaxNode root, int snippetStart, int snippetLength)
    {
        var startToken = root.FindToken(snippetStart);
        if (startToken.IsKind(SyntaxKind.IdentifierToken))
        {
            return snippetStart;
        }

        var snippetEnd = snippetStart + snippetLength;
        var ident = root.DescendantTokens()
            .Where(t => t.SpanStart >= snippetStart && t.SpanStart < snippetEnd &&
                        t.IsKind(SyntaxKind.IdentifierToken))
            .Select(t => (SyntaxToken?)t)
            .LastOrDefault();

        return ident.HasValue ? ident.Value.SpanStart : snippetStart;
    }

    /// <summary>
    /// Finds the SyntaxNode at the position identified by contextSnippet.
    /// </summary>
    public static SyntaxNode FindNodeAtSnippet(
        SyntaxNode root, SourceText text, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        return root.FindNode(new TextSpan(pos, contextSnippet.Length));
    }

    /// <summary>
    /// Gets the ISymbol at the contextSnippet's position: the node's own declared symbol if the
    /// snippet points at a declaration, otherwise the symbol the node references.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT walk up <c>AncestorsAndSelf()</c> looking for the first ancestor with a
    /// declared symbol -> for a snippet that lands on a plain reference (e.g. a field-assignment line
    /// like <c>_x = x;</c>, which is neither a declaration nor annotated with symbol info itself, just
    /// its child IdentifierName is), that walk climbs past the reference entirely and returns the
    /// *enclosing member's* declared symbol (e.g. the constructor), silently resolving to the wrong
    /// symbol instead of the one the snippet actually names. Only <paramref name="node"/> itself is
    /// checked for a declared symbol; a <see cref="FieldDeclarationSyntax"/>/<see cref="EventFieldDeclarationSyntax"/>
    /// snippet (which can declare multiple variables and so has no declared symbol of its own) is
    /// handled by descending to its first <see cref="VariableDeclaratorSyntax"/> instead.
    /// </remarks>
    public static async Task<ISymbol?> FindSymbolAtSnippetAsync(
        Document document, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null,
        CancellationToken cancellationToken = default)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var text = await document.GetTextAsync(cancellationToken);
        if (root == null || model == null)
        {
            return null;
        }

        var pos = FindSnippetPosition(text, contextSnippet, lineBefore, lineAfter);
        var node = root.FindNode(new TextSpan(pos, 0));

        return model.GetDeclaredSymbol(node, cancellationToken)
               ?? model.GetSymbolInfo(node, cancellationToken).Symbol
               ?? node.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                      .Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                      .FirstOrDefault(s => s != null);
    }

    /// <summary>
    /// C# reserved keywords that cannot be used as variable names (in non-verbatim form).
    /// </summary>
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte",
        "sealed", "short", "sizeof", "stackalloc", "static", "string", "struct", "switch",
        "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while"
    };

    /// <summary>
    /// Generates a unique variable name within the given scope (method body, class, etc.)
    /// that doesn't conflict with existing variables or parameters.
    /// 
    /// Examples:
    /// - If baseName="temp" and "temp" is available, returns "temp"
    /// - If "temp" exists, tries "temp1", "temp2", etc. until finding a free name
    /// - If baseName is a reserved keyword (e.g., "class"), appends "1": "class1"
    /// - Returns deterministic, camelCase-safe names for use in local variable extraction
    /// </summary>
    /// <param name="scope">The syntax node representing the scope (method body, class, block, etc.)</param>
    /// <param name="baseName">Base name to use (e.g., "temp", "value", "result"). Will be converted to camelCase.</param>
    /// <returns>A unique variable name safe to use in the given scope.</returns>
    public static string GetUniqueVariableName(SyntaxNode scope, string baseName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            throw new ArgumentException("baseName must not be empty", nameof(baseName));
        }

        // Convert to camelCase: first letter lowercase, rest as-is after first char
        var camelCaseName = char.ToLowerInvariant(baseName[0]) + baseName.Substring(1);

        // Collect all identifiers in the scope (variables, parameters, fields, etc.)
        var existingNames = new HashSet<string>(StringComparer.Ordinal);

        // Add all declared variables in this scope
        var descendants = scope.DescendantNodes();

        // Local variables and parameters
        foreach (var varDecl in descendants.OfType<VariableDeclaratorSyntax>())
        {
            if (varDecl.Identifier.Text is string name && !string.IsNullOrWhiteSpace(name))
            {
                existingNames.Add(name);
            }
        }

        // Parameters (in method declarations, delegates, etc.)
        foreach (var param in descendants.OfType<ParameterSyntax>())
        {
            if (param.Identifier.Text is string name && !string.IsNullOrWhiteSpace(name))
            {
                existingNames.Add(name);
            }
        }

        // Local functions and type parameters
        foreach (var localFunc in descendants.OfType<LocalFunctionStatementSyntax>())
        {
            if (localFunc.Identifier.Text is string name && !string.IsNullOrWhiteSpace(name))
            {
                existingNames.Add(name);
            }
        }

        // Check if base name is reserved or already exists
        if (ReservedKeywords.Contains(camelCaseName) || existingNames.Contains(camelCaseName))
        {
            // Try appending numeric suffixes: name1, name2, name3, ...
            for (int i = 1; i <= 10000; i++)
            {
                var candidate = camelCaseName + i;
                if (!existingNames.Contains(candidate) && !ReservedKeywords.Contains(candidate))
                {
                    return candidate;
                }
            }

            // Fallback: this should never happen in practice, but provide a safe default
            return string.Concat(camelCaseName, "_", Guid.NewGuid().ToString("N").AsSpan(0, 8));
        }

        return camelCaseName;
    }

    /// <summary>
    /// Generates standard C# XML documentation comments for a given symbol.
    /// Returns a string containing properly formatted XML doc tags (///).
    /// Handles methods, properties, constructors, indexers, types, and fields.
    /// </summary>
    /// <param name="symbol">The Roslyn symbol to generate documentation for</param>
    /// <returns>XML documentation string with ///, <summary>, <param>, <returns> tags</returns>
    /// <remarks>
    /// Generates standard-level documentation containing:
    /// - <summary> with placeholder description
    /// - <param> tags for each parameter (methods only)
    /// - <returns> tag (for non-void methods)
    /// 
    /// Example output for a method "GetUser(int id, bool active)":
    /// /// <summary>
    /// /// Gets or retrieves the user.
    /// /// </summary>
    /// /// <param name="id">The unique identifier for the user.</param>
    /// /// <param name="active">A value indicating whether to filter by active status.</param>
    /// /// <returns>The requested user object.</returns>
    /// </remarks>
    public static string GenerateXmlDocumentation(ISymbol symbol)
    {
        if (symbol == null)
        {
            throw new ArgumentNullException(nameof(symbol), "Symbol cannot be null");
        }

        var sb = new System.Text.StringBuilder();

        // Generate documentation based on symbol type
        switch (symbol)
        {
            case IMethodSymbol method:
                GenerateMethodDocumentation(sb, method);
                break;

            case IPropertySymbol property:
                GeneratePropertyDocumentation(sb, property);
                break;

            case IFieldSymbol field:
                GenerateFieldDocumentation(sb, field);
                break;

            case ITypeSymbol type:
                GenerateTypeDocumentation(sb, type);
                break;

            case IEventSymbol @event:
                GenerateEventDocumentation(sb, @event);
                break;

            default:
                // Generic fallback for other symbol types
                sb.AppendLine("/// <summary>");
                sb.AppendLine($"/// {GetFriendlySymbolDescription(symbol.Kind)}.");
                sb.AppendLine("/// </summary>");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private static void GenerateMethodDocumentation(System.Text.StringBuilder sb, IMethodSymbol method)
    {
        // Generate summary based on method name and kind
        string summaryText = GenerateMethodSummary(method);

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// {summaryText}");
        sb.AppendLine("/// </summary>");

        // Generate param tags for each parameter
        foreach (var param in method.Parameters)
        {
            string paramDescription = GenerateParameterDescription(param);
            sb.AppendLine($"/// <param name=\"{param.Name}\">{paramDescription}</param>");
        }

        // Generate returns tag if method returns something (not void, not Task)
        if (!method.ReturnsVoid && !IsTaskType(method.ReturnType))
        {
            string returnDescription = GenerateReturnDescription(method);
            sb.AppendLine($"/// <returns>{returnDescription}</returns>");
        }
        else if (method.ReturnsVoid && method.MethodKind == MethodKind.Constructor)
        {
            // Constructors might have special handling
        }
    }

    private static void GeneratePropertyDocumentation(System.Text.StringBuilder sb, IPropertySymbol property)
    {
        sb.AppendLine("/// <summary>");

        if (property.GetMethod != null && property.SetMethod != null)
        {
            sb.AppendLine($"/// Gets or sets the {property.Name} value.");
        }
        else if (property.GetMethod != null)
        {
            sb.AppendLine($"/// Gets the {property.Name} value.");
        }
        else if (property.SetMethod != null)
        {
            sb.AppendLine($"/// Sets the {property.Name} value.");
        }
        else
        {
            sb.AppendLine($"/// Gets or sets the {property.Name}.");
        }

        sb.AppendLine("/// </summary>");

        // Add returns tag describing the return type
        string returnDescription = GetTypeDescription(property.Type);
        sb.AppendLine($"/// <value>{returnDescription}</value>");
    }

    private static void GenerateFieldDocumentation(System.Text.StringBuilder sb, IFieldSymbol field)
    {
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// The {MakeFriendlyName(field.Name)} field.");
        sb.AppendLine("/// </summary>");
    }

    private static void GenerateTypeDocumentation(System.Text.StringBuilder sb, ITypeSymbol type)
    {
        sb.AppendLine("/// <summary>");

        string typeKind = type.TypeKind.ToString().ToLowerInvariant();
        if (type.TypeKind == TypeKind.Class)
        {
            typeKind = "class";
        }
        else if (type.TypeKind == TypeKind.Struct)
        {
            typeKind = "structure";
        }
        else if (type.TypeKind == TypeKind.Interface)
        {
            typeKind = "interface";
        }
        else if (type.TypeKind == TypeKind.Enum)
        {
            typeKind = "enumeration";
        }

        sb.AppendLine($"/// {type.Name} {typeKind}.");
        sb.AppendLine("/// </summary>");
    }

    private static void GenerateEventDocumentation(System.Text.StringBuilder sb, IEventSymbol @event)
    {
        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Occurs when {MakeFriendlyName(@event.Name)}.");
        sb.AppendLine("/// </summary>");
    }

    private static string GenerateMethodSummary(IMethodSymbol method)
    {
        // Special handling for constructors
        if (method.MethodKind == MethodKind.Constructor)
        {
            return $"Initializes a new instance of the {method.ContainingType?.Name} {method.ContainingType?.TypeKind.ToString().ToLowerInvariant()}.";
        }

        // Parse method name to generate meaningful description
        string methodName = method.Name;
        string action = "";

        if (methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase))
        {
            action = "Gets or retrieves";
        }
        else if (methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase))
        {
            action = "Sets";
        }
        else if (methodName.StartsWith("Add", StringComparison.OrdinalIgnoreCase))
        {
            action = "Adds";
        }
        else if (methodName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase))
        {
            action = "Removes";
        }
        else if (methodName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase))
        {
            action = "Deletes";
        }
        else if (methodName.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
        {
            action = "Creates";
        }
        else if (methodName.StartsWith("Is", StringComparison.OrdinalIgnoreCase))
        {
            action = "Determines whether";
        }
        else if (methodName.StartsWith("Has", StringComparison.OrdinalIgnoreCase))
        {
            action = "Determines whether";
        }
        else if (methodName.StartsWith("Can", StringComparison.OrdinalIgnoreCase))
        {
            action = "Determines whether";
        }
        else
        {
            action = "Performs";
        }

        string subject = MakeFriendlyName(methodName.Substring(action == "Performs" ? 0 : GetPrefixLength(methodName)));
        return $"{action} {subject}.";
    }

    private static string GenerateParameterDescription(IParameterSymbol param)
    {
        string typeName = param.Type.Name;
        string paramName = MakeFriendlyName(param.Name);

        // Generate description based on parameter name and type
        if (param.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            param.Name.Equals("identifier", StringComparison.OrdinalIgnoreCase))
        {
            return "The unique identifier.";
        }

        if (param.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            return "The name.";
        }

        if (param.Name.Equals("value", StringComparison.OrdinalIgnoreCase))
        {
            return $"The {typeName.ToLowerInvariant()} value.";
        }

        if (param.Name.Equals("count", StringComparison.OrdinalIgnoreCase) ||
            param.Name.Equals("size", StringComparison.OrdinalIgnoreCase) ||
            param.Name.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            return $"The {param.Name.ToLowerInvariant()} of the collection.";
        }

        if (param.Name.Equals("index", StringComparison.OrdinalIgnoreCase))
        {
            return "The zero-based index.";
        }

        if (param.Type.Name == "Boolean" || param.Type.Name == "bool")
        {
            return $"A value indicating whether to {MakeFriendlyName(param.Name)}.";
        }

        if (param.Type.TypeKind == TypeKind.Enum)
        {
            return $"The {typeName} value.";
        }

        // Generic fallback
        return $"The {paramName} parameter.";
    }

    private static string GenerateReturnDescription(IMethodSymbol method)
    {
        string typeName = method.ReturnType.Name;

        // Special case for Task<T>
        if (IsTaskType(method.ReturnType))
        {
            if (method.ReturnType is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
            {
                return $"A task representing the asynchronous operation.";
            }
            return "A task representing the asynchronous operation.";
        }

        if (typeName == "Boolean" || typeName == "bool")
        {
            return "A value indicating the result of the operation.";
        }

        if (typeName == "String" || typeName == "string")
        {
            return "The resulting string value.";
        }

        if (typeName == "Int32" || typeName == "int")
        {
            return "The numeric result.";
        }

        if (method.ReturnType.TypeKind == TypeKind.Enum)
        {
            return $"The {typeName} value.";
        }

        // Generic fallback
        return $"The {typeName} result.";
    }

    private static string GetTypeDescription(ITypeSymbol type)
    {
        string typeName = type.Name;

        if (typeName == "String" || typeName == "string")
        {
            return "A string value.";
        }

        if (typeName == "Boolean" || typeName == "bool")
        {
            return "A boolean value.";
        }

        if (typeName == "Int32" || typeName == "int")
        {
            return "An integer value.";
        }

        return $"A {type.Name} value.";
    }

    private static bool IsTaskType(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        var name = type.Name;
        return name == "Task" || name == "Task`1" || (type.ToString()?.StartsWith("System.Threading.Tasks.Task") ?? false);
    }

    private static string MakeFriendlyName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        // Convert camelCase/PascalCase to friendly name
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                result.Append(' ');
            }

            result.Append(char.ToLower(c));
        }
        return result.ToString();
    }

    private static int GetPrefixLength(string methodName)
    {
        if (methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Add", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Can", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Has", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Is", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (methodName.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ||
            methodName.StartsWith("Create", StringComparison.OrdinalIgnoreCase))
        {
            return 6;
        }

        return 0;
    }

    private static string GetFriendlySymbolDescription(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.Method => "Provides method functionality",
            SymbolKind.Property => "Provides property access",
            SymbolKind.Field => "Provides field data",
            SymbolKind.NamedType => "Provides type definition",
            SymbolKind.Event => "Occurs when a specific condition is met",
            _ => "Provides functionality"
        };
    }
}
