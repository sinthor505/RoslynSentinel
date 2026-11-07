using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Common;

/// <summary>
/// Evidence notes for a "not found" tool error, so an agent can tell its own typo or guess from a term that another writer
/// removed. A note states evidence, never a cause, and there is no note when there is no evidence. Operation blobs are
/// searched directly rather than the edit attribution ledger: the ledger is per server, the blob folder is per solution
/// root, so it also shows writes from other windows. Git covers what no blob shows. Never throws.
/// </summary>
public static class NotFoundEvidence
{
    private const string FindingSource = "EditAttribution";
    private const string MainAgentId = "main";
    private const int MinNameLength = 3;
    private const int MaxBlobsOpened = 200;
    private const int MaxReasonChars = 160;
    private const int MaxTermChars = 60;

    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);
    private static readonly Regex BlobTimestamp = new(@"_(\d{8}T\d{6}Z)(?:_(\d{7}))?_", RegexOptions.CultureInvariant | RegexOptions.RightToLeft);

    private enum WriterKind
    {
        Self,
        Foreign,
        Unknown,
    }

    /// <summary>
    /// Notes for the current tool call, read from <see cref="ToolCallContext.Current"/>. Empty unless the error code is a
    /// not-found code and the call carries an agent id, a target file, a mapped term and a solution root.
    /// </summary>
    public static IReadOnlyList<Finding> ForCurrentCall(string? errorCode)
    {
        var context = ToolCallContext.Current;
        if (context is not { AgentId: { } agentId, TargetFilePath: { } filePath, MissingTerm: { } term, SolutionRoot: { } solutionRoot }
            || errorCode is not (ToolErrorCode.NotFound or "MethodNotFound" or "FileNotFound"))
        {
            return [];
        }

        return Find(solutionRoot, filePath, term, agentId, context.SessionId, DateTime.UtcNow);
    }

    /// <summary>
    /// The evidence search: the operation blobs of the last <paramref name="window"/> (default 60 minutes), then Git. Empty
    /// when neither shows the term leaving the file, or when the caller itself is the writer.
    /// </summary>
    public static IReadOnlyList<Finding> Find(string solutionRoot, string filePath, string term, string agentId, string? sessionId, DateTime utcNow, TimeSpan? window = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(term) || (IsName(term) && term.Length < MinNameLength))
            {
                return [];
            }

            var (note, callerWrote) = SearchBlobs(solutionRoot, filePath, term, agentId, sessionId, utcNow, window ?? DefaultWindow);
            note ??= callerWrote ? null : GitNote(solutionRoot, filePath, term);
            if (note is null)
            {
                return [];
            }

            return [new Finding(FindingSource, note, FindingSeverity.Info)];
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Not-found evidence search failed: {ex}");
            return [];
        }
    }

    private static (string? Note, bool CallerWrote) SearchBlobs(string solutionRoot, string filePath, string term, string agentId, string? sessionId, DateTime utcNow, TimeSpan window)
    {
        var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
        if (!Directory.Exists(dir))
        {
            return (null, false);
        }

        var cutoff = utcNow - window;
        var recent = Directory.EnumerateFiles(dir, "*.json")
            .Select(path => (BlobPath: path, Written: TimestampOf(path)))
            .Where(blob => blob.Written >= cutoff)
            .OrderByDescending(blob => blob.Written)
            .ThenByDescending(blob => blob.BlobPath, StringComparer.Ordinal)
            .Take(MaxBlobsOpened);

        var callerWrote = false;
        foreach (var blob in recent)
        {
            using var document = TryParse(blob.BlobPath);
            if (document is null
                || !document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (!PathMatches(StringOf(item, "FilePath"), filePath)
                    || StringOf(item, "Outcome") is "Skipped" or "Failed" or "RolledBack")
                {
                    continue;
                }

                var writerSession = StringOf(item, "AppliedBySessionId");
                var writerAgent = StringOf(item, "AppliedByAgentId");
                var kind = Classify(writerSession, writerAgent, sessionId, agentId);
                callerWrote |= kind == WriterKind.Self;
                if (!Holds(StringOf(item, "BeforeSource"), term))
                {
                    continue;
                }

                // Walking newest first, the first pre-image that still holds the term belongs to the write that removed it.
                if (kind != WriterKind.Foreign)
                {
                    return (null, callerWrote);
                }

                var otherSession = writerSession is not null && sessionId is not null && !string.Equals(writerSession, sessionId, StringComparison.Ordinal);
                return (BlobNote(term, filePath, writerAgent, StringOf(item, "AppliedByAgentType"), otherSession, StringOf(document.RootElement, "toolName"), StringOf(item, "AppliedByReason"), utcNow - blob.Written!.Value), callerWrote);
            }
        }

        return (null, callerWrote);
    }

    private static WriterKind Classify(string? writerSession, string? writerAgent, string? callerSession, string callerAgent)
    {
        if (writerSession is not null && callerSession is not null)
        {
            return string.Equals(writerSession, callerSession, StringComparison.Ordinal) && string.Equals(writerAgent, callerAgent, StringComparison.Ordinal)
                ? WriterKind.Self
                : WriterKind.Foreign;
        }

        if (writerAgent is null)
        {
            return WriterKind.Unknown;
        }

        if (string.Equals(writerAgent, callerAgent, StringComparison.Ordinal))
        {
            return WriterKind.Self;
        }

        // Every window's main session is called main, so without a session id that name does not identify a writer.
        return string.Equals(writerAgent, MainAgentId, StringComparison.Ordinal) ? WriterKind.Unknown : WriterKind.Foreign;
    }

    private static string BlobNote(string term, string filePath, string? agent, string? agentType, bool otherSession, string? tool, string? reason, TimeSpan age)
    {
        var who = agent is null ? "an unidentified writer" : $"agent `{agent}`";
        if (agentType is not null)
        {
            who += $" (type {agentType})";
        }

        if (otherSession)
        {
            who += " in another session";
        }

        var because = reason is null ? string.Empty : $": {Clip(reason, MaxReasonChars)}";
        return $"`{ShortTerm(term)}` was in `{Path.GetFileName(filePath)}` before {who} changed it {EditAttributionFindings.FormatAge(age)} ago via {tool ?? "an unnamed tool"}{because}";
    }

    // Only reached when no blob explains the removal, so it covers edits made outside any server and writes older than the window.
    private static string? GitNote(string solutionRoot, string filePath, string term)
    {
        var fullPath = Path.GetFullPath(Path.IsPathRooted(filePath) ? filePath : Path.Combine(solutionRoot, filePath));
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is null || !Directory.Exists(dir))
        {
            return null;
        }

        var head = GitProcess.TryRun(dir, GitTimeout, "show", $"HEAD:./{Path.GetFileName(fullPath)}");
        if (head is not { ExitCode: 0 } || !Holds(head.Value.StdOut, term))
        {
            return null;
        }

        if (File.Exists(fullPath) && Holds(File.ReadAllText(fullPath), term))
        {
            return null;
        }

        return $"`{ShortTerm(term)}` is in `{Path.GetFileName(fullPath)}` at HEAD but not on disk: it was changed since the last commit, writer unknown";
    }

    private static DateTime? TimestampOf(string blobPath)
    {
        var match = BlobTimestamp.Match(Path.GetFileName(blobPath));
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var written))
        {
            return null;
        }

        // A blob written before the fraction segment was added counts as the start of its second.
        return match.Groups[2].Success
            ? written.AddTicks(long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture))
            : written;
    }

    // A blob may be mid-write by another window, so an unreadable one is skipped rather than failing the search.
    private static JsonDocument? TryParse(string blobPath)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(blobPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // The caller's path may be solution-relative or a bare file name, and a blob's may be absolute.
    private static bool PathMatches(string? itemPath, string filePath)
    {
        if (string.IsNullOrEmpty(itemPath))
        {
            return false;
        }

        var item = NormalizePath(itemPath);
        var target = NormalizePath(filePath);
        return item.Equals(target, StringComparison.OrdinalIgnoreCase)
            || item.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase)
            || target.EndsWith("/" + item, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private static bool IsName(string term) => term.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool Holds(string? text, string term)
    {
        if (text is null)
        {
            return false;
        }

        return IsName(term)
            ? Regex.IsMatch(text, $@"\b{Regex.Escape(term)}\b", RegexOptions.CultureInvariant)
            : NormalizeLineEndings(text).Contains(NormalizeLineEndings(term), StringComparison.Ordinal);
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ShortTerm(string term)
    {
        var trimmed = term.Trim();
        var lineEnd = trimmed.IndexOf('\n');
        return Clip(lineEnd < 0 ? trimmed : trimmed[..lineEnd].TrimEnd('\r'), MaxTermChars);
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "...";
}
