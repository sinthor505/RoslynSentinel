// Battery #12 — CodeStyleEngine / SyntaxUpgradeEngine / ModernizationEngine / RefactoringEngine
// All engines in this battery require SentinelConfiguration, which defaults all features to enabled.

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ════════════════════════════════════════════════════════════════════════════════
// A. CodeStyleEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CodeStyleEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private CodeStyleEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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

        Assert.That(result, Does.Contain("GetName"), "Should generate GetName() method");
        Assert.That(result, Does.Contain("SetName"), "Should generate SetName(value) method");
        Assert.That(result, Does.Contain("_name"), "Should generate a backing field");
    }

    [Test]
    public async Task ConvertPropertyToMethods_UnknownFile_ReturnsEmptyString()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Entity.cs", "public class Entity { public string Name { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertPropertyToMethodsAsync("DoesNotExist.cs", "Name");

        Assert.That(result, Is.Empty, "Unknown file should return empty string");
    }

    [Test]
    public async Task ConvertPropertyToMethods_PropertyInInterface_ReturnsErrorComment()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("IService.cs", "public interface IService { string Name { get; set; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertPropertyToMethodsAsync("IService.cs", "Name");

        // The engine returns an error comment when property is not in a class (e.g. interface)
        Assert.That(result, Does.Contain("// ERROR:"), "Property in interface should return error comment");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. SyntaxUpgradeEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SyntaxUpgradeEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SyntaxUpgradeEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
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

        Assert.That(result, Does.Contain("{"), "Braces should be added around if body");
        Assert.That(result, Does.Contain("System.Console.WriteLine"), "Original statement should be preserved");
    }

    [Test]
    public async Task AddBraces_UnknownFile_ReturnsEmptyString()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddBracesAsync("DoesNotExist.cs");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task UpgradeToModernGuards_NoGuardPatterns_ReturnsNoOpComment()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { public void Run() { var x = 1; } }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.UpgradeToModernGuardsAsync("Code.cs");

        Assert.That(result, Does.StartWith("// No if-throw guard clause"),
            "No patterns should produce a no-op comment");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. ModernizationEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ModernizationEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ModernizationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ModernizationEngine(_workspaceManager, new SentinelConfiguration());
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

        Assert.That(result, Does.Contain("record"), "class should be converted to record");
        Assert.That(result, Does.Not.Contain("class Point"), "original class declaration should be gone");
    }

    [Test]
    public void ClassToRecord_UnknownFile_ThrowsException()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Dto.cs", "public class Point { }")]);
        _workspaceManager.SetTestSolution(solution);

        Assert.ThrowsAsync<InvalidOperationException>(() => _engine.ClassToRecordAsync("DoesNotExist.cs", "Point"));
    }

    [Test]
    public async Task ClassToRecord_UnknownClassName_ReturnsOriginalSource()
    {
        const string source = "public class Point { public int X { get; set; } }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Dto.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ClassToRecordAsync("Dto.cs", "NoSuchClass");

        Assert.That(result, Does.Contain("class Point"), "Original source should be returned unchanged");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. RefactoringEngine
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class RefactoringEngineTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private RefactoringEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new RefactoringEngine(
            NullLogger<RefactoringEngine>.Instance,
            _workspaceManager,
            new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FormatDocument_UnknownFile_ReturnsEmptyString()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C { }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FormatDocumentAsync("DoesNotExist.cs");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FormatDocument_ValidFile_ReturnsFormattedText()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Code.cs", "public class C{void M(){var x=1;}}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FormatDocumentAsync("Code.cs");

        Assert.That(result, Is.Not.Empty, "Formatted text should not be empty");
        Assert.That(result, Does.Contain("class C"), "Class declaration should remain");
    }

    [Test]
    public async Task ChangeSignature_TwoParameterMethod_ReordersParameters()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Calc.cs", "public class Calc { public int Add(int a, int b) { return a + b; } }")]);
        _workspaceManager.SetTestSolution(solution);

        // Swap parameters: new order [1, 0] means b comes first
        var changes = await _engine.ChangeSignatureAsync("Calc.cs", "Add", [1, 0]);

        Assert.That(changes, Is.Not.Empty, "Should return pending changes dict");
        var updatedContent = changes.Values.First();
        // After reorder, parameter b should precede a
        var bIndex = updatedContent.IndexOf("int b", StringComparison.Ordinal);
        var aIndex = updatedContent.IndexOf("int a", StringComparison.Ordinal);
        Assert.That(bIndex, Is.LessThan(aIndex), "Reordered: b should appear before a in parameter list");
    }
}
