using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the Step 2 gap sites the first golden fixtures did not reach: the config set refusal without a value (ActionRequired),
// the worktree remove refusal for the worktree the loaded solution is in (IsCurrent), the lineRange refusal that cuts a hunk whose body
// cannot be cut (SelectHunks), and the success note of a discarded dirty worktree removal. Pinned through GitTools / GitRemoteTools, not GitImpl.
// Literals were captured from the pre-extraction source; a changed literal is a wording change and needs a decision, not a quiet update.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintInlineGoldenGapTests
{
    private string _repoDir = null!;
    private string _worktreeRoot = null!;
    private GitTools _gitTools = null!;
    private GitRemoteTools _gitRemote = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenGap_" + Guid.NewGuid());
        // Worktrees live in their own temp root, outside the repository (the Refused rule), and are removed in TearDown.
        _worktreeRoot = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenGapRoot_" + Guid.NewGuid());
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
        DeleteTree(_worktreeRoot);
        DeleteTree(_repoDir);
    }

    [Test]
    public async Task ConfigSet_WithoutValue_PinsMessageAndDetailAsync()
    {
        var result = await _gitRemote.GitRemoteTool(
            reason: "golden config set without value",
            GitRemoteOperation.config,
            action: GitAction.set,
            configKey: "user.email");

        AssertGolden(
            result,
            GitErrorCodes.ActionRequired,
            "action=set needs configValue (got none). Nothing was changed.",
            "Pass configValue with action=set; an empty string is a valid value. For example GitRemote(reason: \"<why you call this>\", operation: config, action: set, configKey: \"user.name\", configValue: \"Ada\").");
    }

    [Test]
    public async Task WorktreeRemove_LoadedSolutionIsInsideTheWorktree_PinsMessageAndDetailAsync()
    {
        var path = Path.Combine(_worktreeRoot, "wt-current");
        await AddWorktreeAsync(path, "golden-current");
        // The loaded solution sits in the linked worktree, so this tool operates in that worktree and the remove is refused.
        var inside = new GitRemoteTools(
            new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(path, "Fake.sln") },
            NullLogger<GitRemoteTools>.Instance);

        var result = await inside.GitRemoteTool(
            reason: "golden worktree remove current",
            GitRemoteOperation.worktree,
            action: GitAction.remove,
            worktreePath: path);

        // The tool names the solution's own worktree path; the loaded solution sits in it, so the path is interpolated.
        AssertGolden(
            result,
            GitErrorCodes.Refused,
            "Worktree '" + path + "' is the worktree this Git tool is operating in (the loaded solution's repository); removing it would delete the directory the server is working in. Nothing was removed.",
            "Call LoadSolution(reason: \"<why you call this>\", solutionPath: \"<path to .slnx>\") on a solution in a different worktree, then remove this one from there.");
    }

    [Test]
    public async Task StageLineRange_CutWouldJoinUnterminatedLine_PinsMessageAndDetailAsync()
    {
        // Old line 10 has no newline; a range that misses the first added line would glue "new 10" onto it.
        var baseLines = string.Concat(Enumerable.Range(1, 9).Select(n => "line " + n + "\n"));
        File.WriteAllText(Path.Combine(_repoDir, "f.txt"), baseLines + "line 10");
        GitTestRepo.CommitAll(_repoDir, "add f");
        var workingLines = string.Concat(Enumerable.Range(1, 8).Select(n => "line " + n + "\n"));
        File.WriteAllText(Path.Combine(_repoDir, "f.txt"), workingLines + "new 9\nnew 10\nnew 11\n");

        var result = await _gitTools.GitTool(
            reason: "golden stage line range cut would join",
            GitOperation.stage,
            paths: "f.txt",
            lineRange: "10-11");

        AssertGolden(
            result,
            GitErrorCodes.HunkSelection,
            "Hunk 1 spans lines 9-11 but only partly lies in 10-11. The body of hunk 1 is not a run of removed lines followed by a run of added lines (or its cut would join text onto a final line without a newline), so it cannot be cut at that range. Widen the range to 9-11 to take the whole hunk, pass hunkIds from Git(reason: \"<why you call this>\", operation: hunks, paths: \"f.txt\"), or stage the whole file with Git(reason: \"<why you call this>\", operation: stage, paths: \"f.txt\"). Hunks (id: new-file lines): 1: 9-11. Nothing was staged.",
            "Call Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pass hunkIds or lineRange exactly as it lists them.");
    }

    [Test]
    public async Task WorktreeRemove_DirtyWithDiscard_PinsNoteAsync()
    {
        var path = Path.Combine(_worktreeRoot, "wt-discard");
        await AddWorktreeAsync(path, "golden-discard");
        File.WriteAllText(Path.Combine(path, "dirty.txt"), "uncommitted");

        var result = await _gitRemote.GitRemoteTool(
            reason: "golden worktree remove discard",
            GitRemoteOperation.worktree,
            action: GitAction.remove,
            worktreePath: path,
            discardUncommittedChanges: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(((GitWorktreeResult)result.SuccessData!).Note, Is.EqualTo(
            "Removed '" + path + "' and discarded uncommitted changes in 1 path(s). They are not recoverable. "
            + "The branch 'golden-discard' was kept; delete it with GitRemote(reason: \"<why you call this>\", operation: branch, deleteBranch: true) once merged."));
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
        Assert.That(result.IsError, Is.True, "fixture must produce a refusal");
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(result.ErrorData?.Message, Is.EqualTo(message));
            Assert.That(result.ErrorData?.Detail, Is.EqualTo(detail));
        });
    }
}
