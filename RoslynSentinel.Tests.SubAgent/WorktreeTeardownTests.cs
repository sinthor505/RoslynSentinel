using System.Diagnostics;

using RoslynSentinel.Common.GitWorktree;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.SubAgent;

/// <summary>
/// The worktree lifecycle a SubAgent call relies on, against a real throwaway git repo: create a
/// worktree, let a "model" dirty it, then force-remove it and delete its branch. The non-forced
/// removal PlanStepRunner uses refuses a dirty tree, which is exactly why the force option exists.
/// </summary>
[TestFixture]
public class WorktreeTeardownTests
{
    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        Assume.That(RunGit(Path.GetTempPath(), "--version").ExitCode, Is.EqualTo(0), "git must be on PATH");

        _root = Path.Combine(Path.GetTempPath(), "subagent-worktree-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);

        RunGitOrFail(_repo, "init", "--quiet");
        RunGitOrFail(_repo, "config", "user.email", "test@example.com");
        RunGitOrFail(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "hello");
        RunGitOrFail(_repo, "add", "-A");
        RunGitOrFail(_repo, "commit", "--quiet", "-m", "initial");
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        // git marks some of its own files read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdOut, stdErr);
    }

    private static void RunGitOrFail(string workingDirectory, params string[] args)
    {
        var (exitCode, _, stdErr) = RunGit(workingDirectory, args);
        Assert.That(exitCode, Is.EqualTo(0), $"git {string.Join(' ', args)} failed: {stdErr}");
    }

    private (GitWorktreeManager Git, string Worktree, string Branch) CreateRun()
    {
        var runId = SubAgentRunNaming.NewRunId();
        var branch = SubAgentRunNaming.BranchName(runId);
        var logged = new List<string>();
        var git = new GitWorktreeManager(
            _repo, new SharedBranchStrategy(branch, "HEAD"), Path.Combine(_root, "runs", runId), logged.Add);

        git.EnsureBranchExists(SubAgentWorktreeStep.Instance);
        var worktree = git.CreateWorktree(SubAgentWorktreeStep.Instance);
        return (git, worktree, branch);
    }

    private bool BranchExists(string branch) =>
        RunGit(_repo, "rev-parse", "--verify", "--quiet", branch).ExitCode == 0;

    [Test]
    public void DirtyWorktree_IsRefusedWithoutForce_AndRemovedWithIt()
    {
        var (git, worktree, _) = CreateRun();
        File.WriteAllText(Path.Combine(worktree, "model-edit.txt"), "the model wrote this");

        var withoutForce = git.TryRemoveWorktree(worktree);
        var withForce = git.TryRemoveWorktree(worktree, force: true);

        Assert.Multiple(() =>
        {
            Assert.That(withoutForce, Is.Not.Null, "a dirty worktree must be refused unless forced");
            Assert.That(withForce, Is.Null, withForce);
            Assert.That(Directory.Exists(worktree), Is.False);
        });
    }

    [Test]
    public void GetDirtyPaths_ReportsWhatTheModelTouched()
    {
        var (git, worktree, _) = CreateRun();
        File.WriteAllText(Path.Combine(worktree, "a.txt"), "changed");
        File.WriteAllText(Path.Combine(worktree, "new.txt"), "added");

        Assert.That(git.GetDirtyPaths(worktree), Is.EquivalentTo(new[] { "a.txt", "new.txt" }));
    }

    [Test]
    [Category("SubAgentWorktreeStep")] // sentinel:auto-category
    public void TryDeleteBranch_RemovesTheRunsBranch_OnceTheWorktreeIsGone()
    {
        var (git, worktree, branch) = CreateRun();
        Assert.That(BranchExists(branch), Is.True, "precondition: the run's branch was created");

        Assert.That(git.TryRemoveWorktree(worktree, force: true), Is.Null);
        var deleteError = git.TryDeleteBranch(SubAgentWorktreeStep.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(deleteError, Is.Null, deleteError);
            Assert.That(BranchExists(branch), Is.False);
        });
    }

    [Test]
    [Category("SubAgentWorktreeStep")] // sentinel:auto-category
    public void TryDeleteBranch_ReturnsAnError_InsteadOfThrowing_WhenTheBranchIsStillCheckedOut()
    {
        var (git, _, branch) = CreateRun();

        var deleteError = git.TryDeleteBranch(SubAgentWorktreeStep.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(deleteError, Is.Not.Null);
            Assert.That(BranchExists(branch), Is.True);
        });
    }

    [Test]
    public void TwoRunsAtOnce_GetIndependentWorktreesAndBranches()
    {
        var first = CreateRun();
        var second = CreateRun();

        Assert.Multiple(() =>
        {
            Assert.That(second.Worktree, Is.Not.EqualTo(first.Worktree));
            Assert.That(second.Branch, Is.Not.EqualTo(first.Branch));
            Assert.That(Directory.Exists(first.Worktree), Is.True);
            Assert.That(Directory.Exists(second.Worktree), Is.True);
        });
    }
}
