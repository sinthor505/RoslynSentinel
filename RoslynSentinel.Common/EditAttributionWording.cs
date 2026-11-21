namespace RoslynSentinel.Common;

/// <summary>
/// One wording for "who last changed this file", shared by the edit-attribution findings and the refusals that name a
/// writer (StaleRead in <see cref="WriteRefusal"/>, the undo guard), plus the lookup that finds the writer.
/// </summary>
public static class EditAttributionWording
{
    /// <summary>
    /// Text such as <c>agent 'a' (type implementer) via ReplaceSnippet, 5m ago: fix the call</c>; callers prefix it with
    /// their own "last changed by". A writer from another session carries its first 8 session chars. An anonymous writer reads as "an unidentified writer".
    /// </summary>
    public static string Describe(EditAttribution entry, DateTime utcNow, string? callerSessionId = null)
    {
        var who = entry.AgentId is null ? "an unidentified writer" : $"agent '{entry.AgentId}'";
        var sessionLabel = entry.SessionId is null || string.Equals(entry.SessionId, callerSessionId, StringComparison.Ordinal)
            ? string.Empty
            : $" (session {entry.SessionId[..Math.Min(8, entry.SessionId.Length)]})";
        return $"{who}{sessionLabel} (type {entry.AgentType ?? "unknown"}) via {entry.Tool ?? "unknown"}, {EditAttributionFindings.FormatAge(utcNow - entry.UtcTime)} ago: {entry.Reason ?? "no reason recorded"}";
    }

    /// <summary>
    /// The latest recorded writer per path: this server's edit attribution ledger (a path is tried as given and with each
    /// separator form, because the ledger compares paths without normalising separators), then, when
    /// <paramref name="solutionRoot"/> is given, the operation blobs, which also show sibling sessions. The newer of the two wins.
    /// A path with no recorded writer is absent. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, EditAttribution> LastWriters(EditAttributionLedger? ledger, string? solutionRoot, IReadOnlyCollection<string> paths, DateTime utcNow)
    {
        var result = new Dictionary<string, EditAttribution>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (ledger is not null)
            {
                foreach (var path in paths)
                {
                    var entry = ledger.GetLatest(path)
                        ?? ledger.GetLatest(path.Replace('/', '\\'))
                        ?? ledger.GetLatest(path.Replace('\\', '/'));
                    if (entry is not null)
                    {
                        result[path] = entry;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(solutionRoot))
            {
                foreach (var (path, blobEntry) in NotFoundEvidence.LatestBlobWriters(solutionRoot, paths, utcNow))
                {
                    if (!result.TryGetValue(path, out var existing) || blobEntry.UtcTime > existing.UtcTime)
                    {
                        result[path] = blobEntry;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Naming the writer is a courtesy; a lookup failure leaves the refusal text as it was.
        }

        return result;
    }
}
