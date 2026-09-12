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
    public bool Success
    {
        get; set;
    }
    public string Branch { get; set; } = "";
    public bool IsClean
    {
        get; set;
    }
    public List<GitStatusEntry> Staged { get; set; } = [];
    public List<GitStatusEntry> Unstaged { get; set; } = [];
    public List<string> Untracked { get; set; } = [];
    public string? Error
    {
        get; set;
    }
    // Populated when IsTruncated=true; lists above are capped to first 10 entries each as a sample.
    public bool IsTruncated
    {
        get; set;
    }
    public int? TotalStagedCount
    {
        get; set;
    }
    public int? TotalUnstagedCount
    {
        get; set;
    }
    public int? TotalUntrackedCount
    {
        get; set;
    }
    public Dictionary<string, int>? StagedByStatus
    {
        get; set;
    }
    public Dictionary<string, int>? UnstagedByStatus
    {
        get; set;
    }
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
    public bool Success
    {
        get; set;
    }
    public List<GitCommitEntry> Commits { get; set; } = [];
    public string? Error
    {
        get; set;
    }
}

public class GitDiffResult
{
    public bool Success
    {
        get; set;
    }
    public string Diff { get; set; } = "";
    public int FilesChanged
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
}

public class GitCommitResult
{
    public bool Success
    {
        get; set;
    }
    public string CommitHash { get; set; } = "";
    public string Message { get; set; } = "";
    public string? Error
    {
        get; set;
    }
}

public class GitRevertResult
{
    public bool Success
    {
        get; set;
    }
    public string CommitHash { get; set; } = "";
    public string Message { get; set; } = "";
    public bool PendingCommit
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
public class SentinelGitTools
{
    /// <summary>
    /// Bound on how long a single git subprocess invocation may run. Applied whenever no caller-
    /// supplied CancellationToken already carries a shorter deadline — MCP tool calls that omit
    /// cancellationToken get CancellationToken.None here, which would otherwise let a hung git
    /// process (see docs/TODO.md's "Git(operation: status) hung indefinitely" entry) block forever.
    /// </summary>
    private static readonly TimeSpan GitProcessTimeout = TimeSpan.FromSeconds(30);

    // Resolved once and reused: repeatedly letting CreateProcess re-resolve the bare "git" name
    // through PATH on every call is themselves a plausible source of the intermittent 30s stalls
    // documented in docs/current/blockers/blocking_error_git_status_timeout.md (first hang is always
    // the process-spawn itself, not anything git does once running). Falls back to "git" if PATH
    // resolution fails here, preserving prior behavior.
    private static readonly string GitExecutablePath = ResolveGitExecutablePath();

    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<SentinelGitTools> _logger;

    public SentinelGitTools(
        ISolutionProvider workspaceManager,
        ILogger<SentinelGitTools> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
    }
    /// <summary>
    /// Finds the git repository root. Git operations read the working tree, not the Roslyn
    /// compilation, so this deliberately does NOT require a loaded solution: it walks up from the
    /// server's own base directory first and only falls back to the loaded solution's directory if
    /// that finds nothing. Previously this returned "No solution path configured" for every
    /// operation — coupling status/log/diff to state they never used, and failing the very first
    /// Git call an agent makes.
    /// </summary>
    private string? TryGetGitRoot(out string error)
    {
        // 1. Walk up from the server's base directory. This is the common case: the server runs
        //    from a bin/ folder inside the repo it is operating on.
        var fromBaseDirectory = FindRepositoryRoot(AppContext.BaseDirectory);
        if (fromBaseDirectory is not null)
        {
            error = "";
            return fromBaseDirectory;
        }

        // 2. Walk up from the current working directory — covers a server whose binaries are
        //    deployed outside the repo but which was launched from inside it.
        var fromCurrentDirectory = FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (fromCurrentDirectory is not null)
        {
            error = "";
            return fromCurrentDirectory;
        }

        // 3. Last resort: the loaded solution's directory, if there is one.
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is not null)
        {
            var fromSolution = FindRepositoryRoot(solutionRoot);
            if (fromSolution is not null)
            {
                error = "";
                return fromSolution;
            }

            error = $"No git repository found. Searched upward from the server's base directory and from the loaded solution's directory ('{solutionRoot}') without finding a .git entry. Git operations need a git working tree, not a loaded solution.";
            return null;
        }

