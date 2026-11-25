// Regression tests for blocking_error_git_commit_returns_other_sessions_hash: Git(commit) / Git(revert) used to read HEAD back
// after the git command, so a commit by another server instance sharing the working tree that landed in between was reported
// as this call's CommitHash/Message. The race window is reproduced deterministically with a post-commit hook, which git runs
// inside `git commit` / `git revert` AFTER the new commit exists and BEFORE the command returns: the hook moves HEAD onto a
// "foreign" commit exactly as a racing session would, so any implicit HEAD readback afterwards sees the foreign commit.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: pipe EOF reads starved under load. Fix pointer: GitProcess.TryRun dedicated reader threads and GitTestRepo (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitCommitHashRaceTests
{
    private const string ForeignSubject = "foreign commit by another session";

    // Runs inside git commit/revert after the new commit is created: parks a commit with the same tree on top of it and
    // moves HEAD there (commit-tree/update-ref run no hooks, so no recursion). Works on a branch and on a detached HEAD.
    private const string ForeignCommitHook = """
        #!/bin/sh
        tree=$(git rev-parse "HEAD^{tree}")
        parent=$(git rev-parse HEAD)
        foreign=$(git commit-tree -p "$parent" -m "foreign commit by another session" "$tree")
        git update-ref HEAD "$foreign"
        """;

    private string _repoDir = null!;
    private GitTools _gitTools = null!;

    [TearDown]
    public void TearDown() => DeleteDirectoryTree(_repoDir);

    private static void DeleteDirectoryTree(string? directory)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        // Windows can leave .git's object files read-only; clear that before recursive delete.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }

    // A fresh repo with an isolated hooks directory. core.hooksPath is set explicitly so a user-level hooksPath cannot
    // redirect (and so silently skip) the fixture's hooks.
    private void NewRepo(bool withInitialCommit)
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitRace_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "core.hooksPath", HooksDir.Replace('\\', '/'));
        Directory.CreateDirectory(HooksDir);
        if (withInitialCommit)
        {
            WriteFile("README.md", "hello");
            Git("add", "-A");
            Git("commit", "-m", "initial commit");
        }

        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
    }

    private string HooksDir => Path.Combine(_repoDir, ".git", "hooks");

    private string Git(params string[] args) => GitTestRepo.RunGit(_repoDir, args);

    private void WriteFile(string relPath, string content) => File.WriteAllText(Path.Combine(_repoDir, relPath), content);

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

    private string[] FilesOf(string commit) =>
        Git("show", "--no-renames", "--name-only", "--format=", commit)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Order()
            .ToArray();

    // The precondition (the hook really moved HEAD onto the foreign commit) is asserted first: without it the test would
    // pass trivially with the old HEAD-readback code.
    private void AssertHeadIsForeign() =>
        Assert.That(Git("log", "-1", "--format=%s", "HEAD"), Is.EqualTo(ForeignSubject),
            "precondition: the post-commit hook must have moved HEAD onto the foreign commit, or this test proves nothing.");

    private async Task<GitCommitResult> CommitAsync(string message, string? paths = null, GitStageScope? scope = null, bool amend = false)
    {
        var result = await _gitTools.GitTool(reason: "race test", GitOperation.commit, message: message, paths: paths, scope: scope, amend: amend);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        return (GitCommitResult)result.SuccessData!;
    }

    private void AssertOwnCommit(GitCommitResult commit, string expectedMessage, params string[] expectedFiles)
    {
        AssertHeadIsForeign();
        var head = Git("rev-parse", "HEAD");
        Assert.That(commit.CommitHash, Is.Not.EqualTo(head), "CommitHash must not be the foreign commit HEAD points at.");
        Assert.That(commit.CommitHash, Has.Length.EqualTo(40));
        Assert.That(Git("log", "-1", "--format=%B", commit.CommitHash), Is.EqualTo(expectedMessage),
            "CommitHash must resolve to the commit carrying the submitted message.");
        Assert.That(FilesOf(commit.CommitHash), Is.EqualTo(expectedFiles.Order().ToArray()),
            "CommitHash must resolve to the commit with exactly the submitted file list.");
        Assert.That(commit.Message, Is.EqualTo(expectedMessage), "Message must describe this commit, not the foreign one.");
    }

    [Test]
    public async Task Commit_ForeignCommitLandsDuringGitCommit_ReturnsOwnHashAndMessageAsync()
    {
        NewRepo(withInitialCommit: true);
        InstallHook("post-commit", ForeignCommitHook);
        WriteFile("a.txt", "a");
        WriteFile("b.txt", "b");

        var commit = await CommitAsync("own commit", paths: "a.txt");

        AssertOwnCommit(commit, "own commit", "a.txt");
    }

    [Test]
    public async Task Commit_OnUnbornBranch_RootCommitSummaryLine_ReturnsOwnRootHashAsync()
    {
        NewRepo(withInitialCommit: false);
        InstallHook("post-commit", ForeignCommitHook);
        WriteFile("a.txt", "a");

        // "[master (root-commit) abc1234] ..." is the summary shape here.
        var commit = await CommitAsync("first ever commit", scope: GitStageScope.all);

        AssertOwnCommit(commit, "first ever commit", "a.txt");
        Assert.That(Git("rev-list", "--parents", "-n", "1", commit.CommitHash), Is.EqualTo(commit.CommitHash),
            "the returned commit must be the root commit (no parents).");
    }

    [Test]
    public async Task Commit_OnDetachedHead_ReturnsOwnHashAsync()
    {
        NewRepo(withInitialCommit: true);
        Git("checkout", "--detach");
        InstallHook("post-commit", ForeignCommitHook);
        WriteFile("a.txt", "a");

        // "[detached HEAD abc1234] ..." is the summary shape here.
        var commit = await CommitAsync("detached commit", paths: "a.txt");

        AssertOwnCommit(commit, "detached commit", "a.txt");
    }

    [Test]
    public async Task Commit_MessageContainingBracketsAndHashLikeText_ReturnsOwnHashAsync()
    {
        NewRepo(withInitialCommit: true);
        InstallHook("post-commit", ForeignCommitHook);
        WriteFile("a.txt", "a");
        const string message = "[abc1234] fix [deadbeef] and [master 1234567] parser";

        var commit = await CommitAsync(message, paths: "a.txt");

        AssertOwnCommit(commit, message, "a.txt");
    }

    [Test]
    public async Task Commit_HookPrintsLookAlikeSummaryLines_ReturnsOwnHashAsync()
    {
        NewRepo(withInitialCommit: true);
        // Stdout noise from hooks, which resolves to a REAL commit (the initial one): the first/any match must not win.
        // (Git may route hook stdout to stderr; either way the real summary line must still be what is used.)
        InstallHook("pre-commit", "#!/bin/sh\necho \"[master $(git rev-parse --short HEAD)] pre-commit noise\"");
        InstallHook("post-commit", "#!/bin/sh\necho \"[master $(git rev-parse --short HEAD~1)] post-commit noise\"\n" + ForeignCommitHook);
        var initial = Git("rev-parse", "HEAD");
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("own commit", paths: "a.txt");

        AssertOwnCommit(commit, "own commit", "a.txt");
        Assert.That(commit.CommitHash, Is.Not.EqualTo(initial));
    }

    [Test]
    public async Task Commit_Amend_ForeignCommitLandsDuringGitCommit_ReturnsAmendedHashAsync()
    {
        NewRepo(withInitialCommit: true);
        var initial = Git("rev-parse", "HEAD");
        WriteFile("b.txt", "b");
        Git("add", "-A");
        Git("commit", "-m", "to be amended");
        var beforeAmend = Git("rev-parse", "HEAD");
        InstallHook("post-commit", ForeignCommitHook);
        WriteFile("a.txt", "a");

        var commit = await CommitAsync("amended subject", scope: GitStageScope.all, amend: true);

        AssertOwnCommit(commit, "amended subject", "a.txt", "b.txt");
        Assert.That(commit.CommitHash, Is.Not.EqualTo(beforeAmend), "an amend creates a new commit.");
        Assert.That(Git("rev-parse", commit.CommitHash + "^"), Is.EqualTo(initial), "the amended commit replaces the old HEAD.");
    }

    [Test]
    public async Task Revert_ForeignCommitLandsDuringGitRevert_ReturnsOwnRevertHashAsync()
    {
        NewRepo(withInitialCommit: true);
        WriteFile("a.txt", "a");
        Git("add", "-A");
        Git("commit", "-m", "commit to revert");
        var toRevert = Git("rev-parse", "HEAD");
        InstallHook("post-commit", ForeignCommitHook);

        var result = await _gitTools.GitTool(reason: "race test", GitOperation.revert, @ref: toRevert);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        AssertHeadIsForeign();
        Assert.That(revert.CommitHash, Is.Not.EqualTo(Git("rev-parse", "HEAD")), "CommitHash must not be the foreign commit HEAD points at.");
        Assert.That(revert.CommitHash, Has.Length.EqualTo(40));
        Assert.That(Git("log", "-1", "--format=%s", revert.CommitHash), Is.EqualTo("Revert \"commit to revert\""));
        Assert.That(Git("rev-parse", revert.CommitHash + "^"), Is.EqualTo(toRevert), "the revert commit sits directly on the reverted commit.");
        Assert.That(FilesOf(revert.CommitHash), Is.EqualTo(new[] { "a.txt" }));
        Assert.That(result.Findings, Is.Null.Or.Empty, "a resolved hash needs no warning finding.");
    }

    [Test]
    public async Task Revert_NoCommit_KeepsEmptyHashAndPendingFlagAsync()
    {
        NewRepo(withInitialCommit: true);
        WriteFile("a.txt", "a");
        Git("add", "-A");
        Git("commit", "-m", "commit to revert");
        var toRevert = Git("rev-parse", "HEAD");

        var result = await _gitTools.GitTool(reason: "race test", GitOperation.revert, @ref: toRevert, noCommit: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var revert = (GitRevertResult)result.SuccessData!;
        Assert.That(revert.PendingCommit, Is.True);
        Assert.That(revert.CommitHash, Is.Empty);
        Assert.That(result.Findings, Is.Null.Or.Empty, "no commit was requested, so an empty hash is not a warning.");
    }

    // ----- fallback rules (summary line unusable) -----

    private GitImpl NewGitImpl() =>
        new(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") });

    private string[] HeadWithParents() =>
        Git("rev-list", "--parents", "-n", "1", "HEAD").Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task Derive_UnparsableOutput_HeadProvenByParentChain_ReturnsHeadAsync()
    {
        NewRepo(withInitialCommit: true);
        var before = HeadWithParents();
        WriteFile("a.txt", "a");
        Git("add", "-A");
        Git("commit", "-m", "ours");

        var hash = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, "no summary line here\n", before, amend: false, CancellationToken.None);

        Assert.That(hash, Is.EqualTo(Git("rev-parse", "HEAD")));
    }

    [Test]
    public async Task Derive_UnresolvableAbbreviation_FallsBackToProvenHeadAsync()
    {
        NewRepo(withInitialCommit: true);
        var before = HeadWithParents();
        WriteFile("a.txt", "a");
        Git("add", "-A");
        Git("commit", "-m", "ours");

        // 0000000 names no object: rev-parse --verify fails, so the parent-chain fallback decides.
        var hash = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, "[master 0000000] ours\n", before, amend: false, CancellationToken.None);

        Assert.That(hash, Is.EqualTo(Git("rev-parse", "HEAD")));
    }

    [Test]
    public async Task Derive_UnparsableOutput_ForeignCommitOnTop_ReturnsEmptyInsteadOfGuessingAsync()
    {
        NewRepo(withInitialCommit: true);
        var before = HeadWithParents();
        WriteFile("a.txt", "a");
        Git("add", "-A");
        Git("commit", "-m", "ours");
        Git("commit", "--allow-empty", "-m", ForeignSubject);

        var hash = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, "no summary line here\n", before, amend: false, CancellationToken.None);

        Assert.That(hash, Is.Empty, "HEAD is not provably ours, so no hash is claimed.");
    }

    [Test]
    public async Task Derive_UnparsableOutput_NothingCommitted_ReturnsEmptyAsync()
    {
        NewRepo(withInitialCommit: true);
        var before = HeadWithParents();

        var hash = await NewGitImpl().DeriveNewCommitHashAsync(_repoDir, "no summary line here\n", before, amend: false, CancellationToken.None);

        Assert.That(hash, Is.Empty, "HEAD did not change, so it is not a new commit.");
    }

    [Test]
    public void IsOwnHead_ParentChainRules()
    {
        string[] old = ["old", "older"];

        Assert.That(GitImpl.IsOwnHead(old, ["new", "old"], replacesHead: false), Is.True, "new commit on top of the old HEAD");
        Assert.That(GitImpl.IsOwnHead(old, ["foreign", "new"], replacesHead: false), Is.False, "another commit on top of ours");
        Assert.That(GitImpl.IsOwnHead(old, ["old", "older"], replacesHead: false), Is.False, "HEAD did not move");
        Assert.That(GitImpl.IsOwnHead(null, ["root"], replacesHead: false), Is.True, "root commit on an unborn branch");
        Assert.That(GitImpl.IsOwnHead(null, ["foreign", "root"], replacesHead: false), Is.False, "a commit exists on a branch that was unborn");
        Assert.That(GitImpl.IsOwnHead(old, ["amended", "older"], replacesHead: true), Is.True, "amend keeps the old HEAD's parents");
        Assert.That(GitImpl.IsOwnHead(old, ["old", "older"], replacesHead: true), Is.False, "amend that changed nothing");
        Assert.That(GitImpl.IsOwnHead(old, ["foreign", "amended"], replacesHead: true), Is.False, "foreign commit on top of an amend");
        Assert.That(GitImpl.IsOwnHead(null, ["amended"], replacesHead: true), Is.False, "nothing to amend");
        Assert.That(GitImpl.IsOwnHead(old, null, replacesHead: false), Is.False, "HEAD unreadable");
    }
}
