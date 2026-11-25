using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the full-sentence text that each GitErrorDetails member renders (ErrorData.Message and ErrorData.Detail),
// pinned through the tool classes (GitTools / GitRemoteTools), not GitImpl. Literals were captured from the pre-extraction
// source; a changed literal is a wording change and needs a decision, not a quiet update.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintTableGoldenTests
{
    private string _repoDir = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGolden_" + Guid.NewGuid());
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
    public async Task PathNotFound_StageMissingPath_PinsMessageAndDetailAsync()
    {
        var result = await _gitTools.GitTool(
            reason: "golden path not found",
            GitOperation.stage,
            scope: GitStageScope.listed,
            paths: "missing.txt");

        AssertGolden(
            result,
            GitErrorCodes.PathNotFound,
            "The following paths are not tracked and do not exist on disk: \"missing.txt\". Nothing was staged.",
            "Paths must be repo-relative and inside the repository root. Call Git(reason: \"<why you call this>\", operation: status) to list changed and untracked paths, then retry with exact names.");
    }

    [Test]
    public async Task IgnoredPath_StageIgnoredUntrackedFile_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, ".gitignore"), "ignored/\n");
        Directory.CreateDirectory(Path.Combine(_repoDir, "ignored"));
        File.WriteAllText(Path.Combine(_repoDir, "ignored", "file.txt"), "ignored content");

        var result = await _gitTools.GitTool(
            reason: "golden ignored path",
            GitOperation.stage,
            scope: GitStageScope.listed,
            paths: "ignored/file.txt");

        AssertGolden(
            result,
            GitErrorCodes.IgnoredPath,
            "The following paths are ignored by .gitignore: \"ignored/file.txt\". The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to stage them. Nothing was staged.",
            "Remove the ignored entries from paths, or un-ignore them in .gitignore, then retry. This tool never force-adds ignored files.");
    }

    [Test]
    public async Task NothingToCommit_CommitOnCleanTree_PinsMessageAndDetailAsync()
    {
        var branch = GitTestRepo.RunGit(_repoDir, "rev-parse", "--abbrev-ref", "HEAD");

        var result = await _gitTools.GitTool(
            reason: "golden nothing to commit",
            GitOperation.commit,
            message: "nothing here");

        AssertGolden(
            result,
            GitErrorCodes.NothingToCommit,
            "git commit failed: On branch " + branch + "\r\nnothing to commit, working tree clean",
            "Stage changes first with Git(reason: \"<why you call this>\", operation: stage, paths: \"...\"), or pass paths/scope on the commit call. Call Git(reason: \"<why you call this>\", operation: status) to see what changed.");
    }

    [Test]
    public async Task OperationInProgress_ConflictingRevert_PinsMessageAndDetailAsync()
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
            reason: "golden revert conflict",
            GitOperation.revert,
            @ref: twoHash);

        AssertGolden(
            result,
            GitErrorCodes.OperationInProgress,
            "error: could not revert " + twoShort + "... change a to two\n"
            + "hint: After resolving the conflicts, mark them with\n"
            + "hint: \"git add/rm <pathspec>\", then run\n"
            + "hint: \"git revert --continue\".\n"
            + "hint: You can instead skip this commit with \"git revert --skip\".\n"
            + "hint: To abort and get back to the state before \"git revert\",\n"
            + "hint: run \"git revert --abort\".\n"
            + "hint: Disable this message with \"git config set advice.mergeConflict false\" Repository is mid-revert. Resolve the conflicts and commit, or call Git(reason: \"<why you call this>\", operation: abort) to back out.",
            "Call Git(reason: \"<why you call this>\", operation: abort) to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.");
    }

    [Test]
    public async Task RefRequiredReset_ResetWithoutRef_PinsMessageAndDetailAsync()
    {
        var result = await _gitTools.GitTool(
            reason: "golden reset without ref",
            GitOperation.reset,
            mode: GitResetMode.soft);

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "reset needs an explicit ref, e.g. ref: \"HEAD~1\" to undo the last commit (working tree kept). To unstage without moving HEAD use operation=unstage. Nothing was reset.",
            "Pass ref, e.g. ref: \"HEAD~1\" to undo the last commit with the working tree kept, or call Git(reason: \"<why you call this>\", operation: unstage) to unstage without moving HEAD.");
    }

    [Test]
    public async Task RefRequiredRevert_RevertWithoutRef_PinsMessageAndDetailAsync()
    {
        var result = await _gitTools.GitTool(
            reason: "golden revert without ref",
            GitOperation.revert);

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "ref is required for operation=revert.",
            "Pass ref, e.g. a hash taken from Git(reason: \"<why you call this>\", operation: log).");
    }

    [Test]
    public async Task RefRequiredCheckout_CheckoutWithoutBranchName_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden checkout without branch",
            GitRemoteOperation.checkout);

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "branchName is required for GitRemote(reason: \"<why you call this>\", operation: checkout). Pass the branch to switch to, and createBranch=true if it doesn't exist yet.",
            "Pass branchName, with createBranch=true if it does not exist yet. GitRemote(reason: \"<why you call this>\", operation: branch) lists existing branches.");
    }

    [Test]
    public async Task RefRequiredStash_StashApplyWithoutIndex_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash apply without index",
            GitRemoteOperation.stash,
            action: GitAction.apply);

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "Git stash apply needs stashIndex (0 = newest). Nothing was changed.",
            "Pass stashIndex (0 = newest). GitRemote(reason: \"<why you call this>\", operation: stash, action: list) shows every entry.");
    }

    [Test]
    public async Task RefRequiredTag_TagCreateWithoutTagName_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag create without name",
            GitRemoteOperation.tag,
            action: GitAction.create);

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "Git tag create needs tagName. Nothing was changed.",
            "Pass tagName, e.g. GitRemote(reason: \"<why you call this>\", operation: tag, action: create, tagName: \"v1.0.0\").");
    }

    [Test]
    public async Task RefRequiredWorktree_WorktreeAddWithoutPath_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree add without path",
            GitRemoteOperation.worktree,
            action: GitAction.add,
            branchName: "golden-branch");

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "Git worktree add needs worktreePath. Nothing was changed.",
            "Pass worktreePath (absolute, outside the repo) and, for add, branchName.");
    }

    [Test]
    public async Task AlreadyExists_TagCreateOnExistingTag_PinsMessageAndDetailAsync()
    {
        GitTestRepo.RunGit(_repoDir, "tag", "v1.0.0");
        var headShort = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", "HEAD");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag already exists",
            GitRemoteOperation.tag,
            action: GitAction.create,
            tagName: "v1.0.0");

        AssertGolden(
            result,
            GitErrorCodes.AlreadyExists,
            "Tag 'v1.0.0' already exists (points at " + headShort + "). This tool never overwrites or moves a tag. Use a new name, or delete it first (GitRemote(reason: \"<why you call this>\", operation: tag, action: delete, tagName: \"v1.0.0\")) and recreate. Nothing was changed.",
            "Use a different name or path, or inspect the existing entry first with GitRemote(reason: \"<why you call this>\", operation: tag, action: list), GitRemote(reason: \"<why you call this>\", operation: worktree, action: list) or GitRemote(reason: \"<why you call this>\", operation: branch). This tool never overwrites an existing tag, branch or worktree.");
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
