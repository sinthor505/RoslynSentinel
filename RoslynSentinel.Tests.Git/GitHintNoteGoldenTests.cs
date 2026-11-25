using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the success notes (SuccessData.Note) that the engine builds from hand-written text, a tool call or
// the shared StashDiskNote property, pinned through the tool classes (GitTools / GitRemoteTools), not GitImpl.
// Literals are captured from the pre-extraction source; a changed literal is a wording change and needs a decision,
// not a quiet update. The worktree add and remove notes are pinned by GitHintInlineGoldenWorktreeTests.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintNoteGoldenTests
{
    private string _repoDir = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenNote_" + Guid.NewGuid());
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
    public async Task TagDelete_LightweightTag_PinsNoteAsync()
    {
        GitTestRepo.RunGit(_repoDir, "tag", "v1");
        var shortHead = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", "HEAD").Trim();

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag delete lightweight",
            GitRemoteOperation.tag,
            action: GitAction.delete,
            tagName: "v1");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var data = (GitTagResult)result.SuccessData!;
        Assert.That(data.Action, Is.EqualTo("delete"));
        Assert.That(data.Deleted, Is.EqualTo("v1"));
        Assert.That(data.Note, Is.EqualTo(
            "Deleted locally only. To undo: GitRemote(reason: \"<why you call this>\", operation: tag, action: create, tagName: \"v1\", ref: \"" + shortHead + "\")."));
    }

    [Test]
    public async Task TagDelete_AnnotatedTag_PinsNoteAsync()
    {
        GitTestRepo.RunGit(_repoDir, "tag", "-a", "v2", "-m", "release");
        var shortHead = GitTestRepo.RunGit(_repoDir, "rev-parse", "--short", "HEAD").Trim();

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden tag delete annotated",
            GitRemoteOperation.tag,
            action: GitAction.delete,
            tagName: "v2");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var data = (GitTagResult)result.SuccessData!;
        Assert.That(data.Deleted, Is.EqualTo("v2"));
        Assert.That(data.Note, Is.EqualTo(
            "Deleted locally only. To undo: GitRemote(reason: \"<why you call this>\", operation: tag, action: create, tagName: \"v2\", ref: \"" + shortHead + "\")."
            + " The tag was annotated; its message and tagger are not restored by that call (pass message to make the recreated tag annotated)."));
    }

    [Test]
    public async Task StashPush_TrackedChanges_PinsNoteAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "changed");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash push note",
            GitRemoteOperation.stash,
            action: GitAction.push);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var data = (GitStashResult)result.SuccessData!;
        Assert.That(data.Action, Is.EqualTo("push"));
        Assert.That(data.Created, Is.True);
        Assert.That(data.Note, Is.EqualTo(
            "Tracked files changed on disk; call LoadSolution(reason: \"<why you call this>\", solutionPath: \"<solutionPath>\", forceReload: true) before editing C#."
            + " Staged changes come back unstaged on pop/apply (--index is not used)."));
    }

    [Test]
    public async Task StashRestore_ApplyKeepsEntry_PinsNoteAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "changed");
        var pushed = await _gitRemote.GitRemoteTool(
            reason: "golden stash setup",
            GitRemoteOperation.stash,
            action: GitAction.push);
        Assert.That(pushed.IsError, Is.False, "setup: stash push must succeed");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash apply note",
            GitRemoteOperation.stash,
            action: GitAction.apply,
            stashIndex: 0);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var data = (GitStashResult)result.SuccessData!;
        Assert.That(data.Action, Is.EqualTo("apply"));
        Assert.That(data.Note, Is.EqualTo(
            "Tracked files changed on disk; call LoadSolution(reason: \"<why you call this>\", solutionPath: \"<solutionPath>\", forceReload: true) before editing C#."
            + " Staged state is not restored (--index is not used)."
            + " stash@{0} was applied and kept; 1 stash entry total."));
    }

    [Test]
    public async Task StashRestore_PopDropsEntry_PinsNoteAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "changed");
        var pushed = await _gitRemote.GitRemoteTool(
            reason: "golden stash setup",
            GitRemoteOperation.stash,
            action: GitAction.push);
        Assert.That(pushed.IsError, Is.False, "setup: stash push must succeed");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden stash pop note",
            GitRemoteOperation.stash,
            action: GitAction.pop,
            stashIndex: 0);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var data = (GitStashResult)result.SuccessData!;
        Assert.That(data.Action, Is.EqualTo("pop"));
        Assert.That(data.Note, Is.EqualTo(
            "Tracked files changed on disk; call LoadSolution(reason: \"<why you call this>\", solutionPath: \"<solutionPath>\", forceReload: true) before editing C#."
            + " Staged state is not restored (--index is not used)."
            + " stash@{0} was applied and dropped; 0 stash entries left."));
    }

    [Test]
    public async Task Hunks_OneUnstagedFile_PinsNoteAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "changed");

        var result = await _gitTools.GitTool(
            reason: "golden hunks note",
            GitOperation.hunks,
            paths: "README.md");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var data = (GitHunksResult)result.SuccessData!;
        Assert.That(data.TotalHunks, Is.EqualTo(1));
        Assert.That(data.Note, Is.EqualTo(
            "Pass hunkIds with this hunkFingerprint to Git(reason: \"<why you call this>\", operation: stage, paths: \"README.md\"), or lineRange. "
            + "Hunks are zero-context: adjacent edits are separate hunks."));
    }
}
