using System.Text;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The commit message <c>git commit</c> stores, produced in process (step 5c-3 of proposal_libgit2sharp_in_process_git.md; no caller yet, step 5c-4 wires it into the commit tool).
/// It reproduces exactly the argument set the Git tool's commit passes today: <c>git [-c trailer.Tool-Call-Id.ifexists=replace -c trailer.Tool-Agent.ifexists=replace] commit -m
/// &lt;message&gt; [--trailer "Tool-Call-Id: &lt;id&gt;" [--trailer "Tool-Agent: &lt;agent&gt;"]]</c> (one <c>-m</c>, no editor, no <c>--cleanup</c>, no <c>--signoff</c>).
/// <para>
/// What git does with that, in order (verified against git 2.55 by <c>GitCommitMessageParityTests</c>, which commits with the real git and compares the stored bytes):
/// (1) <c>-m</c> text gets a trailing newline when it lacks one; (2) with trailers, <c>git interpret-trailers --in-place --no-divider</c> runs on that raw text (trailer.c: the end of the
/// log message skips trailing comment and bare-LF lines, the trailer block is the last paragraph after the title paragraph that is all trailers, or has a recognised prefix
/// (<c>Signed-off-by: </c>, <c>(cherry picked from commit </c>, or a token matching a configured name by prefix) and at least 25 percent trailer lines; an existing trailer line is re-rendered as
/// <c>Token: value</c>; a new trailer removes the last existing trailer whose token matches by case-insensitive prefix and goes at the end of the block, after a blank line when the
/// text does not already end in one); (3) the cleanup mode that applies with no editor, <c>whitespace</c> (<c>strbuf_stripspace</c> without a comment prefix: trailing whitespace stripped from each
/// line, runs of blank lines collapsed to one, leading and trailing blank lines dropped, one final newline; <c>#</c> lines are kept). git's whitespace is exactly space, tab, LF and CR.
/// </para>
/// <para>
/// Declines (<see cref="CommitMessageOutcome.Declined"/>, nothing produced; the CLI answers): a NUL byte or lone surrogate in the message; a message that is empty after cleanup or holds only blank and
/// <c>Signed-off-by: </c> lines (git aborts with its own text); with trailers, a message holding a <c>Conflicts:</c> line or a scissors line (git's <c>ignored_log_message_bytes</c> treats those specially and this port does not); a blank or
/// multi-line trailer value; and, from <see cref="FormatForRepository"/>, any <c>commit.cleanup</c>, <c>core.commentChar</c>, <c>core.commentString</c> or <c>i18n.commitEncoding</c> entry, and with trailers any <c>trailer.*</c> config entry (<c>trailer.where</c>, <c>trailer.separators</c>, <c>trailer.&lt;token&gt;.*</c> change what
/// interpret-trailers does) plus the reader's own environment and repository declines. <c>commit.template</c> needs no decline: git ignores it for <c>-m</c> (pinned by a test).
/// </para>
/// </summary>
internal static class GitCommitMessage
{
    private const char CommentChar = '#';

    // git_generated_prefixes of trailer.c: lines starting so are trailers and make the block "recognised".
    private static readonly string[] GitGeneratedPrefixes = ["Signed-off-by: ", "(cherry picked from commit "];

    // The trailer.<name>.ifexists=replace config the commit passes: git treats a configured name as recognising a trailer line (and for matching it compares a PREFIX, see TokenMatchesName).
    private static readonly string[] ConfiguredTrailerNames = [GitCommitTrailers.ToolCallIdKey, GitCommitTrailers.ToolAgentKey];

    private const string SignOffHeader = "Signed-off-by: ";

    private const string ScissorsLine = "# ------------------------ >8 ------------------------";

