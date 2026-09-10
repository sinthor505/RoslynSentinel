using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>
/// Runs each plan step in its own git worktree, branched per <see cref="IStepBranchStrategy"/>
/// (default: one dedicated run-owned branch shared by every step, e.g.
/// "eval-defect-remediation-v2-auto-&lt;run-timestamp&gt;" — see <see cref="SharedBranchStrategy"/>)
/// — never the user's own in-progress branch/worktree.
/// A successful step commits onto its branch and the worktree is removed; a failed/halted step
/// leaves its worktree in place under &lt;runDir&gt;\&lt;step-name&gt;\Worktree so the run is
/// inspectable and independently re-runnable via --start-step/--end-step (or --clean, to discard
/// that leftover worktree first) without disturbing anything upstream.
/// </summary>
public sealed class GitWorktreeManager(string sourceRepo, IStepBranchStrategy branchStrategy, string runDir)
{
    private string WorktreePath(string stepFileName) =>
        Path.Combine(runDir, Path.GetFileNameWithoutExtension(stepFileName), "Worktree");

    /// <summary>Creates this step's branch off <see cref="IStepBranchStrategy.BaseRefFor"/> if it doesn't exist yet. No-op otherwise (e.g. a shared branch already created by an earlier step, or resuming onto one from a prior run).</summary>
    public void EnsureBranchExists(PlanStepFile step)
    {
        var branch = branchStrategy.BranchFor(step);
        if (RunGit(sourceRepo, "rev-parse", "--verify", "--quiet", branch).ExitCode != 0)
        {
            var baseRef = branchStrategy.BaseRefFor(step);
            Console.WriteLine($"Branch '{branch}' does not exist — creating it off '{baseRef}' in {sourceRepo}.");
            RunGitOrThrow(sourceRepo, "branch", branch, baseRef);
        }
    }

    public string CreateWorktree(PlanStepFile step)
    {
        var path = WorktreePath(step.FileName);

        if (Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"Worktree path already exists: {path}. If this is a leftover from a halted/failed " +
                "run, inspect it, then remove it (git worktree remove) before re-running this step, " +
                "or pass --clean to have this run remove it automatically.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var branch = branchStrategy.BranchFor(step);
        var (exitCode, stdOut, stdErr) = RunGit(sourceRepo, "worktree", "add", path, branch);
        if (exitCode != 0)
        {
            var blockingPathMatch = Regex.Match(stdErr, @"already used by worktree at '([^']+)'");
            if (blockingPathMatch.Success)
            {
                throw new InvalidOperationException(
                    $"Branch '{branch}' is already checked out in another worktree, left over from " +
                    $"a different (possibly halted) run: {blockingPathMatch.Groups[1].Value}. Resume " +
                    "that run instead (roslynsentinel-planstep.ps1 -ExistingRun <its timestamp>), or " +
                    "remove it (git worktree remove --force <path>) if it's no longer needed, before " +
                    "retrying this run.");
            }

            throw new InvalidOperationException(
                $"git worktree add {path} {branch} failed in {sourceRepo} (exit {exitCode}):\n{stdOut}\n{stdErr}");
        }

        return path;
    }

    /// <summary>Used by --clean to discard a leftover worktree (possibly with uncommitted model
    /// edits) for a step about to (re-)run, before CreateWorktree is called for it. No-op if the
    /// step has no existing worktree.</summary>
    public void RemoveWorktreeIfExists(string stepFileName)
    {
        var path = WorktreePath(stepFileName);

        if (!Directory.Exists(path))
        {
            return;
        }

        Console.WriteLine($"--clean: removing existing worktree at {path}");
        RunGitOrThrow(sourceRepo, "-c", "core.longpaths=true", "worktree", "remove", "--force", path);
    }

    /// <summary>
    /// Every path the model touched in <paramref name="worktreePath"/> — modified, deleted, and
    /// untracked alike — as forward-slashed repo-relative paths.
    /// </summary>
    /// <remarks>
    /// Called <em>before</em> <see cref="CommitWorktree"/>'s `git add -A`, so a read-only step's
    /// violation is caught while the tree is still unstaged. `--untracked-files=all` is explicit
    /// rather than relying on the default: a step that only adds new files would otherwise be
    /// invisible here if the repo's status.showUntrackedFiles config were ever changed.
    /// </remarks>
    public IReadOnlyList<string> GetDirtyPaths(string worktreePath)
    {
        var status = RunGit(worktreePath, "status", "--porcelain", "--untracked-files=all");
        return ParsePorcelainPaths(status.StdOut);
    }

    public void CommitWorktree(string worktreePath, string message)
    {
        RunGitOrThrow(worktreePath, "add", "-A");

        // Nothing to commit is not an error (a step whose model run made no on-disk changes) —
        // just advance the branch pointer as-is by doing nothing further here.
        var status = RunGit(worktreePath, "status", "--porcelain");
        if (string.IsNullOrWhiteSpace(status.StdOut))
        {
            Console.WriteLine($"No changes to commit for {message} — worktree tip already matches branch tip.");
            return;
        }

        RunGitOrThrow(worktreePath, "commit", "-m", message);
    }

    /// <summary>
    /// Pulls the path out of each `git status --porcelain` line. Each line is "XY &lt;path&gt;",
    /// where a rename/copy renders as "old -&gt; new" — the new path is the one that matters for
    /// "what did this step touch", so that's what's returned. Quoted paths (git quotes anything
    /// with spaces or non-ASCII under the default core.quotepath) are unwrapped.
    /// </summary>
    private static List<string> ParsePorcelainPaths(string porcelainOutput)
    {
        var paths = new List<string>();
        foreach (var rawLine in porcelainOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length <= 3)
            {
                continue;
            }

            var path = line[3..].Trim();

            var renameArrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameArrow >= 0)
            {
                path = path[(renameArrow + 4)..];
            }

            if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            {
                path = path[1..^1];
            }

            if (path.Length > 0)
            {
                paths.Add(path.Replace('\\', '/'));
            }
        }

        return paths;
    }

    /// <summary>
    /// Removes a step's worktree after its work is already committed. Returns the git error text on
    /// failure instead of throwing — Windows' ~260-char path limit can make `git worktree remove`
    /// fail deleting deeply-nested build output (e.g. "Filename too long") even though the commit
    /// itself succeeded, and that's not worth aborting the whole run over (see --clean, which already
    /// force-removes leftover worktrees on a later retry).
    /// </summary>
    public string? TryRemoveWorktree(string worktreePath)
    {
        var (exitCode, stdOut, stdErr) = RunGit(sourceRepo, "-c", "core.longpaths=true", "worktree", "remove", worktreePath);
        return exitCode == 0 ? null : $"{stdOut}\n{stdErr}".Trim();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDirectory, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in gitArgs)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static void RunGitOrThrow(string workingDirectory, params string[] gitArgs)
    {
        var (exitCode, stdOut, stdErr) = RunGit(workingDirectory, gitArgs);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', gitArgs)} failed in {workingDirectory} (exit {exitCode}):\n{stdOut}\n{stdErr}");
        }
    }
}
