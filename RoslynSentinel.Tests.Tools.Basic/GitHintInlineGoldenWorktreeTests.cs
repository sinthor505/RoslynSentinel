using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the inline worktree texts (a tool call inside an engine sentence, or a whole sentence with a tool call in
// ErrorData.Detail, and the success notes that name a tool), pinned through GitRemoteTools (operation worktree), not GitImpl.
// Literals were captured from the pre-extraction source; a changed literal is a wording change and needs a decision, not a quiet update.
// Git's own stderr is embedded in some texts (the git worktree add/remove failure branches); its wording is git-version dependent.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintInlineGoldenWorktreeTests
{
    private string _repoDir = null!;
    private string _worktreeRoot = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenWorktree_" + Guid.NewGuid());
        // Worktrees live in their own temp root, outside the repository (the Refused rule), and are removed in TearDown.
        _worktreeRoot = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenWorktreeRoot_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "readme");
        GitTestRepo.CommitAll(_repoDir, "init");

        // Same wiring as GitToolsSmokeTests: the tools only need the fake solution path to find the git root.
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitRemote = new GitRemoteTools(workspaceManager, NullLogger<GitRemoteTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        DeleteTree(_worktreeRoot);
        DeleteTree(_repoDir);
    }

    [Test]
    public async Task WorktreeAdd_BranchMissingWithoutCreateBranch_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree add missing branch",
            GitRemoteOperation.worktree,
            action: GitAction.add,
            branchName: "golden-missing",
            worktreePath: Path.Combine(_worktreeRoot, "wt-missing"));

        AssertGolden(
            result,
            GitErrorCodes.TargetNotFound,
            "Branch 'golden-missing' does not exist. Nothing was changed.",
            "Pass createBranch=true to create it (optionally with startPoint), or list the existing branches with GitRemote(reason: \"<why you call this>\", operation: branch).");
    }

    [Test]
    public async Task WorktreeAdd_PostCheckoutHookRejects_PinsMessageAndDetailAsync()
    {
        var hooksDir = Path.Combine(_repoDir, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        // Isolated hooks directory so a user-level core.hooksPath cannot redirect the fixture's hook.
        GitTestRepo.RunGit(_repoDir, "config", "core.hooksPath", hooksDir.Replace('\\', '/'));
        // LF only: the hook is run by Git for Windows' bundled sh, which rejects a CR after the shebang.
        File.WriteAllText(Path.Combine(hooksDir, "post-checkout"), "#!/bin/sh\necho the hook says no >&2\nexit 1\n");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree add hook rejects",
            GitRemoteOperation.worktree,
            action: GitAction.add,
            branchName: "golden-hook",
            createBranch: true,
            worktreePath: Path.Combine(_worktreeRoot, "wt-hook"));

        // The worktree is registered before git's post-checkout hook fails, so the "partly created" branch is taken.
        // The embedded git stderr (first line and LF separator) is git-version and line-ending dependent.
        AssertGolden(
            result,
            "GitError",
            "git worktree add failed: Preparing worktree (new branch 'golden-hook')\nthe hook says no. The worktree may have been partly created; check GitRemote(reason: \"<why you call this>\", operation: worktree, action: list).",
            null);
    }

    [Test]
    public async Task WorktreeAdd_Success_PinsNoteAsync()
    {
        var path = Path.Combine(_worktreeRoot, "wt-note");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree add success note",
            GitRemoteOperation.worktree,
            action: GitAction.add,
            branchName: "golden-note",
            createBranch: true,
            worktreePath: path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitWorktreeResult)result.SuccessData!).Note, Is.EqualTo(
            "Use LoadSolution(reason: \"<why you call this>\", solutionPath: \"<path to .slnx>\") on a .slnx inside " + path
            + " to work in it; mutating Git operations always target the loaded solution's repo."));
    }

    [Test]
    public async Task WorktreeRemove_UnknownPath_PinsMessageAndDetailAsync()
    {
        var path = Path.Combine(_worktreeRoot, "wt-unknown");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree remove unknown",
            GitRemoteOperation.worktree,
            action: GitAction.remove,
            worktreePath: path);

        AssertGolden(
            result,
            GitErrorCodes.TargetNotFound,
            "No worktree at '" + path + "'. Known worktrees: " + _repoDir + ". Nothing was removed.",
            "Check the exact name with GitRemote(reason: \"<why you call this>\", operation: tag, action: list), GitRemote(reason: \"<why you call this>\", operation: stash, action: list), GitRemote(reason: \"<why you call this>\", operation: worktree, action: list) or GitRemote(reason: \"<why you call this>\", operation: branch), then retry.");
    }

    [Test]
    public async Task WorktreeRemove_MainWorktree_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree remove main",
            GitRemoteOperation.worktree,
            action: GitAction.remove,
            worktreePath: _repoDir);

        AssertGolden(
            result,
            GitErrorCodes.Refused,
            "Worktree '" + _repoDir + "' is the main worktree; this tool never removes it. Nothing was removed.",
            "Only linked worktrees can be removed; GitRemote(reason: \"<why you call this>\", operation: worktree, action: list) shows which entry has IsMain false.");
    }

    [Test]
    public async Task WorktreeRemove_DirtyWithoutDiscard_PinsMessageAndDetailAsync()
    {
        var path = Path.Combine(_worktreeRoot, "wt-dirty");
        await AddWorktreeAsync(path, "golden-dirty");
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree remove dirty",
            GitRemoteOperation.worktree,
            action: GitAction.remove,
            worktreePath: path);

        // The tool call inside the message embeds the path with backslashes doubled (ToolCallHint value escaping).
        AssertGolden(
            result,
            GitErrorCodes.WorkingTreeDirty,
            "Worktree '" + path + "' has uncommitted changes (1 path(s): dirty.txt). Nothing was removed. To delete it anyway and permanently lose those changes, call GitRemote(reason: \"<why you call this>\", operation: worktree, action: remove, worktreePath: \"" + path.Replace("\\", "\\\\") + "\", discardUncommittedChanges: true). To keep them, commit or stash inside that worktree first.",
            "discardUncommittedChanges: true deletes modified and untracked files in that worktree; they cannot be recovered. Committed work and the branch are not affected.");
    }

    [Test]
    public async Task WorktreeRemove_CleanWorktreeKeepsBranch_PinsNoteAsync()
    {
        var path = Path.Combine(_worktreeRoot, "wt-clean");
        await AddWorktreeAsync(path, "golden-clean");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree remove clean",
            GitRemoteOperation.worktree,
            action: GitAction.remove,
            worktreePath: path);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitWorktreeResult)result.SuccessData!).Note, Is.EqualTo(
            "The branch 'golden-clean' was kept; delete it with GitRemote(reason: \"<why you call this>\", operation: branch, deleteBranch: true) once merged."));
    }

    private async Task AddWorktreeAsync(string path, string branch)
    {
        var added = await _gitRemote.GitRemoteTool(
            reason: "golden setup add worktree",
            GitRemoteOperation.worktree,
            action: GitAction.add,
            branchName: branch,
            createBranch: true,
            worktreePath: path);
        Assert.That(added.IsError, Is.False, "setup: worktree add must succeed");
    }

    private static void DeleteTree(string dir)
    {
        // Windows can leave .git object files read-only; clear that before the recursive delete.
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(dir, recursive: true);
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
