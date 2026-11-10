using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>One doc row of a trailer scan or ID lookup. Commit lists are 8-char hashes, newest first.</summary>
public sealed record DocTrailerDoc
{
    /// <summary>Repo-relative path with '/' separators.</summary>
    public required string Path
    {
        get; init;
    }

    /// <summary>The doc ID: the file name without its extension.</summary>
    public required string Id
    {
        get; init;
    }

    /// <summary>The first <c>**Status:**</c> line text trimmed to 60 characters, or "(no Status line)".</summary>
    public required string Status
    {
        get; init;
    }

    /// <summary>True when the doc lives under a folder named <c>closed</c>.</summary>
    public bool InClosed
    {
        get; init;
    }

    /// <summary>True when a <c>Closes:</c> commit names this doc and it is under docs/current outside closed/, Worktree, upstream and obsolete (so it should be reviewed for closure).</summary>
    public bool ClosureCandidate
    {
        get; init;
    }

    public IReadOnlyList<string> PlanCommits { get; init; } = [];

    public IReadOnlyList<string> ClosesCommits { get; init; } = [];
}

/// <summary>A <c>Plan:</c> or <c>Closes:</c> trailer value that names no doc under docs/. <see cref="Commit"/> is the newest commit carrying it.</summary>
public sealed record DocTrailerUnknown
{
    /// <summary>"Plan" or "Closes".</summary>
    public required string Kind
    {
        get; init;
    }

    public required string Id
    {
        get; init;
    }

    public required string Commit
    {
        get; init;
    }
}

/// <summary>Result of <see cref="DocTrailerEngine.ScanTrailers"/>. Only docs with at least one trailer commit appear in <see cref="Docs"/>.</summary>
public sealed record DocTrailerScanResult
{
    /// <summary>Actionable failure text (git or docs/ problem); null on success.</summary>
    public string? Error
    {
        get; init;
    }

    public int DocsScanned
    {
        get; init;
    }

    public int CommitsWithTrailers
    {
        get; init;
    }

    public int ClosureCandidateCount
    {
        get; init;
    }

    public IReadOnlyList<DocTrailerDoc> Docs { get; init; } = [];

    public IReadOnlyList<DocTrailerUnknown> UnknownTrailers { get; init; } = [];
}

/// <summary>Result of <see cref="DocTrailerEngine.ValidateId"/>. A missing doc is not an error: <see cref="Exists"/> is false and <see cref="Message"/> says what was searched.</summary>
public sealed record DocTrailerIdResult
{
    /// <summary>Actionable failure text (invalid ID, git or docs/ problem); null otherwise.</summary>
    public string? Error
    {
        get; init;
    }

    public string Id { get; init; } = "";

    public bool Exists
    {
        get; init;
    }

    /// <summary>One entry per doc whose file name (without extension) equals the ID; more than one means the basename is ambiguous.</summary>
    public IReadOnlyList<DocTrailerDoc> Matches { get; init; } = [];

    /// <summary>Not-found or ambiguity explanation; null when exactly one doc matched.</summary>
    public string? Message
    {
        get; init;
    }
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

    // A markdown link target: ](target) or ](target "title"); group "target" is the part to rewrite.
    private static readonly Regex LinkPattern = new(@"\]\((?<target>[^)\s]+)(?:\s+""[^""]*"")?\s*\)", RegexOptions.CultureInvariant, RegexTimeout);

    // A URL scheme such as http:, https:, mailto: (also matches a Windows drive letter, which is absolute anyway).
    private static readonly Regex SchemePrefix = new(@"^[A-Za-z][A-Za-z0-9+.\-]*:", RegexOptions.CultureInvariant, RegexTimeout);

