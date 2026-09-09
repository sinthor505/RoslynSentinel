using System.Diagnostics;

namespace RoslynSentinel.Tools.PlanStepRunner;

/// <summary>
/// Runs each plan step in its own git worktree branched off a dedicated, runner-owned branch
/// (default "eval-defect-remediation-v2-auto") — never the user's own in-progress branch/worktree.
/// A successful step commits onto that branch and the worktree is removed; a failed/halted step
/// leaves its worktree in place so the run is inspectable and independently re-runnable via
/// --start-step/--end-step without disturbing anything upstream.
/// </summary>
public sealed class GitWorktreeManager(string sourceRepo, string branch, string worktreeRoot)
{
    public void EnsureBranchExists()
    {
        if (RunGit(sourceRepo, "rev-parse", "--verify", "--quiet", branch).ExitCode != 0)
        {
            Console.WriteLine($"Branch '{branch}' does not exist — creating it off the current HEAD of {sourceRepo}.");
            RunGitOrThrow(sourceRepo, "branch", branch);
        }
    }

    public string CreateWorktree(string stepFileName)
    {
        Directory.CreateDirectory(worktreeRoot);
        var name = Path.GetFileNameWithoutExtension(stepFileName);
        var path = Path.Combine(worktreeRoot, name);

        if (Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"Worktree path already exists: {path}. If this is a leftover from a halted/failed " +
                "run, inspect it, then remove it (git worktree remove) before re-running this step.");
        }

        RunGitOrThrow(sourceRepo, "worktree", "add", path, branch);
        return path;
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

    public void RemoveWorktree(string worktreePath)
    {
        RunGitOrThrow(sourceRepo, "worktree", "remove", worktreePath);
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