    /// <summary>
    /// The final message for <c>git commit -m <paramref name="message"/></c> with the commit-trailer arguments for <paramref name="toolCallId"/> (null: no trailers, cleanup only) and,
    /// when it is not blank, <paramref name="toolAgent"/>. Pure text: it reads no configuration (see <see cref="FormatForRepository"/>).
    /// </summary>
    internal static CommitMessageResult Format(string message, string? toolCallId = null, string? toolAgent = null)
    {
        if (message.Contains('\0'))
        {
            return CommitMessageResult.Declined("The message contains a NUL byte; git's text handling stops there.");
        }

        if (HasLoneSurrogate(message))
        {
            return CommitMessageResult.Declined("The message holds a lone surrogate, which has no UTF-8 form for git to receive.");
        }

        // git cleans the -m text up BEFORE interpret-trailers sees it (found by the fuzz: leading blank lines never make the first real paragraph a trailer candidate), and
        // again after; the cleanup is idempotent, so for a message without trailers the second pass changes nothing.
        var text = StripSpace(message);
        if (toolCallId is not null)
        {
            var callIdValue = TrailerValue(toolCallId);
            var agentValue = string.IsNullOrWhiteSpace(toolAgent) ? null : TrailerValue(toolAgent);
            if (callIdValue is null || (!string.IsNullOrWhiteSpace(toolAgent) && agentValue is null))
            {
                return CommitMessageResult.Declined("A trailer value is blank or spans several lines.");
            }

            if (HasSpecialIgnoredLines(text))
            {
                return CommitMessageResult.Declined("The message has a 'Conflicts:' line or a scissors line, which git's trailer handling treats specially.");
            }

            var arguments = new List<(string Token, string Value)> { (GitCommitTrailers.ToolCallIdKey, callIdValue) };
            if (agentValue is not null)
            {
                arguments.Add((GitCommitTrailers.ToolAgentKey, agentValue));
            }

            text = InterpretTrailers(text, arguments);
        }

        var cleaned = StripSpace(text);
        return IsEmptyForGit(cleaned)
            ? CommitMessageResult.Declined("The message is empty after cleanup (blank lines and Signed-off-by lines only); git aborts the commit in its own words.")
            : CommitMessageResult.Formatted(cleaned);
    }

    // commit.c message_is_empty (no template, no --allow-empty-message): git aborts when every line is blank or starts with "Signed-off-by: ". Found by the fuzz.
    private static bool IsEmptyForGit(string cleaned)
    {
        var position = 0;
        while (position < cleaned.Length)
        {
            var next = NextLine(cleaned, position);
            if (!IsBlankLine(cleaned, position) && string.CompareOrdinal(cleaned, position, SignOffHeader, 0, SignOffHeader.Length) != 0)
            {
                return false;
            }

            position = next;
        }

        return true;
    }

    /// <summary>
    /// <see cref="Format"/> for the repository containing <paramref name="workingDirectory"/>, after declining the configuration that changes what git does with a message (see the class comment).
    /// Never throws for a decline; native load failures (<see cref="DllNotFoundException"/>, <see cref="TypeInitializationException"/>) propagate as the other libgit2 entry points do.
    /// </summary>
    internal static CommitMessageResult FormatForRepository(string workingDirectory, string message, string? toolCallId = null, string? toolAgent = null)
    {
        try
        {
            if (Libgit2GitReader.CommitMessageConfigDecline(workingDirectory, trailers: toolCallId is not null) is { } decline)
            {
                return CommitMessageResult.Declined(decline);
            }
        }
        catch (NotSupportedException ex) when (ex is not PlatformNotSupportedException)
        {
            return CommitMessageResult.Declined(ex.Message);
        }
        catch (LibGit2SharpException ex)
        {
            return CommitMessageResult.Declined($"libgit2 could not read the configuration ({ex.GetType().Name}: {ex.Message}); git decides.");
        }

        return Format(message, toolCallId, toolAgent);
    }

    // ----- interpret-trailers --in-place --no-divider (trailer.c) -----

    private static string InterpretTrailers(string buf, List<(string Token, string Value)> arguments)
    {
        var endOfLog = buf.Length - IgnoredLogMessageBytes(buf);
        var blockStart = FindTrailerBlockStart(buf, endOfLog);
        var items = ParseBlock(buf, blockStart, endOfLog);

        // Every argument has ifexists=replace (configured above): the nearest existing trailer of the same token, searched from the end, is dropped; the new trailer always goes last.
        foreach (var (token, value) in arguments)
        {
            var found = -1;
            for (var i = items.Count - 1; i >= 0; i--)
            {
                if (items[i].Token is { } existing && SameToken(existing, token))
                {
                    found = i;
                    break;
                }
            }

            // git (ifexists=replace, where=after) removes that trailer and puts the new one after the last item: a replaced trailer moves to the end of the block (parity-tested).
            if (found >= 0)
            {
                items.RemoveAt(found);
            }

            items.Add(new TrailerItem(token, value));
        }

        var output = new StringBuilder();
        output.Append(buf, 0, blockStart);
        if (!EndsWithBlankLine(buf, blockStart))
        {
            output.Append('\n');
        }

        foreach (var item in items)
        {
            if (item.Token is { } token)
            {
                output.Append(token).Append(": ").Append(item.Value).Append('\n');
            }
            else
            {
                output.Append(item.Value).Append('\n');
            }
        }

        output.Append(buf, endOfLog, buf.Length - endOfLog);
        return output.ToString();
    }

