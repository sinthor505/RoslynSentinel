using System.ComponentModel;
using System.Linq;
using System.Text;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// ─── Result types ────────────────────────────────────────────────────────────

public class DocReadResult
{
    public bool Found
    {
        get; set;
    }
    public string Filename { get; set; } = "";
    public string? Content
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
    /// <summary>
    /// Set when basename fallback resolved the request to a path other than the one asked for.
    /// Without this the substitution is invisible: Found=true and only Filename differs, which
    /// once caused a whole agent run to execute the wrong plan (run 20260910-013550-398).
    /// </summary>
    public string? Warning
    {
        get; set;
    }
}

public class DocWriteResult
{
    public bool Success
    {
        get; set;
    }
    public string Filename { get; set; } = "";
    public string FullPath { get; set; } = "";
    public int BytesWritten
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
}

public class DocListResult
{
    public List<string> Files { get; set; } = [];
    public int Count
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
}

// ─── Tool class ──────────────────────────────────────────────────────────────

[McpServerToolType]
public class SentinelDocumentationTools
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelHostOptions _hostOptions;
    private readonly ILogger<SentinelDocumentationTools> _logger;

    private const int MaxDocBytes = 512 * 1024;   // 512 KB

    public SentinelDocumentationTools(
        IWorkspaceManager workspaceManager,
        SentinelHostOptions hostOptions,
        ILogger<SentinelDocumentationTools> logger)
    {
        _workspaceManager = workspaceManager;
        _hostOptions = hostOptions;
        _logger = logger;
    }

    private bool IsTestingMode => _hostOptions.OperatingMode == OperatingMode.Testing;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the docs root and returns it, or populates <paramref name="error"/> and returns null.
    /// Under <see cref="OperatingMode.Testing"/> this is <c>docs/testing/</c>, not <c>docs/</c>.
    /// </summary>
    private string? TryGetDocsRoot(out string error)
    {
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is null)
        {
            error = "No solution path configured. Call load_solution first.";
            return null;
        }

        error = "";

        // docs/testing/ is kept entirely separate from docs/ (rather than being a subdirectory
        // reachable from it) so an eval fixture can carry the same filename as the production doc
        // it mirrors without either resolving to the other — the collision that made run
        // 20260910-013550-398 execute a different plan than the one it asked for.
        return IsTestingMode
            ? Path.Combine(solutionRoot, "docs", "testing")
            : Path.Combine(solutionRoot, "docs");
    }

    /// <summary>
    /// Resolves the root that per-docType subdirectories (plans/handoffs/completed/documentation)
    /// live under. Some repos archive active docs under docs/current/ (with a matching docs/obsolete/
    /// for retired ones) instead of directly under docs/ — if docs/current/ exists, root subdirs
    /// there so ProjectDoc(read/write) can reach what ProjectDoc(list) already reports.
    /// </summary>
    private static string GetDocTypeSubdirRoot(string docsRoot)
    {
        var currentDir = Path.Combine(docsRoot, "current");
        return Directory.Exists(currentDir) ? currentDir : docsRoot;
    }

    /// <summary>
    /// True when <paramref name="filename"/> names a directory as well as a file — i.e. the caller
    /// stated <em>where</em> the file is, not just what it's called. Basename fallback must not
    /// override such a request: it discards the directory (see <see cref="FindByBasename"/>), so a
    /// same-named file elsewhere in the tree would be substituted silently. Run
    /// 20260910-013550-398 executed an entirely different plan for 60 turns this way.
    /// </summary>
    private static bool IsPathQualified(string filename) =>
        filename.Contains('/') || filename.Contains('\\');

    /// <param name="subdir">The docType's own subdirectory, e.g. docs/current/plans/.</param>
    /// <param name="docTypeSubdirRoot">The directory those subdirs sit in — docs/current/ when it
    /// exists, else docs/. Distinct from <paramref name="docsRoot"/>, and the base that action:list
    /// paths for a docs/current/ layout are most naturally written against.</param>
    /// <param name="docsRoot">docs/ (or docs/testing/ in testing mode).</param>
    /// <param name="ignoreDocType">Testing mode: resolve against <paramref name="docsRoot"/> alone.</param>
    private static DocReadResult ReadFile(string subdir, string filename, DocType docType, string docTypeSubdirRoot, string docsRoot, bool ignoreDocType)
    {
        // Testing mode ignores docType entirely and resolves against the whole testing doc root:
        // fixtures don't need a docType-shaped layout, and a runner that guesses docType wrong
        // still finds its file. Ambiguity within that root still errors via the guards below.
        var primaryScope = ignoreDocType ? docsRoot : subdir;

        var (ok, fullPath, guardError) = DocPathGuard.ResolveSafe(primaryScope, filename);
        if (ok && File.Exists(fullPath))
        {
            return new DocReadResult
            {
                Found = true,
                Filename = filename,
                Content = File.ReadAllText(fullPath)
            };
        }

        // Exact path relative to the wider roots, before any basename search: a caller who supplied
        // a directory may simply have named one outside the docType subdirs (e.g. tests/… under
        // docs/current/, or under docs/ itself), which is a legitimate exact hit, not a miss.
        // Both bases are tried because a docs/current/ layout makes either spelling reasonable.
        if (!ignoreDocType)
        {
            foreach (var wideRoot in docTypeSubdirRoot == docsRoot
                         ? new[] { docsRoot }
                         : new[] { docTypeSubdirRoot, docsRoot })
            {
                var (wideOk, widePath, _) = DocPathGuard.ResolveSafe(wideRoot, filename);
                if (wideOk && File.Exists(widePath))
                {
                    return new DocReadResult
                    {
                        Found = true,
                        Filename = filename,
                        Content = File.ReadAllText(widePath)
                    };
                }
            }
        }

        // A path-qualified request that missed both exact locations is a hard miss. Falling back to
        // a basename search here would discard the directory the caller explicitly gave and could
        // substitute a same-named file from elsewhere in the tree — which is exactly how run
        // 20260910-013550-398 spent 60 turns implementing a plan it never asked for.
        if (ok && IsPathQualified(filename))
        {
            return new DocReadResult
            {
                Found = false,
                Filename = filename,
                Error = ignoreDocType
                    ? $"'{filename}' was not found under the testing doc root. Names containing a directory separator are treated as explicit relative paths, so no basename fallback was attempted. Call ProjectDoc(action: list) to see the exact available paths."
                    : $"'{filename}' was not found under docType='{docType}', nor at that path relative to the docs root. Names containing a directory separator are treated as explicit relative paths, so no basename fallback was attempted — a same-named file in a different directory is not a valid substitute. Call ProjectDoc(action: list) to see the exact available paths."
            };
        }

        // Fallback 1: match by basename (extension-insensitive) anywhere under the primary scope.
        // Handles a bare name or a wrong/missing extension — both observed model behaviors when
        // the exact relative path isn't already known.
        var matches = FindByBasename(primaryScope, filename);
        if (matches.Count == 1)
        {
            return new DocReadResult
            {
                Found = true,
                Filename = matches[0],
                Content = File.ReadAllText(Path.Combine(primaryScope, matches[0])),
                Warning = BuildFallbackWarning(filename, matches[0])
            };
        }

        if (matches.Count > 1)
        {
            return new DocReadResult
            {
                Found = false,
                Filename = filename,
                Error = $"'{filename}' is ambiguous. Did you mean: {string.Join(", ", matches)}"
            };
        }

        // Fallback 2 (bare names only — path-qualified requests already returned above): the
        // requested docType's subdirectory doesn't hold it, but action:list walks all of docs/, so
        // search that same full tree before giving up. Covers docs laid out outside the five known
        // docType subdirs. Skipped when ignoreDocType already made the full root the primary scope.
        if (!ignoreDocType)
        {
            var rootMatches = FindByBasename(docsRoot, filename);
            if (rootMatches.Count == 1)
            {
                return new DocReadResult
                {
                    Found = true,
                    Filename = rootMatches[0],
                    Content = File.ReadAllText(Path.Combine(docsRoot, rootMatches[0])),
                    Warning = BuildFallbackWarning(filename, rootMatches[0])
                };
            }

            if (rootMatches.Count > 1)
            {
                return new DocReadResult
                {
                    Found = false,
                    Filename = filename,
                    Error = $"'{filename}' is ambiguous. Did you mean: {string.Join(", ", rootMatches)}"
                };
            }
        }

        string stem = Path.GetFileNameWithoutExtension(Path.GetFileName(filename));
        var notFoundError = ok
            ? ignoreDocType
                ? $"No file matching '{stem}' (with or without extension) was found anywhere under the testing doc root. Call ProjectDoc(action: list) to see all available files."
                : $"No file matching '{stem}' (with or without extension) was found under docType='{docType}', or anywhere else under docs/. Call ProjectDoc(action: list) to see all available files."
            : guardError;
        return new DocReadResult { Found = false, Filename = filename, Error = notFoundError };
    }

    /// <summary>
    /// Returns a warning when a fallback resolved to a path other than the one requested, or null
    /// when the resolved path is what was asked for (a bare name that matched a file at the scope
    /// root, say). Making every substitution visible is the point: the run-398 failure was
    /// undetectable in the transcript precisely because a substituted read looked identical to a hit.
    /// </summary>
    private static string? BuildFallbackWarning(string requested, string resolved)
    {
        var normalizedRequest = requested.Replace('\\', '/');
        return string.Equals(normalizedRequest, resolved, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"Requested '{requested}' but resolved to '{resolved}' by basename fallback. Confirm this is the file you meant before acting on its contents.";
    }

    /// <summary>
    /// Finds files under <paramref name="subdir"/> whose filename matches <paramref name="name"/>
    /// on basename, ignoring extension and (if present in <paramref name="name"/>) subdirectory.
    /// Returns paths relative to <paramref name="subdir"/> with '/' separators.
    /// </summary>
    private static List<string> FindByBasename(string subdir, string name)
    {
        if (!Directory.Exists(subdir))
        {
            return [];
        }

        string wantStem = Path.GetFileNameWithoutExtension(Path.GetFileName(name));
        if (string.IsNullOrEmpty(wantStem))
        {
            return [];
        }

        return Directory.GetFiles(subdir, "*", SearchOption.AllDirectories)
            .Where(f => string.Equals(Path.GetFileNameWithoutExtension(f), wantStem, StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(subdir, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();
    }

    private static DocWriteResult WriteFile(string subdir, string filename, string content, bool append = false)
    {
        var (ok, fullPath, guardError) = DocPathGuard.ResolveSafe(subdir, filename);
        if (!ok)
        {
            return new DocWriteResult { Success = false, Filename = filename, Error = guardError };
        }

        int bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxDocBytes)
        {
            return new DocWriteResult
            {
                Success = false,
                Filename = filename,
                Error = $"Content exceeds {MaxDocBytes} bytes. Documentation files should be concise."
            };
        }

        Directory.CreateDirectory(subdir);

        if (append)
        {
            File.AppendAllText(fullPath, content);
        }
        else
        {
            File.WriteAllText(fullPath, content);
        }

        return new DocWriteResult
        {
            Success = true,
            Filename = filename,
            FullPath = fullPath,
            BytesWritten = bytes
        };
    }
    [McpServerTool(Name = "ProjectDoc")]
    [Produces(DataTag.Documentation)]
    [Description("Reads, writes, appends, or lists project doc files under docs/ (or docs/current/ if it exists). A bare filename or wrong extension falls back to a basename search; if that substitutes a different file than requested, the result's Warning field names both.")]
    public object ProjectDoc(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("read, write, append (completed_work only), or list.")]
        DocAction action,
        [Description("Which doc category to operate on: plan → plans/, handoff → handoffs/, completed_work → completed/ (append-only), documentation → documentation/, state → docs/migration-state.yaml (name is ignored).")]
        DocType docType,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: name is required for every action except when
        // docType=state (which uses a fixed filename) or action=list.
        [Description("File name or nested relative path (e.g. \"plan-x-steps/01-baseline.md\"), as shown by action=list. A path containing a directory separator is treated as explicit and never substituted. Required for all file-based operations except docType=state.")]
        string? name = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: content is required when action=write or action=append,
        // not used otherwise.
        [Description("File content. Required for action=write or action=append.")]
        string? content = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("project_doc", 30);
            if (rateLimitError is not null)
            {
                return action == DocAction.read || action == DocAction.list
                    ? (object)new DocReadResult { Found = false, Filename = name ?? "", Error = rateLimitError }
                    : new DocWriteResult { Success = false, Filename = name ?? "", Error = rateLimitError };
            }

            var docsRoot = TryGetDocsRoot(out var error);
            if (docsRoot is null)
            {
                return action == DocAction.read || action == DocAction.list
                    ? (object)new DocReadResult { Found = false, Filename = name ?? "", Error = error }
                    : new DocWriteResult { Success = false, Filename = name ?? "", Error = error };
            }

            // ── list ───────────────────────────────────────────────────────────────
            if (action == DocAction.list)
            {
                if (!Directory.Exists(docsRoot))
                {
                    return new DocListResult { Files = [], Count = 0 };
                }

                var files = Directory.GetFiles(docsRoot, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(docsRoot, f).Replace('\\', '/'))
                    .OrderBy(f => f)
                    .ToList();
                return new DocListResult { Files = files, Count = files.Count };
            }

            // ── state (special: fixed path, no filename) ─────────────────────────────
            if (docType == DocType.state)
            {
                if (action == DocAction.read)
                {
                    var fullPath = Path.Combine(docsRoot, "migration-state.yaml");
                    if (!File.Exists(fullPath))
                    {
                        return new DocReadResult { Found = false, Filename = "migration-state.yaml" };
                    }

                    return new DocReadResult { Found = true, Filename = "migration-state.yaml", Content = File.ReadAllText(fullPath) };
                }
                if (action == DocAction.write)
                {
                    if (content is null)
                    {
                        return new DocWriteResult { Success = false, Filename = "migration-state.yaml", Error = "content is required for action=write." };
                    }

                    int bytes = System.Text.Encoding.UTF8.GetByteCount(content);
                    if (bytes > MaxDocBytes)
                    {
                        return new DocWriteResult { Success = false, Filename = "migration-state.yaml", Error = $"Content exceeds {MaxDocBytes} bytes." };
                    }

                    var stateDir = Path.Combine(docsRoot);
                    var statePath = Path.Combine(stateDir, "migration-state.yaml");
                    Directory.CreateDirectory(stateDir);
                    File.WriteAllText(statePath, content);
                    return new DocWriteResult { Success = true, Filename = "migration-state.yaml", FullPath = statePath, BytesWritten = bytes };
                }
                return new DocWriteResult { Success = false, Filename = "migration-state.yaml", Error = $"action='{action}' is not valid for docType=state. Valid: read, write." };
            }

            // ── file-based doc types ───────────────────────────────────────────────
            if (name is null)
            {
                return action == DocAction.read
                    ? (object)new DocReadResult { Found = false, Filename = "", Error = "name is required for file-based operations." }
                    : new DocWriteResult { Success = false, Filename = "", Error = "name is required for file-based operations." };
            }

            var docTypeSubdirRoot = GetDocTypeSubdirRoot(docsRoot);
            var subdir = docType switch
            {
                DocType.plan => Path.Combine(docTypeSubdirRoot, "plans"),
                DocType.handoff => Path.Combine(docTypeSubdirRoot, "handoffs"),
                DocType.completed_work => Path.Combine(docTypeSubdirRoot, "completed"),
                DocType.documentation => Path.Combine(docTypeSubdirRoot, "documentation"),
                _ => null
            };

            if (subdir is null)
            {
                return action == DocAction.read
                    ? (object)new DocReadResult { Found = false, Filename = name, Error = $"Unhandled docType '{docType}'." }
                    : new DocWriteResult { Success = false, Filename = name, Error = $"Unhandled docType '{docType}'." };
            }

            if (action == DocAction.write && content is null)
            {
                return new DocWriteResult { Success = false, Filename = name, Error = "content is required for action=write." };
            }

            if (action == DocAction.append && content is null)
            {
                return new DocWriteResult { Success = false, Filename = name, Error = "content is required for action=append." };
            }

            return action switch
            {
                DocAction.read => (object)ReadFile(subdir, name, docType, docTypeSubdirRoot, docsRoot, ignoreDocType: IsTestingMode),
                DocAction.write => WriteFile(subdir, name, content!),
                DocAction.append => docType == DocType.completed_work
                    ? WriteFile(subdir, name, content!, append: true)
                    : (object)new DocWriteResult { Success = false, Filename = name, Error = "action=append is only valid for docType=completed_work." },
                _ => (object)new DocWriteResult { Success = false, Filename = name, Error = $"Unhandled action '{action}'." }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProjectDoc ({Action}/{DocType}) failed", action, docType);
            return new DocWriteResult { Success = false, Filename = name ?? "", Error = $"ProjectDoc failed: {ex.GetType().Name}: {ex.Message}" };
        }
    }
}
