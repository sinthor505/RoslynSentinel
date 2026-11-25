namespace RoslynSentinel.Common;

/// <summary>
/// The commit trailers the Git tool's <c>commit</c> can add (<c>Tool-Call-Id</c>, <c>Tool-Agent</c>) and the parsing the
/// libgit2 reader and the commit code share. Opt-in per repository through the git config key <see cref="ConfigKey"/>.
/// A trailer is only recognised in the last paragraph of a message that has at least one paragraph before it (the subject
/// paragraph is never a trailer block), key compared case-insensitively, as <c>git interpret-trailers</c> does.
/// </summary>
internal static class GitCommitTrailers
{
    /// <summary>Repo-local boolean git config key that turns the trailers on (default off).</summary>
    internal const string ConfigKey = "roslynsentinel.commitTrailers";

    /// <summary>The trailer naming the tool call that made the commit.</summary>
    internal const string ToolCallIdKey = "Tool-Call-Id";

    /// <summary>The trailer naming the calling agent; only written when the call has an agent id.</summary>
    internal const string ToolAgentKey = "Tool-Agent";

    /// <summary>How many commits back from HEAD the lookup of a just-made commit looks.</summary>
    internal const int LookupDepth = 50;

    /// <summary>
    /// <c>-c trailer.&lt;key&gt;.ifexists=replace</c> for both keys: a trailer of the same key already in the message (an amend
    /// whose message already carries one, a message that quotes one) is replaced, never duplicated.
    /// </summary>
    internal static readonly string[] ReplaceConfigArgs =
    [
        "-c", $"trailer.{ToolCallIdKey}.ifexists=replace",
        "-c", $"trailer.{ToolAgentKey}.ifexists=replace",
    ];

    /// <summary>The <c>--trailer</c> arguments for a commit: always the call id, the agent only when <paramref name="agentId"/> is not blank.</summary>
    internal static string[] TrailerArgs(string callId, string? agentId) =>
        string.IsNullOrWhiteSpace(agentId)
            ? ["--trailer", $"{ToolCallIdKey}: {callId}"]
            : ["--trailer", $"{ToolCallIdKey}: {callId}", "--trailer", $"{ToolAgentKey}: {agentId.Trim()}"];

    /// <summary>
    /// True when <c>git commit</c> rejected <c>--trailer</c> as an unknown option (git older than 2.32): a usage error (exit code 129,
    /// raised while parsing options, so nothing was committed) whose usage text names the option. The exit code is what is relied on;
    /// the option name is matched because the message around it is localised.
    /// </summary>
    internal static bool IsTrailerOptionUnsupported(int exitCode, string stderr) =>
        exitCode == 129 && stderr.Contains("trailer", StringComparison.OrdinalIgnoreCase);

    /// <summary>The values of every <paramref name="key"/> trailer in <paramref name="message"/>, in order (empty when there are none).</summary>
    internal static IReadOnlyList<string> GetValues(string message, string key)
    {
        var lines = SplitKeepingTerminators(message);
        if (!TryFindTrailerBlock(lines, out var start, out var last))
        {
            return [];
        }

        var values = new List<string>();
        for (var i = start; i <= last; i++)
        {
            if (TryParseTrailerLine(lines[i], out var lineKey, out var value) && string.Equals(lineKey, key, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>
    /// <paramref name="message"/> without its <see cref="ToolCallIdKey"/> and <see cref="ToolAgentKey"/> trailer lines; everything
    /// else (line endings, other trailers, body) is untouched. A trailer block left empty is removed with the blank line before it.
    /// </summary>
    internal static string StripOwnTrailers(string message)
    {
        var lines = SplitKeepingTerminators(message);
        if (!TryFindTrailerBlock(lines, out var start, out var last))
        {
            return message;
        }

        var kept = new List<string>();
        var removed = false;
        for (var i = start; i <= last; i++)
        {
            if (TryParseTrailerLine(lines[i], out var lineKey, out _)
                && (string.Equals(lineKey, ToolCallIdKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(lineKey, ToolAgentKey, StringComparison.OrdinalIgnoreCase)))
            {
                removed = true;
            }
            else
            {
                kept.Add(lines[i]);
            }
        }

        if (!removed)
        {
            return message;
        }

        var head = lines.Take(start).ToList();
        if (kept.Count == 0)
        {
            // Drop the blank separator that preceded the (now empty) block.
            while (head.Count > 0 && IsBlank(head[^1]))
            {
                head.RemoveAt(head.Count - 1);
            }

            return string.Concat(head);
        }

        return string.Concat(head.Concat(kept).Concat(lines.Skip(last + 1)));
    }

    // The trailer block: the last non-blank paragraph, provided another paragraph precedes it (start > 0).
    private static bool TryFindTrailerBlock(List<string> lines, out int start, out int last)
    {
        last = lines.Count - 1;
        while (last >= 0 && IsBlank(lines[last]))
        {
            last--;
        }

        start = last;
        if (last < 0)
        {
            return false;
        }

        while (start > 0 && !IsBlank(lines[start - 1]))
        {
            start--;
        }

        return start > 0;
    }

    // "Key: value" with a token key (letters, digits, '-'); an indented continuation line is not a trailer of its own.
    private static bool TryParseTrailerLine(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;
        var text = line.TrimEnd('\r', '\n');
        var colon = text.IndexOf(':');
        if (colon <= 0 || char.IsWhiteSpace(text[0]))
        {
            return false;
        }

        var candidate = text[..colon];
        if (!candidate.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return false;
        }

        key = candidate;
        value = text[(colon + 1)..].Trim();
        return true;
    }

    private static bool IsBlank(string line) => line.AsSpan().Trim().IsEmpty;

    private static List<string> SplitKeepingTerminators(string text)
    {
        var lines = new List<string>();
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[from..(i + 1)]);
                from = i + 1;
            }
        }

        if (from < text.Length)
        {
            lines.Add(text[from..]);
        }

        return lines;
    }
}
