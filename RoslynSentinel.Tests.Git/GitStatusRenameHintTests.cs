using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// The rename hint of <c>Git(operation: status)</c>: an unstaged move shows as a deletion plus an untracked file, and
/// <see cref="GitStatusRenameHints"/> says which two paths are one file. Tier 2 (identical content) is run with both git readers on a
/// throwaway repo; tier 1 (the MoveFile operation blob) is run through <see cref="GitTools"/> with a real
/// <c>move_file</c> blob written by <see cref="OperationBlobWriter"/> (the blob a MoveFile call leaves is the same shape; MoveFileTests covers the
/// tool itself), so the status path with and without <c>repoPath</c> is the real one.
/// </summary>
// KNOWN FLAKE on full/parallel runs (spawns git): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
[TestFixture]
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitStatusRenameHintTests
{
    private const string OldPath = "src/Old.cs";
    private const string NewPath = "moved/New.cs";
    private const string Content = "namespace Demo;\n\npublic class Thing\n{\n    public int Value => 42;\n}\n";

    private string _repoDir = "";
    private PersistentWorkspaceManager _manager = null!;
    private GitTools _gitTools = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        _repoDir = Path.Combine(Path.GetTempPath(), "RoslynSentinelGitRenameHint_" + Guid.NewGuid().ToString("N"));
        InitRepo(_repoDir);
        // Pin EOL handling per test: the machine's global autocrlf must not decide the outcome.
        RunGit(_repoDir, "config", "core.autocrlf", "false");

        // GitTool finds the git root from the manager's solution root, so point the real manager at the temp repo.
        _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _manager.SolutionPath = Path.Combine(_repoDir, "Fake.sln");
        _gitTools = new GitTools(_manager, NullLogger<GitTools>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        _manager?.Dispose();
        if (!Directory.Exists(_repoDir))
        {
            return;
        }

        // git marks pack/object files read-only; clear it so the delete succeeds on Windows.
        foreach (var file in Directory.EnumerateFiles(_repoDir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_repoDir, recursive: true);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_repoDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Commits OldPath, then moves it on disk by delete + create (nothing staged): status shows D plus untracked.
    private void CommitThenMoveOnDisk(string newContent)
    {
        Write(OldPath, Content);
        CommitAll(_repoDir, "base");
        File.Delete(Path.Combine(_repoDir, OldPath.Replace('/', Path.DirectorySeparatorChar)));
        Write(NewPath, newContent);
    }

    private static IGitReader ReaderFor(bool useLibgit2) => useLibgit2 ? new Libgit2GitReader() : new CliGitReader();

    private async Task<GitStatusResult> StatusAsync(IGitReader reader)
    {
        var impl = new GitImpl(new FakeWorkspaceManager(WorkspaceAccess.ReadOnly), NullLogger<GitImpl>.Instance, reader);
        return await impl.StatusAsync(_repoDir, GitImpl.DefaultStatusMaxEntries, CancellationToken.None);
    }

    // The blob a MoveFile call writes: the deleted source (before text, no after text) and the created destination (after text), tool name move_file.
    private async Task<string> WriteMoveBlobAsync(string changeId, string destinationText)
    {
        List<OperationItemRecord> items =
        [
            new() { FilePath = Path.Combine(_repoDir, "src", "Old.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = Content },
            new() { FilePath = Path.Combine(_repoDir, "moved", "New.cs"), Outcome = ItemRecordOutcome.Succeeded, AfterSource = destinationText, Created = true },
        ];
        var written = await OperationBlobWriter.WriteAsync("move_file", changeId, items, _repoDir);
        Assert.That(written.Written, Is.True, written.Diagnostic);
        return Path.Combine(_repoDir, ".roslynsentinel", "operations", written.FileName!);
    }

    // A move_file blob from sourcePath to destinationPath (repo-relative, '/'): the same shape as WriteMoveBlobAsync, with the paths as parameters.
    private async Task<string> WriteMoveBlobBetweenAsync(string changeId, string sourcePath, string destinationPath)
    {
        List<OperationItemRecord> items =
        [
            new() { FilePath = Path.Combine(_repoDir, sourcePath.Replace('/', Path.DirectorySeparatorChar)), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = Content },
            new() { FilePath = Path.Combine(_repoDir, destinationPath.Replace('/', Path.DirectorySeparatorChar)), Outcome = ItemRecordOutcome.Succeeded, AfterSource = Content, Created = true },
        ];
        var written = await OperationBlobWriter.WriteAsync("move_file", changeId, items, _repoDir);
        Assert.That(written.Written, Is.True, written.Diagnostic);
        return Path.Combine(_repoDir, ".roslynsentinel", "operations", written.FileName!);
    }

    private static List<string> GitMessages(SentinelCallToolResult<object> result) =>
        [.. (result.Findings ?? []).Where(f => f.Source == "Git").Select(f => f.Message)];

    [TestCase(true)]
    [TestCase(false)]
    public async Task Rename_IdenticalContentDeletedPlusUntracked_NamesBothPathsAsync(bool useLibgit2)
    {
        var reader = ReaderFor(useLibgit2);
        CommitThenMoveOnDisk(Content);
        var status = await StatusAsync(reader);

        var findings = GitStatusRenameHints.Findings(_repoDir, status, movePairs: null, reader);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Message, Is.EqualTo($"{OldPath} -> {NewPath}: renamed (identical content); stage both to record the rename"));
            Assert.That(findings[0].Severity, Is.EqualTo(FindingSeverity.Info));
        });
        // The hint is findings only: the status payload still lists D plus untracked.
        Assert.That(status.Unstaged.Select(e => $"{e.Status}:{e.Path}"), Is.EqualTo(new[] { $"deleted:{OldPath}" }));
        Assert.That(status.Untracked, Is.EqualTo(new[] { NewPath }));
    }

    [Test]
    public async Task Rename_ChangedContent_HasNoFindingAsync()
    {
        var reader = new CliGitReader();
        CommitThenMoveOnDisk(Content + "// edited\n");
        var status = await StatusAsync(reader);

        var findings = GitStatusRenameHints.Findings(_repoDir, status, movePairs: null, reader);

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public async Task Rename_EmptyFiles_AreNotPairedAsync()
    {
        var reader = new CliGitReader();
        Write(OldPath, string.Empty);
        CommitAll(_repoDir, "base");
        File.Delete(Path.Combine(_repoDir, "src", "Old.cs"));
        Write(NewPath, string.Empty);
        var status = await StatusAsync(reader);

        var findings = GitStatusRenameHints.Findings(_repoDir, status, movePairs: null, reader);

        Assert.That(findings, Is.Empty, "Every empty file equals every other, so an empty pair says nothing about a move.");
    }

    [Test]
    public async Task Status_IdenticalContentMove_ReportsRenameFindingThroughGitToolAsync()
    {
        CommitThenMoveOnDisk(Content);

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(GitMessages(result), Is.EqualTo(new[] { $"{OldPath} -> {NewPath}: renamed (identical content); stage both to record the rename" }));
    }

    [Test]
    public async Task Status_MoveFileBlob_ReportsDefinitiveFindingAndTierTwoDoesNotRepeatItAsync()
    {
        CommitThenMoveOnDisk(Content);
        await WriteMoveBlobAsync("abcd1234", Content);

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(GitMessages(result), Is.EqualTo(new[] { $"{OldPath} -> {NewPath}: moved by MoveFile (changeId abcd1234); git shows it as D plus untracked until both are staged" }));
    }

    [Test]
    public async Task Status_MoveFileBlobWithEditedDestination_StillReportsDefinitiveFindingAsync()
    {
        CommitThenMoveOnDisk(Content + "// edited after the move\n");
        await WriteMoveBlobAsync("abcd1234", Content);

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status);

        Assert.That(GitMessages(result), Is.EqualTo(new[] { $"{OldPath} -> {NewPath}: moved by MoveFile (changeId abcd1234); git shows it as D plus untracked until both are staged" }), "The operation record names the move even when the content no longer matches HEAD.");
    }

    [Test]
    public async Task Status_UndoneMoveFileBlob_IsNotReportedAsMovedAsync()
    {
        CommitThenMoveOnDisk(Content + "// edited after the move\n");
        var blobPath = await WriteMoveBlobAsync("abcd1234", Content);
        OperationBlobWriter.MarkUndone(blobPath);

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status);

        Assert.That(GitMessages(result), Is.Empty);
    }

    [Test]
    public async Task Status_WithRepoPath_SkipsTheMoveFileRecordButKeepsIdenticalContentAsync()
    {
        CommitThenMoveOnDisk(Content);
        await WriteMoveBlobAsync("abcd1234", Content);

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status, repoPath: _repoDir);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(GitMessages(result), Is.EqualTo(new[] { $"{OldPath} -> {NewPath}: renamed (identical content); stage both to record the rename" }));
    }

    [Test]
    public async Task Status_WithRepoPathAndEditedDestination_ReportsNothingFromTheMoveFileRecordAsync()
    {
        CommitThenMoveOnDisk(Content + "// edited after the move\n");
        await WriteMoveBlobAsync("abcd1234", Content);

        var result = await _gitTools.GitTool(reason: "test message", GitOperation.status, repoPath: _repoDir);

        Assert.That(GitMessages(result), Is.Empty, "repoPath names another repo, so this server's MoveFile record is not consulted.");
    }

    [Test]
    public async Task ReadMovePairs_TwoChainedMoves_AlsoYieldsTheJoinedFirstSourceToLastDestinationAsync()
    {
        await WriteMoveBlobBetweenAsync("aaaa1111", "src/Old.cs", "moved/New.cs");
        await WriteMoveBlobBetweenAsync("bbbb2222", "moved/New.cs", "final/Last.cs");

        var pairs = GitStatusRenameHints.ReadMovePairs(_repoDir, _repoDir);

        Assert.That(pairs.Any(p => p.Source == "src/Old.cs" && p.Destination == "final/Last.cs"), Is.True, "A -> B then B -> C must join into A -> C.");
    }

    [Test]
    public async Task ReadMovePairs_MoveCycle_DoesNotHangAndYieldsNoSelfMoveAsync()
    {
        await WriteMoveBlobBetweenAsync("aaaa1111", "src/Old.cs", "moved/New.cs");
        await WriteMoveBlobBetweenAsync("bbbb2222", "moved/New.cs", "src/Old.cs");

        var pairs = GitStatusRenameHints.ReadMovePairs(_repoDir, _repoDir);

        Assert.That(pairs.Where(p => p.Source == p.Destination), Is.Empty, "A move cycle must not produce a pair whose source equals its destination.");
    }
}
