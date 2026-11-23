// Battery #12 -> CodeStyleEngine / SyntaxUpgradeEngine / ModernizationEngine / BasicRefactoringEngine
// All engines in this battery require SentinelConfiguration, which defaults all features to enabled.

using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

// ════════════════════════════════════════════════════════════════════════════════
// A. CodeStyleEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("CodeStyleEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class CodeStyleEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private CodeStyleEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.CodeStyleEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task ConvertPropertyToMethods_ClassWithProperty_GeneratesGetterAndSetter()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Entity.cs", "public class Entity { public string Name { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertPropertyToMethodsAsync("Entity.cs", "Name");

        Assert.That(result.UpdatedText, Does.Contain("GetName"), "Should generate GetName() method");
        Assert.That(result.UpdatedText, Does.Contain("SetName"), "Should generate SetName(value) method");
        Assert.That(result.UpdatedText, Does.Contain("_name"), "Should generate a backing field");
    }

    [Test]
    public async Task ConvertPropertyToMethods_MissingFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Entity.cs", "public class Entity { public string Name { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertPropertyToMethodsAsync("DoesNotExist.cs", "Name"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ConvertPropertyToMethods_PropertyInInterface_ThrowsTargetIneligible()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("IService.cs", "public interface IService { string Name { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolTargetIneligibleException>(async () => await _engine.ConvertPropertyToMethodsAsync("IService.cs", "Name"));

        // The engine throws ToolTargetIneligibleException when the property is not in a class (e.g. interface)
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible), "Property in interface should throw TargetIneligible");
        Assert.That(ex.Message, Does.Contain("must be in a class"));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. ModernizationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("SyntaxModernizationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class ModernizationEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SyntaxModernizationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task ClassToRecord_SimpleClass_ConvertsToRecord()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Dto.cs", "public class Point { public int X { get; set; } public int Y { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ClassToRecordAsync("Dto.cs", "Point");

        Assert.That(result.UpdatedText, Does.Contain("record"), "class should be converted to record");
        Assert.That(result.UpdatedText, Does.Not.Contain("class Point"), "original class declaration should be gone");
    }

    [Test]
    public async Task ClassToRecord_MissingFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Dto.cs", "public class Point { }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ClassToRecordAsync("DoesNotExist.cs", "Point"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ClassToRecord_UnknownClassName_ThrowsNotFound()
    {
        const string source = "public class Point { public int X { get; set; } }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Dto.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        // An unknown class is a typed NotFound failure, not a returned result.
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _engine.ClassToRecordAsync("Dto.cs", "NoSuchClass"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(ex.Message, Does.Contain("Class 'NoSuchClass' not found."));
    }
}