    // A parsed trailer line: Token null means a line kept as it was (Value is then the whole line without its LF).
    private readonly record struct TrailerItem(string? Token, string Value);

    private static List<TrailerItem> ParseBlock(string buf, int blockStart, int endOfLog)
    {
        // Physical lines, a line starting with git's whitespace continuing the previous one.
        var entries = new List<string>();
        var position = blockStart;
        while (position < endOfLog)
        {
            var next = NextLine(buf, position);
            var line = buf[position..next];
            if (line[0] == CommentChar)
            {
                // Dropped before the continuation rule applies (found by the fuzz: an indented line after a comment line is its own entry, not a continuation of the comment).
                position = next;
                continue;
            }

            if (entries.Count > 0 && IsGitSpace(line[0]))
            {
                entries[^1] += line;
            }
            else
            {
                entries.Add(line);
            }

            position = next;
        }

        var items = new List<TrailerItem>();
        foreach (var entry in entries)
        {
            var separator = FindSeparator(entry, 0);
            if (separator >= 1)
            {
                items.Add(new TrailerItem(GitTrim(entry[..separator]), GitTrim(entry[(separator + 1)..])));
            }
            else
            {
                items.Add(new TrailerItem(null, entry.EndsWith('\n') ? entry[..^1] : entry));
            }
        }

        return items;
    }

    // commit.c ignored_log_message_bytes, ported literally including its use of 0 as "no comment run open" (a run starting at offset 0 is therefore not recorded). The scissors
    // cutoff and the old "Conflicts:" block are not ported: Format declines messages that have them.
    private static int IgnoredLogMessageBytes(string buf)
    {
        var boc = 0;
        var bol = 0;
        while (bol < buf.Length)
        {
            var next = NextLine(buf, bol);
            if (buf[bol] == CommentChar || buf[bol] == '\n')
            {
                if (boc == 0)
                {
                    boc = bol;
                }
            }
            else if (boc != 0)
            {
                boc = 0;
            }

            bol = next;
        }

        return boc != 0 ? buf.Length - boc : 0;
    }

    // trailer.c find_trailer_block_start: the offset the trailer block starts at, or len when there is none.
    private static int FindTrailerBlockStart(string buf, int len)
    {
        // The first paragraph is the title and cannot hold trailers; comment lines do not end it.
        var endOfTitle = 0;
        while (endOfTitle < len)
        {
            if (buf[endOfTitle] != CommentChar && IsBlankLine(buf, endOfTitle))
            {
                break;
            }

            endOfTitle = NextLine(buf, endOfTitle);
        }

        var possibleContinuationLines = 0;
        var trailerLines = 0;
        var nonTrailerLines = 0;
        var recognizedPrefix = false;
        var onlySpaces = true;

        for (var l = LastLine(buf, len); l >= endOfTitle; l = LastLine(buf, l))
        {
            if (buf[l] == CommentChar)
            {
                nonTrailerLines += possibleContinuationLines;
                possibleContinuationLines = 0;
                continue;
            }

            if (IsBlankLine(buf, l))
            {
                if (onlySpaces)
                {
                    continue;
                }

                nonTrailerLines += possibleContinuationLines;
                if (recognizedPrefix && trailerLines * 3 >= nonTrailerLines)
                {
                    return NextLine(buf, l);
                }

                if (trailerLines > 0 && nonTrailerLines == 0)
                {
                    return NextLine(buf, l);
                }

                return len;
            }

            onlySpaces = false;

            if (GitGeneratedPrefixes.Any(prefix => string.CompareOrdinal(buf, l, prefix, 0, prefix.Length) == 0))
            {
                trailerLines++;
                possibleContinuationLines = 0;
                recognizedPrefix = true;
                continue;
            }

            var separator = FindSeparator(buf, l);
            if (separator >= 1 && !IsGitSpace(buf[l]))
            {
                trailerLines++;
                possibleContinuationLines = 0;
                if (!recognizedPrefix && ConfiguredTrailerNames.Any(name => TokenMatchesName(buf, l, separator, name)))
                {
                    recognizedPrefix = true;
                }
            }
            else if (IsGitSpace(buf[l]))
            {
                possibleContinuationLines++;
            }
            else
            {
                nonTrailerLines += 1 + possibleContinuationLines;
                possibleContinuationLines = 0;
            }
        }

        return len;
    }

