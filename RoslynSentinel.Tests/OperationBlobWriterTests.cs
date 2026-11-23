// OperationBlobWriter's filename sanitization and typed failure result.
//
// The A3 defect from run 20260910-013550-398: 17 call sites in AdvancedRefactoringTools
// passed slashed operation names ("WrapRange/region", "Inline/method", …). Path.Combine resolved
// the blob into a non-existent operations/WrapRange/ subdirectory, the write threw
// DirectoryNotFoundException, and the catch swallowed it into a return string nobody inspected ->
// so WrapRange, ExtractMembers, SyncInterface, Inline and MoveType all returned changeIds
// UndoChange could never resolve. The call sites were renamed, but sanitization here is the
// durable fix: it holds for any future name regardless of caller discipline. These tests pin the
// sink, so a reverted rename can no longer reintroduce the bug.

namespace RoslynSentinel.Tests;

[TestFixture]
public class OperationBlobWriterTests
{
    private string _solutionRoot = null!;

    [SetUp]
    public void Setup()
    {
        _solutionRoot = Path.Combine(Path.GetTempPath(), "OperationBlobWriterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_solutionRoot);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_solutionRoot))
        {
            Directory.Delete(_solutionRoot, recursive: true);
        }
    }

    private static List<OperationItemRecord> OneItem() =>
    [
        new OperationItemRecord
        {
            FilePath = "C:/fake/File.cs",
            Outcome = ItemRecordOutcome.Succeeded,
            BeforeSource = "// before",
        }
    ];

    [Test]
    public async Task WriteAsync_SlashedToolName_StillProducesAFindableBlobAsync()
    {
        // The regression, stated directly. Before sanitization this threw
        // DirectoryNotFoundException and FindBlobPath returned null.
        const string changeId = "abc12345";

        var result = await OperationBlobWriter.WriteAsync(
            "WrapRange/region", changeId, OneItem(), _solutionRoot);

        Assert.That(result.Written, Is.True, result.Diagnostic);
        Assert.That(result.FileName, Is.Not.Null.And.Contain(changeId));
        Assert.That(OperationBlobWriter.FindBlobPath(changeId, _solutionRoot), Is.Not.Null,
            "a changeId that was issued must be resolvable by UndoChange");
    }

    [Test]
    public async Task WriteAsync_SlashedToolName_FlattensTheSlashRatherThanNestingAsync()
    {
        await OperationBlobWriter.WriteAsync("Inline/method", "def67890", OneItem(), _solutionRoot);

        var operationsDir = Path.Combine(_solutionRoot, ".roslynsentinel", "operations");
        Assert.That(Directory.GetDirectories(operationsDir), Is.Empty,
            "the operation name must not create a subdirectory - that nesting was the root cause");
        Assert.That(Path.GetFileName(Directory.GetFiles(operationsDir).Single()),
            Does.StartWith("Inline_method_"));
    }

    [Test]
    public async Task WriteAsync_NamesTheBlobFromTheCallsWriteTimeAsync()
    {
        var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);
        var context = new ToolCallContext("a", "implementer", "Member", "name test", 0);
        context.MarkWritten(stamp);
        ToolCallContext.Set(context);
        try
        {
            var result = await OperationBlobWriter.WriteAsync("Tool", "abc12345", OneItem(), _solutionRoot);

            Assert.That(result.FileName, Is.EqualTo("Tool_20260102T030405Z_1234567_abc12345.json"));
        }
        finally
        {
            ToolCallContext.Set(null);
        }
    }

    [Test]
    public async Task WriteAsync_WithoutACallContext_StillWritesAFractionSegmentAsync()
    {
        ToolCallContext.Set(null);

        var result = await OperationBlobWriter.WriteAsync("Tool", "abc12345", OneItem(), _solutionRoot);

        Assert.That(result.FileName, Does.Match(@"^Tool_\d{8}T\d{6}Z_\d{7}_abc12345\.json$"));
        Assert.That(OperationBlobWriter.FindBlobPath("abc12345", _solutionRoot), Is.Not.Null);
    }

