using System.ComponentModel;
using System.Text;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

// ─── Result types ────────────────────────────────────────────────────────────

public class DocReadResult
{
    public bool    Found    { get; set; }
    public string  Filename { get; set; } = "";
    public string? Content  { get; set; }
    public string? Error    { get; set; }
}

public class DocWriteResult
{
    public bool    Success      { get; set; }
    public string  Filename     { get; set; } = "";
    public string  FullPath     { get; set; } = "";
    public int     BytesWritten { get; set; }
    public string? Error        { get; set; }
}

public class DocListResult
{
    public List<string> Files { get; set; } = [];
    public int          Count { get; set; }
    public string?      Error { get; set; }
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
            error = "No solution is loaded. Call load_solution first.";
            return null;
        }

        error = "";
        return Path.Combine(solutionRoot, "docs");
    }

    private static DocReadResult ReadFile(string subdir, string filename)
    {
        var (ok, fullPath, guardError) = DocPathGuard.ResolveSafe(subdir, filename);
        if (!ok)
            return new DocReadResult { Found = false, Filename = filename, Error = guardError };

        if (!File.Exists(fullPath))
            return new DocReadResult { Found = false, Filename = filename };

        return new DocReadResult
        {
            Found    = true,
            Filename = filename,
            Content  = File.ReadAllText(fullPath)
        };
    }

    private static DocWriteResult WriteFile(string subdir, string filename, string content, bool append = false)
    {
        var (ok, fullPath, guardError) = DocPathGuard.ResolveSafe(subdir, filename);
        if (!ok)
            return new DocWriteResult { Success = false, Filename = filename, Error = guardError };

        int bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxDocBytes)
            return new DocWriteResult
            {
                Success  = false,
                Filename = filename,
                Error    = $"Content exceeds {MaxDocBytes} bytes. Documentation files should be concise."
            };

        Directory.CreateDirectory(subdir);

        if (append)
            File.AppendAllText(fullPath, content);
        else
            File.WriteAllText(fullPath, content);

        return new DocWriteResult
        {
            Success      = true,
            Filename     = filename,
            FullPath     = fullPath,
            BytesWritten = bytes
        };
    }

    // ── list_project_documentation ───────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Lists all files in the solution's docs/ directory tree. Returns relative paths within docs/ only. " +
        "Cannot enumerate source files or directories outside docs/.")]
    public DocListResult ListProjectDocumentation()
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("list_project_documentation", 20);
        if (rateLimitError is not null)
            return new DocListResult { Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocListResult { Error = error };

        if (!Directory.Exists(docsRoot))
            return new DocListResult { Files = [], Count = 0 };

        var files = Directory.GetFiles(docsRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(docsRoot, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        return new DocListResult { Files = files, Count = files.Count };
    }

    // ── read_project_documentation ───────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Reads a project documentation file from the solution's docs/documentation/ directory. " +
        "The filename parameter is treated as a bare filename — any path components are stripped and rejected. " +
        "Only documentation file types (.md, .yaml, .yml, .json, .txt) are permitted. " +
        "Cannot read source files or files outside the docs directory.")]
    public DocReadResult ReadProjectDocumentation(string filename)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("read_project_documentation", 30);
        if (rateLimitError is not null)
            return new DocReadResult { Found = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocReadResult { Found = false, Filename = filename, Error = error };

        return ReadFile(Path.Combine(docsRoot, "documentation"), filename);
    }

    // ── update_project_documentation ─────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Writes (creates or overwrites) a project documentation file in the solution's docs/documentation/ directory. " +
        "Filename is treated as bare — path components are stripped and rejected. " +
        "Only documentation file types permitted. Content size capped at 512 KB. " +
        "Cannot write source files or files outside the docs directory.")]
    public DocWriteResult UpdateProjectDocumentation(string filename, string content)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("update_project_documentation", 10);
        if (rateLimitError is not null)
            return new DocWriteResult { Success = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocWriteResult { Success = false, Filename = filename, Error = error };

        return WriteFile(Path.Combine(docsRoot, "documentation"), filename, content);
    }

    // ── read_plan ────────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Reads a migration or refactor plan file from the solution's docs/plans/ directory. " +
        "Only bare filenames are accepted; only documentation extensions are permitted.")]
    public DocReadResult ReadPlan(string filename)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("read_plan", 30);
        if (rateLimitError is not null)
            return new DocReadResult { Found = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocReadResult { Found = false, Filename = filename, Error = error };

        return ReadFile(Path.Combine(docsRoot, "plans"), filename);
    }

    // ── update_plan ──────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Writes (creates or overwrites) a plan file in the solution's docs/plans/ directory. " +
        "Only bare filenames and documentation extensions are accepted. Content size capped at 512 KB.")]
    public DocWriteResult UpdatePlan(string filename, string content)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("update_plan", 10);
        if (rateLimitError is not null)
            return new DocWriteResult { Success = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocWriteResult { Success = false, Filename = filename, Error = error };

        return WriteFile(Path.Combine(docsRoot, "plans"), filename, content);
    }

    // ── read_handoff ─────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Reads a session handoff document from the solution's docs/handoffs/ directory. " +
        "Only bare filenames and documentation extensions are accepted.")]
    public DocReadResult ReadHandoff(string filename)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("read_handoff", 30);
        if (rateLimitError is not null)
            return new DocReadResult { Found = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocReadResult { Found = false, Filename = filename, Error = error };

        return ReadFile(Path.Combine(docsRoot, "handoffs"), filename);
    }

    // ── write_handoff ────────────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Writes (creates or overwrites) a session handoff document in the solution's docs/handoffs/ directory. " +
        "Only bare filenames and documentation extensions are accepted. Content size capped at 512 KB.")]
    public DocWriteResult WriteHandoff(string filename, string content)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("write_handoff", 10);
        if (rateLimitError is not null)
            return new DocWriteResult { Success = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocWriteResult { Success = false, Filename = filename, Error = error };

        return WriteFile(Path.Combine(docsRoot, "handoffs"), filename, content);
    }

    // ── read_completed_work ──────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Reads a completed-work log file from the solution's docs/completed/ directory. " +
        "Only bare filenames and documentation extensions are accepted.")]
    public DocReadResult ReadCompletedWork(string filename)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("read_completed_work", 30);
        if (rateLimitError is not null)
            return new DocReadResult { Found = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocReadResult { Found = false, Filename = filename, Error = error };

        return ReadFile(Path.Combine(docsRoot, "completed"), filename);
    }

    // ── append_completed_work ────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Appends an entry to a completed-work log in docs/completed/. " +
        "Append-only — existing content cannot be overwritten or deleted. " +
        "Use to record finished work as an immutable audit trail. " +
        "Only bare filenames and documentation extensions are accepted. Entry size capped at 512 KB.")]
    public DocWriteResult AppendCompletedWork(string filename, string entry)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("append_completed_work", 15);
        if (rateLimitError is not null)
            return new DocWriteResult { Success = false, Filename = filename, Error = rateLimitError };

        var docsRoot = TryGetDocsRoot(out var error);
        if (docsRoot is null)
            return new DocWriteResult { Success = false, Filename = filename, Error = error };

        return WriteFile(Path.Combine(docsRoot, "completed"), filename, entry, append: true);
    }

    // ── read_current_state ───────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Reads the migration state file (docs/migration-state.yaml). " +
        "Takes no parameters — there is exactly one state file.")]
    public DocReadResult ReadCurrentState()
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("read_current_state", 30);
        if (rateLimitError is not null)
            return new DocReadResult { Found = false, Filename = "migration-state.yaml", Error = rateLimitError };

        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is null)
            return new DocReadResult { Found = false, Filename = "migration-state.yaml", Error = "No solution is loaded. Call load_solution first." };

        var fullPath = Path.Combine(solutionRoot, "docs", "migration-state.yaml");
        if (!File.Exists(fullPath))
            return new DocReadResult { Found = false, Filename = "migration-state.yaml" };

        return new DocReadResult
        {
            Found    = true,
            Filename = "migration-state.yaml",
            Content  = File.ReadAllText(fullPath)
        };
    }

    // ── update_current_state ─────────────────────────────────────────────────

    [McpServerTool]
    [Description(
        "Writes the migration state file (docs/migration-state.yaml). " +
        "Takes no filename parameter — there is exactly one state file. " +
        "Content size capped at 512 KB.")]
    public DocWriteResult UpdateCurrentState(string content)
    {
        var rateLimitError = _workspaceManager.CheckRateLimit("update_current_state", 5);
        if (rateLimitError is not null)
            return new DocWriteResult { Success = false, Filename = "migration-state.yaml", Error = rateLimitError };

        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is null)
            return new DocWriteResult { Success = false, Filename = "migration-state.yaml", Error = "No solution is loaded. Call load_solution first." };

        int bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaxDocBytes)
            return new DocWriteResult
            {
                Success  = false,
                Filename = "migration-state.yaml",
                Error    = $"Content exceeds {MaxDocBytes} bytes. Documentation files should be concise."
            };

        var docsDir  = Path.Combine(solutionRoot, "docs");
        var fullPath = Path.Combine(docsDir, "migration-state.yaml");
        Directory.CreateDirectory(docsDir);
        File.WriteAllText(fullPath, content);

        return new DocWriteResult
        {
            Success      = true,
            Filename     = "migration-state.yaml",
            FullPath     = fullPath,
            BytesWritten = bytes
        };
    }
}
