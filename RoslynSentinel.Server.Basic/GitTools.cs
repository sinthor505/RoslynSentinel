using System.ComponentModel;
using System.Text;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Basic;

// ─── Result types ────────────────────────────────────────────────────────────

public class GitStatusEntry
{
    public string Status { get; set; } = "";
    public string Path { get; set; } = "";
}

public class GitStatusResult
{
    public bool Success { get; set; }
    public string Branch { get; set; } = "";
    public bool IsClean { get; set; }
    public List<GitStatusEntry> Staged { get; set; } = [];
    public List<GitStatusEntry> Unstaged { get; set; } = [];
    public List<string> Untracked { get; set; } = [];
    public string? Error { get; set; }
}

public class GitCommitEntry
{
    public string Hash { get; set; } = "";
    public string ShortHash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Message { get; set; } = "";
}

public class GitLogResult
{
    public bool Success { get; set; }
    public List<GitCommitEntry> Commits { get; set; } = [];
    public string? Error { get; set; }
}

public class GitDiffResult
{
    public bool Success { get; set; }
    public string Diff { get; set; } = "";
    public int FilesChanged { get; set; }
    public string? Error { get; set; }
}

public class GitCommitResult
{
    public bool Success { get; set; }
    public string CommitHash { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Error { get; set; }
}

public class GitRevertResult
{
    public bool Success { get; set; }
    public string CommitHash { get; set; } = "";
    public string Message { get; set; } = "";
    public bool PendingCommit { get; set; }
    public string? Error { get; set; }
}

// ─── Tool class ──────────────────────────────────────────────────────────────

[McpServerToolType]
public class GitTools
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<GitTools> _logger;

    public GitTools(
        PersistentWorkspaceManager workspaceManager,
        ILogger<GitTools> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? TryGetGitRoot(out string error)
    {
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is null)
        {
            error = "No solution path configured. Call load_solution first.";
            return null;
        }

        var dir = solutionRoot;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
            {
                error = "";
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        error = $"No git repository found at '{solutionRoot}' or any parent directory.";
        return null;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string gitRoot, string[] args, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = gitRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string StatusLabel(char code) => code switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'U' => "conflict",
        _ => code.ToString()
    };

    // ── Git (unified) ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "Git")]
    [Produces(DataTag.Report)]
    [Description("""
        Unified git tool. Select an operation via the `operation` parameter.

        ┌─────────────────────────────────────────────────────────────────────┐
        │ operation  │ description                                            │
        ├─────────────────────────────────────────────────────────────────────┤
        │ status     │ Branch name, staged, unstaged, and untracked files.    │
        │ log        │ Recent commits (hash, author, date, subject).          │
        │ diff       │ Unified diff of working tree, staged area, or a commit.│
        │ commit     │ Stage files and create a commit.                       │
        │ revert     │ Undo a commit by creating a new inverse commit.        │
        └─────────────────────────────────────────────────────────────────────┘

        Parameters by operation
        ───────────────────────
        status  — no additional parameters.

        log     — count : number of commits to return (default 20, max 100).

        diff    — target   : "working" (unstaged, default), "staged", or a commit hash.
                  paths    : optional comma-separated repo-relative paths to restrict the diff.
                  maxBytes : truncate output at this many bytes (default 65536, max 524288).

        commit  — message : required commit message.
                  files   : optional comma-separated paths to stage. If omitted, all
                            tracked modifications and deletions are staged (git add -u).
                            Untracked files are never staged automatically — list them
                            explicitly in files when they should be included.

        revert  — commitHash : full or short hash of the commit to revert (from log).
                  noCommit   : when true, stages the revert without committing — call
                               commit afterwards to finalise. Default false.
        """)]
    public async Task<object> Git(
        string operation,
        // log
        int count = 20,
        // diff
        string target = "working",
        string? paths = null,
        int maxBytes = 65536,
        // commit
        string? message = null,
        string? files = null,
        // revert
        string? commitHash = null,
        bool noCommit = false,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        var ct = cancellationToken ?? CancellationToken.None;
        var gitRoot = TryGetGitRoot(out var rootError);
        if (gitRoot is null)
            return new { Success = false, Error = rootError };

        return operation switch
        {
            "status" => await StatusAsync(gitRoot, ct),
            "log"    => await LogAsync(gitRoot, count, ct),
            "diff"   => await DiffAsync(gitRoot, target, paths, maxBytes, ct),
            "commit" => await CommitAsync(gitRoot, message, files, ct),
            "revert" => await RevertAsync(gitRoot, commitHash, noCommit, ct),
            _ => (object)new { Success = false, Error = $"Unknown operation '{operation}'. Valid: status, log, diff, commit, revert." }
        };
    }

    // ── Operation implementations ─────────────────────────────────────────────

    private async Task<GitStatusResult> StatusAsync(string gitRoot, CancellationToken ct)
    {
        try
        {
            var (branchExit, branchOut, _) = await RunGitAsync(gitRoot,
                ["rev-parse", "--abbrev-ref", "HEAD"], ct);
            var branch = branchExit == 0 ? branchOut.Trim() : "unknown";

            var (statusExit, statusOut, statusErr) = await RunGitAsync(gitRoot,
                ["status", "--porcelain=v1"], ct);
            if (statusExit != 0)
                return new GitStatusResult { Success = false, Branch = branch, Error = statusErr.Trim() };

            var staged = new List<GitStatusEntry>();
            var unstaged = new List<GitStatusEntry>();
            var untracked = new List<string>();

            foreach (var line in statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 3) continue;
                var x = line[0];
                var y = line[1];
                var path = line[3..].Trim();

                if (x == '?' && y == '?')
                {
                    untracked.Add(path);
                    continue;
                }
                if (x != ' ' && x != '?')
                    staged.Add(new GitStatusEntry { Status = StatusLabel(x), Path = path });
                if (y != ' ' && y != '?')
                    unstaged.Add(new GitStatusEntry { Status = StatusLabel(y), Path = path });
            }

            return new GitStatusResult
            {
                Success = true,
                Branch = branch,
                IsClean = staged.Count == 0 && unstaged.Count == 0 && untracked.Count == 0,
                Staged = staged,
                Unstaged = unstaged,
                Untracked = untracked,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git status failed");
            return new GitStatusResult { Success = false, Error = $"Git status failed: {ex.Message}" };
        }
    }

    private async Task<GitLogResult> LogAsync(string gitRoot, int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 100);
        try
        {
            // Unit separator (ASCII 31) used as field delimiter — safe in commit messages.
            const string sep = "\x1f";
            var format = $"%H{sep}%h{sep}%an{sep}%aI{sep}%s";

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot,
                ["log", $"--max-count={count}", $"--format={format}"], ct);

            if (exitCode != 0)
                return new GitLogResult { Success = false, Error = stderr.Trim() };

            var commits = new List<GitCommitEntry>();
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(sep);
                if (parts.Length < 5) continue;
                commits.Add(new GitCommitEntry
                {
                    Hash = parts[0],
                    ShortHash = parts[1],
                    Author = parts[2],
                    Date = parts[3],
                    Message = parts[4],
                });
            }

