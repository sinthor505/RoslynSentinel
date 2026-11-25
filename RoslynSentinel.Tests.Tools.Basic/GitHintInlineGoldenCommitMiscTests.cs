using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the inline (hand-written) failure texts of the commit, checkout and in-progress advice paths (ErrorData.Message and
// ErrorData.Detail), pinned through the tool classes (GitTools / GitRemoteTools), not GitImpl. Literals are captured from
// the pre-extraction source; a changed literal is a wording change and needs a decision, not a quiet update.
// Sibling of GitHintTableGoldenTests (table texts only). Git stderr is embedded in some texts: git's wording is version dependent.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintInlineGoldenCommitMiscTests
{
    private const string RejectingHook = "#!/bin/sh\necho the hook says no >&2\nexit 1";

    private string _repoDir = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenCommitMisc_" + Guid.NewGuid());
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

    // GitImpl.CommitAsync failure branch: scope=listed pre-stages an untracked path, then a rejecting pre-commit hook declines the
    // commit, so the failure text gets the "Staging already ran" note with a live unstage call.
    [Test]
    public async Task Commit_ListedUntrackedThenHookRejects_StagingNoteAsync()
    {
        var hooksDir = Path.Combine(_repoDir, ".git", "hooks");
        GitTestRepo.RunGit(_repoDir, "config", "core.hooksPath", hooksDir.Replace('\\', '/'));
        Directory.CreateDirectory(hooksDir);
        File.WriteAllText(Path.Combine(hooksDir, "pre-commit"), RejectingHook + "\n");
        File.WriteAllText(Path.Combine(_repoDir, "n.txt"), "new");

        var result = await _gitTools.GitTool(
            reason: "golden commit listed hook",
            GitOperation.commit,
            message: "should not land",
            paths: "n.txt");

        AssertGolden(
            result,
            GitErrorCodes.HookRejected,
            "git commit failed: the hook says no Staging already ran; the index now holds 1 staged paths. Call Git(reason: \"<why you call this>\", operation: unstage) to undo.",
            "The text above ends with the hook's own output. Fix what the hook reports and commit again; the hooks live in .git/hooks, or in the directory named by core.hooksPath.");
    }

    // GitImpl.InProgressAdviceAsync rebase/am branch: a commit attempted while a conflicting rebase is in progress.
    [Test]
    public async Task Commit_MidRebase_InProgressAdviceAsync()
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
            reason: "golden commit mid rebase",
            GitOperation.commit,
            message: "should not land");

        AssertGolden(
            result,
            GitErrorCodes.OperationInProgress,
            "git commit failed: U\ta.txt\nerror: Committing is not possible because you have unmerged files.\nhint: Fix them up in the work tree, and then use 'git add/rm <file>'\nhint: as appropriate to mark resolution and make a commit.\nfatal: Exiting because of an unresolved conflict. Repository is mid-rebase. This tool cannot continue it; call Git(reason: \"<why you call this>\", operation: abort) to back out (the branch returns to its pre-rebase state), or finish it in the shell.",
            "Call Git(reason: \"<why you call this>\", operation: abort) to back out of the in-progress operation, or resolve the conflicts in the working tree and commit.");
    }

    // GitImpl.InProgressAdviceAsync default branch (revert, merge, cherry-pick): a conflicting revert leaves the repo mid-revert.
    [Test]
    public async Task Revert_ConflictingRevert_InProgressAdviceAsync()
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

    // GitImpl.CheckoutAsync: branchName missing, so the Error text carries the Git call for checkout in its middle.
    [Test]
    public async Task RefRequiredCheckout_NoBranchName_InlineCallAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden checkout no branch",
            GitRemoteOperation.checkout);

        AssertGolden(
            result,
            GitErrorCodes.RefRequired,
            "branchName is required for GitRemote(reason: \"<why you call this>\", operation: checkout). Pass the branch to switch to, and createBranch=true if it doesn't exist yet.",
            "Pass branchName, with createBranch=true if it does not exist yet. GitRemote(reason: \"<why you call this>\", operation: branch) lists existing branches.");
    }

    // Asserts the refusal shape and then the full literals for the error code, the Message (ErrorData.Message) and the
    // rendered Detail (ErrorData.Detail). Assert.Multiple reports every mismatch in one run.
    private static void AssertGolden(SentinelCallToolResult<object> result, string errorCode, string message, string? detail)
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
