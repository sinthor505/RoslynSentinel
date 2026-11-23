using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("MsAugmentResult")] // sentinel:auto-category
[Category("MsToolAugmentEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class MsToolAugmentEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private MsToolAugmentEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _mgr = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.MsToolAugmentEngine(_mgr);
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]));
    }

    [TearDown]
    public void TearDown()
    {
        _mgr?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task EncapsulateFieldSafe_UnknownFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.EncapsulateFieldSafeAsync("NoSuchFile.cs", "MyClass", "_field"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task FormatDocumentSafe_UnknownFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FormatDocumentSafeAsync("NoSuchFile.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task SortAndDeduplicateUsings_UnknownFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.SortAndDeduplicateUsingsAsync("NoSuchFile.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task EncapsulateFieldSafeAsync_NonExistentFile_ThrowsNotFound()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.EncapsulateFieldSafeAsync("NonExistent.cs", "MyField"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound), "error should be NotFound code");
    }
}