    [TestCase("Weird:Name")]
    [TestCase("back\\slash")]
    [TestCase("with*star")]
    public async Task WriteAsync_OtherInvalidFileNameChars_AreAlsoSanitizedAsync(string toolName)
    {
        // Sanitization covers Path.GetInvalidFileNameChars() as a set, not just the two separators,
        // so a future operation name can't find a different way to break the same invariant.
        var changeId = Guid.NewGuid().ToString("n")[..8];

        var result = await OperationBlobWriter.WriteAsync(toolName, changeId, OneItem(), _solutionRoot);

        Assert.That(result.Written, Is.True, result.Diagnostic);
        Assert.That(result.FileName, Is.Not.Null.And.Contain(changeId));
        Assert.That(OperationBlobWriter.FindBlobPath(changeId, _solutionRoot), Is.Not.Null);
    }

    [Test]
    public async Task WriteAsync_NoSolutionRoot_IsNotAnIntegrityFailureAsync()
    {
        // With no solution root there is nowhere a blob could live and no undo semantics at all ->
        // the in-memory/test-solution case, not a server fault. Classifying it as a failure tripped
        // the unrecoverable breaker on the first apply of every SetTestSolution-based fixture and
        // then refused all subsequent ones, so this distinction is load-bearing.
        var result = await OperationBlobWriter.WriteAsync("Member", "aaa11111", OneItem(), solutionRoot: null);

        Assert.Multiple(() =>
        {
            Assert.That(result.Written, Is.False);
            Assert.That(result.Required, Is.False);
            Assert.That(result.IsIntegrityFailure, Is.False);
            Assert.That(result.Diagnostic, Is.EqualTo("no solution root - blob not applicable"));
        });
    }

