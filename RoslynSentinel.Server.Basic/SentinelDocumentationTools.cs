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
    private readonly ILogger<SentinelDocumentationTools> _logger;

    private const int MaxDocBytes = 512 * 1024;   // 512 KB

    public SentinelDocumentationTools(
        IWorkspaceManager workspaceManager,
        ILogger<SentinelDocumentationTools> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the docs root and returns it, or populates <paramref name="error"/> and returns null.
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
        return Path.Combine(solutionRoot, "docs");
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

    private static DocReadResult ReadFile(string subdir, string filename, DocType docType, string docsRoot)
    {
        var (ok, fullPath, guardError) = DocPathGuard.ResolveSafe(subdir, filename);
        if (ok && File.Exists(fullPath))
        {
            return new DocReadResult
            {
                Found = true,
                Filename = filename,
                Content = File.ReadAllText(fullPath)
            };
        }

        // Fallback 1: match by basename (extension-insensitive) anywhere under subdir.
        // Handles a bare name, a wrong/missing extension, or an un-guessed subfolder —
        // all observed model behaviors when the exact relative path isn't already known.
        var matches = FindByBasename(subdir, filename);
        if (matches.Count == 1)
        {
            return new DocReadResult
            {
                Found = true,
                Filename = matches[0],
                Content = File.ReadAllText(Path.Combine(subdir, matches[0]))
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

        // Fallback 2: the requested docType's subdirectory doesn't hold it, but action:list
        // walks all of docs/ — so search that same full tree before giving up. Covers docs
        // laid out outside the five known docType subdirs (e.g. docs/tests/...).
        var (rootOk, rootFullPath, _) = DocPathGuard.ResolveSafe(docsRoot, filename);
        if (rootOk && File.Exists(rootFullPath))
        {
            return new DocReadResult
            {
                Found = true,
                Filename = filename,
                Content = File.ReadAllText(rootFullPath)
            };
        }

        var rootMatches = FindByBasename(docsRoot, filename);
        if (rootMatches.Count == 1)
        {
            return new DocReadResult
            {
                Found = true,
                Filename = rootMatches[0],
                Content = File.ReadAllText(Path.Combine(docsRoot, rootMatches[0]))
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

        string stem = Path.GetFileNameWithoutExtension(Path.GetFileName(filename));
        var notFoundError = ok
            ? $"No file matching '{stem}' (with or without extension) was found under docType='{docType}', or anywhere else under docs/. Call ProjectDoc(action: list) to see all available files."
            : guardError;
        return new DocReadResult { Found = false, Filename = filename, Error = notFoundError };
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

    // ── project_doc ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "ProjectDoc")]
    [Produces(DataTag.Documentation)]
    [Description("Unified accessor for project doc files under docs/ (or docs/current/ if that subdirectory exists). plan → .../plans/; handoff → .../handoffs/; completed_work → .../completed/ (append only); documentation → .../documentation/; state → docs/migration-state.yaml (name ignored, always directly under docs/). name required for all file-based operations, accepts a nested relative path (e.g. 'plan-x-steps/01-baseline.md') as shown by action:list; on read, a bare/wrong-extension name also falls back to a basename search, and if the docType's own subdirectory has no match, falls back further to searching all of docs/ (the same tree action:list walks) — so any file action:list can show, read can load regardless of docType. Auto-resolves if exactly one file matches. content required for write/append.")]
    public object ProjectDoc(
        [Description(ToolParams.Reason)] string reason,
        DocAction action,
        DocType docType,
        string? name = null,
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

            // ── list ─────────────────────────────────────────────────────────────
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

            // ── state (special: fixed path, no filename) ─────────────────────────
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

            // ── file-based doc types ──────────────────────────────────────────────
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
                DocAction.read => (object)ReadFile(subdir, name, docType, docsRoot),
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
