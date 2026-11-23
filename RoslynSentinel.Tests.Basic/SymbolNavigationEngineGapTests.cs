// Battery #25 -> Gap Coverage: 40 untested engine methods across 14 engines
// Each group covers the specific methods that had zero test references.
// All tests use in-memory AdhocWorkspace via TestSolutionBuilder (no MSBuild/project loading).
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
[Category("SymbolNavigationEngine")] // sentinel:auto-category
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class SymbolNavigationEngineGapTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private SymbolNavigationEngine _engine = null!;
    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }
    [Test]
    [Category("SymbolHoverInfo")] // sentinel:auto-category
    public async Task GetSymbolInfoAsync_KnownMethodSnippet_ReturnsNonNull()
    {
        var source = @"
public class Calculator {
    public int Add(int a, int b) => a + b;
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calculator.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        // The method may return null if it can't resolve the symbol in in-memory workspace
        var result = await _engine.GetSymbolInfoAsync("Calculator.cs", "Add");
        // Either a valid result or null is acceptable -> method should not throw
        Assert.That(result, Is.Not.Null); Assert.That(result!.Name, Is.EqualTo("Add"));
    }

    [Test]
    [Category("SymbolHoverInfo")]
    public async Task GetSymbolInfoAsync_SnippetMatchesKeywordOnly_ErrorNamesNodeKindAndSaysNoSymbol()
    {
        var source = @"
public class Worker {
    public void Run() {
        return;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Worker.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.GetSymbolInfoAsync("Worker.cs", "return;");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.True);
        Assert.That(result.Message, Does.Contain("but no symbol is there"));
    }

    [Test]
    public async Task FindAllImplementationsAsync_KnownInterface_ReturnsNonNull()
    {
        var source = @"
public interface IAnimal { void Speak(); }
public class Dog : IAnimal { public void Speak() { } }
";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Animals.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.FindAllImplementationsAsync("IAnimal");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].TypeName, Is.EqualTo("Dog"));
    }

    [Test]
    public async Task FindAllImplementationsAsync_UnknownType_ReturnsEmptyList()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.FindAllImplementationsAsync("IDoesNotExist");
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task GetTypeMembersDetailAsync_KnownType_ReturnsNonNull()
    {
        var source = @"
public class Person {
    public string Name { get; set; } = """";
    public int Age { get; set; }
    public void Greet() {}
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Person.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.GetTypeMembersDetailAsync("Person");
        Assert.That(result, Has.Some.Property("Name").EqualTo("Name"));
        Assert.That(result, Has.Some.Property("Name").EqualTo("Age"));
    }

    [Test]
    public async Task GetTypeMembersDetailAsync_UnknownType_ReturnsEmptyList()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.GetTypeMembersDetailAsync("NoSuchType");
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task VerifyInterfaceCompletenessAsync_InterfaceWithImplementors_ReturnsNonNull()
    {
        var source = @"
public interface IWorker { void Work(); string Report(); }
public class Worker1 : IWorker { public void Work() {} public string Report() => """"; }
";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Workers.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.VerifyInterfaceCompletenessAsync("IWorker");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ImplementorName, Is.EqualTo("Worker1"));
        Assert.That(result[0].IsFullyComplete, Is.True);
    }

    [Test]
    public async Task VerifyInterfaceCompletenessAsync_UnknownInterface_ReturnsEmptyList()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.VerifyInterfaceCompletenessAsync("IDoesNotExist");
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task FindExtensionMethodsAsync_KnownTargetType_ReturnsNonNull()
    {
        var source = @"
public static class StringExtensions {
    public static string Shout(this string s) => s.ToUpperInvariant() + ""!"";
}
";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Extensions.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.FindExtensionMethodsAsync("string");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].MethodName, Is.EqualTo("Shout"));
        Assert.That(result[0].DefiningClass, Is.EqualTo("StringExtensions"));
    }

    [Test]
    public async Task FindExtensionMethodsAsync_TypeWithNoExtensions_ReturnsEmptyOrNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.FindExtensionMethodsAsync("SomeRandomType");
        Assert.That(result, Is.Empty);
    }
}
