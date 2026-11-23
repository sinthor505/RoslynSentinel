using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SolutionStructureEngineTests2
{
    private PersistentWorkspaceManager _mgr = null!;
    private SolutionStructureEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _mgr = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SolutionStructureEngine(_mgr, Basic.TestEngines.SentinelConfiguration());
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
    public async Task FixMismatchedNamespaces_UnknownFile_ThrowsFileNotFound()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(
            async () => await _engine.FixMismatchedNamespacesAsync("NoSuchFile.cs"));
    }

    [Test]
    public async Task FindStructuralSmells_UnknownFile_Throws_ToolNotFound()
    {
        await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FindStructuralSmellsAsync(filePath: "NoSuchFile.cs"));
    }

    [Test]
    public async Task FindStructuralSmells_MultipleTypesInOneFile_DetectsMultiTypeSmell()
    {
        // MultiType smell: more than one type declared in a single file
        const string source = @"
public class ClassA {}
public class ClassB {}
";
        _mgr.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("TwoTypes.cs", source)]));

        var smells = await _engine.FindStructuralSmellsAsync(filePath: "TwoTypes.cs");

        Assert.That(smells.Select(s => s.ToString()), Has.Some.Contains("MULTI_TYPE"), "two types in one file should trigger a MultiType structural smell");
    }
}
