using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the full-sentence text that the remaining GitErrorDetails members render (ErrorData.Message and
// ErrorData.Detail), pinned through the tool classes (GitTools / GitRemoteTools), not GitImpl. Literals were captured
// from the pre-extraction source; a changed literal is a wording change and needs a decision, not a quiet update.
// Sibling of GitHintTableGoldenTests (the other 12 members).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintTableGoldenHunksTests
{
    private const string RejectingPreCommitHook = "#!/bin/sh\necho the hook says no >&2\nexit 1\n";

    private string _repoDir = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenHunks_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "readme");
        GitTestRepo.CommitAll(_repoDir, "init");

        // Same wiring as GitToolsSmokeTests: the tools only need the fake solution path to find the git root.
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
        _gitRemote = new GitRemoteTools(workspaceManager, NullLogger<GitRemoteTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        // Windows can leave .git object files read-only; clear that before the recursive delete.
        if (!Directory.Exists(_repoDir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repoDir, recursive: true);
    }

    [Test]
    public async Task TargetNotFound_TagDeleteMissingTag_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag delete missing",
            GitRemoteOperation.tag,
            action: GitAction.delete,
            tagName: "no-such-tag");

        AssertGolden(
            result,
            GitErrorCodes.TargetNotFound,
            "Tag 'no-such-tag' does not exist. The repository has no tags. Nothing was changed.",
            "Check the exact name with GitRemote(reason: \"<why you call this>\", operation: tag, action: list), GitRemote(reason: \"<why you call this>\", operation: stash, action: list), GitRemote(reason: \"<why you call this>\", operation: worktree, action: list) or GitRemote(reason: \"<why you call this>\", operation: branch), then retry.");
    }

    [Test]
    public async Task InvalidName_TagCreateWithSpace_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden invalid tag name",
            GitRemoteOperation.tag,
            action: GitAction.create,
            tagName: "bad name");

        AssertGolden(
            result,
            GitErrorCodes.InvalidName,
            "'bad name' is not a valid tag name (no spaces, '..', '~', '^', ':', leading '-' or trailing '.lock'). Nothing was changed.",
            "Use a name that git accepts: no spaces, no '..', '~', '^', ':' or trailing '.lock'. Then retry.");
    }

    [Test]
    public async Task WorkingTreeDirty_StashApplyOntoDirtyTree_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");
        var pushed = await _gitRemote.GitRemoteTool(
            reason: "golden stash setup",
            GitRemoteOperation.stash,
            action: GitAction.push);
        Assert.That(pushed.IsError, Is.False, "setup: stash push must succeed");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "three");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash apply dirty",
            GitRemoteOperation.stash,
            action: GitAction.apply,
            stashIndex: 0);

        AssertGolden(
            result,
            GitErrorCodes.WorkingTreeDirty,
            "Cannot apply stash@{0}: the tracked tree has uncommitted changes in: a.txt. Nothing was changed.",
            "Commit them (Git(reason: \"<why you call this>\", operation: commit)) or stash them (GitRemote(reason: \"<why you call this>\", operation: stash, action: push)) first; applying onto a dirty tree can mix the two sets of changes. Git(reason: \"<why you call this>\", operation: status) lists the changed paths.");
    }

    [Test]
    public async Task NoChanges_StashPushOnCleanTree_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash push clean",
            GitRemoteOperation.stash,
            action: GitAction.push);

        AssertGolden(
            result,
            GitErrorCodes.NoChanges,
            "Nothing to stash: no modified tracked files. Nothing was changed.",
            "Nothing to act on. Call Git(reason: \"<why you call this>\", operation: status) to see what changed, then retry with the paths that have changes.");
    }

    [Test]
    public async Task StashConflict_StashApplyConflictsWithTree_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");
        var pushed = await _gitRemote.GitRemoteTool(
            reason: "golden stash setup",
            GitRemoteOperation.stash,
            action: GitAction.push);
        Assert.That(pushed.IsError, Is.False, "setup: stash push must succeed");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "three");
        GitTestRepo.CommitAll(_repoDir, "change a to three");

        // The stash was made on "one" and changes a.txt again, so applying it onto "three" conflicts and is rolled back.
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash apply conflict",
            GitRemoteOperation.stash,
            action: GitAction.apply,
            stashIndex: 0);

        AssertGolden(
            result,
            GitErrorCodes.StashConflict,
            "Applying stash@{0} conflicts with the current tree in: a.txt. The attempt was rolled back (git reset --merge): the tracked tree is clean again and stash@{0} was kept. Nothing was changed.",
            "Check out the commit or branch the stash was created on (GitRemote(reason: \"<why you call this>\", operation: checkout)), then apply again; GitRemote(reason: \"<why you call this>\", operation: stash, action: list) shows where each entry was made.");
    }

    [Test]
    public async Task Refused_WorktreeAddInsideRepo_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree inside repo",
            GitRemoteOperation.worktree,
            action: GitAction.add,
            branchName: "golden-wt",
            worktreePath: Path.Combine(_repoDir, "wt-inside"));

        AssertGolden(
            result,
            GitErrorCodes.Refused,
            "A worktree inside the repository would be compiled into the loaded solution and shows up in this repo's diffs (the Worktree/ trap). Use a sibling directory, e.g. '" + _repoDir + "-golden-wt'. Nothing was changed.",
            "Take the safe alternative the message names, then retry. Nothing was changed.");
    }

    [Test]
    public async Task HunkUnsupported_HunksOfUntrackedFile_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "new.txt"), "untracked");

        var result = await _gitTools.GitTool(
            reason: "golden hunks untracked",
            GitOperation.hunks,
            paths: "new.txt");

        AssertGolden(
            result,
            GitErrorCodes.HunkUnsupported,
            "'new.txt' is untracked: untracked files have no hunks to choose from; stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"...\"). Nothing was changed.",
            "Stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"<path>\") instead, or use a file kind that has text hunks.");
    }

    [Test]
    public async Task HunkSelection_HunksOfTwoFiles_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_repoDir, "b.txt"), "b");

        var result = await _gitTools.GitTool(
            reason: "golden hunks two paths",
            GitOperation.hunks,
            paths: "a.txt,b.txt");

        AssertGolden(
            result,
            GitErrorCodes.HunkSelection,
            "hunks works on one file at a time; got 2. Nothing was changed.",
            "Call Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pass hunkIds or lineRange exactly as it lists them.");
    }

    [Test]
    public async Task IndexLocked_CommitWithIndexLockHeld_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "a");
        GitTestRepo.RunGit(_repoDir, "add", "a.txt");
        // A held lock file stands in for a concurrent git process; no other process touches the repo.
        var indexLockPath = Path.Combine(_repoDir, ".git", "index.lock");
        File.WriteAllText(indexLockPath, string.Empty);

        var result = await _gitTools.GitTool(
            reason: "golden commit index locked",
            GitOperation.commit,
            message: "should not land");

        // Git prints the lock path with forward slashes; GitImpl joins the git dir (forward slashes) with a backslash.
        AssertGolden(
            result,
            GitErrorCodes.IndexLocked,
            "git commit failed: fatal: Unable to create '" + indexLockPath.Replace('\\', '/') + "': File exists.\n\nAnother git process seems to be running in this repository, or the lock file may be stale",
            "Another git process holds " + _repoDir.Replace('\\', '/') + "/.git\\index.lock. Wait for it to finish and retry the commit; if no git process is running, the lock is stale and deleting that file clears it.");
    }

    [Test]
    public async Task HookRejected_PreCommitHookRejects_PinsMessageAndDetailAsync()
    {
        var hooksDir = Path.Combine(_repoDir, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        // Isolated hooks directory so a user-level core.hooksPath cannot redirect the fixture's hook.
        GitTestRepo.RunGit(_repoDir, "config", "core.hooksPath", hooksDir.Replace('\\', '/'));
        var hookPath = Path.Combine(hooksDir, "pre-commit");
        // LF only: the hook is run by Git for Windows' bundled sh, which rejects a CR after the shebang.
        File.WriteAllText(hookPath, RejectingPreCommitHook);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "a");
        GitTestRepo.RunGit(_repoDir, "add", "a.txt");

        var result = await _gitTools.GitTool(
            reason: "golden commit hook rejected",
            GitOperation.commit,
            message: "should not land");

        AssertGolden(
            result,
            GitErrorCodes.HookRejected,
            "git commit failed: the hook says no",
            "The text above ends with the hook's own output. Fix what the hook reports and commit again; the hooks live in .git/hooks, or in the directory named by core.hooksPath.");
    }

    [Test]
    public async Task HunkStale_StageWithWrongFingerprint_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        var result = await _gitTools.GitTool(
            reason: "golden stage stale fingerprint",
            GitOperation.stage,
            paths: "a.txt",
            hunkIds: "1",
            hunkFingerprint: "not-the-fingerprint");

        AssertGolden(
            result,
            GitErrorCodes.HunkStale,
            "'a.txt' changed since the hunks were listed (fingerprint not-the-fingerprint vs 82a8e5e4cb0c). Re-run Git(reason: \"<why you call this>\", operation: hunks, paths: \"a.txt\") and pick again. Nothing was staged.",
            "Re-run Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pick again with the new hunkFingerprint. Nothing was staged.");
    }

    // Asserts the refusal shape and then the full literals for the error code, the Message (ErrorData.Message) and the
    // rendered Detail (ErrorData.Detail). Assert.Multiple reports every mismatch in one run.
    private static void AssertGolden(SentinelCallToolResult<object> result, string errorCode, string message, string detail)
    {
        Assert.That(result.IsError, Is.True, "fixture must produce a refusal");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(result.ErrorData?.Message, Is.EqualTo(message));
            Assert.That(result.ErrorData?.Detail, Is.EqualTo(detail));
        });
    }
}
