// Regression tests for the commit failure classification: a commit declined by a pre-commit / commit-msg hook used to fall into
// the NothingStagedAsync check and report GitNothingToCommit (with the hint to stage) whenever the index was empty. The cases below
// pin what the installed git really does: pre-commit runs BEFORE git's emptiness check (so it can reject an empty index), commit-msg
// runs only after it (so an empty index never reaches it and git's own "nothing to commit" is the real cause).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Git;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitHookRejectionTests
{
    private const string RejectingHook = "#!/bin/sh\necho the hook says no >&2\nexit 1";
    private const string AcceptingHook = "#!/bin/sh\nexit 0";

    private string _repoDir = null!;
    private GitTools _gitTools = null!;

    [TearDown]
    public void TearDown()
    {
        if (_repoDir is null || !Directory.Exists(_repoDir))
        {
            return;
        }

        // Windows can leave .git's object files read-only; clear that before recursive delete.
        foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repoDir, recursive: true);
    }

    // A fresh repo with an initial commit and an isolated hooks directory (core.hooksPath set explicitly so a user-level
    // hooksPath cannot redirect, and so silently skip, the fixture's hooks).
    private void NewRepo()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitHookReject_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "core.hooksPath", HooksDir.Replace('\\', '/'));
        Directory.CreateDirectory(HooksDir);
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "hello");
        Git("add", "-A");
        Git("commit", "-m", "initial commit");

        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
    }

    private string HooksDir => Path.Combine(_repoDir, ".git", "hooks");

    private string Git(params string[] args) => GitTestRepo.RunGit(_repoDir, args);

    private void InstallHook(string name, string script)
    {
        var path = Path.Combine(HooksDir, name);
        // LF only: the hook is run by Git for Windows' bundled sh, which rejects a CR after the shebang.
        File.WriteAllText(path, script.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private void StageAChange()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "a");
        Git("add", "a.txt");
    }

    private async Task<SentinelCallToolResult<object>> CommitAsync() =>
        await _gitTools.GitTool(reason: "hook rejection test", GitOperation.commit, message: "should not land");

    [Test]
    public async Task Commit_CommitMsgHookRejects_SomethingStaged_ReportsHookRejectedAsync()
    {
        NewRepo();
        InstallHook("commit-msg", RejectingHook);
        StageAChange();

        var result = await CommitAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(GitErrorCodes.HookRejected));
            Assert.That(result.ErrorData.Message, Does.Contain("the hook says no"), "the hook's own output is carried in the error text");
        });
    }

    [Test]
    public async Task Commit_PreCommitHookRejects_SomethingStaged_ReportsHookRejectedAsync()
    {
        NewRepo();
        InstallHook("pre-commit", RejectingHook);
        StageAChange();

        var result = await CommitAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(GitErrorCodes.HookRejected));
            Assert.That(result.ErrorData.Message, Does.Contain("the hook says no"));
        });
    }

    // git runs pre-commit before its emptiness check, so on the installed git a rejecting pre-commit hook declines even an empty index.
    [Test]
    public async Task Commit_PreCommitHookRejects_EmptyIndex_ReportsHookRejectedNotNothingToCommitAsync()
    {
        NewRepo();
        InstallHook("pre-commit", RejectingHook);

        var result = await CommitAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(GitErrorCodes.HookRejected));
            Assert.That(result.ErrorData.Message, Does.Contain("the hook says no"));
        });
    }

    // A passing pre-commit hook on an empty index: git itself finds nothing to commit (its status text is the tie-break), so the old answer stays.
    [Test]
    public async Task Commit_PreCommitHookPasses_EmptyIndex_StillReportsNothingToCommitAsync()
    {
        NewRepo();
        InstallHook("pre-commit", AcceptingHook);

        var result = await CommitAsync();

        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(GitErrorCodes.NothingToCommit));
    }

    // git never runs commit-msg when the index is empty, so a rejecting commit-msg hook is not the cause there.
    [Test]
    public async Task Commit_CommitMsgHookRejects_EmptyIndex_ReportsNothingToCommitAsync()
    {
        NewRepo();
        InstallHook("commit-msg", RejectingHook);

        var result = await CommitAsync();

        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(GitErrorCodes.NothingToCommit));
    }

    [Test]
    public async Task Commit_NoHook_EmptyIndex_StillReportsNothingToCommitAsync()
    {
        NewRepo();

        var result = await CommitAsync();

        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(GitErrorCodes.NothingToCommit));
    }
}
