using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common
{
    /// <summary>
    /// A contextSnippet/oldContent matched the file only at a looser whitespace tolerance than the call accepts, so
    /// nothing was located or applied; the message lists the file's own copy-ready lines. Distinct from
    /// <see cref="ToolNotFoundException"/> (nothing close exists) so a caller can tell "retype with the file's text"
    /// from "re-read the file". Maps to <see cref="ToolErrorCode.MatchedLooserThanRequested"/>. Lives beside
    /// <c>SnippetErrorFormatter</c> for now; it can move to ToolException.cs when callers migrate.
    /// </summary>
    public sealed class ToolMatchedLooserTierException : ToolException
    {
        public override string ErrorCode => ToolErrorCode.MatchedLooserThanRequested;

        public ToolMatchedLooserTierException(string message) : base(message)
        {
        }
    }
}

namespace RoslynSentinel.Common.SnippetMatching
{
    /// <summary>
    /// Turns a failed <see cref="SnippetMatchResult"/> into a <see cref="ToolException"/> whose text always carries the
    /// file's own copy-ready candidates (slice 3b of the whitespace-tolerance proposal, "Candidates in every failure"),
    /// so a weak model recovers on its next turn by copying text instead of reconstructing it.
    /// <list type="bullet">
    /// <item><b>NoMatch</b>: the tiers and case mode probed, the snippet echoed with visible whitespace, then the ranked
    /// closest windows as the file's literal lines, or the explicit statement that nothing close exists.</item>
    /// <item><b>MatchedLooserThanRequested</b>: each near-match as the file's literal lines plus the structured
    /// differences as plain statements; never presented as applied.</item>
    /// <item><b>Ambiguous / StillAmbiguous</b>: every candidate in file order with its literal lines and the values that
    /// select it, the filter trail when selectors ran.</item>
    /// <item><b>SelectorMismatch / AdjacentLineMismatch</b>: the candidates the filter removed, with selecting values.</item>
    /// <item><b>InvalidDisambiguator</b>: which selector is malformed and why.</item>
    /// </list>
    /// Rules: candidate lines are the file's text verbatim in a fenced block with nothing added to them; candidates are
    /// ranked or in file order before any truncation and the omitted count is stated; the message never exceeds
    /// <see cref="MaxMessageChars"/> (only the best-ranked candidate is ever shortened); output is deterministic, and
    /// everything the formatter itself writes is ASCII (the file's own lines are reproduced verbatim). No paths, stack
    /// traces or exception text are included.
    /// </summary>
    public static class SnippetErrorFormatter
    {
        /// <summary>Upper bound on the length of a message: the successor to ContextHelper's 2000-character diagnosis cap, sized for whole candidates.</summary>
        internal const int MaxMessageChars = 6000;

        /// <summary>Most lines of the snippet echoed back in a no-match message.</summary>
        internal const int MaxEchoLines = 8;

        private const int MaxEchoLineChars = 100;
        private const int MaxDifferenceTextChars = 80;
        private const int MaxDifferencesListed = 8;
        private const int MaxParameterEchoChars = 200;
        private const int FooterReserveChars = 450;
        private const int MinFirstCandidateChars = 400;

        private sealed record Candidate(string Heading, IReadOnlyList<string> Notes, IReadOnlyList<string> Lines);

