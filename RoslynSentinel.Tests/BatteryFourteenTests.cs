using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ────────────────────────────────────────────────────────────────────────────
// Battery #14 — AdvancedLogicEngine, AdvancedRefactoringEngine,
//               AdvancedStructuralEngine, AntiPatternEngine
// ────────────────────────────────────────────────────────────────────────────

[TestFixture]
public class AdvancedLogicEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private AdvancedLogicEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedLogicEngine(_mgr);
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task InvertBooleanLogic_UnknownFile_ReturnsEmptyDict()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.InvertBooleanLogicAsync("NoSuchFile.cs", "myBool");
        Assert.That(result, Is.Empty, "unknown file should yield empty dictionary");
    }

    [Test]
    public async Task ConvertIfToSwitchExpression_UnknownFile_ReturnsEmptyString()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.ConvertIfToSwitchExpressionAsync("NoSuchFile.cs", "GetValue");
        Assert.That(result.UpdatedText!, Is.EqualTo(""), "unknown file should return empty string");
    }

    [Test]
    public async Task ExtensionToStatic_MethodWithThisParam_RemovesThisKeyword()
    {
        const string source = @"
namespace MyNs;
public static class MyExtensions
{
    public static string Shout(this string s) => s.ToUpperInvariant();
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Extensions.cs", source)]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.ExtensionToStaticAsync("Extensions.cs", "Shout");

        Assert.That(result, Does.Not.Contain("this string"), "this keyword should be stripped from first parameter");
        Assert.That(result, Does.Contain("Shout"), "method should still be present");
    }
}

[TestFixture]
public class AdvancedRefactoringEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private AdvancedRefactoringEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedRefactoringEngine(_mgr);
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public void ReplaceStringConcat_UnknownFile_ThrowsException()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _mgr.SetTestSolution(solution);

        Assert.ThrowsAsync<Exception>(
            async () => await _engine.ReplaceStringConcatWithInterpolationAsync("NoSuchFile.cs"),
            "missing file should throw Exception");
    }

    [Test]
    public async Task ReplaceStringConcat_WithPlusConcat_ConvertsToInterpolation()
    {
        const string source = @"
namespace MyNs;
public class Greeter
{
    public string Greet(string name) => ""Hello "" + name + ""!"";
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Greeter.cs", source)]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.ReplaceStringConcatWithInterpolationAsync("Greeter.cs");

        Assert.That(result, Does.Contain("$\""), "result should use string interpolation syntax");
    }

    [Test]
    public void OptimizeTaskWait_UnknownFile_ThrowsException()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _mgr.SetTestSolution(solution);

        Assert.ThrowsAsync<Exception>(
            async () => await _engine.OptimizeTaskWaitAsync("NoSuchFile.cs"),
            "missing file should throw Exception");
    }
}

[TestFixture]
public class AdvancedStructuralEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private AdvancedStructuralEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedStructuralEngine(_mgr);
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task ConvertAbstractClassToInterface_UnknownFile_ReturnsEmptyString()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.ConvertAbstractClassToInterfaceAsync("NoSuchFile.cs", "MyBase");
        Assert.That(result.UpdatedText!, Is.EqualTo(""), "unknown file should return empty string");
    }

    [Test]
    public async Task ConvertAbstractClassToInterface_AbstractClass_GeneratesInterface()
    {
        const string source = @"
namespace MyNs;
public abstract class Animal
{
    public abstract void Speak();
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Animal.cs", source)]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.ConvertAbstractClassToInterfaceAsync("Animal.cs", "Animal");

        Assert.That(result, Does.Contain("interface IAnimal"), "abstract class should become an interface");
        Assert.That(result, Does.Contain("Speak"), "interface should include the abstract method signature");
    }

    [Test]
    public async Task ReplaceConstructorWithFactory_ClassWithCtor_AddsFactoryMethod()
    {
        const string source = @"
namespace MyNs;
public class Widget
{
    private readonly int _id;
    public Widget(int id) { _id = id; }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Widget.cs", source)]);
        _mgr.SetTestSolution(solution);

        var result = await _engine.ReplaceConstructorWithFactoryAsync("Widget.cs", "Widget");

        Assert.That(result, Does.Contain("static"), "factory method should be static");
        Assert.That(result, Does.Contain("Create"), "factory method should be named Create");
    }
}

[TestFixture]
public class AntiPatternEngineTests
{
    private PersistentWorkspaceManager _mgr = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _mgr = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_mgr);
    }

    [TearDown]
    public void TearDown() => _mgr?.Dispose();

    [Test]
    public async Task DetectAntiPatterns_AsyncVoidMethod_ReturnsFindings()
    {
        const string source = @"
public class Handler
{
    public async void OnEvent() { await System.Threading.Tasks.Task.CompletedTask; }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Handler.cs", source)]);
        _mgr.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync(
            filePath: "Handler.cs",
            patternFilter: new[] { "AsyncVoidMethod" });

        Assert.That(findings, Is.Not.Empty, "async void method should be detected as an anti-pattern");
        Assert.That(findings.All(f => f.Pattern == "AsyncVoidMethod"), Is.True, "all findings should match the requested pattern");
    }

    [Test]
    public async Task DetectAntiPatterns_PatternFilter_LimitsResults()
    {
        const string source = @"
public class BigClass
{
    public async void BadHandler() { await System.Threading.Tasks.Task.CompletedTask; }
    public void Looper(System.Collections.Generic.List<string> list) 
    {
        string result = """";
        foreach (var s in list) result += s;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("BigClass.cs", source)]);
        _mgr.SetTestSolution(solution);

        // Only ask for StringConcatInLoop; async void should NOT appear
        var findings = await _engine.DetectAntiPatternsAsync(
            filePath: "BigClass.cs",
            patternFilter: new[] { "StringConcatInLoop" });

        Assert.That(findings.All(f => f.Pattern == "StringConcatInLoop"), Is.True,
            "pattern filter should exclude AsyncVoidMethod findings");
    }

    [Test]
    public async Task DetectAntiPatterns_UnknownFilePath_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Other.cs", "public class Other {}")]);
        _mgr.SetTestSolution(solution);

        var findings = await _engine.DetectAntiPatternsAsync(filePath: "Ghost.cs");
        Assert.That(findings, Is.Empty, "non-existent file path should yield no findings");
    }
}
