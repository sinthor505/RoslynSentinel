using System.ComponentModel;
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
public class DocumentationTools
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<DocumentationTools> _logger;

    private const int MaxDocBytes = 512 * 1024;   // 512 KB

    public DocumentationTools(
        PersistentWorkspaceManager workspaceManager,
        ILogger<DocumentationTools> logger)
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

    private static DocReadResult ReadFile(string subdir, string filename)
    {
        var (ok, fullPath, guardError) = DocPathGuard.ResolveSafe(subdir, filename);
        if (!ok)
        {
            return new DocReadResult { Found = false, Filename = filename, Error = guardError };
        }

        if (!File.Exists(fullPath))
        {
            return new DocReadResult { Found = false, Filename = filename };
        }

        return new DocReadResult
        {
            Found = true,
            Filename = filename,
            Content = File.ReadAllText(fullPath)
        };
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
    [Description("""
        Unified documentation accessor for all project doc files stored under docs/.

        action  : read | write | append | list
        docType : plan | handoff | completed_work | documentation | state

        Routing table:
          list   × documentation   — lists all files in docs/ (name is ignored).
          read   × plan            — reads docs/plans/<name>.
          write  × plan            — writes docs/plans/<name> with content.
          read   × handoff         — reads docs/handoffs/<name>.
          write  × handoff         — writes docs/handoffs/<name> with content.
          read   × completed_work  — reads docs/completed/<name>.
          append × completed_work  — appends content to docs/completed/<name> (audit trail, no overwrite).
          read   × documentation   — reads docs/documentation/<name>.
          write  × documentation   — writes docs/documentation/<name> with content.
          read   × state           — reads docs/migration-state.yaml (name is ignored).
          write  × state           — writes docs/migration-state.yaml with content (name is ignored).

        name    : required for all file-scoped operations except state.
        content : required for write and append.
        """)]
    public object ProjectDoc(
        string action,
        string docType,
        string? name = null,
        string? content = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        try
        {
            var rateLimitError = _workspaceManager.CheckRateLimit("project_doc", 30);
            if (rateLimitError is not null)
            {
                return action is "read" or "list"
                    ? (object)new DocReadResult { Found = false, Filename = name ?? "", Error = rateLimitError }
                    : new DocWriteResult { Success = false, Filename = name ?? "", Error = rateLimitError };
            }

            var docsRoot = TryGetDocsRoot(out var error);
            if (docsRoot is null)
            {
                return action is "read" or "list"
                    ? (object)new DocReadResult { Found = false, Filename = name ?? "", Error = error }
                    : new DocWriteResult { Success = false, Filename = name ?? "", Error = error };
            }

            // ── list ─────────────────────────────────────────────────────────────
            if (action == "list")
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
            if (docType == "state")
            {
                if (action == "read")
                {
                    var fullPath = Path.Combine(docsRoot, "migration-state.yaml");
                    if (!File.Exists(fullPath))
                    {
                        return new DocReadResult { Found = false, Filename = "migration-state.yaml" };
                    }

                    return new DocReadResult { Found = true, Filename = "migration-state.yaml", Content = File.ReadAllText(fullPath) };
                }
                if (action == "write")
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
                return action is "read"
                    ? (object)new DocReadResult { Found = false, Filename = "", Error = "name is required for file-based operations." }
                    : new DocWriteResult { Success = false, Filename = "", Error = "name is required for file-based operations." };
            }

            var subdir = docType switch
            {
                "plan" => Path.Combine(docsRoot, "plans"),
                "handoff" => Path.Combine(docsRoot, "handoffs"),
                "completed_work" => Path.Combine(docsRoot, "completed"),
                "documentation" => Path.Combine(docsRoot, "documentation"),
                _ => null
            };

            if (subdir is null)
            {
                return action is "read"
                    ? (object)new DocReadResult { Found = false, Filename = name, Error = $"Unknown docType '{docType}'. Valid: plan, handoff, completed_work, documentation, state." }
                    : new DocWriteResult { Success = false, Filename = name, Error = $"Unknown docType '{docType}'. Valid: plan, handoff, completed_work, documentation, state." };
            }

            if (action == "write" && content is null)
            {
                return new DocWriteResult { Success = false, Filename = name, Error = "content is required for action=write." };
            }

            if (action == "append" && content is null)
            {
                return new DocWriteResult { Success = false, Filename = name, Error = "content is required for action=append." };
            }

            return action switch
            {
                "read" => (object)ReadFile(subdir, name),
                "write" => WriteFile(subdir, name, content!),
                "append" => docType == "completed_work"
                    ? WriteFile(subdir, name, content!, append: true)
                    : (object)new DocWriteResult { Success = false, Filename = name, Error = "action=append is only valid for docType=completed_work." },
                _ => (object)new DocWriteResult { Success = false, Filename = name, Error = $"Unknown action '{action}'. Valid: read, write, append, list." }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProjectDoc ({Action}/{DocType}) failed", action, docType);
            return new DocWriteResult { Success = false, Filename = name ?? "", Error = $"ProjectDoc failed: {ex.GetType().Name}: {ex.Message}" };
        }
    }
}