        error = "No git repository found. Searched upward from the server's base directory and working directory without finding a .git entry, and no solution is loaded to search from either. If the repository is elsewhere, call LoadSolution with a solution inside it first.";
        return null;
    }
    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a <c>.git</c> entry. Accepts a
    /// <c>.git</c> <b>file</b> as well as a directory: linked worktrees and submodules use a
    /// <c>.git</c> file holding a gitdir pointer, and a directory-only check silently walks past
    /// them into the parent repository — which is exactly how a PlanStepRunner harness clone would
    /// end up having git operations aimed at the wrong repo.
    /// </summary>
    private static string? FindRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        string? dir;
        try
        {
            dir = Path.GetFullPath(startDirectory);
        }
        catch (Exception)
        {
            // A malformed or over-long path is not something the caller can act on — treat it as
            // "nothing found here" so the next candidate directory still gets tried.
            return null;
        }

        while (!string.IsNullOrEmpty(dir))
        {
            var gitEntry = Path.Combine(dir, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return dir;

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }

        return null;
    }
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string gitRoot, string[] args, CancellationToken cancellationToken)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = GitExecutablePath,
            WorkingDirectory = gitRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        // A caller-supplied token (if any) can still cancel earlier than GitProcessTimeout — this
        // only adds a ceiling for the common case (MCP call omits cancellationToken, so this method
        // otherwise gets CancellationToken.None and would wait on a hung git process forever).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(GitProcessTimeout);

        process.Start();
        // Close stdin immediately: an inherited/open but undrained stdin handle is a known cause of
        // a spawned console child stalling indefinitely waiting for input it will never receive,
        // even for commands (like rev-parse) that never read from it.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillGitProcess(process);
            // Full detail (args, timeout value, TODO cross-reference) is server-side only, via the
            // logger call below — every caller's catch block folds the thrown exception's Message
            // straight into the ResultError text returned to the agent, so that Message must stay
            // a plain, self-contained statement with no internal paths/docs the agent can't open.
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(
                    "git {Args} did not exit within {TimeoutSeconds}s and was killed. Not a normal git " +
                    "failure — see docs/TODO.md's 'Git(operation: status) hung indefinitely' entry.",
                    string.Join(' ', args), GitProcessTimeout.TotalSeconds);
            }
            throw new TimeoutException($"Git operation timed out after {GitProcessTimeout.TotalSeconds:0}s and was cancelled.");
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static string ResolveGitExecutablePath()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';')
            : [""];

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (dir.Length == 0)
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        // Fall back to bare "git" (prior behavior) so a PATH this scan couldn't see (e.g. an App
        // Paths registry redirect) still has a chance to work rather than failing outright.
        return "git";
    }

    private static void TryKillGitProcess(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have exited between the check and the kill, or the
            // handle may already be invalid. Nothing more useful to do here.
        }
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
    [McpServerTool(Name = "Git")]
    [Produces(DataTag.Report)]
    [Description("Unified git tool covering status, log, diff, staging, commit, revert, branch, checkout, push, fetch, and pull.")]
    public async Task<object> Git(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Description("Which git operation to run.")]
        GitOperation operation,
        [Description("log: number of commits to return (max 100).")]
        int count = 20,
        [Description("diff: \"working\" (unstaged), \"staged\", or a commit hash.")]
        string target = "working",
        [Description("diff: comma-separated repo-relative paths to restrict the diff to.")]
        string? paths = null,
        [Description("diff: byte cap on the returned diff (max 524288).")]
        int maxBytes = 65536,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: message is required when operation=commit, unused otherwise.
        [Description("commit: the commit message. Required for operation=commit.")]
        string? message = null,
        [Description("stage/commit: which files to stage. \"tracked\" (default) stages modifications and deletions of already-tracked files only (git add -u) and does NOT stage new files. \"all\" stages everything in the working tree including untracked files (git add -A). \"listed\" stages exactly the paths you name in files/paths, untracked ones included — use this whenever you know which files you want. Naming files alongside a scope other than \"listed\" is rejected, so a file list can never be silently overridden.")]
        GitStageScope scope = GitStageScope.tracked,
        [Description("stage/commit: comma-separated repo-relative paths to stage. Requires scope=\"listed\". Alias of paths for these operations — pass one or the other, not both.")]
        string? files = null,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: commitHash is required when operation=revert, unused otherwise.
        [Description("revert: the commit to revert (full or short hash, from log). Required for operation=revert.")]
        string? commitHash = null,
        [Description("revert: true stages the revert without committing; call Git(operation: commit) to finalize.")]
        bool noCommit = false,
        // CONDITIONAL-PARAM-REVIEW-REQUIRED: branchName is required for operation=checkout; optional for operation=branch (omit to list).
        [Description("branch/checkout: the branch to create, delete, or switch to. branch: omit to list all branches instead. checkout: required.")]
        string? branchName = null,
        [Description("branch: base ref for a newly created branch (defaults to HEAD). checkout: base ref for a new branch, only used together with createBranch=true.")]
        string? startPoint = null,
        [Description("branch: true deletes branchName (git branch -d, refuses if unmerged) instead of creating it. Ignored when branchName is omitted (list mode).")]
        bool deleteBranch = false,
        [Description("checkout: true creates branchName first if it doesn't already exist (git checkout -b), optionally from startPoint.")]
        bool createBranch = false,
        [Description("push/fetch/pull: the remote to operate on.")]
        string remoteName = "origin",
        [Description("push: true also sets the pushed branch's upstream tracking (git push -u).")]
        bool setUpstream = false,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        var gitRoot = TryGetGitRoot(out var rootError);
        if (gitRoot is null)
            return new { Success = false, Error = rootError };

        // `files` and `paths` name the same concept; `paths` is the git-native spelling that `diff`
        // already used, so `files` stays an accepted alias rather than breaking callers. Setting
        // both is ambiguous, so it is refused instead of letting one silently win.
        if (!string.IsNullOrWhiteSpace(files) && !string.IsNullOrWhiteSpace(paths))
        {
            return new
            {
                Success = false,
                Error = "Both 'files' and 'paths' were supplied — they are aliases for the same list and must not be combined. Pass just one of them (either spelling is accepted)."
            };
        }
        var resolvedPaths = !string.IsNullOrWhiteSpace(files) ? files : paths;

        return operation switch
        {
            GitOperation.status => await StatusAsync(gitRoot, cancellationToken),
            GitOperation.log => await LogAsync(gitRoot, count, cancellationToken),
            GitOperation.diff => await DiffAsync(gitRoot, target, resolvedPaths, maxBytes, cancellationToken),
            GitOperation.stage or GitOperation.add => await StageAsync(gitRoot, scope, resolvedPaths, cancellationToken),
            GitOperation.unstage => await UnstageAsync(gitRoot, resolvedPaths, cancellationToken),
            GitOperation.commit => await CommitAsync(gitRoot, message, scope, resolvedPaths, cancellationToken),
            GitOperation.revert => await RevertAsync(gitRoot, commitHash, noCommit, cancellationToken),
            GitOperation.branch => await BranchAsync(gitRoot, branchName, startPoint, deleteBranch, cancellationToken),
            GitOperation.checkout => await CheckoutAsync(gitRoot, branchName, createBranch, startPoint, cancellationToken),
            GitOperation.push => await PushAsync(gitRoot, remoteName, setUpstream, cancellationToken),
            GitOperation.fetch => await FetchAsync(gitRoot, remoteName, cancellationToken),
            GitOperation.pull => await PullAsync(gitRoot, remoteName, cancellationToken),
            _ => (object)new { Success = false, Error = $"Unknown operation '{operation}'." }
        };
    }

    // ── Operation implementations ─────────────────────────────────────────────

    private async Task<GitStatusResult> StatusAsync(string gitRoot, CancellationToken cancellationToken)
    {
        try
        {
            var (branchExit, branchOut, _) = await RunGitAsync(gitRoot,
                ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            var branch = branchExit == 0 ? branchOut.Trim() : "unknown";

            var (statusExit, statusOut, statusErr) = await RunGitAsync(gitRoot,
                ["status", "--porcelain=v1"], cancellationToken);
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

            bool isClean = staged.Count == 0 && unstaged.Count == 0 && untracked.Count == 0;
            int total = staged.Count + unstaged.Count + untracked.Count;
            const int threshold = 50;
            const int sampleSize = 10;

            if (total > threshold)
            {
                return new GitStatusResult
                {
                    Success = true,
                    Branch = branch,
                    IsClean = isClean,
                    IsTruncated = true,
                    TotalStagedCount = staged.Count,
                    TotalUnstagedCount = unstaged.Count,
                    TotalUntrackedCount = untracked.Count,
                    StagedByStatus = staged.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
                    UnstagedByStatus = unstaged.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
                    Staged = staged.Take(sampleSize).ToList(),
                    Unstaged = unstaged.Take(sampleSize).ToList(),
                    Untracked = untracked.Take(sampleSize).ToList(),
                };
            }

            return new GitStatusResult
            {
                Success = true,
                Branch = branch,
                IsClean = isClean,
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

    private async Task<GitLogResult> LogAsync(string gitRoot, int count, CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 1, 100);
        try
        {
            // Unit separator (ASCII 31) used as field delimiter — safe in commit messages.
            const string sep = "\x1f";
            var format = $"%H{sep}%h{sep}%an{sep}%aI{sep}%s";

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot,
                ["log", $"--max-count={count}", $"--format={format}"], cancellationToken);

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
        string gitRoot, string target, string? paths, int maxBytes, CancellationToken cancellationToken)
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

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, [.. args], cancellationToken);

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

    /// <summary>
    /// Stages files according to <paramref name="scope"/>. Scope and path list are one decision
    /// rather than two independent inputs: the former <c>stageAll</c> boolean could override an
    /// explicit file list and run <c>git add -A</c>, quietly staging unrelated untracked files.
    /// Every mismatch between scope and paths is refused up front, naming the conflict and the
    /// call that would have worked.
    /// </summary>
    private async Task<GitStatusResult> StageAsync(
        string gitRoot, GitStageScope scope, string? paths, CancellationToken cancellationToken)
    {
        var hasPaths = !string.IsNullOrWhiteSpace(paths);

        if (scope != GitStageScope.listed && hasPaths)
        {
            return new GitStatusResult
            {
                Success = false,
                Error = $"You named files to stage but passed scope=\"{scope}\", which ignores them. " +
                        "Pass scope=\"listed\" to stage exactly the files you named (untracked ones " +
                        $"included), or drop the file list to stage by scope=\"{scope}\". Nothing was staged."
            };
        }

        if (scope == GitStageScope.listed && !hasPaths)
        {
            return new GitStatusResult
            {
                Success = false,
                Error = "scope=\"listed\" stages exactly the files you name, but no files were given. " +
                        "Pass files (or paths) as a comma-separated list of repo-relative paths, e.g. " +
                        "files: \"src/Foo.cs,docs/notes.md\". To stage without naming files use " +
                        "scope=\"tracked\" (already-tracked changes only) or scope=\"all\" (everything, " +
                        "untracked included). Nothing was staged."
            };
        }

        try
        {
            string[] stageArgs;
            switch (scope)
            {
                case GitStageScope.all:
                    stageArgs = ["add", "-A"];
                    break;
                case GitStageScope.listed:
                    // `git add -- <paths>` stages untracked paths as well as tracked modifications,
                    // which is what "stage exactly these" has to mean to be useful.
                    var filePaths = paths!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    stageArgs = ["add", "--", .. filePaths];
                    break;
                case GitStageScope.tracked:
                default:
                    stageArgs = ["add", "-u"];
                    break;
            }

            var (stageExit, _, stageErr) = await RunGitAsync(gitRoot, stageArgs, cancellationToken);
            if (stageExit != 0)
                return new GitStatusResult { Success = false, Error = $"git add failed: {stageErr.Trim()}" };

            return await StatusAsync(gitRoot, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git stage failed");
            return new GitStatusResult { Success = false, Error = $"Git stage failed: {ex.Message}" };
        }
    }
    // Added by InsertMemberBefore (expected - used for diagnostics)
    /// <summary>
    /// Removes files from the index (<c>git reset</c>), leaving the working tree untouched. The
    /// tool could previously stage but never un-stage, so any mis-stage forced a shell fallback —
    /// a state the tool could create but not exit. With no paths this resets the whole index; with
    /// paths it un-stages only those.
    /// </summary>
    private async Task<GitStatusResult> UnstageAsync(
        string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        try
        {
            string[] resetArgs;
            if (string.IsNullOrWhiteSpace(paths))
            {
                // Whole index. Deliberately a plain `git reset` (mixed, no ref): it un-stages
                // everything while leaving every working-tree edit on disk, so this can never
                // destroy uncommitted work the way `reset --hard` would.
                resetArgs = ["reset"];
            }
            else
            {
                var filePaths = paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                resetArgs = ["reset", "--", .. filePaths];
            }

            var (resetExit, _, resetErr) = await RunGitAsync(gitRoot, resetArgs, cancellationToken);

            // `git reset` exits 1 when the index still differs from HEAD after the reset, which is
            // the normal outcome here, not a failure. Treat stderr content as the real signal
            // rather than trusting the exit code alone.
            var resetErrText = resetErr.Trim();
            if (resetExit != 0 && resetErrText.Length > 0)
                return new GitStatusResult { Success = false, Error = $"git reset failed: {resetErrText}" };

            return await StatusAsync(gitRoot, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git unstage failed");
            return new GitStatusResult { Success = false, Error = $"Git unstage failed: {ex.Message}" };
        }
    }
    private async Task<GitCommitResult> CommitAsync(
        string gitRoot, string? message, GitStageScope scope, string? paths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return new GitCommitResult
            {
                Success = false,
                Error = "message is required for operation=commit. Pass message: \"<what this commit does>\". Nothing was committed."
            };
        }

        try
        {
            var stageResult = await StageAsync(gitRoot, scope, paths, cancellationToken);
            if (!stageResult.Success)
                return new GitCommitResult { Success = false, Error = stageResult.Error };

            // When specific files were named (scope=listed), restrict the commit itself to those
            // paths via a pathspec. Without this, `git commit -m message` commits the ENTIRE
            // current index regardless of what was just staged above — silently sweeping in
            // anything left over from earlier staging in the same working tree. `files`/`paths`
            // must narrow the commit, not just add to what StageAsync staged.
            string[] commitArgs;
            if (scope == GitStageScope.listed && !string.IsNullOrWhiteSpace(paths))
            {
                var filePaths = paths!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                commitArgs = ["commit", "-m", message, "--", .. filePaths];
            }
            else
            {
                commitArgs = ["commit", "-m", message];
            }

            var (commitExit, commitOut, commitErr) = await RunGitAsync(gitRoot, commitArgs, cancellationToken);
            if (commitExit != 0)
            {
                var detail = string.Join("\n", new[] { commitOut.Trim(), commitErr.Trim() }
                    .Where(s => !string.IsNullOrEmpty(s)));
                var errorText = string.IsNullOrEmpty(detail)
                    ? $"git commit exited with code {commitExit}"
                    : detail;
                return new GitCommitResult { Success = false, Error = $"git commit failed: {errorText}" };
            }

            var (hashExit, hashOut, _) = await RunGitAsync(gitRoot, ["rev-parse", "HEAD"], cancellationToken);
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
        string gitRoot, string? commitHash, bool noCommit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
            return new GitRevertResult { Success = false, Error = "commitHash is required for operation=revert." };

        try
        {
            var args = new List<string> { "revert", "--no-edit" };
            if (noCommit)
                args.Add("--no-commit");
            args.Add(commitHash);

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, [.. args], cancellationToken);

            if (exitCode != 0)
                return new GitRevertResult { Success = false, CommitHash = commitHash, Error = stderr.Trim() };

            string newHash = "";
            if (!noCommit)
            {
                var (hashExit, hashOut, _) = await RunGitAsync(gitRoot, ["rev-parse", "HEAD"], cancellationToken);
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
    // Added by InsertMemberAfter (expected - used for diagnostics)
    /// <summary>
    /// Lists branches (default), or creates/deletes one when <paramref name="branchName"/> is
    /// given. Creation is a plain, non-destructive <c>git branch &lt;name&gt; [startPoint]</c> — it
    /// does not switch to the new branch; use <c>checkout</c> for that, optionally with
    /// <c>createBranch=true</c> to do both in one call. Deletion uses the safe <c>-d</c> form
    /// (refuses an unmerged branch) rather than <c>-D</c>, so a mistaken delete can't silently
    /// discard commits.
    /// </summary>
    private async Task<GitBranchResult> BranchAsync(
        string gitRoot, string? branchName, string? startPoint, bool deleteBranch, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                var (listExit, listOut, listErr) = await RunGitAsync(gitRoot,
                    ["branch", "--list", "--all"], cancellationToken);
                if (listExit != 0)
                    return new GitBranchResult { Success = false, Error = listErr.Trim() };

                var branches = new List<GitBranchEntry>();
                foreach (var rawLine in listOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    var isCurrent = line.StartsWith("* ", StringComparison.Ordinal);
                    var name = (isCurrent ? line[2..] : line[2..]).Trim();
                    if (name.Contains("->", StringComparison.Ordinal)) continue; // e.g. "remotes/origin/HEAD -> origin/main"
                    var isRemote = name.StartsWith("remotes/", StringComparison.Ordinal);
                    branches.Add(new GitBranchEntry
                    {
                        Name = isRemote ? name["remotes/".Length..] : name,
                        IsCurrent = isCurrent,
                        IsRemote = isRemote,
                    });
                }

                return new GitBranchResult { Success = true, Branches = branches };
            }

            if (deleteBranch)
            {
                var (delExit, _, delErr) = await RunGitAsync(gitRoot,
                    ["branch", "-d", branchName], cancellationToken);
                if (delExit != 0)
                    return new GitBranchResult
                    {
                        Success = false,
                        Error = $"git branch -d failed: {delErr.Trim()}. If the branch truly should be discarded unmerged, this tool deliberately does not expose -D; use the shell as a documented exception."
                    };

                return new GitBranchResult { Success = true, Deleted = branchName };
            }

            string[] createArgs = string.IsNullOrWhiteSpace(startPoint)
                ? ["branch", branchName]
                : ["branch", branchName, startPoint];
            var (createExit, _, createErr) = await RunGitAsync(gitRoot, createArgs, cancellationToken);
            if (createExit != 0)
                return new GitBranchResult { Success = false, Error = $"git branch failed: {createErr.Trim()}" };

            return new GitBranchResult { Success = true, Created = branchName };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git branch failed");
            return new GitBranchResult { Success = false, Error = $"Git branch failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Switches to <paramref name="branchName"/>, optionally creating it first
    /// (<c>git checkout -b</c>) when <paramref name="createBranch"/> is set and it doesn't already
    /// exist. Uses plain <c>checkout</c> rather than <c>switch</c> for compatibility with older git.
    /// </summary>
    private async Task<GitCheckoutResult> CheckoutAsync(
        string gitRoot, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return new GitCheckoutResult
            {
                Success = false,
                Error = "branchName is required for operation=checkout. Pass the branch to switch to, and createBranch=true if it doesn't exist yet."
            };
        }

        try
        {
            List<string> args = ["checkout"];
            if (createBranch)
                args.Add("-b");
            args.Add(branchName);
            if (createBranch && !string.IsNullOrWhiteSpace(startPoint))
                args.Add(startPoint);

            var (exitCode, _, stderr) = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            if (exitCode != 0)
                return new GitCheckoutResult { Success = false, Branch = branchName, Error = stderr.Trim() };

            return new GitCheckoutResult { Success = true, Branch = branchName, CreatedNewBranch = createBranch };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git checkout failed (branch={Branch})", branchName);
            return new GitCheckoutResult { Success = false, Branch = branchName, Error = $"Git checkout failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Pushes the current branch to <paramref name="remoteName"/>. Never forces — there is no
    /// exposed <c>--force</c>/<c>--force-with-lease</c>, since a wrong force-push is destructive to
    /// shared history and this tool has no confirmation gate for it yet.
    /// </summary>
    private async Task<GitRemoteResult> PushAsync(
        string gitRoot, string remoteName, bool setUpstream, CancellationToken cancellationToken)
    {
        try
        {
            var (branchExit, branchOut, _) = await RunGitAsync(gitRoot,
                ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            var branch = branchExit == 0 ? branchOut.Trim() : null;

            List<string> args = ["push"];
            if (setUpstream)
                args.Add("-u");
            args.Add(remoteName);
            if (!string.IsNullOrEmpty(branch))
                args.Add(branch);

            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            var detail = string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
            if (exitCode != 0)
                return new GitRemoteResult { Success = false, Operation = "push", Error = detail.Length > 0 ? detail : $"git push exited with code {exitCode}" };

            return new GitRemoteResult { Success = true, Operation = "push", Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git push failed (remote={Remote})", remoteName);
            return new GitRemoteResult { Success = false, Operation = "push", Error = $"Git push failed: {ex.Message}" };
        }
    }

    private async Task<GitRemoteResult> FetchAsync(
        string gitRoot, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, ["fetch", remoteName], cancellationToken);
            var detail = string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
            if (exitCode != 0)
                return new GitRemoteResult { Success = false, Operation = "fetch", Error = detail.Length > 0 ? detail : $"git fetch exited with code {exitCode}" };

            return new GitRemoteResult { Success = true, Operation = "fetch", Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git fetch failed (remote={Remote})", remoteName);
            return new GitRemoteResult { Success = false, Operation = "fetch", Error = $"Git fetch failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Pulls (fetch + merge, git's default) the current branch from <paramref name="remoteName"/>.
    /// No <c>--rebase</c>/<c>--force</c> options exposed yet — a plain merge pull is the
    /// least-surprising default and never rewrites local commits.
    /// </summary>
    private async Task<GitRemoteResult> PullAsync(
        string gitRoot, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunGitAsync(gitRoot, ["pull", remoteName], cancellationToken);
            var detail = string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(s => s.Length > 0));
            if (exitCode != 0)
                return new GitRemoteResult { Success = false, Operation = "pull", Error = detail.Length > 0 ? detail : $"git pull exited with code {exitCode}" };

            return new GitRemoteResult { Success = true, Operation = "pull", Detail = detail };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Git pull failed (remote={Remote})", remoteName);
            return new GitRemoteResult { Success = false, Operation = "pull", Error = $"Git pull failed: {ex.Message}" };
        }
    }}
// Added by AddTopLevelType (expected - used for diagnostics)
public class GitBranchEntry
{
    public string Name { get; set; } = "";
    public bool IsCurrent
    {
        get; set;
    }
    public bool IsRemote
    {
        get; set;
    }
}

public class GitBranchResult
{
    public bool Success
    {
        get; set;
    }
    public List<GitBranchEntry> Branches { get; set; } = [];
    public string? Created
    {
        get; set;
    }
    public string? Deleted
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
}// Added by AddTopLevelType (expected - used for diagnostics)
public class GitCheckoutResult
{
    public bool Success
    {
        get; set;
    }
    public string Branch { get; set; } = "";
    public bool CreatedNewBranch
    {
        get; set;
    }
    public string? Error
    {
        get; set;
    }
}// Added by AddTopLevelType (expected - used for diagnostics)
public class GitRemoteResult
{
    public bool Success
    {
        get; set;
    }
    public string Operation { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? Error
    {
        get; set;
    }
}