        /// <summary>
        /// Builds the exception for a failed result. Throws <see cref="InvalidOperationException"/> for
        /// <see cref="SnippetMatchOutcome.Matched"/> (a success has no error), like the removed legacy ContextErrorBuilder.
        /// </summary>
        /// <param name="result">The failed result from <see cref="SnippetResolver"/>.</param>
        /// <param name="sourceText">The text the lookup ran against; the source of every candidate line.</param>
        /// <param name="snippet">The snippet exactly as the caller sent it (echoed in a no-match message).</param>
        /// <param name="snippetLabel">The caller's parameter name for the snippet (for example oldContent or contextSnippet), used in the wording.</param>
        /// <param name="memberNameAt">Maps a character offset to its enclosing member name, to name each candidate's member; optional.</param>
        /// <param name="selectorsSupported">True when the calling tool accepts matchLine (and withinMember); changes the advice and adds matchLine to each candidate.</param>
        public static ToolException Build(
            SnippetMatchResult result, SourceText sourceText, string snippet, string snippetLabel,
            Func<int, string?>? memberNameAt = null, bool selectorsSupported = false)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(sourceText);
            ArgumentNullException.ThrowIfNull(snippet);
            ArgumentNullException.ThrowIfNull(snippetLabel);

            switch (result.Outcome)
            {
                case SnippetMatchOutcome.NoMatch:
                    return BuildNoMatch(result, snippet, snippetLabel);

                case SnippetMatchOutcome.MatchedLooserThanRequested:
                    return BuildLooser(result, snippetLabel);

                case SnippetMatchOutcome.Ambiguous:
                    return BuildAmbiguous(result, sourceText, snippetLabel, memberNameAt, selectorsSupported, stillAmbiguous: false);

                case SnippetMatchOutcome.StillAmbiguous:
                case SnippetMatchOutcome.TrueTie:
                    return BuildAmbiguous(result, sourceText, snippetLabel, memberNameAt, selectorsSupported, stillAmbiguous: true);

                case SnippetMatchOutcome.SelectorMismatch:
                    return BuildSelectorMismatch(result, sourceText, snippetLabel, memberNameAt, selectorsSupported);

                case SnippetMatchOutcome.AdjacentLineMismatch:
                    return BuildAdjacentLineMismatch(result, sourceText, snippetLabel, memberNameAt, selectorsSupported);

                case SnippetMatchOutcome.InvalidDisambiguator:
                    return BuildInvalidDisambiguator(result, selectorsSupported);

                case SnippetMatchOutcome.Matched:
                    throw new InvalidOperationException(
                        "SnippetMatchOutcome.Matched is a success outcome; there is no error to build for it.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Unhandled SnippetMatchOutcome.");
            }
        }

        // ---- NoMatch ----

        private static ToolNotFoundException BuildNoMatch(SnippetMatchResult result, string snippet, string label)
        {
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return new ToolNotFoundException(
                    $"{label} is empty or whitespace only, so there is nothing to look for and nothing was searched. " +
                    $"No candidates exist. Supply the text to find as {label}, copied verbatim from the file.");
            }

            var sb = new StringBuilder();
            sb.Append($"{label} was not found: it matches nothing at any whitespace tolerance probed.");
            sb.Append('\n').Append(DescribeProbe(result));
            sb.Append('\n').Append(DescribeEcho(snippet, label));

            if (result.ClosestWindows.Count == 0)
            {
                sb.Append('\n').Append(
                    $"No candidates: no window of the file reaches a similarity of {SnippetCandidateFinder.MinClosestWindowScore.ToString("0.00", CultureInfo.InvariantCulture)} " +
                    $"to your {label}, so there is no close text to show. The text does not appear to be in this file; " +
                    "re-read the file and check the file path before retrying.");
                return new ToolNotFoundException(sb.ToString());
            }

