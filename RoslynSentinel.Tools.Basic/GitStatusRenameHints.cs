using System.Text;
using System.Text.Json;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

/// <summary>
/// Info findings that say which deleted path and which untracked path of a <c>Git(operation: status)</c> result are one file moved:
/// git shows an unstaged move as a deletion plus an untracked file until both are staged. Two tiers, both findings only (the status
/// payload is not touched).
/// Tier 1 is definitive: the operation blobs of <c>MoveFile</c> (tool name <c>move_file</c>, not marked <c>.undone</c>), each listing the
/// written destination and the deleted source. The blob is the durable source rather than <see cref="FileMoveMemory"/>, which is in
/// memory only, is cleared on a solution load, carries no change id and does not know about an undo.
/// Tier 2: a deleted path whose HEAD blob equals the text of an untracked file (exact string compare after the same BOM drop the
/// readers apply), bounded to <see cref="MaxCandidates"/> deleted and <see cref="MaxCandidates"/> untracked paths and files of at most
/// <see cref="MaxFileBytes"/> bytes. Empty files are never paired (every empty file equals every other).
/// Everything is fail-open: a hint is a convenience and never fails the status call.
/// DTO-EXEMPT: no new data type; a move pair is a value tuple (repo-relative source, repo-relative destination, change id).
/// </summary>
internal static class GitStatusRenameHints
{
    /// <summary>Most deleted paths, and most untracked paths, considered by tier 2.</summary>
    internal const int MaxCandidates = 20;

    /// <summary>Largest file tier 2 reads (on disk, and the HEAD blob as characters).</summary>
    internal const long MaxFileBytes = 1024 * 1024;

    /// <summary>Most <c>move_file</c> blobs opened per status call, newest first.</summary>
    internal const int MaxMoveBlobsOpened = 100;

