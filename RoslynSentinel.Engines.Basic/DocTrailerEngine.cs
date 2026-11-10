using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>One doc row of a trailer scan or ID lookup. Commit lists are 8-char hashes, newest first.</summary>
public sealed record DocTrailerDoc
{
    /// <summary>Repo-relative path with '/' separators.</summary>
    public required string Path { get; init; }

    /// <summary>The doc ID: the file name without its extension.</summary>
    public required string Id { get; init; }

    /// <summary>The first <c>**Status:**</c> line text trimmed to 60 characters, or "(no Status line)".</summary>
    public required string Status { get; init; }

    /// <summary>True when the doc lives under a folder named <c>closed</c>.</summary>
    public bool InClosed { get; init; }

    /// <summary>True when a <c>Closes:</c> commit names this doc and it is under docs/current outside closed/, Worktree, upstream and obsolete (so it should be reviewed for closure).</summary>
    public bool ClosureCandidate { get; init; }

    public IReadOnlyList<string> PlanCommits { get; init; } = [];

    public IReadOnlyList<string> ClosesCommits { get; init; } = [];
}

/// <summary>A <c>Plan:</c> or <c>Closes:</c> trailer value that names no doc under docs/. <see cref="Commit"/> is the newest commit carrying it.</summary>
public sealed record DocTrailerUnknown
{
    /// <summary>"Plan" or "Closes".</summary>
    public required string Kind { get; init; }

    public required string Id { get; init; }

    public required string Commit { get; init; }
}

/// <summary>Result of <see cref="DocTrailerEngine.ScanTrailers"/>. Only docs with at least one trailer commit appear in <see cref="Docs"/>.</summary>
public sealed record DocTrailerScanResult
{
    /// <summary>Actionable failure text (git or docs/ problem); null on success.</summary>
    public string? Error { get; init; }

    public int DocsScanned { get; init; }

    public int CommitsWithTrailers { get; init; }

    public int ClosureCandidateCount { get; init; }

    public IReadOnlyList<DocTrailerDoc> Docs { get; init; } = [];

    public IReadOnlyList<DocTrailerUnknown> UnknownTrailers { get; init; } = [];
}

/// <summary>Result of <see cref="DocTrailerEngine.ValidateId"/>. A missing doc is not an error: <see cref="Exists"/> is false and <see cref="Message"/> says what was searched.</summary>
public sealed record DocTrailerIdResult
{
    /// <summary>Actionable failure text (invalid ID, git or docs/ problem); null otherwise.</summary>
    public string? Error { get; init; }

    public string Id { get; init; } = "";

    public bool Exists { get; init; }

    /// <summary>One entry per doc whose file name (without extension) equals the ID; more than one means the basename is ambiguous.</summary>
    public IReadOnlyList<DocTrailerDoc> Matches { get; init; } = [];

    /// <summary>Not-found or ambiguity explanation; null when exactly one doc matched.</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Reads <c>Plan:</c> / <c>Closes:</c> commit trailers (docs/current/templates/README.md rule 11) and relates them to
/// docs under docs/. A doc ID is a file name without extension, compared ordinal (case-sensitive). The trailer line
/// pattern mirrors scripts/Test-GuidanceHygiene.ps1 (the TRAILER check). Only <c>Closes:</c> counts as done;
/// <c>Plan:</c> alone is in progress. Nothing here throws for git or IO problems: the result carries an
/// <c>Error</c> with the next step.
/// </summary>
public static class DocTrailerEngine
{
    private const int StatusMaxLength = 60;
    private const int ShortHashLength = 8;
    private const string NoStatusText = "(no Status line)";
    private const string UnreadableStatusText = "(unreadable)";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex TrailerLine = new(
        @"^(Plan|Closes):\s*([A-Za-z0-9_.\-]+(?:\s*,\s*[A-Za-z0-9_.\-]+)*)\s*$",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_.\-]+$", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex StatusLine = new(@"^\*\*Status:?\*\*:?\s*(.*)$", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex CommaSeparator = new(@"\s*,\s*", RegexOptions.CultureInvariant, RegexTimeout);

    // Folder names that exclude a doc from closure-candidate status (matched case-insensitively on directory segments).
    private static readonly string[] NonCandidateFolders = ["Worktree", "upstream", "obsolete", "closed"];

    private sealed record DocEntry(string RelPath, string Id, bool InClosed, bool InCandidateScope);

    private sealed record TrailerHit(string Kind, string Id, string Hash);

