using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tests.Battery.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Golden pins for the inline hunk and stage texts (Error and ErrorDetail, with the tool calls rendered), pinned through the
// tool class GitTools, not GitImpl. Literals were captured from the pre-extraction source; a changed literal is a wording
// change and needs a decision, not a quiet update. Sites with no cheap GitTools trigger are renderer-pinned only (see report).
// Sibling of GitHintTableGoldenHunksTests (table detail) and GitHintInlineGoldenTagStashTests.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Category("GitTools")]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitHintInlineGoldenHunkStageTests
{
    private string _repoDir = null!;
    private GitTools _gitTools = null!;

    [SetUp]
    public void SetUp()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitGoldenHunkStage_" + Guid.NewGuid());
        GitTestRepo.InitRepo(_repoDir);
        File.WriteAllText(Path.Combine(_repoDir, "README.md"), "readme");
        GitTestRepo.CommitAll(_repoDir, "init");

        // Same wiring as GitHintTableGoldenHunksTests: the tools only need the fake solution path to find the git root.
        var workspaceManager = new FakeWorkspaceManager(WorkspaceAccess.ReadOnly) { SolutionPath = Path.Combine(_repoDir, "Fake.sln") };
        _gitTools = new GitTools(workspaceManager, NullLogger<GitTools>.Instance);
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
    public async Task NoChanges_HunksOfCleanTrackedFile_PinsMessageAndDetailAsync()
    {
        var result = await _gitTools.GitTool(
            reason: "golden hunks no unstaged changes",
            GitOperation.hunks,
            paths: "README.md");

        AssertGolden(
            result,
            GitErrorCodes.NoChanges,
            "No unstaged changes in 'README.md'. Already staged? Git(reason: \"<why you call this>\", operation: diff, paths: \"...\", ref: \"staged\"). Nothing was changed.",
            "Nothing to act on. Call Git(reason: \"<why you call this>\", operation: status) to see what changed, then retry with the paths that have changes.");
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
    public async Task HunkSelection_StageHunkIdsWithoutFingerprint_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        var result = await _gitTools.GitTool(
            reason: "golden stage hunkIds no fingerprint",
            GitOperation.stage,
            paths: "a.txt",
            hunkIds: "1");

        AssertGolden(
            result,
            GitErrorCodes.HunkSelection,
            "hunkIds needs the hunkFingerprint returned by Git(reason: \"<why you call this>\", operation: hunks). Nothing was staged.",
            "Call Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pass hunkIds or lineRange exactly as it lists them.");
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

    [Test]
    public async Task HunkSelection_StageHunkIdsOutOfRange_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        var result = await _gitTools.GitTool(
            reason: "golden stage hunkIds out of range",
            GitOperation.stage,
            paths: "a.txt",
            hunkIds: "9",
            hunkFingerprint: "82a8e5e4cb0c");

        AssertGolden(
            result,
            GitErrorCodes.HunkSelection,
            "Hunk id 9 is out of range. Valid hunk ids are 1 to 1. Hunks (id: new-file lines): 1: 1. Nothing was staged.",
            "Call Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pass hunkIds or lineRange exactly as it lists them.");
    }

    [Test]
    public async Task HunkSelection_StageLineRangeNotNumbers_PinsMessageAndDetailAsync()
    {
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "one");
        GitTestRepo.CommitAll(_repoDir, "add a");
        File.WriteAllText(Path.Combine(_repoDir, "a.txt"), "two");

        var result = await _gitTools.GitTool(
            reason: "golden stage lineRange not numbers",
            GitOperation.stage,
            paths: "a.txt",
            lineRange: "abc");

        AssertGolden(
            result,
            GitErrorCodes.HunkSelection,
            "'abc' is not a valid line range. Use a single new-file line number like \"52\" or a range like \"40-80\". Hunks (id: new-file lines): 1: 1. Nothing was staged.",
            "Call Git(reason: \"<why you call this>\", operation: hunks, paths: \"<path>\") and pass hunkIds or lineRange exactly as it lists them.");
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