            sb.Append('\n').Append(
                $"Closest windows in the file, best first (the file's own text; copy the one you meant, verbatim, as your next {label}):");
            var candidates = result.ClosestWindows
                .Select((w, i) => new Candidate(
                    $"Window {i + 1} (starts at line {w.StartLine}, score {w.Score.ToString("0.00", CultureInfo.InvariantCulture)}):",
                    Array.Empty<string>(), w.ActualLines))
                .ToList();
            ListCandidates(sb, candidates, result.ClosestWindowTotalCount, "window(s)");
            return new ToolNotFoundException(sb.ToString());
        }

        // ---- MatchedLooserThanRequested ----

        private static ToolMatchedLooserTierException BuildLooser(SnippetMatchResult result, string label)
        {
            var tier = result.NearMatches.Count > 0 ? result.NearMatches[0].LowestTier : result.RequestedTolerance;
            var sb = new StringBuilder();
            sb.Append($"{label} matched only at a looser whitespace tolerance than this call accepts: it matches at {tier}, ");
            sb.Append($"but this call requires {result.RequestedTolerance}. The near-match was NOT used and nothing was applied.");
            sb.Append('\n').Append($"Resend {label} using the file's own text shown below, copied verbatim (do not retype it).");
            sb.Append('\n').Append(DescribeProbe(result));

            var total = Math.Max(result.NearMatchTotalCount, result.NearMatches.Count);
            var candidates = new List<Candidate>();
            for (var i = 0; i < result.NearMatches.Count; i++)
            {
                var near = result.NearMatches[i];
                var notes = new List<string>();
                if (near.Differences.Count == 0)
                {
                    notes.Add($"- no per-line whitespace difference could be isolated; the file's text below matches your {label} at {near.LowestTier}.");
                }
                else
                {
                    foreach (var difference in near.Differences.Take(MaxDifferencesListed))
                    {
                        notes.Add("- " + DescribeDifference(difference));
                    }

                    if (near.Differences.Count > MaxDifferencesListed)
                    {
                        notes.Add($"- ({near.Differences.Count - MaxDifferencesListed} more difference(s) not listed)");
                    }
                }

                notes.Add("File text to send, verbatim:");
                candidates.Add(new Candidate(
                    $"Near-match {i + 1} of {total} (starts at line {near.StartLine}, matches at {near.LowestTier}); differences from your {label}:",
                    notes, near.ActualLines));
            }

            ListCandidates(sb, candidates, total, "near-match(es)");
            return new ToolMatchedLooserTierException(sb.ToString());
        }

        private static string DescribeDifference(SnippetDifference d)
        {
            var where = d.FileLine > 0 ? $" (file line {d.FileLine})" : string.Empty;
            var prefix = $"line {d.Line}: ";
            switch (d.Kind)
            {
                case SnippetDifferenceKind.LeadingWhitespace:
                    return $"{prefix}file has {DescribeWhitespace(d.Got, "leading")}, snippet has {DescribeWhitespace(d.Expected, "leading")}{where}";
                case SnippetDifferenceKind.TrailingWhitespace:
                    return $"{prefix}file has {DescribeWhitespace(d.Got, "trailing")}, snippet has {DescribeWhitespace(d.Expected, "trailing")}{where}";
                case SnippetDifferenceKind.LineEnding:
                    return $"{prefix}the file ends this line with {d.Got}, the snippet has {d.Expected}{where}";
                case SnippetDifferenceKind.InteriorWhitespace:
                    return $"{prefix}whitespace inside the line differs: file has \"{Quote(d.Got, MaxDifferenceTextChars)}\", " +
                           $"snippet has \"{Quote(d.Expected, MaxDifferenceTextChars)}\"{where}";
                default:
                    return $"{prefix}{d.Kind}{where}";
            }
        }

        private static string DescribeWhitespace(string whitespace, string position)
        {
            if (whitespace.Length == 0)
            {
                return "none";
            }

            var spaces = whitespace.Count(c => c == ' ');
            var tabs = whitespace.Count(c => c == '\t');
            var other = whitespace.Length - spaces - tabs;
            var parts = new List<string>();
            if (spaces > 0)
            {
                parts.Add($"{spaces} {position} {(spaces == 1 ? "space" : "spaces")}");
            }

            if (tabs > 0)
            {
                parts.Add($"{tabs} {position} {(tabs == 1 ? "tab" : "tabs")}");
            }

            if (other > 0)
            {
                parts.Add($"{other} other {position} whitespace {(other == 1 ? "character" : "characters")}");
            }

            return string.Join(" and ", parts);
        }

        // ---- Ambiguous family ----

        private static ToolAmbiguousMatchException BuildAmbiguous(
            SnippetMatchResult result, SourceText sourceText, string label, Func<int, string?>? memberNameAt,
            bool selectorsSupported, bool stillAmbiguous)
        {
            var matches = result.Matches;
            var candidates = DescribeMatches(sourceText, matches, memberNameAt, selectorsSupported, out var anyMember, out var before, out var after);
            var sb = new StringBuilder();
            if (stillAmbiguous)
            {
                sb.Append($"{label} is still ambiguous: {matches.Count} matches remain after the selectors you supplied.");
                AppendTrail(sb, result);
                sb.Append('\n').Append("Narrow it further: ").Append(AmbiguityAdvice(label, selectorsSupported, anyMember));
            }
            else
            {
                var tier = matches.Count > 0 ? matches[0].Tier.ToString() : result.RequestedTolerance.ToString();
                sb.Append($"{label} is ambiguous: it matches {matches.Count} places in the file (at {tier}). Select one: ");
                sb.Append(AmbiguityAdvice(label, selectorsSupported, anyMember));
            }

            sb.Append('\n').Append("Candidates in file order (the file's own text, with the values that select each):");
            var listed = ListCandidates(sb, candidates, matches.Count, "candidate(s)");
            AppendTieNote(sb, label, selectorsSupported, before, after, listed);
            return new ToolAmbiguousMatchException(sb.ToString());
        }

        private static string AmbiguityAdvice(string label, bool selectorsSupported, bool anyMember)
        {
            if (!selectorsSupported)
            {
                return $"pass lineBefore and/or lineAfter copied verbatim from one candidate below, or widen {label} so it matches one place only.";
            }

            var member = anyMember ? " (and withinMember where listed)" : string.Empty;
            return $"pass matchLine{member} copied from one candidate below, optionally with lineBefore/lineAfter, or widen {label} so it matches one place only.";
        }

        private static ToolNotFoundException BuildSelectorMismatch(
            SnippetMatchResult result, SourceText sourceText, string label, Func<int, string?>? memberNameAt, bool selectorsSupported)
        {
            var matches = result.PreFilterMatches;
            var supplied = SuppliedSelectors(result, "matchLine", "withinMember");
            var candidates = DescribeMatches(sourceText, matches, memberNameAt, selectorsSupported, out _, out var before, out var after);
            var sb = new StringBuilder();
            sb.Append($"{label} was found ({matches.Count} match(es)), but not at the {supplied} you supplied; nothing was located.");
            AppendTrail(sb, result);
            sb.Append('\n').Append(
                $"Candidates in file order (the file's own text; pass the matchLine/withinMember shown for the one you meant, or widen {label}):");
            var listed = ListCandidates(sb, candidates, matches.Count, "candidate(s)");
            AppendTieNote(sb, label, selectorsSupported, before, after, listed);
            return new ToolNotFoundException(sb.ToString());
        }

        private static ToolNotFoundException BuildAdjacentLineMismatch(
            SnippetMatchResult result, SourceText sourceText, string label, Func<int, string?>? memberNameAt, bool selectorsSupported)
        {
            var matches = result.SelectorSurvivors.Count > 0 ? result.SelectorSurvivors : result.PreFilterMatches;
            var supplied = SuppliedSelectors(result, "lineBefore", "lineAfter");
            var candidates = DescribeMatches(sourceText, matches, memberNameAt, selectorsSupported, out _, out var before, out var after);
            var sb = new StringBuilder();
            sb.Append($"{label} was found ({matches.Count} match(es)), but not next to the {supplied} you supplied; nothing was located. ");
            sb.Append("lineBefore/lineAfter must be the single real line immediately before/after the match.");
            AppendTrail(sb, result);
            sb.Append('\n').Append(
                $"Candidates in file order (the file's own text; copy the lineBefore/lineAfter shown for the one you meant, or widen {label}):");
            var listed = ListCandidates(sb, candidates, matches.Count, "candidate(s)");
            AppendTieNote(sb, label, selectorsSupported, before, after, listed);
            return new ToolNotFoundException(sb.ToString());
        }

        private static string SuppliedSelectors(SnippetMatchResult result, params string[] names)
        {
            var supplied = result.FilterTrail.Select(s => s.Selector).Where(n => names.Contains(n, StringComparer.Ordinal)).ToList();
            return string.Join("/", supplied.Count > 0 ? supplied : names.ToList());
        }

        private static void AppendTrail(StringBuilder sb, SnippetMatchResult result)
        {
            if (result.FilterTrail.Count == 0)
            {
                return;
            }

            sb.Append('\n').Append(
                $"Selectors applied ({result.CandidatesBeforeSelectors} candidate(s) before selectors): " +
                string.Join(", ", result.FilterTrail.Select(s => $"{s.Selector} removed {s.Removed}")) + ".");
        }

        private static List<Candidate> DescribeMatches(
            SourceText sourceText, IReadOnlyList<SnippetMatch> matches, Func<int, string?>? memberNameAt, bool selectorsSupported,
            out bool anyWithinMember, out List<string> beforeValues, out List<string> afterValues)
        {
            string? MemberOf(SnippetMatch m) => memberNameAt?.Invoke(SnippetSelectorFilter.FirstNonWhitespacePosition(sourceText, m));

            // A member name only separates a match when no other candidate is in a member of that name.
            var members = matches.Select(MemberOf).ToList();
            var uniqueMembers = selectorsSupported && memberNameAt != null
                ? members.Where(n => !string.IsNullOrEmpty(n)).GroupBy(n => n!, StringComparer.Ordinal)
                    .Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            anyWithinMember = uniqueMembers.Count > 0;

            beforeValues = new List<string>();
            afterValues = new List<string>();
            var candidates = new List<Candidate>();
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                var firstLine = sourceText.Lines.GetLinePosition(match.Start).Line;
                var lastLine = sourceText.Lines.GetLinePosition(Math.Max(match.Start, match.Start + match.Length - 1)).Line;
                var lines = new List<string>();
                for (var l = firstLine; l <= lastLine; l++)
                {
                    lines.Add(sourceText.Lines[l].ToString());
                }

                var before = firstLine > 0 ? sourceText.Lines[firstLine - 1].ToString().Trim() : "(start of file)";
                var after = lastLine < sourceText.Lines.Count - 1 ? sourceText.Lines[lastLine + 1].ToString().Trim() : "(end of file)";
                beforeValues.Add(before);
                afterValues.Add(after);

                var member = members[i];
                var inMember = string.IsNullOrEmpty(member) ? string.Empty : $", in {member}";
                var span = firstLine == lastLine ? $"line {firstLine + 1}" : $"lines {firstLine + 1}-{lastLine + 1}";
                var selectors = new StringBuilder();
                if (selectorsSupported)
                {
                    var anchorLine = sourceText.Lines.GetLinePosition(SnippetSelectorFilter.FirstNonWhitespacePosition(sourceText, match)).Line + 1;
                    if (!string.IsNullOrEmpty(member) && uniqueMembers.Contains(member))
                    {
                        selectors.Append($"withinMember=\"{member}\" ");
                    }

                    selectors.Append($"matchLine={anchorLine} ");
                }

                selectors.Append($"lineBefore=\"{before}\" lineAfter=\"{after}\"");
                candidates.Add(new Candidate(
                    $"Candidate {i + 1} of {matches.Count} ({span}{inMember}):", new[] { selectors.ToString() }, lines));
            }

            return candidates;
        }

        private static void AppendTieNote(
            StringBuilder sb, string label, bool selectorsSupported, List<string> before, List<string> after, int listed)
        {
            var groups = Enumerable.Range(0, Math.Min(listed, before.Count))
                .GroupBy(i => (before[i], after[i])).Where(g => g.Count() > 1);
            foreach (var group in groups)
            {
                var numbers = string.Join(" and ", group.Select(i => i + 1));
                sb.Append('\n').Append(
                    $"Candidates {numbers} have identical surrounding lines, so lineBefore/lineAfter cannot tell them apart. " +
                    (selectorsSupported
                        ? $"Use matchLine (and withinMember where listed), or widen {label}."
                        : $"Widen {label} with distinguishing text, or use a different tool."));
            }
        }

        // ---- InvalidDisambiguator ----

        private static ToolNotFoundException BuildInvalidDisambiguator(SnippetMatchResult result, bool selectorsSupported)
        {
            var name = result.InvalidParameterName ?? "selector";
            var value = Quote(result.InvalidParameterValue ?? string.Empty, MaxParameterEchoChars);
            switch (name)
            {
                case "lineBefore":
                case "lineAfter":
                    var direction = name == "lineAfter" ? "after" : "before";
                    return new ToolNotFoundException(
                        $"Invalid argument: {name} must be a single line, but the supplied value spans multiple lines: \"{value}\". " +
                        $"Pass only the one real source line immediately {direction} the match (the nearest distinguishing line, not a multi-line block).");

                case "withinMember":
                    var alternative = selectorsSupported ? "matchLine, lineBefore or lineAfter" : "lineBefore or lineAfter";
                    return new ToolNotFoundException(
                        $"Invalid argument: withinMember (\"{value}\") cannot be applied here because member names cannot be resolved for this lookup. " +
                        $"Remove withinMember and narrow with {alternative} instead.");

                default:
                    return new ToolNotFoundException(
                        $"Invalid argument: {name} (\"{value}\") is not a usable selector value for this lookup. Remove it or correct it.");
            }
        }

        // ---- shared rendering ----

        private static string DescribeProbe(SnippetMatchResult result)
        {
            var tiers = result.TiersProbed.Count == 0 ? "not recorded" : string.Join(", ", result.TiersProbed);
            var caseMode = result.RequestedCaseMode == SnippetCaseMode.CaseInsensitive ? "case-insensitive" : "case-sensitive";
            var text = $"Probed tiers (strictest first): {tiers}. Requested tolerance: {result.RequestedTolerance}. Case mode: {caseMode}.";
            if (result.LinesExamined > 0)
            {
                text += $" Examined {result.LinesExamined} file line(s) and scored {result.WindowsExamined} window(s).";
            }

            return text;
        }

        // The snippet as received, with whitespace made visible: each line between [ and ], escapes for tab, CR, LF,
        // backslash and non-ASCII characters, and the line break after each line. Capped in lines and in characters per line.
        private static string DescribeEcho(string snippet, string label)
        {
            var parts = SplitForEcho(snippet);
            var sb = new StringBuilder();
            sb.Append($"{label} as received ({parts.Count} line(s), {snippet.Length} char(s)). Each line is shown between [ and ]; ");
            sb.Append("escapes: \\t tab, \\r CR, \\n LF, \\\\ backslash, \\uXXXX other non-ASCII; spaces are literal:");
            for (var i = 0; i < parts.Count && i < MaxEchoLines; i++)
            {
                var (text, ending) = parts[i];
                var shown = text.Length > MaxEchoLineChars ? text.Substring(0, MaxEchoLineChars) : text;
                var more = text.Length > MaxEchoLineChars ? $"...(+{text.Length - MaxEchoLineChars} chars)" : string.Empty;
                var endingText = ending.Length == 0 ? "(end)" : Visible(ending);
                sb.Append('\n').Append($"  {i + 1}: [{Visible(shown)}{more}] {endingText}");
            }

            if (parts.Count > MaxEchoLines)
            {
                sb.Append('\n').Append($"  ... {parts.Count - MaxEchoLines} more line(s) not shown");
            }

            return sb.ToString();
        }

        private static List<(string Text, string Ending)> SplitForEcho(string text)
        {
            var parts = new List<(string, string)>();
            var start = 0;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\r' || c == '\n')
                {
                    var length = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                    parts.Add((text.Substring(start, i - start), text.Substring(i, length)));
                    i += length;
                    start = i;
                }
                else
                {
                    i++;
                }
            }

            if (start < text.Length)
            {
                parts.Add((text.Substring(start), string.Empty));
            }

            return parts;
        }

        private static string Quote(string text, int maxChars)
        {
            var shown = text.Length > maxChars ? text.Substring(0, maxChars) : text;
            var more = text.Length > maxChars ? $"...(+{text.Length - maxChars} chars)" : string.Empty;
            return Visible(shown) + more;
        }

        private static string Visible(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    default:
                        if (c < 32 || c > 126)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Appends the candidates in their given order (already ranked or in file order) while they fit the message budget,
        /// then a sentence naming how many were omitted. The first candidate is always shown, shortened only if it alone
        /// exceeds the budget. Returns how many candidates were listed.
        /// </summary>
        private static int ListCandidates(StringBuilder sb, IReadOnlyList<Candidate> candidates, int totalCount, string noun)
        {
            var budget = Math.Max(0, MaxMessageChars - sb.Length - FooterReserveChars);
            var listed = 0;
            foreach (var candidate in candidates)
            {
                var full = RenderCandidate(candidate, int.MaxValue);
                if (full.Length + 1 <= budget)
                {
                    sb.Append('\n').Append(full);
                    budget -= full.Length + 1;
                    listed++;
                    continue;
                }

                if (listed == 0)
                {
                    sb.Append('\n').Append(RenderCandidate(candidate, Math.Max(budget, MinFirstCandidateChars)));
                    listed++;
                }

                break;
            }

            var omitted = Math.Max(totalCount, candidates.Count) - listed;
            if (omitted > 0)
            {
                sb.Append('\n').Append($"({omitted} more {noun} omitted to keep this message short.)");
            }

            return listed;
        }

        private static string RenderCandidate(Candidate candidate, int maxChars)
        {
            var sb = new StringBuilder();
            sb.Append(candidate.Heading);
            foreach (var note in candidate.Notes)
            {
                sb.Append("\n  ").Append(note);
            }

            var fence = FenceFor(candidate.Lines);
            sb.Append('\n').Append(fence);
            var remaining = maxChars == int.MaxValue ? int.MaxValue : maxChars - sb.Length - fence.Length - 120;
            var shown = 0;
            foreach (var line in candidate.Lines)
            {
                if (remaining != int.MaxValue && line.Length + 1 > remaining)
                {
                    if (shown == 0)
                    {
                        sb.Append('\n').Append(line.Substring(0, Math.Min(line.Length, Math.Max(remaining, 40)))).Append(" (line truncated)");
                        shown++;
                    }

                    break;
                }

                sb.Append('\n').Append(line);
                if (remaining != int.MaxValue)
                {
                    remaining -= line.Length + 1;
                }

                shown++;
            }

            sb.Append('\n').Append(fence);
            if (shown < candidate.Lines.Count)
            {
                sb.Append('\n').Append($"({candidate.Lines.Count - shown} more line(s) of this candidate not shown; copy the rest from ReadFile)");
            }

            return sb.ToString();
        }

        // A fence longer than any backtick run in the lines, so a candidate that itself contains a fence cannot close it early.
        private static string FenceFor(IReadOnlyList<string> lines)
        {
            var fence = "```";
            while (lines.Any(l => l.Contains(fence, StringComparison.Ordinal)))
            {
                fence += "`";
            }

            return fence;
        }
    }
}