    /// <summary>
    /// Lists every doc with at least one <c>Plan:</c>/<c>Closes:</c> commit (or that is a closure candidate), plus trailer
    /// values that match no doc. Docs are ordered by path (ordinal); commit hashes are newest first.
    /// </summary>
    public static DocTrailerScanResult ScanTrailers(string repoRoot, TimeSpan timeout)
    {
        var docs = EnumerateDocs(repoRoot, out var error);
        if (docs is null)
        {
            return new DocTrailerScanResult { Error = error };
        }

        var hits = ReadTrailerHits(repoRoot, timeout, out error);
        if (hits is null)
        {
            return new DocTrailerScanResult { Error = error, DocsScanned = docs.Count };
        }

        var known = new HashSet<string>(docs.Select(d => d.Id), StringComparer.Ordinal);
        var unknown = new Dictionary<(string Kind, string Id), string>();
        var (plan, closes) = (new Dictionary<string, List<string>>(StringComparer.Ordinal), new Dictionary<string, List<string>>(StringComparer.Ordinal));
        foreach (var hit in hits)
        {
            if (!known.Contains(hit.Id))
            {
                unknown.TryAdd((hit.Kind, hit.Id), hit.Hash);
                continue;
            }

            AddCommit(hit.Kind == "Closes" ? closes : plan, hit.Id, hit.Hash);
        }

        var rows = new List<DocTrailerDoc>();
        foreach (var entry in docs.OrderBy(d => d.RelPath, StringComparer.Ordinal))
        {
            var planCommits = CommitsFor(plan, entry.Id);
            var closesCommits = CommitsFor(closes, entry.Id);
            if (planCommits.Count == 0 && closesCommits.Count == 0)
            {
                continue;
            }

            rows.Add(ToDoc(repoRoot, entry, planCommits, closesCommits));
        }

        return new DocTrailerScanResult
        {
            DocsScanned = docs.Count,
            CommitsWithTrailers = hits.Select(h => h.Hash).Distinct(StringComparer.Ordinal).Count(),
            ClosureCandidateCount = rows.Count(r => r.ClosureCandidate),
            Docs = rows,
            UnknownTrailers = unknown
                .OrderBy(u => u.Key.Id, StringComparer.Ordinal)
                .ThenBy(u => u.Key.Kind, StringComparer.Ordinal)
                .Select(u => new DocTrailerUnknown { Kind = u.Key.Kind, Id = u.Key.Id, Commit = u.Value })
                .ToList(),
        };
    }

    /// <summary>
    /// Checks whether <paramref name="id"/> (a file name without extension) names a doc under docs/, and returns each
    /// matching doc with its trailer commits. More than one match means the basename is ambiguous.
    /// </summary>
    public static DocTrailerIdResult ValidateId(string repoRoot, string id, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsValidId(id.Trim()))
        {
            return new DocTrailerIdResult { Id = id ?? "", Error = InvalidIdMessage() };
        }

        id = id.Trim();
        var docs = EnumerateDocs(repoRoot, out var error);
        if (docs is null)
        {
            return new DocTrailerIdResult { Id = id, Error = error };
        }

        var matched = docs.Where(d => string.Equals(d.Id, id, StringComparison.Ordinal)).OrderBy(d => d.RelPath, StringComparer.Ordinal).ToList();
        if (matched.Count == 0)
        {
            var extensionHint = id.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? " The ID is the file name WITHOUT '.md'; retry without the extension."
                : "";
            return new DocTrailerIdResult
            {
                Id = id,
                Message = $"No doc with ID '{id}' exists. Searched: every *.md file under docs/ (recursive, Worktree folders skipped; {docs.Count} docs scanned), "
                    + "comparing the file name without its extension to the ID exactly (case-sensitive; no prefix or fuzzy match)."
                    + extensionHint
                    + " Next: call ProjectDoc with action=trailers to list the docs and trailer IDs in use, then correct the ID.",
            };
        }

        var hits = ReadTrailerHits(repoRoot, timeout, out error);
        if (hits is null)
        {
            return new DocTrailerIdResult { Id = id, Error = error };
        }

        var planCommits = hits.Where(h => h.Kind == "Plan" && h.Id == id).Select(h => h.Hash).Distinct(StringComparer.Ordinal).ToList();
        var closesCommits = hits.Where(h => h.Kind == "Closes" && h.Id == id).Select(h => h.Hash).Distinct(StringComparer.Ordinal).ToList();
        return new DocTrailerIdResult
        {
            Id = id,
            Exists = true,
            Matches = matched.Select(m => ToDoc(repoRoot, m, planCommits, closesCommits)).ToList(),
            Message = matched.Count > 1
                ? $"Ambiguous: {matched.Count} docs share the ID '{id}' ({string.Join(", ", matched.Select(m => m.RelPath))}). A trailer naming it counts for all of them; rename one so IDs stay unique."
                : null,
        };
    }