    // trailer.c find_separator for the default separators ":": the offset of the separator, or -1. A token is letters, digits and '-', optionally followed by blanks.
    private static int FindSeparator(string buf, int lineStart)
    {
        var whitespaceFound = false;
        for (var i = lineStart; i < buf.Length; i++)
        {
            var c = buf[i];
            if (c == ':')
            {
                return i - lineStart;
            }

            if (!whitespaceFound && (IsAsciiAlphaNumeric(c) || c == '-'))
            {
                continue;
            }

            if (i != lineStart && (c == ' ' || c == '\t'))
            {
                whitespaceFound = true;
                continue;
            }

            break;
        }

        return -1;
    }

    // token_matches_item: strncasecmp(line, name, separatorPos) == 0, so the line's token (blanks before the separator included) may be a case-insensitive PREFIX of the configured name.
    private static bool TokenMatchesName(string buf, int lineStart, int separatorPos, string name) =>
        separatorPos <= name.Length && string.Compare(buf, lineStart, name, 0, separatorPos, StringComparison.OrdinalIgnoreCase) == 0;

    // same_token: compares only as many characters as the shorter token has once trailing non-alphanumerics are dropped (so "Tool" matches "Tool-Call-Id").
    private static bool SameToken(string existing, string argument)
    {
        var existingLength = LengthWithoutSeparator(existing);
        var argumentLength = LengthWithoutSeparator(argument);
        var min = Math.Min(existingLength, argumentLength);
        return string.Compare(existing, 0, argument, 0, min, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static int LengthWithoutSeparator(string token)
    {
        var length = token.Length;
        while (length > 0 && !IsAsciiAlphaNumeric(token[length - 1]))
        {
            length--;
        }

        return length;
    }

    private static bool EndsWithBlankLine(string buf, int len)
    {
        var last = LastLine(buf, len);
        return last >= 0 && IsBlankLine(buf, last);
    }

    // trailer.c last_line: the start of the last line of buf[0..len), -1 for an empty range; a final newline belongs to that line.
    private static int LastLine(string buf, int len)
    {
        if (len == 0)
        {
            return -1;
        }

        if (len == 1)
        {
            return 0;
        }

        for (var i = len - 2; i >= 0; i--)
        {
            if (buf[i] == '\n')
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static int NextLine(string buf, int position)
    {
        var newline = buf.IndexOf('\n', position);
        return newline < 0 ? buf.Length : newline + 1;
    }

    private static bool IsBlankLine(string buf, int lineStart)
    {
        for (var i = lineStart; i < buf.Length && buf[i] != '\n'; i++)
        {
            if (!IsGitSpace(buf[i]))
            {
                return false;
            }
        }

        return true;
    }

    // git's sane_ctype: isspace is exactly space, tab, LF and CR (not VT or FF).
    private static bool IsGitSpace(char c) => c is ' ' or '\t' or '\n' or '\r';

    private static bool IsAsciiAlphaNumeric(char c) => char.IsAsciiLetterOrDigit(c);

    private static string GitTrim(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsGitSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsGitSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    // The value git stores for "--trailer 'Key: value'": trimmed. Null when that is empty or spans lines (the argument is then not a plain one-line trailer).
    private static string? TrailerValue(string value)
    {
        var trimmed = GitTrim(value);
        return trimmed.Length == 0 || trimmed.AsSpan().IndexOfAny('\n', '\r') >= 0 ? null : trimmed;
    }

    // ----- cleanup (strbuf_stripspace without a comment prefix) -----

    private static string StripSpace(string text)
    {
        var output = new StringBuilder(text.Length);
        var empties = 0;
        var position = 0;
        while (position < text.Length)
        {
            var next = NextLine(text, position);
            var end = next;
            if (end > position && text[end - 1] == '\n')
            {
                end--;
            }

            while (end > position && IsGitSpace(text[end - 1]))
            {
                end--;
            }

            if (end > position)
            {
                if (empties > 0 && output.Length > 0)
                {
                    output.Append('\n');
                }

                empties = 0;
                output.Append(text, position, end - position).Append('\n');
            }
            else
            {
                empties++;
            }

            position = next;
        }

        return output.ToString();
    }

    // ----- declines -----

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSpecialIgnoredLines(string text) =>
        text.Contains(ScissorsLine, StringComparison.Ordinal) || text.StartsWith("Conflicts:", StringComparison.Ordinal) || text.Contains("\nConflicts:", StringComparison.Ordinal);
}
