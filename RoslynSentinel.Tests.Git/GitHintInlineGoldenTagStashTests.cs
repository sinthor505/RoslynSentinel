using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the inline (hand-written) failure texts of the tag, stash and config operations (ErrorData.Message and
// ErrorData.Detail), pinned through the tool classes (GitTools / GitRemoteTools), not GitImpl. Literals are captured from
// the pre-extraction source; a changed literal is a wording change and needs a decision, not a quiet update.
// Sibling of GitHintTableGoldenTests and GitHintTableGoldenHunksTests (table texts only).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintInlineGoldenTagStashTests
{
    private string _repoDir = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenTagStash_" + Guid.NewGuid());
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
    public async Task Refused_TagWithNoAction_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag no action",
            GitRemoteOperation.tag);

        AssertGolden(result, GitErrorCodes.ActionRequired, "operation=tag needs action to be one of: list, create, delete (got none). Nothing was changed.", "Pass action, e.g. GitRemote(reason: \"<why you call this>\", operation: tag, action: list).");
    }

    [Test]
    public async Task Refused_ConfigWithPushAction_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden config bad action",
            GitRemoteOperation.config,
            action: GitAction.push);

        AssertGolden(result, GitErrorCodes.ActionRequired, "operation=config needs action to be one of: get, set, unset (got push). Nothing was changed.", "Valid actions are get, set and unset; get takes configKey, set takes configKey and configValue, unset takes configKey. For example GitRemote(reason: \"<why you call this>\", operation: config, action: get, configKey: \"user.name\").");
    }

    [Test]
    public async Task Refused_ConfigSetOfNonWritableKey_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden config set refused key",
            GitRemoteOperation.config,
            action: GitAction.set,
            configKey: "core.bare",
            configValue: "true");

        AssertGolden(result, GitErrorCodes.Refused, "Config key 'core.bare' is not one this operation writes. Nothing was changed.", "Writable keys: roslynsentinel.*, user.name, user.email, core.autocrlf, core.eol, core.safecrlf, core.longpaths, pull.rebase, push.default, init.defaultBranch, branch.<name>.remote, branch.<name>.merge, branch.<name>.rebase. The value of any other key can be read with action=get.");
    }

    [Test]
    public async Task AlreadyExists_TagCreateOfExistingTag_PinsMessageAndDetailAsync()
    {
        GitTestRepo.RunGit(_repoDir, "tag", "v1");
        var shortHead = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", "HEAD").Trim();

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag exists",
            GitRemoteOperation.tag,
            action: GitAction.create,
            tagName: "v1");

        AssertGolden(
            result,
            GitErrorCodes.AlreadyExists,
            "Tag 'v1' already exists (points at " + shortHead + "). This tool never overwrites or moves a tag. Use a new name, or delete it first (GitRemote(reason: \"<why you call this>\", operation: tag, action: delete, tagName: \"v1\")) and recreate. Nothing was changed.",
            "Use a different name or path, or inspect the existing entry first with GitRemote(reason: \"<why you call this>\", operation: tag, action: list), GitRemote(reason: \"<why you call this>\", operation: worktree, action: list) or GitRemote(reason: \"<why you call this>\", operation: branch). This tool never overwrites an existing tag, branch or worktree.");
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
    public async Task Fallback_StashApplyBlockedByUntrackedFile_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        // A stash that adds b.txt; after the stash b.txt is gone from the tree and the index.
        File.WriteAllText(Path.Combine(_repoDir, "b.txt"), "from stash");
        GitTestRepo.RunGit(_repoDir, "add", "b.txt");
        var pushed = await _gitRemote.GitRemoteTool(
            reason: "golden stash setup",
            GitRemoteOperation.stash,
            action: GitAction.push);
        Assert.That(pushed.IsError, Is.False, "setup: stash push must succeed");
        // An untracked file at the same path makes git refuse the apply without touching the tracked tree.
        File.WriteAllText(Path.Combine(_repoDir, "b.txt"), "untracked in the way");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash apply untracked",
            GitRemoteOperation.stash,
            action: GitAction.apply,
            stashIndex: 0);

        AssertGolden(result, GitErrorCodes.Fallback, "git stash apply failed: error: The following untracked working tree files would be overwritten by merge:\n\tb.txt\nPlease move or remove them before you merge.\nAborting. Nothing was changed.", null);
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
