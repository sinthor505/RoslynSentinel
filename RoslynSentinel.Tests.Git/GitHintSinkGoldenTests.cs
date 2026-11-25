using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the text that passes through each pass-through sink of GitImpl: the methods that copy Error and
// ErrorDetail from one result into another. A sink that stops carrying a hint would change one of these literals.
// Sinks 1-4 are pinned through the tool classes (GitTools / GitRemoteTools). Sink 5 (the index-changed HunkStale) is
// reachable only through the afterHunkDiffLoaded constructor argument, so it is pinned on the engine behind
// GitHintApplyingOperations. Literals were captured by running this fixture; git's own stderr is embedded in some of
// them and depends on the git version, and commit hashes are interpolated.
// Sibling of GitHintInlineGoldenCommitMiscTests, GitHintInlineGoldenHunkStageTests and GitHintTableGoldenTests.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintSinkGoldenTests
{
    private string _repoDir = null!;
    private string _pullRoot = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenSink_" + Guid.NewGuid());
        _pullRoot = _repoDir + "-pull";
        GitTestRepo.InitRepo(_repoDir);
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "readme");
        GitTestRepo.CommitAll(_repoDir, "init");

        // Same wiring as GitHintInlineGoldenHunkStageTests: the tools only need the fake solution path to find the git root.
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
        _gitRemote = new GitRemoteTools(workspaceManager, NullLogger<GitRemoteTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        DeleteTree(_repoDir);
        DeleteTree(_pullRoot);
    }

    // Sink 1, AppendInProgressAsync: a commit attempted while a conflicting rebase is in progress.
    [Test]
    public async Task Commit_MidRebase_InProgressAdviceSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        var baseBranch = GitTestRepo.RunGit(_repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        GitTestRepo.RunGit(_repoDir, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");
        GitTestRepo.CommitAll(_repoDir, "change a to two");
        GitTestRepo.RunGit(_repoDir, "checkout", baseBranch);
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "three");
        GitTestRepo.CommitAll(_repoDir, "change a to three");
        GitTestRepo.RunGit(_repoDir, "checkout", "feature");

        // Rebasing feature onto the base conflicts on a.txt and leaves the repo mid-rebase.
        var (rebaseExit, _, _) = GitTestRepo.RunGitRaw(_repoDir, "rebase", baseBranch);
        Assert.That(rebaseExit, Is.Not.EqualTo(0), "setup: the rebase must conflict");

        var result = await _gitTools.GitTool(
            reason: "golden sink commit mid rebase",
            GitOperation.commit,
            message: "should not land");

        AssertGolden(
            result,
            GitErrorCodes.OperationInProgress,
            "git commit failed: U\ta.txt\nerror: Committing is not possible because you have unmerged files.\nhint: Fix them up in the work tree, and then use 'git add/rm <file>'\nhint: as appropriate to mark resolution and make a commit.\nfatal: Exiting because of an unresolved conflict. Repository is mid-rebase. This tool cannot continue it; call Git(reason: \"<why you call this>\", operation: abort) to back out (the branch returns to its pre-rebase state), or finish it in the shell.",
            "Call Git(reason: \"<why you call this>\", operation: abort) to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.");
    }

    // Sink 1, AppendInProgressAsync: a conflicting revert leaves the repo mid-revert.
    [Test]
    public async Task Revert_ConflictingRevert_InProgressAdviceSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");
        var twoHash = GitTestRepo.CommitAll(_repoDir, "change a to two");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "three");
        GitTestRepo.CommitAll(_repoDir, "change a to three");
        var twoShort = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", twoHash);

        // Reverting "two" after "three" conflicts on a.txt and leaves the repo mid-revert.
        var result = await _gitTools.GitTool(
            reason: "golden sink revert conflict",
            GitOperation.revert,
            @ref: twoHash);

        AssertGolden(
            result,
            GitErrorCodes.OperationInProgress,
            "error: could not revert " + twoShort + "... change a to two\nhint: After resolving the conflicts, mark them with\nhint: \"git add/rm <pathspec>\", then run\nhint: \"git revert --continue\".\nhint: You can instead skip this commit with \"git revert --skip\".\nhint: To abort and get back to the state before \"git revert\",\nhint: run \"git revert --abort\".\nhint: Disable this message with \"git config set advice.mergeConflict false\" Repository is mid-revert. Resolve the conflicts and commit, or call Git(reason: \"<why you call this>\", operation: abort) to back out.",
            "Call Git(reason: \"<why you call this>\", operation: abort) to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.");
    }

    // Sink 1, AppendInProgressAsync via the GitRemote pull route: a pull whose merge conflicts leaves MERGE_HEAD behind.
    [Test]
    public async Task Pull_ConflictingMerge_InProgressAdviceSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "base");
        GitTestRepo.CommitAll(_repoDir, "add a");
        var baseShort = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", "HEAD");
        var branch = GitTestRepo.RunGit(_repoDir, "rev-parse", "--abbrev-ref", "HEAD");
        var remote = Path.Combine(_pullRoot, "remote.git");
        var other = Path.Combine(_pullRoot, "other");
        Directory.CreateDirectory(_pullRoot);
        GitTestRepo.RunGit(_pullRoot, "init", "--bare", remote);
        GitTestRepo.RunGit(_repoDir, "remote", "add", "origin", remote);
        GitTestRepo.RunGit(_repoDir, "push", "-u", "origin", branch);
        GitTestRepo.RunGit(_repoDir, "config", "pull.rebase", "false");

        // A second clone pushes a conflicting change to a.txt; the local repo then commits a different one.
        GitTestRepo.RunGit(_pullRoot, "clone", remote, other);
        GitTestRepo.RunGit(other, "config", "user.name", "Test");
        GitTestRepo.RunGit(other, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(other, "a.txt"), "remote");
        GitTestRepo.CommitAll(other, "remote change to a");
        GitTestRepo.RunGit(other, "push", "origin", branch);
        var remoteShort = GitTestRepo.RunGit(other, "rev-parse", "--short", "HEAD");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "local");
        GitTestRepo.CommitAll(_repoDir, "local change to a");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden sink pull conflict",
            GitRemoteOperation.pull);

        AssertGolden(
            result,
            GitErrorCodes.OperationInProgress,
            "Auto-merging a.txt\r\nCONFLICT (content): Merge conflict in a.txt\r\nAutomatic merge failed; fix conflicts and then commit the result.\nFrom " + Path.Combine(_pullRoot, "remote") + "\n   " + baseShort + ".." + remoteShort + "  master     -> origin/master Repository is mid-merge. Resolve the conflicts and commit, or call Git(reason: \"<why you call this>\", operation: abort) to back out.",
            "Call Git(reason: \"<why you call this>\", operation: abort) to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.");
    }

    // Sink 2, StageHunksAsync load copy: hunkIds on an untracked file fail in LoadFileDiffAsync and are copied as is.
    [Test]
    public async Task Stage_HunkIdsOnUntrackedFile_LoadFailureSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "new.txt"), "untracked");

        var result = await _gitTools.GitTool(
            reason: "golden sink stage untracked hunks",
            GitOperation.stage,
            paths: "new.txt",
            hunkIds: "1",
            hunkFingerprint: "unused");

        AssertGolden(
            result,
            GitErrorCodes.HunkUnsupported,
            "'new.txt' is untracked: untracked files have no hunks to choose from; stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"...\"). Nothing was changed.",
            "Stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"<path>\") instead, or use a file kind that has text hunks.");
    }

    // Sink 2, StageHunksAsync SelectHunks copy: hunkIds without a fingerprint is refused by SelectHunks and copied as is.
    [Test]
    public async Task Stage_HunkIdsWithoutFingerprint_SelectionFailureSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        var result = await _gitTools.GitTool(
            reason: "golden sink stage hunkIds no fingerprint",
            GitOperation.stage,
            paths: "a.txt",
            hunkIds: "1");

        AssertGolden(
            result,
            GitErrorCodes.HunkSelection,
            "hunkIds needs the hunkFingerprint returned by Git(reason: \"<why you call this>\", operation: hunks). Nothing was staged.",
            "Call Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pass hunkIds or lineRange exactly as it lists them.");
    }

    // Sink 2, StageHunksAsync SelectHunks copy: a fingerprint that does not match the file is refused by SelectHunks.
    [Test]
    public async Task Stage_HunkIdsWrongFingerprint_SelectionFailureSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        var result = await _gitTools.GitTool(
            reason: "golden sink stage wrong fingerprint",
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

    // Sink 3, HunksAsync load copy: hunks of an untracked file fail in LoadFileDiffAsync and are copied into the GitHunksResult.
    [Test]
    public async Task Hunks_UntrackedFile_LoadFailureSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "new.txt"), "untracked");

        var result = await _gitTools.GitTool(
            reason: "golden sink hunks untracked",
            GitOperation.hunks,
            paths: "new.txt");

        AssertGolden(
            result,
            GitErrorCodes.HunkUnsupported,
            "'new.txt' is untracked: untracked files have no hunks to choose from; stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"...\"). Nothing was changed.",
            "Stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"<path>\") instead, or use a file kind that has text hunks.");
    }

    // Sink 4, CommitAsync stageResult.Error copy: scope=tracked with paths is refused by StageAsync, which sets only Error.
    [Test]
    public async Task Commit_ScopeTrackedIndexLocked_StageRefusalSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "changed");
        File.WriteAllText(Path.Combine(_repoDir, ".git", "index.lock"), "");
        var lockPath = _repoDir.Replace('\\', '/') + "/.git/index.lock";

        var result = await _gitTools.GitTool(
            reason: "golden sink commit index locked",
            GitOperation.commit,
            message: "should not land",
            scope: GitStageScope.tracked);

        AssertGolden(
            result,
            GitErrorCodes.Fallback,
            "git add failed: fatal: Unable to create '" + lockPath + "': File exists.\n\nAnother git process seems to be running in this repository, or the lock file may be stale",
            null);
    }

    // Not a copy: CommitAsync returns its own PathNotFound text for a listed path that does not exist. Pinned because the
    // brief asks for it; the sink-4 copy is the scope=tracked route above.
    [Test]
    public async Task Commit_MissingListedPath_DirectReturnAsync()
    {
        var result = await _gitTools.GitTool(
            reason: "golden commit missing path",
            GitOperation.commit,
            message: "should not land",
            paths: "nope.txt");

        AssertGolden(
            result,
            GitErrorCodes.PathNotFound,
            "The following paths are not tracked and do not exist on disk: \"nope.txt\". Nothing was committed.",
            "Paths must be repo-relative and inside the repository root. Call Git(reason: \"<why you call this>\", operation: status) to list changed and untracked paths, then retry with exact names.");
    }

    // Sink 5, StageHunksAsync HunkStale from IndexEntryChangedMessageAsync: the index changes after the hunk diff is loaded.
    // Reachable only through the engine's afterHunkDiffLoaded hook (GitTools never sets it), so the engine is built directly
    // and wrapped in the decorator the tools use.
    [Test]
    public async Task Stage_IndexChangedUnderTheCall_EngineHunkStaleSinkAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        Action<string> changeIndexUnderTheCall = root =>
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "three");
            GitTestRepo.RunGit(root, "add", "a.txt");
        };
        var ops = new GitHintApplyingOperations(new GitImpl(
            new FakeWorkspaceManager(WorkspaceAccess.ReadOnly),
            NullLogger<GitImpl>.Instance,
            GitReaders.Default,
            afterHunkDiffLoaded: changeIndexUnderTheCall));

        var hunks = await ops.HunksAsync(_repoDir, "a.txt", 20, CancellationToken.None);
        Assert.That(hunks.IsError, Is.False, "setup: hunks must list the unstaged change");

        // The index entry before the hook runs is the "was" blob; after the stage it is the "now" blob.
        var wasShort = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", ":a.txt");
        var result = await ops.StageHunksAsync(_repoDir, "a.txt", "1", hunks.Fingerprint, null, CancellationToken.None);
        var nowShort = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", ":a.txt");

        Assert.That(result.IsError, Is.True, "setup: the stage must be refused");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorKind, Is.EqualTo(GitErrorCodes.HunkStale));
            Assert.That(result.Error, Is.EqualTo(
                "The index entry for 'a.txt' changed between reading the diff and applying it (was " + wasShort + ", now " + nowShort + "): another session changed what is staged for this file. Re-run Git(reason: \"<why you call this>\", operation: hunks, paths: \"a.txt\") for the current hunks and select again, or stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"a.txt\"). Nothing was staged."));
            Assert.That(result.ErrorDetail, Is.EqualTo(
                "Re-run Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pick again with the new hunkFingerprint. Nothing was staged."));
        });
    }

    // Asserts the refusal shape and then the full literals for the error code, the Message (ErrorData.Message) and the
    // rendered Detail (ErrorData.Detail). Assert.Multiple reports every mismatch in one run.
    private static void AssertGolden(SentinelCallToolResult<object> result, string errorCode, string message, string? detail)
    {
        Assert.That(result.IsError, Is.True, "setup: the call must be refused");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(result.ErrorData?.Message, Is.EqualTo(message));
            Assert.That(result.ErrorData?.Detail, Is.EqualTo(detail));
        });
    }

    // Windows can leave .git object files read-only; clear that before the recursive delete.
    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }
}