            return new GitLogResult { Success = true, Commits = commits };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git log failed");
            return new GitLogResult { Success = false, Error = $"Git log failed: {ex.Message}" };
        }
    }

    private async Task<GitDiffResult> DiffAsync(
        string gitRoot, string target, string? paths, int maxBytes, CancellationToken ct)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 524288);
        try
        {
            var args = new List<string> { "diff" };

            if (target == "staged")
            {
                args.Add("--cached");
            }
            else if (target != "working")
            {
                // Show what a specific commit changed (diff against its parent).
                args.Add($"{target}^");
                args.Add(target);
            }

            if (!string.IsNullOrWhiteSpace(paths))
            {
                args.Add("--");
                foreach (var p in paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    args.Add(p);
            }

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, [.. args], ct);

            if (exitCode != 0)
                return new GitDiffResult { Success = false, Error = stderr.Trim() };

            var filesChanged = stdout.Split('\n')
                .Count(l => l.StartsWith("diff --git", StringComparison.Ordinal));

            var diff = stdout.Length > maxBytes
                ? stdout[..maxBytes] + $"\n... (truncated at {maxBytes} bytes)"
                : stdout;

            return new GitDiffResult { Success = true, Diff = diff, FilesChanged = filesChanged };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git diff failed (target={Target})", target);
            return new GitDiffResult { Success = false, Error = $"Git diff failed: {ex.Message}" };
        }
    }

    private async Task<GitCommitResult> CommitAsync(
        string gitRoot, string? message, string? files, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommitResult { Success = false, Error = "message is required for operation=commit." };

        try
        {
            string[] stageArgs;
            if (string.IsNullOrWhiteSpace(files))
            {
                stageArgs = ["add", "-u"];
            }
            else
            {
                var filePaths = files.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                stageArgs = ["add", "--", .. filePaths];
            }

            var (stageExit, _, stageErr) = await RunGitAsync(gitRoot, stageArgs, ct);
            if (stageExit != 0)
                return new GitCommitResult { Success = false, Error = $"git add failed: {stageErr.Trim()}" };

            var (commitExit, _, commitErr) = await RunGitAsync(gitRoot, ["commit", "-m", message], ct);
            if (commitExit != 0)
                return new GitCommitResult { Success = false, Error = $"git commit failed: {commitErr.Trim()}" };

            var (hashExit, hashOut, _) = await RunGitAsync(gitRoot, ["rev-parse", "HEAD"], ct);
            var hash = hashExit == 0 ? hashOut.Trim() : "";

            return new GitCommitResult { Success = true, CommitHash = hash, Message = message };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git commit failed");
            return new GitCommitResult { Success = false, Error = $"Git commit failed: {ex.Message}" };
        }
    }

    private async Task<GitRevertResult> RevertAsync(
        string gitRoot, string? commitHash, bool noCommit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
            return new GitRevertResult { Success = false, Error = "commitHash is required for operation=revert." };

        try
        {
            var args = new List<string> { "revert", "--no-edit" };
            if (noCommit)
                args.Add("--no-commit");
            args.Add(commitHash);

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, [.. args], ct);

            if (exitCode != 0)
                return new GitRevertResult { Success = false, CommitHash = commitHash, Error = stderr.Trim() };

            string newHash = "";
            if (!noCommit)
            {
                var (hashExit, hashOut, _) = await RunGitAsync(gitRoot, ["rev-parse", "HEAD"], ct);
                newHash = hashExit == 0 ? hashOut.Trim() : "";
            }

            return new GitRevertResult
            {
                Success = true,
                CommitHash = newHash,
                Message = noCommit ? "Revert staged — call commit to finalise." : stdout.Trim(),
                PendingCommit = noCommit,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git revert failed (hash={Hash})", commitHash);
            return new GitRevertResult { Success = false, Error = $"Git revert failed: {ex.Message}" };
        }
    }
}