    [Test]
    public async Task WriteAsync_SolutionRootPresentButUnwritable_IsAnIntegrityFailureAsync()
    {
        // The case that must still halt: a root exists, so a blob is genuinely owed, but the write
        // cannot land. "Owed but absent" has to stay distinguishable from "not owed" -> a caller
        // that can't tell them apart either warns on every no-op or stays silent on every fault.
        // A file standing where the operations directory must go makes CreateDirectory throw.
        var blocker = Path.Combine(_solutionRoot, ".roslynsentinel");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var result = await OperationBlobWriter.WriteAsync("Member", "eee55555", OneItem(), _solutionRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.Written, Is.False);
            Assert.That(result.Required, Is.True);
            Assert.That(result.IsIntegrityFailure, Is.True);
            Assert.That(result.Diagnostic, Does.StartWith("blob write failed: "));
        });
    }

    [Test]
    public async Task WriteApplyBlobAsync_NothingWritten_IsNotNeededRatherThanFailedAsync()
    {
        var result = await OperationBlobWriter.WriteApplyBlobAsync(
            "Member", "bbb22222",
            new ApplyChangesResult(IsError: false, SucceededFiles: [], FailedFiles: new(), Summary: "no-op"),
            _solutionRoot);

        Assert.Multiple(() =>
        {
            Assert.That(result.Written, Is.False);
            Assert.That(result.Required, Is.False, "no files were written, so no blob is owed");
            Assert.That(result.IsIntegrityFailure, Is.False, "a no-op must not read as a fault");
        });
    }

    // Items read back the way UndoChange / GetOperationDetail do: each element deserialised to OperationItemRecord.
    private List<OperationItemRecord> ReadBlobItems(string changeId)
    {
        var path = OperationBlobWriter.FindBlobPath(changeId, _solutionRoot);
        Assert.That(path, Is.Not.Null, "the blob must exist");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path!));
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<OperationItemRecord>(e.GetRawText())!)
            .ToList();
    }

    private static ApplyChangesResult ResultWithImages(
    IReadOnlyDictionary<string, string?>? preImages, IReadOnlyDictionary<string, string?>? postImages) =>
    new(IsError: false, SucceededFiles: ["C:/fake/A.cs", "C:/fake/B.cs"], FailedFiles: new(), Summary: "ok",
        PreImages: preImages, PostImages: postImages);

    [Test]
    public async Task WriteApplyBlobAsync_RecordsBeforeAndAfterSourceFromPreAndPostImagesAsync()
    {
        var result = ResultWithImages(
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A before", ["C:/fake/B.cs"] = "// B before" },
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A after\r\n", ["C:/fake/B.cs"] = "// B after" });

        var blob = await OperationBlobWriter.WriteApplyBlobAsync("Member", "fff66666", result, _solutionRoot);

        Assert.That(blob.Written, Is.True, blob.Diagnostic);
        var items = ReadBlobItems("fff66666");
        Assert.Multiple(() =>
        {
            Assert.That(items.Select(i => (i.FilePath, i.BeforeSource, i.AfterSource)), Is.EqualTo(new[]
            {
            ("C:/fake/A.cs", (string?)"// A before", (string?)"// A after\r\n"),
            ("C:/fake/B.cs", (string?)"// B before", (string?)"// B after"),
            }));
        });
    }

    [Test]
    public async Task WriteApplyBlobAsync_FileWithNoPostImageEntry_LeavesAfterSourceNullAsync()
    {
        // B is a no-op write (or a delete): the chokepoint recorded no PostImages entry for it.
        var result = ResultWithImages(
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A before", ["C:/fake/B.cs"] = "// B before" },
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A after" });

        await OperationBlobWriter.WriteApplyBlobAsync("Member", "ggg77777", result, _solutionRoot);

        var items = ReadBlobItems("ggg77777");
        Assert.Multiple(() =>
        {
            Assert.That(items.Single(i => i.FilePath == "C:/fake/A.cs").AfterSource, Is.EqualTo("// A after"));
            Assert.That(items.Single(i => i.FilePath == "C:/fake/B.cs").AfterSource, Is.Null);
            Assert.That(items.Single(i => i.FilePath == "C:/fake/B.cs").BeforeSource, Is.EqualTo("// B before"));
        });
    }

    [Test]
    public async Task WriteApplyBlobAsync_NullPostImages_LeavesEveryAfterSourceNullAsync()
    {
        var result = ResultWithImages(
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A before", ["C:/fake/B.cs"] = "// B before" },
            postImages: null);

        await OperationBlobWriter.WriteApplyBlobAsync("Member", "hhh88888", result, _solutionRoot);

        Assert.That(ReadBlobItems("hhh88888").Select(i => i.AfterSource), Is.All.Null);
    }

    [Test]
    public async Task WriteBlobForApplyAsync_RecordsBeforeAndAfterSourceFromPreAndPostImagesAsync()
    {
        using var manager = Fakes.FakeWorkspaceManager.ReadWrite();
        manager.SolutionPath = Path.Combine(_solutionRoot, "Test.sln");
        var result = ResultWithImages(
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A before", ["C:/fake/B.cs"] = "// B before" },
            new Dictionary<string, string?> { ["C:/fake/A.cs"] = "// A after" });

        var blob = await OperationBlobHelper.WriteBlobForApplyAsync(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, manager, "Member", result, "iii99999");

        Assert.That(blob.Written, Is.True, blob.Diagnostic);
        var items = ReadBlobItems("iii99999");
        Assert.Multiple(() =>
        {
            Assert.That(items.Single(i => i.FilePath == "C:/fake/A.cs").BeforeSource, Is.EqualTo("// A before"));
            Assert.That(items.Single(i => i.FilePath == "C:/fake/A.cs").AfterSource, Is.EqualTo("// A after"));
            Assert.That(items.Single(i => i.FilePath == "C:/fake/B.cs").BeforeSource, Is.EqualTo("// B before"));
            Assert.That(items.Single(i => i.FilePath == "C:/fake/B.cs").AfterSource, Is.Null, "no PostImages entry");
        });
    }

    [Test]
    public async Task BuildApplyItems_CreatedItemWithTextOfDeletedItem_StoresTextOnceAsync()
    {
        // A move: src is deleted (its pre-image holds the text) and dst is created with the same text.
        // dst must reference src rather than carry a second copy of the text in the blob.
        const string moved = "// MOVED_BODY_MARKER";
        var result = new ApplyChangesResult(
            IsError: false,
            SucceededFiles: ["C:/fake/src.cs", "C:/fake/dst.cs"],
            FailedFiles: new(),
            Summary: "ok",
            PreImages: new Dictionary<string, string?> { ["C:/fake/src.cs"] = moved },
            PostImages: new Dictionary<string, string?> { ["C:/fake/dst.cs"] = moved },
            CreatedFiles: ["C:/fake/dst.cs"]);

        var items = OperationBlobWriter.BuildApplyItems(result);

        Assert.Multiple(() =>
        {
            var src = items.Single(i => i.FilePath == "C:/fake/src.cs");
            var dst = items.Single(i => i.FilePath == "C:/fake/dst.cs");
            Assert.That(src.BeforeSource, Is.EqualTo(moved));
            Assert.That(src.AfterSource, Is.Null);
            Assert.That(dst.Created, Is.True);
            Assert.That(dst.AfterSource, Is.Null);
            Assert.That(dst.AfterSourceFromFilePath, Is.EqualTo("C:/fake/src.cs"));
        });

        var blob = await OperationBlobWriter.WriteApplyBlobAsync("Member", "hhh88888", result, _solutionRoot);

        Assert.That(blob.Written, Is.True, blob.Diagnostic);
        var blobText = File.ReadAllText(OperationBlobWriter.FindBlobPath("hhh88888", _solutionRoot)!);
        Assert.That(blobText.Split(moved).Length - 1, Is.EqualTo(1), "the moved text must be stored exactly once");
    }

    [Test]
    public void BuildApplyItems_CreatedItemWithDifferentText_KeepsAfterSource()
    {
        var result = new ApplyChangesResult(
            IsError: false,
            SucceededFiles: ["C:/fake/src.cs", "C:/fake/dst.cs"],
            FailedFiles: new(),
            Summary: "ok",
            PreImages: new Dictionary<string, string?> { ["C:/fake/src.cs"] = "// SRC_TEXT" },
            PostImages: new Dictionary<string, string?> { ["C:/fake/dst.cs"] = "// DIFFERENT_TEXT" },
            CreatedFiles: ["C:/fake/dst.cs"]);

        var items = OperationBlobWriter.BuildApplyItems(result);

        var dst = items.Single(i => i.FilePath == "C:/fake/dst.cs");
        Assert.Multiple(() =>
        {
            Assert.That(dst.Created, Is.True);
            Assert.That(dst.AfterSource, Is.EqualTo("// DIFFERENT_TEXT"));
            Assert.That(dst.AfterSourceFromFilePath, Is.Null);
        });
    }

    [Test]
    public void BuildApplyItems_NonCreatedItem_KeepsAfterSourceEvenWhenEqualToAnotherItemsBeforeSource()
    {
        // Only created items are deduplicated: an edited file keeps its after-text even when that text
        // happens to equal another file's before-text.
        const string shared = "// SHARED_TEXT";
        var result = new ApplyChangesResult(
            IsError: false,
            SucceededFiles: ["C:/fake/a.cs", "C:/fake/b.cs"],
            FailedFiles: new(),
            Summary: "ok",
            PreImages: new Dictionary<string, string?> { ["C:/fake/a.cs"] = shared, ["C:/fake/b.cs"] = "// B before" },
            PostImages: new Dictionary<string, string?> { ["C:/fake/b.cs"] = shared });

        var items = OperationBlobWriter.BuildApplyItems(result);

        var b = items.Single(i => i.FilePath == "C:/fake/b.cs");
        Assert.Multiple(() =>
        {
            Assert.That(b.Created, Is.False);
            Assert.That(b.AfterSource, Is.EqualTo(shared));
            Assert.That(b.AfterSourceFromFilePath, Is.Null);
        });
    }

    [Test]
    public void ResolveAfterSources_FillsAfterSourceFromTheReferencedItem_AndLeavesOldBlobItemsAlone()
    {
        var src = new OperationItemRecord { FilePath = "C:/fake/src.cs", Outcome = ItemRecordOutcome.Succeeded, BeforeSource = "// RESOLVED_TEXT" };
        var dst = new OperationItemRecord { FilePath = "C:/fake/dst.cs", Outcome = ItemRecordOutcome.Succeeded, Created = true, AfterSourceFromFilePath = "C:/FAKE/SRC.cs" };
        var orphan = new OperationItemRecord { FilePath = "C:/fake/orphan.cs", Outcome = ItemRecordOutcome.Succeeded, Created = true, AfterSourceFromFilePath = "C:/fake/missing.cs" };
        var noBefore = new OperationItemRecord { FilePath = "C:/fake/nobefore.cs", Outcome = ItemRecordOutcome.Succeeded, BeforeSource = null };
        var nullRef = new OperationItemRecord { FilePath = "C:/fake/nullref.cs", Outcome = ItemRecordOutcome.Succeeded, Created = true, AfterSourceFromFilePath = "C:/fake/nobefore.cs" };
        // An item written before AfterSourceFromFilePath existed: the key is absent from the JSON.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<OperationItemRecord>(
            "{\"FilePath\":\"C:/fake/legacy.cs\",\"AfterSource\":\"// LEGACY_AFTER\"}")!;

        OperationItemRecord.ResolveAfterSources([src, dst, orphan, noBefore, nullRef, legacy]);

        Assert.Multiple(() =>
        {
            Assert.That(dst.AfterSource, Is.EqualTo("// RESOLVED_TEXT"), "FilePath matches OrdinalIgnoreCase");
            Assert.That(orphan.AfterSource, Is.Null, "an unmatched reference leaves AfterSource null");
            Assert.That(nullRef.AfterSource, Is.Null, "a referenced item with a null BeforeSource leaves AfterSource null");
            Assert.That(legacy.AfterSource, Is.EqualTo("// LEGACY_AFTER"));
            Assert.That(legacy.AfterSourceFromFilePath, Is.Null);
            Assert.That(src.AfterSource, Is.Null);
        });
    }

    [Test]
    public async Task WriteBatchBlobOrTripAsync_OnFailure_TripsTheBreakerAsync()
    {
        // The batch path (Asyncify, BulkComment, …). Their result shape carries a BlobName string
        // rather than an ApplyOutcome, and all ten call sites previously assigned the write's
        // return value straight into it without checking -> hence the centralized helper.
        var breaker = new RecordingBreaker();
        // Unwritable root, not a missing one: a missing root means no blob is owed at all.
        await File.WriteAllTextAsync(Path.Combine(_solutionRoot, ".roslynsentinel"), "not a directory");

        var blobName = await OperationBlobWriter.WriteBatchBlobOrTripAsync(
            breaker, "asyncify", "ccc33333", OneItem(), _solutionRoot);

        Assert.That(breaker.Tripped, Is.True, "a failed batch blob write must halt the session");
        Assert.That(blobName, Does.Contain("blob"), "the diagnostic should still reach the result");
    }

    [Test]
    public async Task WriteBatchBlobOrTripAsync_OnSuccess_DoesNotTripAsync()
    {
        var breaker = new RecordingBreaker();

        var blobName = await OperationBlobWriter.WriteBatchBlobOrTripAsync(
            breaker, "asyncify", "ddd44444", OneItem(), _solutionRoot);

        Assert.That(breaker.Tripped, Is.False);
        Assert.That(blobName, Does.StartWith("asyncify_"));
    }

    /// <summary>
    /// Minimal <see cref="IUnrecoverableBreaker"/> that just records whether it was tripped.
    /// Used instead of a real workspace manager because these tests are about the writer's
    /// decision to trip, not about the workspace manager's halt state.
    /// </summary>
    private sealed class RecordingBreaker : IUnrecoverableBreaker
    {
        public bool Tripped { get; private set; }
        public string? Message { get; private set; }

        public void Trip(string toolName, string changeId, string diagnostic)
        {
            Tripped = true;
            Message = $"{toolName}/{changeId}: {diagnostic}";
        }

        bool IUnrecoverableBreaker.IsTripped() => Tripped;
        string? IUnrecoverableBreaker.StateMessage() => Message;
        bool ICircuitBreaker.IsTripped() => Tripped;
        string? ICircuitBreaker.StateMessage() => Message;
    }
}
