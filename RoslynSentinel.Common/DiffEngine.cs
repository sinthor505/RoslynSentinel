using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Common;

public class DiffEngine
{
    private static readonly string[] separatorArray = new[] { "\r\n", "\r", "\n" };
    private readonly ILogger<DiffEngine> _logger;

    public DiffEngine() : this(NullLogger<DiffEngine>.Instance)
    {
    }

    public DiffEngine(ILogger<DiffEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// How far (in lines, either direction) a hunk's declared line number may drift from its
    /// actual position before <see cref="ApplyDiff"/> gives up re-anchoring it. Line numbers in a
    /// caller-authored diff routinely go stale after any earlier edit to the same file — the
    /// window trades a bounded amount of false-positive risk for tolerating that drift instead of
    /// failing (or silently corrupting the file) on every offset mismatch.
    /// </summary>
    private const int HunkReanchorWindow = 60;

    /// <summary>
    /// Applies a standard Unified Diff to a SourceText object and returns the updated text.
    /// Supports multiple hunks. Each hunk's declared line number is treated as a starting guess,
    /// not ground truth: if the hunk's own content (its first context/removal line) isn't found
    /// there, this searches a window around the declared position and re-anchors to the real
    /// match. This is what makes the tool tolerant of stale line numbers from an earlier edit to
    /// the same file — the single largest cause of diff-apply failures in practice. If no anchor
    /// can be found within the window, this throws rather than guessing and silently corrupting
    /// unrelated lines.
    /// </summary>
    /// <remarks>
    /// Every call is also run through <see cref="DiffHunkAnalyzer"/>, logged via the constructor's
    /// <see cref="ILogger{TCategoryName}"/>: as a warning on success if the analyzer found
    /// something worth flagging (e.g. a header whose declared counts don't match its actual body,
    /// even though the apply itself tolerated it structurally), or always on a
    /// <see cref="DiffApplyException"/> — so a failure comes with a ready-made per-hunk breakdown
    /// instead of requiring the kind of manual line-by-line archaeology that root-caused the
    /// original bug this analyzer exists to catch automatically (see docs/current/blockers).
    /// </remarks>
    public SourceText ApplyDiff(SourceText sourceText, string unifiedDiff)
    {
        try
        {
            var result = ApplyDiffCore(sourceText, unifiedDiff);
            var report = DiffHunkAnalyzer.Analyze(unifiedDiff);
            if (report.HasFindings)
            {
                _logger.LogWarning("ApplyDiff succeeded but its diff has findings: {Report}", report.Describe());
            }
            return result;
        }
        catch (DiffApplyException ex)
        {
            _logger.LogWarning(ex, "ApplyDiff failed: {Report}", DiffHunkAnalyzer.Analyze(unifiedDiff).Describe());
            throw;
        }
    }

    private SourceText ApplyDiffCore(SourceText sourceText, string unifiedDiff)
    {
        var lines = sourceText.Lines.Select(l => l.ToString()).ToList();

        // Preserve each original line's own line-break characters ("\r\n", "\n", "\r", or "" for
        // a file with no trailing newline) instead of forcing one convention onto the whole file.
        // TextLine excludes the break from ToString(), so it's read separately here via the span
        // between End and EndIncludingLineBreak. A file with mixed endings (routine after a partial
        // manual edit, or a merge) previously got every line forced to whichever ending merely
        // *appeared* anywhere in the file — see docs/TODO.md's "ApplyDiff reflows far more of the
        // file" entry, root-caused to this. Lines inserted by this diff get the file's dominant
        // ending unless they land as the new last line, matching what an original last line with
        // no ending of its own would get (see the reassembly loop's promotion logic below).
        var endings = sourceText.Lines
            .Select(l => sourceText.GetSubText(TextSpan.FromBounds(l.End, l.EndIncludingLineBreak)).ToString())
            .ToList();
        var dominantEnding = endings
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? Environment.NewLine;

        var diffLines = unifiedDiff.Split(separatorArray, StringSplitOptions.None);

        int offset = 0; // Track how much the file has grown/shrunk
        var hunkHeaderRegex = new Regex(@"^@@\s+\-(\d+),?(\d*)\s+\+(\d+),?(\d*)\s+@@");

        for (int i = 0; i < diffLines.Length; i++)
        {
            var line = diffLines[i];
            var match = hunkHeaderRegex.Match(line);

            if (match.Success)
            {
                int oldStart = int.Parse(match.Groups[1].Value) - 1;
                int declaredLine = oldStart + offset;

                var hunkBody = ReadHunkBody(diffLines, i + 1);
                int currentLine = ReanchorHunk(lines, hunkBody, declaredLine, match.Value);

                // Process hunk lines
                i++;
                while (i < diffLines.Length && !hunkHeaderRegex.IsMatch(diffLines[i]))
                {
                    var diffLine = diffLines[i];
                    // A blank line right before the next hunk header, or right at the end of the
                    // diff text, isn't a real blank context line from the file — it's a split
                    // artifact from the diff's own trailing newline (or the blank separator line
                    // conventionally placed between hunks). See ReadHunkBody's comment.
                    var isTrailingArtifact = i == diffLines.Length - 1
                        || (i + 1 < diffLines.Length && hunkHeaderRegex.IsMatch(diffLines[i + 1]));
                    if (string.IsNullOrEmpty(diffLine) && isTrailingArtifact)
                    {
                        break;
                    }

                    if (diffLine.StartsWith("+"))
                    {
                        // Ending is left empty here, not set to dominantEnding directly: the
                        // reassembly loop below already promotes an empty ending to dominant
                        // unless the line lands last, so this line naturally gets no trailing
                        // separator if it ends up as the new last line of a file that originally
                        // had none (e.g. inserting after the former last line).
                        lines.Insert(currentLine, diffLine.Substring(1));
                        endings.Insert(currentLine, "");
                        currentLine++;
                        offset++;
                    }
                    else if (diffLine.StartsWith("-"))
                    {
                        if (currentLine >= lines.Count)
                        {
                            throw new DiffApplyException($"Line {currentLine + 1} out of bounds.");
                        }
                        var expected = diffLine.Substring(1).Trim();
                        var actual = lines[currentLine].Trim();
                        if (expected != actual)
                        {
                            throw new DiffApplyException(
                                $"hunk '{match.Value}' expected to remove \"{expected}\" " +
                                $"at line {currentLine + 1}, but found \"{actual}\". The hunk's line numbers may be " +
                                "stale relative to hunks applied earlier in this same diff — regenerate the diff " +
                                "against the file's current content, or use a whole-member/whole-file replacement " +
                                "tool instead.");
                        }
                        lines.RemoveAt(currentLine);
                        endings.RemoveAt(currentLine);
                        offset--;
                    }
                    else if (diffLine.StartsWith(" ") || diffLine.Length == 0)
                    {
                        // Context line - validate it matches. A zero-length line (no leading space
                        // marker at all) is tolerated as an implicit blank context line — see
                        // IsContextOrRemovalLine's doc comment for why some diffs omit the marker
                        // on otherwise-blank lines, and why silently dropping it here would
                        // desynchronize currentLine from the anchor ReanchorHunk already matched.
                        if (currentLine >= lines.Count)
                        {
                            throw new DiffApplyException($"Context line {currentLine + 1} out of bounds.");
                        }

                        var expected = diffLine.Length == 0 ? "" : diffLine.Substring(1).Trim();
                        var actual = lines[currentLine].Trim();
                        if (expected != actual)
                        {
                            throw new DiffApplyException(
                                $"hunk '{match.Value}' expected context \"{expected}\" " +
                                $"at line {currentLine + 1}, but found \"{actual}\". The hunk's line numbers may be " +
                                "stale relative to hunks applied earlier in this same diff — regenerate the diff " +
                                "against the file's current content, or use a whole-member/whole-file replacement " +
                                "tool instead.");
                        }
                        currentLine++;
                    }
                    i++;
                }
                i--; // Back up so the outer loop can see the next hunk header or end
            }
        }

        // Reassemble using each surviving/inserted line's own ending rather than one convention for
        // the whole file. An empty ending is only valid on the actual last line (a file with no
        // trailing newline) — if an earlier line ended up with one (e.g. a line was inserted after
        // what used to be the last line), it needs a real separator or the two lines would run
        // together with no break at all.
        var sb = new StringBuilder();
        for (int j = 0; j < lines.Count; j++)
        {
            sb.Append(lines[j]);
            var ending = endings[j];
            if (j < lines.Count - 1 && ending.Length == 0)
            {
                ending = dominantEnding;
            }
            sb.Append(ending);
        }
        return SourceText.From(sb.ToString(), sourceText.Encoding);
    }

    /// <summary>
    /// Collects a hunk's raw lines (context/+/-) up to the next hunk header or diff end. The
    /// hunk header's own declared old/new counts are not used as the boundary — a model-generated
    /// diff routinely gets them wrong even when its line content is otherwise fine (see
    /// docs/current/blockers for a real transcript whose header claimed 7 old-side lines when the
    /// body only had 3) — so this scans structurally instead: everything up to the next "@@" or
    /// end of input belongs to this hunk.
    /// </summary>
    private static List<string> ReadHunkBody(string[] diffLines, int start)
    {
        var body = new List<string>();
        var hunkHeaderRegex = new Regex(@"^@@\s+\-(\d+),?(\d*)\s+\+(\d+),?(\d*)\s+@@");
        for (int i = start; i < diffLines.Length && !hunkHeaderRegex.IsMatch(diffLines[i]); i++)
        {
            // A blank line right before the next hunk header, or right at the end of the diff
            // text, isn't a real blank context line from the file — it's a split artifact from
            // the diff's own trailing newline (or the blank separator line conventionally placed
            // between hunks). Including it in the body would make ReanchorHunk require a blank
            // line the file doesn't actually have there, defeating an otherwise-correct match at
            // every position in the search window.
            var isTrailingArtifact = i == diffLines.Length - 1
                || (i + 1 < diffLines.Length && hunkHeaderRegex.IsMatch(diffLines[i + 1]));
            if (string.IsNullOrEmpty(diffLines[i]) && isTrailingArtifact)
            {
                break;
            }
            body.Add(diffLines[i]);
        }
        return body;
    }

    /// <summary>
    /// True for a hunk-body line that represents a context/removal line the file must already
    /// contain: explicitly-marked (" "/"-") lines, plus a bare blank line with no marker at all —
    /// tolerated as an implicit blank context line, since diff producers routinely emit an
    /// unprefixed empty line for a blank line in the file rather than a single trailing space.
    /// Without this, such a line is silently dropped from the anchor, desynchronizing the anchor
    /// list from the declared line number by exactly one line and causing the re-anchor search to
    /// look in the wrong place — a real defect this class had (see docs/TODO.md's entry on
    /// ApplyDiff's misleading anchor-failure message, root-caused to this).
    /// </summary>
    private static bool IsContextOrRemovalLine(string line) => line.StartsWith(" ") || line.StartsWith("-") || line.Length == 0;

    /// <summary>
    /// Finds where a hunk actually belongs by matching its leading run of context/removal lines
    /// (the only lines guaranteed to already exist in <paramref name="lines"/>) against the file,
    /// searching outward from <paramref name="declaredLine"/> within <see cref="HunkReanchorWindow"/>.
    /// Returns <paramref name="declaredLine"/> unchanged if it already matches (the common case) or
    /// if the hunk has no context/removal lines to anchor on (a pure insertion at file start/end).
    /// Throws if the declared position doesn't match and no nearby match can be found either.
    /// </summary>
    private static int ReanchorHunk(List<string> lines, List<string> hunkBody, int declaredLine, string hunkHeader)
    {
        var anchorLines = hunkBody
            .Where(IsContextOrRemovalLine)
            .Select(l => l.Length == 0 ? "" : l.Substring(1).Trim())
            .ToList();

        if (anchorLines.Count == 0)
        {
            return declaredLine; // Nothing to anchor on (pure insertion) — trust the declared offset.
        }

        // Track the best (most leading anchor lines matched) candidate seen across the whole
        // search, not just whether any position fully matched — this is what makes the failure
        // message below point at the actual point of divergence instead of always blaming
        // anchorLines[0]. A hunk whose first few anchor lines are correct but whose LATER context
        // (e.g. stale content copied from an earlier read, describing what's actually further
        // down the file) doesn't match anywhere is a real, observed failure mode — see
        // docs/current/blockers — and "First expected line: anchorLines[0]" is actively misleading
        // for it, since line 0 matched fine at the best candidate position.
        var best = (Position: declaredLine, MatchedCount: 0);

        void ConsiderCandidate(int position)
        {
            var matchedCount = MatchLeadingCount(lines, anchorLines, position);
            if (matchedCount > best.MatchedCount)
            {
                best = (position, matchedCount);
            }
        }

        ConsiderCandidate(declaredLine);
        if (best.MatchedCount == anchorLines.Count)
        {
            return declaredLine;
        }

        for (int delta = 1; delta <= HunkReanchorWindow; delta++)
        {
            ConsiderCandidate(declaredLine - delta);
            if (best.MatchedCount == anchorLines.Count)
            {
                return declaredLine - delta;
            }
            ConsiderCandidate(declaredLine + delta);
            if (best.MatchedCount == anchorLines.Count)
            {
                return declaredLine + delta;
            }
        }

        // Report the line that actually diverged at the closest-matching position found, not
        // just anchorLines[0] — e.g. "matched the first 3 anchor line(s) at line 269, but then
        // expected ... at line 272". If nothing matched at all (best.MatchedCount == 0), this
        // degrades to the original "first expected line" framing, which is still the right
        // message for that case.
        var mismatchLineNumber = best.Position + best.MatchedCount + 1;
        var mismatchDetail = best.MatchedCount == 0
            ? $"First expected line: \"{anchorLines[0]}\"."
            : $"Matched the first {best.MatchedCount} anchor line(s) starting at line {best.Position + 1}, " +
              $"but then expected \"{anchorLines[best.MatchedCount]}\" at line {mismatchLineNumber} — found " +
              $"\"{(best.Position + best.MatchedCount < lines.Count ? lines[best.Position + best.MatchedCount].Trim() : "<end of file>")}\" " +
              "instead. This usually means a context/removal line partway through the hunk is stale — e.g. copied " +
              "from a different part of the file during an earlier read — rather than the hunk's start position " +
              "being wrong.";

        throw new DiffApplyException(
            $"hunk '{hunkHeader}' declares line {declaredLine + 1}, but its content " +
            $"wasn't found there or within {HunkReanchorWindow} lines in either direction. {mismatchDetail} " +
            "Regenerate the diff against the file's current content, or use a " +
            "whole-member/whole-file replacement tool instead.");
    }

    /// <summary>
    /// Returns how many leading <paramref name="anchorLines"/> match the file starting at
    /// <paramref name="start"/> before the first divergence (or the file/list boundary) —
    /// <c>anchorLines.Count</c> means a full match. Used both to decide whether a candidate
    /// position is the real anchor (full match) and, when no candidate fully matches, to find the
    /// closest near-miss for a more precise error message.
    /// </summary>
    private static int MatchLeadingCount(List<string> lines, List<string> anchorLines, int start)
    {
        if (start < 0)
        {
            return 0;
        }

        for (int j = 0; j < anchorLines.Count; j++)
        {
            if (start + j >= lines.Count || lines[start + j].Trim() != anchorLines[j])
            {
                return j;
            }
        }
        return anchorLines.Count;
    }

    /// <summary>
    /// Generates a simple line-based Unified Diff between two versions of text.
    /// </summary>
    public static string CreateDiff(string oldText, string newText)
    {
        var oldLines = oldText.Split(separatorArray, StringSplitOptions.None);
        var newLines = newText.Split(separatorArray, StringSplitOptions.None);

        var sb = new StringBuilder();
        sb.AppendLine("--- Original");
        sb.AppendLine("+++ Modified");

        // Simple line-by-line diff (not a full LCS algorithm, but sufficient for previewing changes)
        int oldIdx = 0;
        int newIdx = 0;

        while (oldIdx < oldLines.Length || newIdx < newLines.Length)
        {
            if (oldIdx < oldLines.Length && newIdx < newLines.Length && oldLines[oldIdx] == newLines[newIdx])
            {
                // Lines are identical
                oldIdx++;
                newIdx++;
            }
            else
            {
                // Lines differ - find the next match to determine if it's an insertion or deletion
                sb.AppendLine($"@@ -{oldIdx + 1} +{newIdx + 1} @@");

                // For simplicity in this tool, we show the removal then the addition
                if (oldIdx < oldLines.Length)
                {
                    sb.AppendLine($"-{oldLines[oldIdx]}");
                    oldIdx++;
                }
                if (newIdx < newLines.Length)
                {
                    sb.AppendLine($"+{newLines[newIdx]}");
                    newIdx++;
                }
            }
        }

        return sb.ToString();
    }
}