    private const long MaxBlobBytes = 32L * 1024 * 1024;
    private const string FindingSource = "Git";
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tier 1 then tier 2 findings for <paramref name="status"/>, ordered by source path within each tier. <paramref name="movePairs"/> is null to
    /// skip tier 1 (a status of another repository). Paths in the status are repo-root-relative with '/'; <paramref name="gitRoot"/> is that root.
    /// </summary>
    internal static IReadOnlyList<Finding> Findings(string gitRoot, GitStatusResult status, IReadOnlyList<(string Source, string Destination, string ChangeId)>? movePairs, IGitReader reader)
    {
        try
        {
            var deleted = DeletedPaths(status);
            var untracked = UntrackedPaths(status);
            if (deleted.Count == 0 || untracked.Count == 0)
            {
                return [];
            }

            var findings = new List<Finding>();
            var claimedDeleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var claimedUntracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (movePairs is not null)
            {
                findings.AddRange(MoveRecordFindings(deleted, untracked, movePairs, claimedDeleted, claimedUntracked));
            }

            findings.AddRange(IdenticalContentFindings(gitRoot, deleted, untracked, claimedDeleted, claimedUntracked, reader));
            return findings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Tier 1: each pair whose source is a deleted path and whose destination is an untracked path of the status, newest blob first
    /// as given. Claims both paths so tier 2 does not repeat them. Path comparison ignores case, as the file system does.
    /// </summary>
    internal static IReadOnlyList<Finding> MoveRecordFindings(
        IReadOnlyList<string> deleted,
        IReadOnlyList<string> untracked,
        IReadOnlyList<(string Source, string Destination, string ChangeId)> movePairs,
        ISet<string> claimedDeleted,
        ISet<string> claimedUntracked)
    {
        var deletedLookup = deleted.ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
        var untrackedLookup = untracked.ToDictionary(p => p, p => p, StringComparer.OrdinalIgnoreCase);
        var matched = new List<(string Source, string Destination, string ChangeId)>();
        foreach (var (source, destination, changeId) in movePairs)
        {
            if (!deletedLookup.TryGetValue(source, out var deletedPath)
                || !untrackedLookup.TryGetValue(destination, out var untrackedPath)
                || claimedDeleted.Contains(deletedPath)
                || claimedUntracked.Contains(untrackedPath))
            {
                continue;
            }

            claimedDeleted.Add(deletedPath);
            claimedUntracked.Add(untrackedPath);
            matched.Add((deletedPath, untrackedPath, changeId));
        }

        return [.. matched
            .OrderBy(m => m.Source, StringComparer.Ordinal)
            .ThenBy(m => m.Destination, StringComparer.Ordinal)
            .Select(m => new Finding(
                FindingSource,
                $"{m.Source} -> {m.Destination}: moved by MoveFile (changeId {m.ChangeId}); git shows it as D plus untracked until both are staged",
                FindingSeverity.Info))];
    }

    /// <summary>
    /// Tier 2: pairs a deleted path with the first (path order) unclaimed untracked file holding exactly its HEAD content.
    /// </summary>
    internal static IReadOnlyList<Finding> IdenticalContentFindings(
        string gitRoot,
        IReadOnlyList<string> deleted,
        IReadOnlyList<string> untracked,
        ISet<string> claimedDeleted,
        ISet<string> claimedUntracked,
        IGitReader reader)
    {
        var deletedContent = new List<(string Path, string Content)>();
        foreach (var path in deleted.Where(p => !claimedDeleted.Contains(p)).Take(MaxCandidates))
        {
            var head = reader.ReadBlobAtHead(gitRoot, path, GitTimeout);
            if (!string.IsNullOrEmpty(head) && head.Length <= MaxFileBytes)
            {
                deletedContent.Add((path, head));
            }
        }

        var pairs = new List<(string Deleted, string Untracked)>();
        if (deletedContent.Count > 0)
        {
            foreach (var path in untracked.Where(p => !claimedUntracked.Contains(p)).Take(MaxCandidates))
            {
                var text = ReadWorkingText(gitRoot, path);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var index = deletedContent.FindIndex(d => string.Equals(d.Content, text, StringComparison.Ordinal));
                if (index < 0)
                {
                    continue;
                }

                pairs.Add((deletedContent[index].Path, path));
                deletedContent.RemoveAt(index);
                if (deletedContent.Count == 0)
                {
                    break;
                }
            }
        }

        return [.. pairs
            .OrderBy(p => p.Deleted, StringComparer.Ordinal)
            .ThenBy(p => p.Untracked, StringComparer.Ordinal)
            .Select(p => new Finding(
                FindingSource,
                $"{p.Deleted} -> {p.Untracked}: renamed (identical content); stage both to record the rename",
                FindingSeverity.Info))];
    }

    /// <summary>
    /// The (source, destination, change id) of every <c>move_file</c> blob under <paramref name="solutionRoot"/> that is not undone, newest first,
    /// as repo-relative paths with '/' (relative to <paramref name="gitRoot"/>). A path outside the repository is dropped. A chain of moves (A to B, then B to C) also yields the joined pair A to C, appended after the pairs it joins. Never throws.
    /// </summary>
    internal static IReadOnlyList<(string Source, string Destination, string ChangeId)> ReadMovePairs(string? solutionRoot, string gitRoot)
    {
        var pairs = new List<(string Source, string Destination, string ChangeId)>();
        try
        {
            if (string.IsNullOrWhiteSpace(solutionRoot))
            {
                return pairs;
            }

            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
            if (!Directory.Exists(dir))
            {
                return pairs;
            }

            var blobs = Directory.EnumerateFiles(dir, "move_file_*.json")
                .Select(path => (BlobPath: path, Written: NotFoundEvidence.TimestampOf(path)))
                .Where(blob => blob.Written is not null)
                .OrderByDescending(blob => blob.Written)
                .ThenByDescending(blob => blob.BlobPath, StringComparer.Ordinal)
                .Take(MaxMoveBlobsOpened);
            foreach (var blob in blobs)
            {
                if (OperationBlobWriter.IsUndone(blob.BlobPath))
                {
                    continue;
                }

                if (ReadMovePair(blob.BlobPath, solutionRoot, gitRoot) is { } pair)
                {
                    pairs.Add(pair);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A hint only: an unreadable operations folder costs the definitive tier, never the status call.
        }

        return JoinChains(pairs);
    }

    // Adds the joined pair of every chain head: a pair whose source is not the destination of another pair, followed through
    // Destination -> Source links (visited-set cycle guard) to the last destination. Only chains of 2+ links add a pair; a joined
    // pair that is a self-move or duplicates an existing pair is skipped. The original pairs are kept in order, joined pairs after them.
    private static List<(string Source, string Destination, string ChangeId)> JoinChains(IReadOnlyList<(string Source, string Destination, string ChangeId)> pairs)
    {
        var result = new List<(string Source, string Destination, string ChangeId)>(pairs);
        for (var head = 0; head < pairs.Count; head++)
        {
            var source = pairs[head].Source;
            var isChainHead = true;
            for (var other = 0; other < pairs.Count; other++)
            {
                if (other != head && string.Equals(pairs[other].Destination, source, StringComparison.OrdinalIgnoreCase))
                {
                    isChainHead = false;
                    break;
                }
            }

            if (!isChainHead)
            {
                continue;
            }

            var visited = new HashSet<int> { head };
            var last = head;
            var links = 1;
            while (true)
            {
                var next = -1;
                for (var candidate = 0; candidate < pairs.Count; candidate++)
                {
                    if (!visited.Contains(candidate)
                        && string.Equals(pairs[candidate].Source, pairs[last].Destination, StringComparison.OrdinalIgnoreCase))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next < 0)
                {
                    break;
                }

                visited.Add(next);
                last = next;
                links++;
            }

            if (links < 2)
            {
                continue;
            }

            var destination = pairs[last].Destination;
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
                || result.Any(existing => string.Equals(existing.Source, source, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Destination, destination, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add((source, destination, pairs[last].ChangeId));
        }

        return result;
    }

    // One blob: toolName move_file, exactly one created item (the destination) and one item without an after-source that was not created (the deleted source).
    private static (string Source, string Destination, string ChangeId)? ReadMovePair(string blobPath, string solutionRoot, string gitRoot)
    {
        try
        {
            if (new FileInfo(blobPath).Length > MaxBlobBytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(blobPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGet(root, "toolName", out var toolName) || toolName.ValueKind != JsonValueKind.String
                || !string.Equals(toolName.GetString(), "move_file", StringComparison.Ordinal)
                || !TryGet(root, "changeId", out var changeId) || changeId.ValueKind != JsonValueKind.String
                || !TryGet(root, "items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? source = null;
            string? destination = null;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !TryGet(item, "filePath", out var filePath) || filePath.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var created = TryGet(item, "created", out var createdValue) && createdValue.ValueKind == JsonValueKind.True;
                var hasAfter = TryGet(item, "afterSource", out var after) && after.ValueKind == JsonValueKind.String;
                if (created)
                {
                    if (destination is not null)
                    {
                        return null;
                    }

                    destination = filePath.GetString();
                }
                else if (!hasAfter)
                {
                    if (source is not null)
                    {
                        return null;
                    }

                    source = filePath.GetString();
                }
                else
                {
                    return null;
                }
            }

            var relativeSource = RepoRelative(gitRoot, solutionRoot, source);
            var relativeDestination = RepoRelative(gitRoot, solutionRoot, destination);
            return relativeSource is null || relativeDestination is null || changeId.GetString() is not { Length: > 0 } id
                ? null
                : (relativeSource, relativeDestination, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // A blob mid-write by another window, or malformed: skipped alone.
            return null;
        }
    }

    private static string? RepoRelative(string gitRoot, string solutionRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var relative = Path.GetRelativePath(gitRoot, Path.GetFullPath(path, solutionRoot)).Replace('\\', '/');
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative) ? null : relative;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // The file's text as the readers present a blob: UTF-8 with a leading byte-order mark dropped. Null when the file is missing, over the size
    // cap or not valid UTF-8 (the replacement characters of a lenient decode could make two different files look equal).
    private static string? ReadWorkingText(string gitRoot, string relativePath)
    {
        var fullPath = Path.Combine(gitRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length > MaxFileBytes)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(fullPath);
        var preamble = Encoding.UTF8.Preamble;
        var skip = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // Deleted paths of the status (staged or unstaged), path order, distinct.
    private static IReadOnlyList<string> DeletedPaths(GitStatusResult status) =>
        [.. status.Staged.Concat(status.Unstaged)
            .Where(e => e.Status == "deleted")
            .Select(e => e.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)];

    // Untracked file paths of the status, path order; a collapsed directory entry (trailing '/') is not a file.
    private static IReadOnlyList<string> UntrackedPaths(GitStatusResult status) =>
        [.. status.Untracked
            .Where(p => !p.EndsWith('/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)];
}