    // The leading "- " and optional status tag "[OPEN] " of a TODO.md line (Close-TodoItem.ps1 drops the tag; the bullet is dropped too because CLOSED.md entries have none).
    private static readonly Regex TodoBulletTag = new(@"^- (\[[A-Za-z\-]+\] )?", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Heading1 = new(@"^# +(.+?)\s*$", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>A text file read once: full path, decoded text (BOM stripped) and whether it had a UTF-8 BOM, so it can be written back unchanged in style.</summary>
    private sealed record LoadedFile(string FullPath, string Text, bool HasBom);

    /// <summary>
    /// Re-points relative markdown links after a doc moves. A link resolves against <see cref="OldDirFull"/> and is
    /// rewritten relative to <see cref="NewDirFull"/>. With <see cref="AllLinks"/> false only links that resolve to
    /// the moved doc change (used for other files, where the directory does not move); true re-points every relative
    /// link (used for the moved doc itself). Absolute paths, URLs, anchor-only and angle-bracket targets are left alone.
    /// </summary>
    private sealed record LinkMap(string OldDirFull, string NewDirFull, string OldDocFull, string NewDocFull, bool AllLinks)
    {
        /// <summary>The replacement target (path plus any #fragment) or null when the link is not rewritten.</summary>
        public string? Remap(string target)
        {
            if (target[0] is '#' or '<' or '/' or '\\' || SchemePrefix.IsMatch(target))
            {
                return null;
            }

            var hash = target.IndexOf('#');
            var pathPart = hash < 0 ? target : target[..hash];
            if (pathPart.Length == 0)
            {
                return null;
            }

            var fragment = hash < 0 ? "" : target[hash..];
            string resolved;
            try
            {
                resolved = Path.GetFullPath(pathPart, OldDirFull);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return null;
            }

            var isMovedDoc = string.Equals(resolved, OldDocFull, StringComparison.OrdinalIgnoreCase);
            if (!AllLinks && !isMovedDoc)
            {
                return null;
            }

            var relative = Path.GetRelativePath(NewDirFull, isMovedDoc ? NewDocFull : resolved).Replace('\\', '/');
            if ((pathPart[^1] is '/' or '\\') && !relative.EndsWith('/'))
            {
                relative += "/";
            }

            return relative + fragment;
        }
    }

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
                    + " " + ToolCallHint.Next("ProjectDoc", "to list the docs and trailer IDs in use, then correct the ID.", ("action", "trailers")),
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

    /// <summary>
    /// Closes a doc (docs/current/templates/README.md rule 8): replaces its first <c>**Status:**</c> line with
    /// <c>**Status:** &lt;statusText&gt;</c>, moves it to <c>docs/current/&lt;genre&gt;/closed/</c>, re-points relative links
    /// (inside the moved doc, in every other .md under docs/ outside Worktree and upstream, and in the repo-root
    /// CLAUDE.md), and records the close at the top of docs/current/CLOSED.md. When exactly one docs/current/TODO.md
    /// line starting with "- " links to the doc, that line (tag dropped, link re-pointed) becomes the CLOSED.md body and is
    /// removed from TODO.md; otherwise the body is "&lt;doc title&gt; - [doc](genre/closed/file)". The entry layout is the one
    /// scripts/Close-TodoItem.ps1 writes. Every file keeps its own line endings and BOM state.
    /// <para>
    /// Refuses, writing nothing, when the ID matches zero or several docs, the doc is already under a closed folder or is
    /// not a direct child of docs/current/&lt;genre&gt;/, <paramref name="statusText"/> is empty or multi-line, the doc has no
    /// Status line, or the destination exists. All new contents are computed in memory before the first write. The writes
    /// run in this order: move, moved doc, other docs, CLOSED.md, TODO.md; a write failure returns an Error naming the steps
    /// that completed. Missing or unreadable TODO.md/CLOSED.md, an unreadable file during link fixing, and unavailable git
    /// (no <c>Closes:</c> commits to list) are warnings, not errors.
    /// </para>
    /// </summary>
    public static DocCloseResult CloseDoc(string repoRoot, string id, string statusText, TimeSpan timeout, DateTime? today = null)
    {
        id = (id ?? "").Trim();
        try
        {
            return CloseDocCore(repoRoot, id, (statusText ?? "").Trim(), timeout, today);
        }
        catch (RegexMatchTimeoutException)
        {
            return CloseFail(id, "Closing stopped while scanning for links (a regex timed out on an unusually long line). Nothing was changed; shorten the offending line and retry.", []);
        }
    }

    private static DocCloseResult CloseDocCore(string repoRoot, string id, string status, TimeSpan timeout, DateTime? today)
    {
        var warnings = new List<string>();
        if (status.Length == 0 || status.IndexOfAny(['\r', '\n']) >= 0)
        {
            return CloseFail(id, "statusText must be one non-empty line, for example 'IMPLEMENTED (abcd1234)'. Nothing was changed.", warnings);
        }

        if (!TryFindDocPaths(repoRoot, id, out var matches, out var findError))
        {
            return CloseFail(id, findError!, warnings);
        }

        if (matches.Count == 0)
        {
            return CloseFail(id, $"No doc with ID '{id}' exists under docs/ (file name without extension, case-sensitive). Nothing was changed. " + ToolCallHint.Next("ProjectDoc", "to list the docs, then retry with the exact ID.", ("action", "trailers")), warnings);
        }

        if (matches.Count > 1)
        {
            return CloseFail(id, $"ID '{id}' is ambiguous: {string.Join(", ", matches)}. Nothing was changed; rename one so IDs are unique, then retry.", warnings);
        }

        var from = matches[0];
        var parts = from.Split('/');
        if (parts[..^1].Any(f => string.Equals(f, "closed", StringComparison.OrdinalIgnoreCase)))
        {
            return CloseFail(id, $"{from} is already under a closed folder. Nothing was changed.", warnings, from);
        }

        if (parts.Length != 4 || !string.Equals(parts[0], "docs", StringComparison.OrdinalIgnoreCase) || !string.Equals(parts[1], "current", StringComparison.OrdinalIgnoreCase))
        {
            return CloseFail(id, $"{from} is not a direct child of docs/current/<genre>/ (for example docs/current/plans/), so there is no closed/ folder to move it to. Nothing was changed.", warnings, from);
        }

        var root = Path.GetFullPath(repoRoot);
        var to = $"{parts[0]}/{parts[1]}/{parts[2]}/closed/{parts[3]}";
        var oldFull = Path.GetFullPath(from, root);
        var newFull = Path.GetFullPath(to, root);
        if (File.Exists(newFull) || Directory.Exists(newFull))
        {
            return CloseFail(id, $"The destination {to} already exists. Nothing was changed; resolve the name clash by hand, then retry.", warnings, from, to);
        }

        var doc = TryLoad(oldFull, out var problem);
        if (doc is null)
        {
            return CloseFail(id, $"Could not read {from} ({problem}). Nothing was changed; check the file and retry.", warnings, from, to);
        }

        var docLines = SplitLines(doc.Text);
        var statusIndex = docLines.FindIndex(l => StatusLine.IsMatch(l.Content));
        if (statusIndex < 0)
        {
            return CloseFail(id, $"{from} has no '**Status:**' line. Nothing was changed; add one (for example '**Status:** open') and retry.", warnings, from, to);
        }

        var statusLineText = "**Status:** " + status;
        docLines[statusIndex] = (statusLineText, docLines[statusIndex].Eol);

        var oldDir = Path.GetDirectoryName(oldFull)!;
        var newDir = Path.GetDirectoryName(newFull)!;
        var (docText, linksFixed) = RewriteLinks(JoinLines(docLines), new LinkMap(oldDir, newDir, oldFull, newFull, true));
        var docTitle = FirstHeading(docText) ?? id;

        // Closes: commits. Git trouble only costs the hash list.
        var closesCommits = new List<string>();
        var hits = ReadTrailerHits(root, timeout, out var gitError);
        if (hits is null)
        {
            warnings.Add($"Closes: commits could not be listed: {gitError}");
        }
        else
        {
            closesCommits = hits.Where(h => h.Kind == "Closes" && h.Id == id).Select(h => h.Hash).Distinct(StringComparer.Ordinal).ToList();
            if (closesCommits.Count == 0)
            {
                warnings.Add("No Closes: commit names this doc; closing anyway. Make sure the status text names the commit(s) that finished it.");
            }
        }

        // TODO.md / CLOSED.md (both live in docs/current, so TODO links are relative to that folder).
        var docsCurrent = Path.Combine(root, parts[0], parts[1]);
        var todoPath = Path.Combine(docsCurrent, "TODO.md");
        var closedPath = Path.Combine(docsCurrent, "CLOSED.md");
        var todoMap = new LinkMap(docsCurrent, docsCurrent, oldFull, newFull, false);
        var todo = LoadOptional(todoPath, "docs/current/TODO.md", warnings);
        var closed = LoadOptional(closedPath, "docs/current/CLOSED.md", warnings);
        List<(string Content, string Eol)> todoLines = todo is null ? [] : SplitLines(todo.Text);
        List<(string Content, string Eol)> closedLines = closed is null ? [] : SplitLines(closed.Text);

        var todoHits = new List<int>();
        for (var i = 0; i < todoLines.Count; i++)
        {
            if (todoLines[i].Content.StartsWith("- ", StringComparison.Ordinal) && RewriteLinks(todoLines[i].Content, todoMap).Fixed > 0)
            {
                todoHits.Add(i);
            }
        }

        var headingIndex = closedLines.FindIndex(l => l.Content.StartsWith("## ", StringComparison.Ordinal));
        if (closed is not null && headingIndex < 0)
        {
            warnings.Add("docs/current/CLOSED.md has no '## ' entry heading, so no entry was added and the TODO.md line (if any) was left in place; record the close by hand.");
        }

        var canRecord = closed is not null && headingIndex >= 0;
        if (todoHits.Count > 1)
        {
            warnings.Add($"{todoHits.Count} docs/current/TODO.md lines link to this doc, so none was moved to CLOSED.md; remove the stale one(s) by hand.");
        }

        var moveTodo = canRecord && todo is not null && todoHits.Count == 1;
        string body;
        var entryTitle = docTitle;
        if (moveTodo)
        {
            var (rewritten, bodyFixed) = RewriteLinks(todoLines[todoHits[0]].Content, todoMap);
            linksFixed += bodyFixed;
            body = TodoBulletTag.Replace(rewritten, "");
            var cut = new[] { body.IndexOf(" (", StringComparison.Ordinal), body.IndexOf(" - ", StringComparison.Ordinal) }.Where(i => i > 0).DefaultIfEmpty(-1).Min();
            if (cut > 0 && body[..cut].Trim().Length > 0)
            {
                entryTitle = body[..cut].Trim();
            }
        }
        else
        {
            body = $"{docTitle} - [doc]({parts[2]}/closed/{parts[3]})";
        }

        var pending = new Dictionary<string, LoadedFile>(StringComparer.OrdinalIgnoreCase);
        if (todo is not null)
        {
            var todoText = moveTodo ? JoinLines(todoLines.Where((_, i) => i != todoHits[0])) : todo.Text;
            var (rewrittenTodo, todoFixed) = RewriteLinks(todoText, todoMap);
            linksFixed += todoFixed;
            if (!string.Equals(rewrittenTodo, todo.Text, StringComparison.Ordinal))
            {
                pending[todoPath] = todo with { Text = rewrittenTodo };
            }
        }

        if (canRecord)
        {
            var cnl = DetectNewline(closed!.Text);
            var date = (today ?? DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var entry = $"## {entryTitle} - resolved ({date}){cnl}{cnl}{body}{cnl}{cnl}Evidence: {status}{cnl}{cnl}";
            var offset = closedLines.Take(headingIndex).Sum(l => l.Content.Length + l.Eol.Length);
            var (rewrittenClosed, closedFixed) = RewriteLinks(closed.Text.Insert(offset, entry), todoMap);
            linksFixed += closedFixed;
            pending[closedPath] = closed with { Text = rewrittenClosed };
        }
        else if (closed is not null)
        {
            var (rewrittenClosed, closedFixed) = RewriteLinks(closed.Text, todoMap);
            if (closedFixed > 0)
            {
                linksFixed += closedFixed;
                pending[closedPath] = closed with { Text = rewrittenClosed };
            }
        }

        // Every other doc that links to the old path (and CLAUDE.md).
        var candidates = new List<string>();
        var docs = EnumerateDocs(root, out _);
        if (docs is not null)
        {
            candidates.AddRange(docs
                .Where(d => !d.RelPath.Split('/')[..^1].Any(f => string.Equals(f, "upstream", StringComparison.OrdinalIgnoreCase)))
                .Select(d => Path.GetFullPath(d.RelPath, root)));
        }

        var claudeMd = Path.Combine(root, "CLAUDE.md");
        if (File.Exists(claudeMd))
        {
            candidates.Add(claudeMd);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal))
        {
            if (SamePath(candidate, oldFull) || SamePath(candidate, todoPath) || SamePath(candidate, closedPath))
            {
                continue;
            }

            var file = TryLoad(candidate, out var loadProblem);
            if (file is null)
            {
                warnings.Add($"Link fixing skipped {RelOf(root, candidate)} ({loadProblem}); check it for links to {from}.");
                continue;
            }

            if (!file.Text.Contains(parts[3], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(candidate)!;
            var (rewrittenText, fixedHere) = RewriteLinks(file.Text, new LinkMap(dir, dir, oldFull, newFull, false));
            if (fixedHere > 0)
            {
                linksFixed += fixedHere;
                pending[candidate] = file with { Text = rewrittenText };
            }
        }

        // Writes: the move first (a failure there leaves everything untouched); CLOSED.md before TODO.md so a failure duplicates rather than loses the entry.
        var done = new List<string>();
        var step = "moving the doc";
        try
        {
            Directory.CreateDirectory(newDir);
            if (File.Exists(newFull))
            {
                return CloseFail(id, $"The destination {to} appeared while closing. Nothing was changed; retry.", warnings, from, to);
            }

            File.Move(oldFull, newFull);
            done.Add($"moved {from} to {to}");
            step = $"updating Status and links in {to}";
            WriteText(newFull, docText);
            done.Add($"updated Status and links in {to}");
            foreach (var path in pending.Keys.OrderBy(k => SamePath(k, todoPath) ? 2 : SamePath(k, closedPath) ? 1 : 0).ThenBy(k => k, StringComparer.Ordinal))
            {
                var rel = RelOf(root, path);
                step = $"writing {rel}";
                WriteText(path, pending[path].Text);
                done.Add($"wrote {rel}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var completed = done.Count == 0 ? "nothing" : string.Join("; ", done);
            return CloseFail(id, $"Closing stopped while {step} ({ex.GetType().Name}). Completed: {completed}. The remaining steps were not done; finish them by hand or fix the cause and redo them.", warnings, from, to);
        }

        return new DocCloseResult
        {
            Id = id,
            FromPath = from,
            ToPath = to,
            StatusLine = statusLineText,
            ClosesCommits = closesCommits,
            TodoEntryMoved = moveTodo,
            ClosedMdUpdated = canRecord,
            LinksFixed = linksFixed,
            Warnings = warnings,
        };
    }

    private static bool IsValidId(string id) => IdPattern.IsMatch(id) && id.Any(c => c != '.');

    /// <summary>Splits text into lines, keeping each line's own terminator ("\r\n", "\n", "\r" or "" for an unterminated last line) so joining restores the text exactly.</summary>
    private static List<(string Content, string Eol)> SplitLines(string text)
    {
        var lines = new List<(string Content, string Eol)>();
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
            {
                lines.Add((text[start..], ""));
                break;
            }

            var eol = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? "\r\n" : text[end].ToString();
            lines.Add((text[start..end], eol));
            start = end + eol.Length;
        }

        return lines;
    }

    private static string JoinLines(IEnumerable<(string Content, string Eol)> lines) => string.Concat(lines.Select(l => l.Content + l.Eol));

    // Same rule as Close-TodoItem.ps1: CRLF when the file contains one anywhere, else LF.
    private static string DetectNewline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static bool IsFenceLine(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static string RelOf(string root, string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first "# Title" heading outside code fences, or null.</summary>
    private static string? FirstHeading(string text)
    {
        var inFence = false;
        foreach (var (content, _) in SplitLines(text))
        {
            if (IsFenceLine(content))
            {
                inFence = !inFence;
            }
            else if (!inFence && Heading1.Match(content) is { Success: true } m)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites every relative markdown link that <paramref name="map"/> re-points, skipping fenced code blocks, and
    /// returns the new text with the number of links rewritten. Line terminators are untouched.
    /// </summary>
    private static (string Text, int Fixed) RewriteLinks(string text, LinkMap map)
    {
        // TODO reference-style link definitions ("[label]: target") and angle-bracket targets are not rewritten.
        // Parked: none exist in the repo; scripts/Test-GuidanceHygiene.ps1 reports them as LINK-FORM, which is the
        // trigger to implement this.
        if (!text.Contains("](", StringComparison.Ordinal))
        {
            return (text, 0);
        }

        var sb = new StringBuilder(text.Length + 64);
        var fixedCount = 0;
        var inFence = false;
        foreach (var (content, eol) in SplitLines(text))
        {
            if (IsFenceLine(content))
            {
                inFence = !inFence;
                sb.Append(content).Append(eol);
                continue;
            }

            if (inFence || !content.Contains("](", StringComparison.Ordinal))
            {
                sb.Append(content).Append(eol);
                continue;
            }

            sb.Append(LinkPattern.Replace(content, m =>
            {
                var target = m.Groups["target"];
                var mapped = map.Remap(target.Value);
                if (mapped is null || string.Equals(mapped, target.Value, StringComparison.Ordinal))
                {
                    return m.Value;
                }

                fixedCount++;
                var offset = target.Index - m.Index;
                return m.Value[..offset] + mapped + m.Value[(offset + target.Length)..];
            })).Append(eol);
        }

        return (sb.ToString(), fixedCount);
    }

    /// <summary>Reads a file as strict UTF-8 (BOM noted, not kept in the text). Returns null with a short <paramref name="problem"/> on IO or decoding failure.</summary>
    private static LoadedFile? TryLoad(string fullPath, out string? problem)
    {
        problem = null;
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip = hasBom ? 3 : 0;
            return new LoadedFile(fullPath, StrictUtf8.GetString(bytes, skip, bytes.Length - skip), hasBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            problem = ex.GetType().Name;
            return null;
        }
    }

    /// <summary>Like <see cref="TryLoad"/> for a file the close can do without: a missing or unreadable file becomes a warning.</summary>
    private static LoadedFile? LoadOptional(string fullPath, string relPath, List<string> warnings)
    {
        if (!File.Exists(fullPath))
        {
            warnings.Add($"{relPath} was not found, so its part of the close was skipped.");
            return null;
        }

        var file = TryLoad(fullPath, out var problem);
        if (file is null)
        {
            warnings.Add($"{relPath} could not be read ({problem}), so its part of the close was skipped.");
        }

        return file;
    }

    /// <summary>
    /// Writes UTF-8 through <see cref="EolFileWriter"/>, which keeps a BOM already on disk and the file's own line ending (the pre-image is the
    /// text on disk before this write). Throws IO exceptions; the caller reports them.
    /// </summary>
    private static void WriteText(string fullPath, string text)
    {
        var preImage = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
        // The engine API is synchronous and runs without a SynchronizationContext, so blocking on the async writer cannot deadlock.
        var refusal = EolFileWriter.WriteAllTextResolvedAsync(fullPath, text, preImage).GetAwaiter().GetResult();
        if (refusal is not null)
        {
            throw new IOException(refusal);
        }
    }

    private static DocCloseResult CloseFail(string id, string error, List<string> warnings, string? from = null, string? to = null) => new()
    {
        Error = error,
        Id = id,
        FromPath = from,
        ToPath = to,
        Warnings = warnings,
    };

    private static string InvalidIdMessage() =>
        "A doc ID is a file name without extension: letters, digits, '_', '.' and '-' only, no path separators or spaces (for example 'plan_foo'). "
        + ToolCallHint.Next("ProjectDoc", "to list the docs and trailer IDs in use.", ("action", "trailers"));

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
/// <summary>
/// Result of <see cref="DocTrailerEngine.CloseDoc"/>. When <see cref="Error"/> is set nothing was written, unless the
/// message says which steps completed (a write failed midway). Paths are repo-relative with '/'.
/// </summary>
public sealed record DocCloseResult
{
    /// <summary>Actionable failure text; null on success.</summary>
    public string? Error
    {
        get; init;
    }

    public string Id { get; init; } = "";

    public string? FromPath
    {
        get; init;
    }

    public string? ToPath
    {
        get; init;
    }

    /// <summary>The Status line now in the moved doc.</summary>
    public string? StatusLine
    {
        get; init;
    }

    /// <summary>8-char hashes of commits whose <c>Closes:</c> trailer names the doc (empty when none or git was unavailable; see <see cref="Warnings"/>).</summary>
    public IReadOnlyList<string> ClosesCommits { get; init; } = [];

    /// <summary>True when the doc's TODO.md line was removed and used as the CLOSED.md body.</summary>
    public bool TodoEntryMoved
    {
        get; init;
    }

    public bool ClosedMdUpdated
    {
        get; init;
    }

    /// <summary>Number of relative links rewritten across the moved doc, other docs, CLAUDE.md and the CLOSED.md body.</summary>
    public int LinksFixed
    {
        get; init;
    }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
