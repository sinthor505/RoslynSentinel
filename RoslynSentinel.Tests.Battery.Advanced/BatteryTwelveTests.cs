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
public class CodeStyleEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private CodeStyleEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new CodeStyleEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

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
// B. SyntaxUpgradeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
public class SyntaxUpgradeEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SyntaxUpgradeEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SyntaxUpgradeEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AddBraces_IfWithoutBraces_AddsBraces()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { void M(int x) { if (x > 0) System.Console.WriteLine(x); } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddBracesAsync("Code.cs");

        Assert.That(result.UpdatedText, Does.Contain("{"), "Braces should be added around if body");
        Assert.That(result.UpdatedText, Does.Contain("System.Console.WriteLine"), "Original statement should be preserved");
    }

    [Test]
    public async Task AddBraces_UnknownFile_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.AddBracesAsync("DoesNotExist.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UpgradeToModernGuards_NoGuardPatterns_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { public void Run() { var x = 1; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UpgradeToModernGuardsAsync("Code.cs"));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. ModernizationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("SyntaxModernizationEngine")] // sentinel:auto-category
public class ModernizationEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SyntaxModernizationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SyntaxModernizationEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

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

// ════════════════════════════════════════════════════════════════════════════════
// D. BasicRefactoringEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
public class RefactoringEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FormatDocument_MissingFile_ThrowsNotFound()
    {
        // NOTE: FormatDocumentAsync is dead code (no production caller; owner decision 2026-10-22). This test is kept as-is; do not re-investigate.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { }")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.FormatDocumentAsync("DoesNotExist.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task FormatDocument_ValidFile_ReturnsFormattedText()
    {
        // NOTE: FormatDocumentAsync is dead code (no production caller; owner decision 2026-10-22). This test is kept as-is; do not re-investigate.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C{void M(){var x=1;}}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FormatDocumentAsync("Code.cs");

        Assert.That(result.UpdatedText, Is.Not.Empty, "Formatted text should not be empty");
        Assert.That(result.UpdatedText, Does.Contain("class C"), "Class declaration should remain");
        Assert.That(result.UpdatedText, Does.Contain("var x = 1;"), "Formatter should normalize spacing around operators");
    }

    [Test]
    [Category("ChangeSignatureResult")] // sentinel:auto-category
    [Category("ExistingParameterSpec")] // sentinel:auto-category
    public async Task ChangeSignature_TwoParameterMethod_ReordersParameters()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Calc.cs", "public class Calc { public int Add(int a, int b) { return a + b; } }")]);
        _workspaceManager.SetTestSolution(solution);

        // Swap parameters: new order [1, 0] means b comes first
        var result = await _engine.ChangeSignatureAsync("Calc.cs", "Add", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });
        var changes = result.Changes;

        Assert.That(changes, Is.Not.Empty, "Should return pending changes dict");
        var updatedContent = changes.Values.First();
        // After reorder, parameter b should precede a
        var bIndex = updatedContent.IndexOf("int b", StringComparison.Ordinal);
        var aIndex = updatedContent.IndexOf("int a", StringComparison.Ordinal);
        Assert.That(bIndex, Is.LessThan(aIndex), "Reordered: b should appear before a in parameter list");
    }
}