    /// <summary>
    /// Finds the docs under docs/ whose file name without extension equals <paramref name="id"/> (ordinal), as
    /// repo-relative paths with '/' sorted ordinal. Rejects an ID that is not a plain file-name stem, so the result can
    /// never point outside docs/. Returns false with an actionable <paramref name="error"/> for an invalid ID or an
    /// unreadable docs/ tree; true with an empty list when nothing matches.
    /// </summary>
    public static bool TryFindDocPaths(string repoRoot, string id, out IReadOnlyList<string> paths, out string? error)
    {
        paths = [];
        if (string.IsNullOrWhiteSpace(id) || !IsValidId(id.Trim()))
        {
            error = InvalidIdMessage();
            return false;
        }

        var docs = EnumerateDocs(repoRoot, out error);
        if (docs is null)
        {
            return false;
        }

        var wanted = id.Trim();
        paths = docs.Where(d => string.Equals(d.Id, wanted, StringComparison.Ordinal)).Select(d => d.RelPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        return true;
    }

    private static bool IsValidId(string id) => IdPattern.IsMatch(id) && id.Any(c => c != '.');

    private static string InvalidIdMessage() =>
        "A doc ID is a file name without extension: letters, digits, '_', '.' and '-' only, no path separators or spaces (for example 'plan_foo'). "
        + "Call ProjectDoc with action=trailers to list the docs and trailer IDs in use.";

    private static void AddCommit(Dictionary<string, List<string>> map, string id, string hash)
    {
        if (!map.TryGetValue(id, out var list))
        {
            map[id] = list = [];
        }

        if (!list.Contains(hash))
        {
            list.Add(hash);
        }
    }

    private static List<string> CommitsFor(Dictionary<string, List<string>> map, string id) =>
        map.TryGetValue(id, out var list) ? list : [];

    private static DocTrailerDoc ToDoc(string repoRoot, DocEntry entry, IReadOnlyList<string> planCommits, IReadOnlyList<string> closesCommits) => new()
    {
        Path = entry.RelPath,
        Id = entry.Id,
        Status = ReadStatus(repoRoot, entry.RelPath),
        InClosed = entry.InClosed,
        ClosureCandidate = closesCommits.Count > 0 && entry.InCandidateScope,
        PlanCommits = planCommits,
        ClosesCommits = closesCommits,
    };

    private static string ReadStatus(string repoRoot, string relPath)
    {
        try
        {
            var full = System.IO.Path.Combine(repoRoot, relPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            foreach (var line in File.ReadLines(full))
            {
                var m = StatusLine.Match(line);
                if (m.Success)
                {
                    var text = m.Groups[1].Value.Trim();
                    return text.Length > StatusMaxLength ? text[..StatusMaxLength] : text;
                }
            }

            return NoStatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return UnreadableStatusText;
        }
    }

    /// <summary>All *.md under repoRoot/docs, outside Worktree folders and reparse points, or null with an error.</summary>
    private static List<DocEntry>? EnumerateDocs(string repoRoot, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            error = "The repo root directory does not exist. Pass the solution/repo root directory (the folder containing docs/).";
            return null;
        }

        var docsDir = System.IO.Path.Combine(repoRoot, "docs");
        if (!Directory.Exists(docsDir))
        {
            error = "The repo root has no docs/ directory, so there are no doc IDs to relate trailers to. Pass the repo root that contains docs/.";
            return null;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };
        var docs = new List<DocEntry>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(docsDir, "*.md", options))
            {
                if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rel = System.IO.Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                var folders = rel.Split('/')[..^1];
                if (folders.Any(f => string.Equals(f, "Worktree", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var inClosed = folders.Any(f => string.Equals(f, "closed", StringComparison.OrdinalIgnoreCase));
                var inScope = rel.StartsWith("docs/current/", StringComparison.Ordinal)
                    && !folders.Any(f => NonCandidateFolders.Contains(f, StringComparer.OrdinalIgnoreCase));
                docs.Add(new DocEntry(rel, System.IO.Path.GetFileNameWithoutExtension(file), inClosed, inScope));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not enumerate docs/ ({ex.GetType().Name}). Check the folder permissions and retry.";
            return null;
        }

        return docs;
    }

    /// <summary>One hit per (commit, kind, id), in git log order (newest first), or null with an error.</summary>
    private static List<TrailerHit>? ReadTrailerHits(string repoRoot, TimeSpan timeout, out string? error)
    {
        error = null;
        var run = GitProcess.TryRun(repoRoot, timeout, "log", "--format=%H%x1f%B%x1e");
        if (run is null)
        {
            error = $"git could not be run to read commit trailers ({GitProcess.LastFailure ?? "unknown reason"}). Check that git is on PATH and retry.";
            return null;
        }

        if (run.Value.ExitCode != 0)
        {
            error = $"git log exited with code {run.Value.ExitCode}: the repo root is probably not a git repository or has no commits yet. Run the call from a repo root with at least one commit.";
            return null;
        }

        var hits = new List<TrailerHit>();
        try
        {
            foreach (var record in run.Value.StdOut.Split('\u001e'))
            {
                var parts = record.Split('\u001f', 2);
                if (parts.Length < 2)
                {
                    continue;
                }

                var sha = parts[0].Trim();
                if (sha.Length < 7)
                {
                    continue;
                }

                var hash = sha[..Math.Min(ShortHashLength, sha.Length)];
                var seen = new HashSet<(string, string)>();
                foreach (var rawLine in parts[1].Split('\n'))
                {
                    var m = TrailerLine.Match(rawLine.TrimEnd('\r'));
                    if (!m.Success)
                    {
                        continue;
                    }

                    var kind = m.Groups[1].Value;
                    foreach (var id in CommaSeparator.Split(m.Groups[2].Value))
                    {
                        if (seen.Add((kind, id)))
                        {
                            hits.Add(new TrailerHit(kind, id, hash));
                        }
                    }
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Reading commit trailers timed out on an unusually large commit message. Retry after shortening it, or report this as a bug.";
            return null;
        }

        return hits;
    }
}
