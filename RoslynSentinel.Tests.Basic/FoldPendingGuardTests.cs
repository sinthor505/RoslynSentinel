using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

// Fold-pending guard at the write chokepoint (stale-read follow-ups step 8b): a batch is refused with
// ToolErrorCode.FoldPending, non-latching, when a target's in-memory document differs from its disk pre-image beyond
// the trivia FoldComparison converges (BOM, line endings, per-line trailing whitespace). The refused files are folded
// from disk inline, so the retry sees fresh text. Also covers FoldComparison itself and WriteRefusal's FoldPending
// mapping. Real-manager tests use PersistentWorkspaceManager, never FakeWorkspaceManager.
[TestFixture]
public class FoldPendingGuardTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    [SetUp]
    public void ResetIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearIdentity() => ToolCallContext.Set(null);

    private static string Variant(int n) => Original.Replace("=> 1", $"=> {n}");

    private static void AsAgent(string agentId, long startSeq = 0) =>
        ToolCallContext.Set(new ToolCallContext(agentId, null, "WriteFile", null, startSeq));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FoldPendingGuard", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Real documents whose FilePath is the temp file, so the fold guard has an in-memory text to compare with disk.
    private static Solution SolutionWithDocuments(params (string File, string Text)[] documents)
    {
        var project = new AdhocWorkspace().AddProject("FoldPending", LanguageNames.CSharp);
        foreach (var (file, text) in documents)
        {
            project = project.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project;
        }

        return project.Solution;
    }

    private static PersistentWorkspaceManager NewManager(params (string File, string Text)[] documents)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(SolutionWithDocuments(documents));
        return manager;
    }

    private static async Task<string> InMemoryText(PersistentWorkspaceManager manager, string file)
    {
        var documentId = manager.CurrentSolution!.GetDocumentIdsWithFilePath(file).Single();
        return (await manager.CurrentSolution.GetDocument(documentId)!.GetTextAsync()).ToString();
    }

    [TestCase("\uFEFFclass A { }\n", "class A { }\n", TestName = "Bom")]
    [TestCase("class A {\r\n}\r\n", "class A {\n}\n", TestName = "CrlfVsLf")]
    [TestCase("class A {  \n}\n", "class A {\n}\n", TestName = "TrailingSpacesPerLine")]
    public void Compare_DiffersOnlyByBomEolOrTrailingWhitespace_IsConverged(string disk, string memory)
    {
        Assert.That(FoldComparison.Compare(disk, memory), Is.EqualTo(FoldComparisonOutcome.Converged));
    }

    [Test]
    public void Compare_IdenticalText_IsEqual()
    {
        Assert.That(FoldComparison.Compare("class A { }\n", "class A { }\n"), Is.EqualTo(FoldComparisonOutcome.Equal));
    }

    [TestCase("// old\nclass A { }\n", "// new\nclass A { }\n", TestName = "ChangedComment")]
    [TestCase("class A { }\n", "class A { }", TestName = "FinalNewlineDiffers")]
    public void Compare_ContentOrFinalNewlineDiffers_IsMismatch(string disk, string memory)
    {
        Assert.That(FoldComparison.Compare(disk, memory), Is.EqualTo(FoldComparisonOutcome.Mismatch));
    }

    private const string ToolName = "WriteFile";
    private const string Dir = @"C:\repo\src\";

    private static string FileNamed(int index) => $@"{Dir}F{index:D2}.cs";

    private static ApplyChangesResult FoldPendingRefusal(params string[] files)
    {
        var failed = new Dictionary<FilePathWrapper, string>();
        foreach (var file in files)
        {
            failed.Add(new FilePathWrapper(file), "changing on disk; retry in a moment");
        }

        return new ApplyChangesResult(
            IsError: true,
            SucceededFiles: new List<string>(),
            FailedFiles: failed,
            Summary: "FoldPending",
            RefusalCode: ToolErrorCode.FoldPending);
    }

    [Test]
    public void FoldPendingSingleFile_MapsToFoldPendingErrorNamingFileAndRetry()
    {
        var file = FileNamed(1);

        var mapped = WriteRefusal.TryMap(ToolName, FoldPendingRefusal(file), out var error);

        Assert.That(mapped, Is.True);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.FoldPending));
        Assert.That(error.Message, Does.Contain(file));
        Assert.That(error.Message, Does.Contain("is changing on disk; retry in a moment"));
    }

    [Test]
    public void FoldPendingTwelveFiles_ListsFirstTenAndCountsTheRestAsMore()
    {
        var files = Enumerable.Range(1, 12).Select(FileNamed).ToArray();

        var mapped = WriteRefusal.TryMap(ToolName, FoldPendingRefusal(files), out var error);

        Assert.That(mapped, Is.True);
        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.FoldPending));
        for (var i = 0; i < 10; i++)
        {
            Assert.That(error.Message, Does.Contain(files[i]), $"first ten should be listed: {files[i]}");
        }

        for (var i = 10; i < 12; i++)
        {
            Assert.That(error.Message, Does.Not.Contain(files[i]), $"file beyond the first ten should not be listed: {files[i]}");
        }

        Assert.That(error.Message, Does.Contain("and 2 more"));
    }

    [Test]
    public async Task DiskDivergesFromMemory_RefusesWithFoldPendingWritesNothingAndDoesNotLatch()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Variant(3));
            using var manager = NewManager((file, Original));
            AsAgent("agentA");

            var refused = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(refused.IsError, Is.True, refused.Summary);
            Assert.That(refused.RefusalCode, Is.EqualTo(ToolErrorCode.FoldPending));
            Assert.That(refused.Summary, Does.StartWith("FoldPending:"));
            Assert.That(refused.FailedFiles.ContainsKey(new FilePathWrapper(file)), Is.True);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)),
                "A fold refusal must not write anything.");
            Assert.That(manager.IsSessionHalted(), Is.False, "A fold refusal is non-latching.");
            Assert.That(await InMemoryText(manager, file), Is.EqualTo(Variant(3)),
                "The refused file must be folded from disk inline.");

            var retried = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(retried.IsError, Is.False, retried.Summary);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task OneDivergingTargetInBatch_RefusesWholeBatchAndWritesNeither()
    {
        var dir = NewTempDir();
        try
        {
            var diverged = Path.Combine(dir, "Diverged.cs");
            var clean = Path.Combine(dir, "Clean.cs");
            File.WriteAllText(diverged, Variant(3));
            File.WriteAllText(clean, Original);
            using var manager = NewManager((diverged, Original), (clean, Original));
            AsAgent("agentA");

            var refused = await manager.ApplyProposedChangesAsync(
                new() { [diverged] = Variant(2), [clean] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(refused.IsError, Is.True, refused.Summary);
            Assert.That(refused.RefusalCode, Is.EqualTo(ToolErrorCode.FoldPending));
            Assert.That(refused.FailedFiles.ContainsKey(new FilePathWrapper(diverged)), Is.True);
            Assert.That(refused.FailedFiles.ContainsKey(new FilePathWrapper(clean)), Is.False,
                "Only the diverging file is named; the clean file is refused by the batch, not by its own check.");
            Assert.That(await File.ReadAllTextAsync(diverged), Is.EqualTo(Variant(3)));
            Assert.That(await File.ReadAllTextAsync(clean), Is.EqualTo(Original),
                "All-or-nothing: the clean file must not be written when the batch is refused.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task DiskDivergesFromMemory_ExactRestore_IsNotRefused()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Variant(3));
            using var manager = NewManager((file, Original));
            AsAgent("agentA");

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false, exactRestore: true);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.FoldPending));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task NewFileWithNoDocument_IsNotChecked()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "New.cs");
            using var manager = NewManager();
            AsAgent("agentA");

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.FoldPending));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TargetWithNoDiskPreImage_IsNotChecked()
    {
        var dir = NewTempDir();
        try
        {
            // The document exists in memory but the file does not exist on disk, so there is no pre-image to compare.
            var file = Path.Combine(dir, "A.cs");
            using var manager = NewManager((file, Variant(4)));
            AsAgent("agentA");

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.RefusalCode, Is.Not.EqualTo(ToolErrorCode.FoldPending));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task StaleLedgerEntryAndDivergence_RefusesWithStaleReadNotFoldPending()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Variant(4)));
            AsAgent("agentA");
            manager.RecordObserved(new FilePathWrapper(file), Original);
            File.WriteAllText(file, Variant(3));

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: StaleReadPolicy.RefuseIfObservedStale);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.StaleRead),
                "StaleRead wins over FoldPending for the same target.");
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task CrlfOnDiskWhereMemoryHasLf_ProceedsWithConvergedInfoFinding()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original);
            using var manager = NewManager((file, Original.Replace("\r\n", "\n")));
            AsAgent("agentA");

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
            Assert.That(result.Findings, Is.Not.Null);
            Assert.That(result.Findings!.Any(f => f.Source == "WriteChokepoint"
                && f.Severity == FindingSeverity.Info && f.Message.Contains("converged")), Is.True,
                "A converged target must carry an Info finding from WriteChokepoint.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task TrailingWhitespaceOnDisk_ProceedsWithConvergedInfoFinding()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Original.Replace("=> 1;", "=> 1;   "));
            using var manager = NewManager((file, Original));
            AsAgent("agentA");

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false);

            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(2)));
            Assert.That(result.Findings, Is.Not.Null);
            Assert.That(result.Findings!.Any(f => f.Source == "WriteChokepoint"
                && f.Severity == FindingSeverity.Info && f.Message.Contains("converged")), Is.True,
                "A converged target must carry an Info finding from WriteChokepoint.");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestCase(StaleReadPolicy.None)]
    [TestCase(StaleReadPolicy.RefuseIfObservedStale)]
    [TestCase(StaleReadPolicy.RefuseIfObservedStaleQuiet)]
    public async Task DiskDivergesFromMemory_RefusedUnderEveryStaleReadPolicy(StaleReadPolicy policy)
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, Variant(3));
            using var manager = NewManager((file, Original));
            AsAgent("agentA");

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = Variant(2) }, retryCount: 0, validateChanges: false,
                staleReadPolicy: policy);

            Assert.That(result.IsError, Is.True, result.Summary);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.FoldPending));
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(Variant(3)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
