using System.ComponentModel;
using System.Text;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Tools.Basic;

// ─── Result types ────────────────────────────────────────────────────────────

public class DocReadResult : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null || !Found;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => !Found && Error is null ? ResultStatus.NotFound : ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => !IsError ? null : (ErrorCode ?? (!Found && Error is null ? ToolErrorCode.NotFound : ToolErrorCode.Exception));

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ErrorCode { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error ?? (Found ? null : "Document not found");

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

public class DocWriteResult : IOperationResult
{
    public bool IsError
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

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ErrorCode ?? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ErrorCode { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;
}

public class DocListResult : IOperationResult
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public ResultStatus Status => ResultStatusExtensions.FromFlag(IsError, Code);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Code => IsError ? ErrorCode ?? ToolErrorCode.Exception : null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ErrorCode { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Message => Error;

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
public class DocumentationTools
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelHostOptions _hostOptions;
    private readonly ILogger<DocumentationTools> _logger;

    private const int MaxDocBytes = 512 * 1024;   // 512 KB

    public DocumentationTools(
        IWorkspaceManager workspaceManager,
        SentinelHostOptions hostOptions,
        ILogger<DocumentationTools> logger)
    {
        _workspaceManager = workspaceManager;
        _hostOptions = hostOptions;
        _logger = logger;
    }

    private bool IsTestingMode => _hostOptions.OperatingMode == OperatingMode.Testing;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the docs root and returns it, or populates <paramref name="error"/> and returns null.
    /// Under <see cref="OperatingMode.Testing"/> this is <c>docs/current/evals/</c>, not <c>docs/</c>.
    /// </summary>
    private string? TryGetDocsRoot(out string error)
    {
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is null)
        {
            error = $"ProjectDoc resolves docs/ paths relative to the loaded solution, but no solution is loaded. Call {ToolCallHint.Call("LoadSolution")} first.";
            return null;
        }

        error = "";

        // Testing mode resolves only inside docs/current/evals/ -- never the docType subdirectories or
        // the docs/-wide fallback -- so an eval fixture can carry the same filename as the production
        // doc it mirrors without either resolving to the other -> the collision that made run
        // 20260910-013550-398 execute a different plan than the one it asked for.
        return IsTestingMode
            ? Path.Combine(solutionRoot, "docs", "current", "evals")
            : Path.Combine(solutionRoot, "docs");
    }

    /// <summary>
    /// Resolves the root that per-docType subdirectories (plans/handoffs/completed/documentation)
    /// live under. Some repos archive active docs under docs/current/ (with a matching docs/obsolete/
    /// for retired ones) instead of directly under docs/ -> if docs/current/ exists, root subdirs
    /// there so ProjectDocTool(read/write) can reach what ProjectDocTool(list) already reports.
    /// </summary>
    private static string GetDocTypeSubdirRoot(string docsRoot)
    {
        var currentDir = Path.Combine(docsRoot, "current");
        return Directory.Exists(currentDir) ? currentDir : docsRoot;
    }

    /// <summary>
    /// True when <paramref name="filename"/> names a directory as well as a file -> i.e. the caller
    /// stated <em>where</em> the file is, not just what it's called. Basename fallback must not
    /// override such a request: it discards the directory (see <see cref="FindByBasename"/>), so a
    /// same-named file elsewhere in the tree would be substituted silently. Run
    /// 20260910-013550-398 executed an entirely different plan for 60 turns this way.
    /// </summary>
    private static bool IsPathQualified(string filename) =>
        filename.Contains('/') || filename.Contains('\\');

    /// <param name="subdir">The docType's own subdirectory, e.g. docs/current/plans/.</param>
    /// <param name="docTypeSubdirRoot">The directory those subdirs sit in -> docs/current/ when it
    /// exists, else docs/. Distinct from <paramref name="docsRoot"/>, and the base that action:list
    /// paths for a docs/current/ layout are most naturally written against.</param>
    /// <param name="docsRoot">docs/ (or docs/current/evals/ in testing mode).</param>
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
        // substitute a same-named file from elsewhere in the tree -> which is exactly how run
        // 20260910-013550-398 spent 60 turns implementing a plan it never asked for.
        if (ok && IsPathQualified(filename))
        {
            return new DocReadResult
            {
                Found = false,
                Filename = filename,
                ErrorCode = ToolErrorCode.NotFound,
                Error = ignoreDocType
                    ? $"'{filename}' was not found under the testing doc root. Names containing a directory separator are treated as explicit relative paths, so no basename fallback was attempted. " + DocMissFacts(filename, docType, docsRoot, null, TestingDocsLabel)
                    : $"'{filename}' was not found under docType='{docType}', nor at that path relative to the docs root. Names containing a directory separator are treated as explicit relative paths, so no basename fallback was attempted, and a same-named file in a different directory is not substituted. " + DocMissFacts(filename, docType, docsRoot, subdir, ProductionDocsLabel)
            };
        }

        // Fallback 1: match by basename (extension-insensitive) anywhere under the primary scope.
        // Handles a bare name or a wrong/missing extension -> both observed model behaviors when
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
                ErrorCode = ToolErrorCode.Ambiguous,
                Error = $"'{filename}' is ambiguous. Did you mean: {string.Join(", ", matches)}"
            };
        }

        // Fallback 2 (bare names only -> path-qualified requests already returned above): the
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
                    ErrorCode = ToolErrorCode.Ambiguous,
                    Error = $"'{filename}' is ambiguous. Did you mean: {string.Join(", ", rootMatches)}"
                };
            }
        }

        string stem = Path.GetFileNameWithoutExtension(Path.GetFileName(filename));
        var notFoundError = ok
            ? ignoreDocType
                ? $"No file matching '{stem}' (with or without extension) was found anywhere under the testing doc root. " + DocMissFacts(filename, docType, docsRoot, null, TestingDocsLabel)
                : $"No file matching '{stem}' (with or without extension) was found under docType='{docType}', or anywhere else under docs/. " + DocMissFacts(filename, docType, docsRoot, subdir, ProductionDocsLabel)
            : guardError;
        return new DocReadResult { Found = false, Filename = filename, ErrorCode = ok ? ToolErrorCode.NotFound : ToolErrorCode.InvalidArgument, Error = notFoundError };
    }

    private const string ProductionDocsLabel = "docs/";
    private const string TestingDocsLabel = "docs/current/evals/";

    /// <summary>
    /// The facts and options that close a ProjectDoc read miss: ranked similar doc names (the docType's own files first, so ties
    /// favour them) with a prefilled read retry, or the no-similar-name fact with how many files were searched, then the list
    /// option. <paramref name="retryDocType"/> goes into the prefilled retry; any docType resolves a path relative to the docs root.
    /// </summary>
    private static string DocMissFacts(string requested, DocType retryDocType, string docsRoot, string? docTypeSubdir, string docsLabel)
    {
        var docs = Directory.Exists(docsRoot)
            ? Directory.GetFiles(docsRoot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(docsRoot, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList()
            : [];
        if (docTypeSubdir is not null && Directory.Exists(docTypeSubdir))
        {
            var prefix = Path.GetRelativePath(docsRoot, docTypeSubdir).Replace('\\', '/') + "/";
            docs = docs.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Concat(docs.Where(d => !d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(requested));
        var candidates = NameCandidates.Rank(stem, docs, d => Path.GetFileNameWithoutExtension(d));
        var parts = new List<string>();
        if (candidates.Count == 0)
        {
            parts.Add($"No file under {docsLabel} has a name similar to '{stem}' ({docs.Count} file(s) searched).");
        }

        foreach (var candidate in candidates)
        {
            var retry = ToolCallHint.Call("ProjectDoc", ("action", "read"), ("docType", retryDocType.ToString()), ("name", candidate.Item));
            parts.Add($"Did you mean '{candidate.Item}'? Retry with {retry}.");
        }

        parts.Add($"{ToolCallHint.Call("ProjectDoc", ("action", "list"))} lists every file under {docsLabel}.");
        return string.Join(" ", parts);
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
            return new DocWriteResult { IsError = true, Filename = filename, ErrorCode = ToolErrorCode.InvalidArgument, Error = guardError };
        }

        int bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxDocBytes)
        {
            return new DocWriteResult
            {
                IsError = true,
                Filename = filename,
                ErrorCode = ToolErrorCode.InvalidArgument,
                Error = $"Content exceeds {MaxDocBytes} bytes. Documentation files should be concise."
            };
        }

        Directory.CreateDirectory(subdir);

        // Sync MCP tool signature is kept; the payload is capped by MaxDocBytes, so blocking on the async writer is cheap.
        string? existing = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
        string text = append ? (existing ?? string.Empty) + content : content;
        string? refusal = EolFileWriter.WriteAllTextResolvedAsync(fullPath, text, existing).GetAwaiter().GetResult();
        if (refusal is not null)
        {
            return new DocWriteResult { IsError = true, Filename = filename, ErrorCode = ToolErrorCode.EolChangeRefused, Error = refusal };
        }

        return new DocWriteResult
        {
            IsError = false,
            Filename = filename,
            FullPath = fullPath,
            BytesWritten = bytes
        };
    }
    // Legacy bare-object implementation behind the ProjectDoc tool; ProjectDocTool wraps it in SentinelCallToolResult.
    public object ProjectDocCore(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("read, write, append (completed_work only), list, trailers (docs with Plan:/Closes: commits, unknown IDs), validate (name = doc ID), close (name = doc ID, content = Status text: sets Status, moves to closed/, CLOSED.md entry, fixes links).")]
        DocAction action,
        [Description("Doc category: plan->plans/, handoff->handoffs/, completed_work->completed/ (append-only), documentation->documentation/, state->migration-state.yaml (name ignored). Ignored for trailers/validate/close.")]
        DocType docType,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: name is required for every action except when
        // docType=state (which uses a fixed filename) or action=list.
        [Description("File name or nested relative path, as shown by action=list; doc ID for validate/close. Required except for docType=state.")]
        string? name = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: content is required when action=write or action=append,
        // not used otherwise.
        [Description("Required for action=write/append.")]
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
                    ? new DocReadResult { Found = false, Filename = name ?? "", ErrorCode = ToolErrorCode.RateLimited, Error = rateLimitError }
                    : new DocWriteResult { IsError = true, Filename = name ?? "", ErrorCode = ToolErrorCode.RateLimited, Error = rateLimitError };
            }

            var docsRoot = TryGetDocsRoot(out var error);
            if (docsRoot is null)
            {
                return action == DocAction.read || action == DocAction.list
                    ? new DocReadResult { Found = false, Filename = name ?? "", ErrorCode = ToolErrorCode.SolutionNotLoaded, Error = error }
                    : new DocWriteResult { IsError = true, Filename = name ?? "", ErrorCode = ToolErrorCode.SolutionNotLoaded, Error = error };
            }

            // trailers / validate / close: reconcile docs with Plan:/Closes: commit trailers (DocTrailerEngine).
            if (action is DocAction.trailers or DocAction.validate or DocAction.close)
            {
                if (IsTestingMode)
                {
                    return new DocWriteResult { IsError = true, Filename = name ?? "", ErrorCode = ToolErrorCode.InvalidArgument, Error = $"action={action} is not available in testing mode." };
                }

                var repoRoot = _workspaceManager.GetSolutionRoot()!;
                var timeout = TimeSpan.FromSeconds(20);
                if (action == DocAction.trailers)
                {
                    return DocTrailerEngine.ScanTrailers(repoRoot, timeout);
                }

                if (name is null)
                {
                    return new DocWriteResult { IsError = true, Filename = "", ErrorCode = ToolErrorCode.InvalidArgument, Error = "name is required (doc ID, the file name without extension)." };
                }

                if (action == DocAction.validate)
                {
                    return DocTrailerEngine.ValidateId(repoRoot, name, timeout);
                }

                if (content is null)
                {
                    return new DocWriteResult { IsError = true, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = "content is required for action=close (the new Status text)." };
                }

                return DocTrailerEngine.CloseDoc(repoRoot, name, content, timeout);
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
                        // docType state ignores name, so a similar file elsewhere under docs/ is offered as a documentation read.
                        var stateLabel = IsTestingMode ? TestingDocsLabel : ProductionDocsLabel;
                        return new DocReadResult
                        {
                            Found = false,
                            Filename = "migration-state.yaml",
                            ErrorCode = ToolErrorCode.NotFound,
                            Error = $"'migration-state.yaml' (docType state) does not exist at {stateLabel}migration-state.yaml; the path is fixed and name is ignored for docType state. " + DocMissFacts("migration-state.yaml", DocType.documentation, docsRoot, null, stateLabel)
                        };
                    }

                    return new DocReadResult { Found = true, Filename = "migration-state.yaml", Content = File.ReadAllText(fullPath) };
                }
                if (action == DocAction.write)
                {
                    if (content is null)
                    {
                        return new DocWriteResult { IsError = true, Filename = "migration-state.yaml", ErrorCode = ToolErrorCode.InvalidArgument, Error = "content is required for action=write." };
                    }

                    int bytes = System.Text.Encoding.UTF8.GetByteCount(content);
                    if (bytes > MaxDocBytes)
                    {
                        return new DocWriteResult { IsError = true, Filename = "migration-state.yaml", ErrorCode = ToolErrorCode.InvalidArgument, Error = $"Content exceeds {MaxDocBytes} bytes." };
                    }

                    var stateDir = Path.Combine(docsRoot);
                    var statePath = Path.Combine(stateDir, "migration-state.yaml");
                    Directory.CreateDirectory(stateDir);
                    string? stateExisting = File.Exists(statePath) ? File.ReadAllText(statePath) : null;
                    // Sync MCP tool signature is kept; the payload is capped by MaxDocBytes, so blocking on the async writer is cheap.
                    string? stateRefusal = EolFileWriter.WriteAllTextResolvedAsync(statePath, content, stateExisting, cancellationToken).GetAwaiter().GetResult();
                    if (stateRefusal is not null)
                    {
                        return new DocWriteResult { IsError = true, Filename = "migration-state.yaml", ErrorCode = ToolErrorCode.EolChangeRefused, Error = stateRefusal };
                    }
                    return new DocWriteResult { IsError = false, Filename = "migration-state.yaml", FullPath = statePath, BytesWritten = bytes };
                }
                return new DocWriteResult { IsError = true, Filename = "migration-state.yaml", ErrorCode = ToolErrorCode.InvalidArgument, Error = $"action='{action}' is not valid for docType=state. Valid: read, write." };
            }

            // ── file-based doc types ───────────────────────────────────────────────
            if (name is null)
            {
                return action == DocAction.read
                    ? (object)new DocReadResult { Found = false, Filename = "", ErrorCode = ToolErrorCode.InvalidArgument, Error = "name is required for file-based operations." }
                    : new DocWriteResult { IsError = true, Filename = "", ErrorCode = ToolErrorCode.InvalidArgument, Error = "name is required for file-based operations." };
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
                    ? (object)new DocReadResult { Found = false, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = $"Unhandled docType '{docType}'." }
                    : new DocWriteResult { IsError = true, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = $"Unhandled docType '{docType}'." };
            }

            if (action == DocAction.write && content is null)
            {
                return new DocWriteResult { IsError = true, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = "content is required for action=write." };
            }

            if (action == DocAction.append && content is null)
            {
                return new DocWriteResult { IsError = true, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = "content is required for action=append." };
            }

            return action switch
            {
                DocAction.read => (object)ReadFile(subdir, name, docType, docTypeSubdirRoot, docsRoot, ignoreDocType: IsTestingMode),
                DocAction.write => WriteFile(subdir, name, content!),
                DocAction.append => docType == DocType.completed_work
                    ? WriteFile(subdir, name, content!, append: true)
                    : (object)new DocWriteResult { IsError = true, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = "action=append is only valid for docType=completed_work." },
                _ => (object)new DocWriteResult { IsError = true, Filename = name, ErrorCode = ToolErrorCode.InvalidArgument, Error = $"Unhandled action '{action}'." }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProjectDoc ({Action}/{DocType}) failed", action, docType);
            return new DocWriteResult { IsError = true, Filename = name ?? "", Error = $"ProjectDoc failed unexpectedly ({ex.GetType().Name}). Check the server log for details." };
        }
    }

    [McpServerTool(Name = "ProjectDoc")]
    [Produces(DataTag.Documentation)]
    [Description("Reads, writes, appends, lists project doc files under docs/; trailers/validate/close reconcile docs with Plan:/Closes: commit trailers.")]
    public SentinelCallToolResult<object> ProjectDocTool(
    [Description(ToolParams.Reason)] ToolCallReason reason,
    [Description("read, write, append (completed_work only), list, trailers (docs with Plan:/Closes: commits, unknown IDs), validate (name = doc ID), close (name = doc ID, content = Status text: sets Status, moves to closed/, CLOSED.md entry, fixes links).")]
        DocAction action,
    [Description("Doc category: plan->plans/, handoff->handoffs/, completed_work->completed/ (append-only), documentation->documentation/, state->migration-state.yaml (name ignored). Ignored for trailers/validate/close.")]
        DocType docType,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: name is required for every action except when
    // docType=state (which uses a fixed filename) or action=list.
    [Description("File name or nested relative path, as shown by action=list; doc ID for validate/close. Required except for docType=state.")]
        string? name = null,
    // CONDITIONAL-PARAM-REVIEW-REQUIRED: content is required when action=write or action=append,
    // not used otherwise.
    [Description("Required for action=write/append.")]
        string? content = null,
    // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        var core = ProjectDocCore(reason, action, docType, name, content, cancellationToken);
        return ToToolResult(core, name, docType);
    }

    /// <summary>
    /// Folds the legacy bare-object result of <see cref="ProjectDocCore"/> into the envelope: the DTO becomes
    /// SuccessData, and any failure uses ErrorData (code and message come from the DTO's own Code/Message).
    /// </summary>
    private static SentinelCallToolResult<object> ToToolResult(object core, string? name, DocType docType)
    {
        switch (core)
        {
            case DocReadResult r when r.IsError:
                var readMessage = r.Error is null && !r.Found
                    ? $"'{name ?? r.Filename}' (docType {docType}) matched no document. {ToolCallHint.Call("ProjectDoc", ("action", "list"))} lists the available files."
                    : r.Message;
                return ToError(r.Code, readMessage);
            case DocReadResult r:
                return ToSuccess(r);
            case DocWriteResult w when w.IsError:
                return ToError(w.Code, w.Message);
            case DocWriteResult w:
                return ToSuccess(w);
            case DocListResult l when l.IsError:
                return ToError(l.Code, l.Message);
            case DocListResult l:
                return ToSuccess(l);
            case DocTrailerScanResult t when t.Error is not null:
                return ToError(ToolErrorCode.InvalidArgument, t.Error);
            case DocTrailerScanResult t:
                return ToSuccess(t);
            case DocTrailerIdResult t when t.Error is not null:
                return ToError(ToolErrorCode.InvalidArgument, t.Error);
            case DocTrailerIdResult t:
                return ToSuccess(t);
            case DocCloseResult c when c.Error is not null:
                return ToError(ToolErrorCode.InvalidArgument, c.Error);
            case DocCloseResult c:
                return ToSuccess(c);
            default:
                return ToError(ToolErrorCode.Exception, "ProjectDoc returned an unrecognized result.");
        }

        static SentinelCallToolResult<object> ToSuccess(object data)
            => new()
            {
                IsError = false,
                SuccessData = data
            };

        static SentinelCallToolResult<object> ToError(string? code, string? message)
            => new()
            {
                IsError = true,
                ErrorData = new ResultError(code ?? ToolErrorCode.Exception, message ?? "ProjectDoc failed.")
            };
    }
}
