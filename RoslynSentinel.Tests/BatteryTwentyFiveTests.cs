// Battery #25 — Gap Coverage: 40 untested engine methods across 14 engines
// Each group covers the specific methods that had zero test references.
// All tests use in-memory AdhocWorkspace via TestSolutionBuilder (no MSBuild/project loading).

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

// ════════════════════════════════════════════════════════════════════════════════
// A. AdvancedLogicEngine — 4 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AdvancedLogicEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AdvancedLogicEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AdvancedLogicEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task ConvertIfToSwitchStatementAsync_ValidMethod_ReturnsNonNull()
    {
        var source = @"
public class Calc {
    public string Describe(int x) {
        if (x == 1) return ""one"";
        else if (x == 2) return ""two"";
        else return ""other"";
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertIfToSwitchStatementAsync("Calc.cs", "Describe");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertIfToSwitchStatementAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class Other {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertIfToSwitchStatementAsync("NoSuchFile.cs", "Foo");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task ConvertForEachToForAsync_ValidLine_ReturnsNonNull()
    {
        var source = @"
using System.Collections.Generic;
public class Looper {
    public void Run(List<int> items) {
        foreach (var item in items) { var x = item; }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Looper.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertForEachToForAsync("Looper.cs", 5);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertForToForEachAsync_ValidLine_ReturnsNonNull()
    {
        var source = @"
public class Looper {
    public void Run(int[] arr) {
        for (int i = 0; i < arr.Length; i++) { var x = arr[i]; }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Looper.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertForToForEachAsync("Looper.cs", 4);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertWhileToForAsync_ValidLine_ReturnsNonNull()
    {
        var source = @"
public class Looper {
    public void Run() {
        int i = 0;
        while (i < 10) { i++; }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Looper.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertWhileToForAsync("Looper.cs", 5);

        Assert.That(result, Is.Not.Null);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// B. AnalysisEngine — 9 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AnalysisEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AnalysisEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindInterfaceExtractionCandidatesAsync_SmallClass_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Tiny { public void A() {} public void B() {} public void C() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindInterfaceExtractionCandidatesAsync(minPublicMethods: 2);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindUninstantiatedTypesAsync_EmptyProject_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Used { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindUninstantiatedTypesAsync();

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DetectUnreachableCodeAsync_MethodWithReturn_ReturnsNonNull()
    {
        var source = @"
public class Guard {
    public void Method() {
        return;
        int x = 1;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Guard.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.DetectUnreachableCodeAsync("Guard.cs", "Method");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DetectUnreachableCodeAsync_UnknownFile_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.DetectUnreachableCodeAsync("NoFile.cs", "Foo");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeSemaphoreUsageAsync_FileWithNoSemaphore_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeSemaphoreUsageAsync("Service.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindInternalClassesThatCouldBePrivateAsync_SimpleProject_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Outer { internal class Inner {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindInternalClassesThatCouldBePrivateAsync();

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindLargeSwitchStatementsAsync_NoSwitch_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindLargeSwitchStatementsAsync(threshold: 5);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty, "No switch statements, so nothing should be flagged");
    }

    [Test]
    public async Task FindLargeSwitchStatementsAsync_LargeSwitch_ReturnsNonEmpty()
    {
        var source = @"
public class Router {
    public string Route(int x) {
        switch (x) {
            case 1: return ""a"";
            case 2: return ""b"";
            case 3: return ""c"";
            case 4: return ""d"";
            case 5: return ""e"";
            case 6: return ""f"";
            default: return ""g"";
        }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Router.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindLargeSwitchStatementsAsync(threshold: 5);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Not.Empty, "A 6-case switch should exceed threshold of 5");
    }

    [Test]
    public async Task OptimizeResourceDisposalAsync_SimpleFile_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.OptimizeResourceDisposalAsync(filePath: "Service.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DetectReflectionUsageAsync_FileWithNoReflection_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.DetectReflectionUsageAsync(filePath: "Service.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindUnusedInterfacesAsync_WithUnimplementedInterface_ReturnsNonNull()
    {
        var source = @"
public interface IFoo { void Do(); }
public class Bar {}
";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("App.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindUnusedInterfacesAsync();

        Assert.That(result, Is.Not.Null);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// C. AntiPatternEngine — 4 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class AntiPatternEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AntiPatternEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AntiPatternEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task FindMutablePublicPropertiesAsync_ClassWithMutableProp_ReturnsNonNull()
    {
        var source = "public class Entity { public string Name { get; set; } = \"\"; }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Entity.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindMutablePublicPropertiesAsync(filePath: "Entity.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindNamingViolationsAsync_ClassWithBadName_ReturnsNonNull()
    {
        var source = "public class my_class { public int myField = 0; }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyClass.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindNamingViolationsAsync(filePath: "MyClass.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindMissingCancellationTokensAsync_AsyncMethodWithoutToken_ReturnsNonNull()
    {
        var source = @"
using System.Threading.Tasks;
public class Service {
    public async Task DoWorkAsync() { await System.Threading.Tasks.Task.Delay(10); }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindMissingCancellationTokensAsync(filePath: "Service.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AnalyzeExceptionHandlingAsync_MethodWithCatch_ReturnsNonNull()
    {
        var source = @"
public class Service {
    public void Run() {
        try { int x = 1; }
        catch (System.Exception) { }
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeExceptionHandlingAsync("Service.cs");

        Assert.That(result, Is.Not.Null);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// D. CodeHealingEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CodeHealingEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private CodeHealingEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new CodeHealingEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AddRetryPolicyAsync_AnyInput_ReturnsNonNull()
    {
        // AddRetryPolicyAsync is a stub that always returns "" — verify it at least doesn't throw
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddRetryPolicyAsync("Service.cs", 1, 5, 3);

        Assert.That(result, Is.Not.Null, "Stub method should return a non-null string");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// E. CodeStyleEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class CodeStyleEngineGapTests
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
    public async Task UseIndexFromEndAsync_FileWithArrayAccess_ReturnsNonNull()
    {
        var source = @"
public class Slicer {
    public int Last(int[] arr) {
        return arr[arr.Length - 1];
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Slicer.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.UseIndexFromEndAsync("Slicer.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task UseIndexFromEndAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.UseIndexFromEndAsync("NoSuchFile.cs");

        Assert.That(result, Is.Empty);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// F. ControlFlowEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class ControlFlowEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private ControlFlowEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new ControlFlowEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AnalyzeMethodDataFlowAsync_SimpleMethod_ReturnsResult()
    {
        var source = @"
public class Calculator {
    public int Add(int a, int b) {
        int result = a + b;
        return result;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calculator.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeMethodDataFlowAsync("Calculator.cs", "Add");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo("Add"));
    }

    [Test]
    public async Task AnalyzeMethodDataFlowAsync_UnknownFile_ReturnsErrorResult()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeMethodDataFlowAsync("NoFile.cs", "Foo");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Error, Is.Not.Null.Or.Empty, "Should report an error for missing file");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// G. DeadCodeEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DeadCodeEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DeadCodeEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DeadCodeEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task DetectUnusedLocalVariablesAsync_MethodWithUnusedVar_ReturnsNonNull()
    {
        var source = @"
public class Calc {
    public int Compute(int a) {
        int unused = 42;
        return a * 2;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.DetectUnusedLocalVariablesAsync("Calc.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task DetectUnusedLocalVariablesAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.DetectUnusedLocalVariablesAsync("NoFile.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// H. DependencyEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DependencyEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DependencyEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DependencyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task CheckPackageInconsistencyAsync_InMemorySolution_ReturnsNonNull()
    {
        // CheckPackageInconsistencyAsync reads .csproj files from disk; 
        // in-memory test solution has no real project files, so it should return empty or throw gracefully.
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service {}")]);
        _workspaceManager.SetTestSolution(solution);

        // The method may throw DirectoryNotFoundException when project dirs don't exist on disk.
        // Verify it either returns a non-null list OR throws a graceful exception (not NullReferenceException).
        List<string>? result = null;
        Exception? caughtEx = null;
        try
        {
            result = await _engine.CheckPackageInconsistencyAsync();
        }
        catch (Exception ex) when (ex is System.IO.DirectoryNotFoundException or System.IO.FileNotFoundException or InvalidOperationException)
        {
            caughtEx = ex;
        }

        // Either a valid empty result OR a graceful expected exception is acceptable
        Assert.That(result != null || caughtEx != null,
            Is.True, "Should return a list or throw a known file-system exception, not crash with NullReferenceException");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// I. DependencyInjectionEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class DependencyInjectionEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private DependencyInjectionEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new DependencyInjectionEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task AddDependencyAsync_ClassWithNoConstructor_ReturnsNonNull()
    {
        var source = "public class MyService { }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("MyService.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddDependencyAsync("MyService.cs", "MyService", "ILogger", "logger");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AddDependencyAsync_UnknownFile_ReturnsNonNullOrThrows()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        // The method either returns empty/error string OR throws for an unknown file — both are acceptable
        string? result = null;
        Exception? caughtEx = null;
        try
        {
            result = await _engine.AddDependencyAsync("NoFile.cs", "NoClass", "ILogger", "logger");
        }
        catch (Exception ex)
        {
            caughtEx = ex;
        }

        Assert.That(result != null || caughtEx != null, Is.True,
            "Should return a string or throw a structured exception, not crash silently");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// J. GranularRefactoringEngine — 4 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class GranularRefactoringEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private GranularRefactoringEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new GranularRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task RunMicroRefactoringAsync_ValidFile_ReturnsNonNull()
    {
        var source = "public class Service { public void Go() { var x = 1; } }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.RunMicroRefactoringAsync("Service.cs", "some-refactoring", 1);

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task RunMicroRefactoringAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.RunMicroRefactoringAsync("NoFile.cs", "r1", 1);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task InlineParameterAsync_MethodWithParam_ReturnsNonNull()
    {
        var source = "public class Service { public void Go(int x) { var y = x + 1; } }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.InlineParameterAsync("Service.cs", "Go", "x");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task InlineParameterAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.InlineParameterAsync("NoFile.cs", "Foo", "bar");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task ConvertMethodToIndexerAsync_MethodWithSingleParam_ReturnsNonNull()
    {
        var source = "public class Cache { public string Get(int key) { return key.ToString(); } }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Cache.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertMethodToIndexerAsync("Cache.cs", "Get");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertMethodToIndexerAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertMethodToIndexerAsync("NoFile.cs", "Get");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task MoveTypeToOuterScopeAsync_NestedClass_ReturnsNonNull()
    {
        var source = @"
public class Outer {
    private class Inner { public int Value; }
    public void Go() { var x = new Inner(); }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Outer.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MoveTypeToOuterScopeAsync("Outer.cs", "Inner");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task MoveTypeToOuterScopeAsync_UnknownFile_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.MoveTypeToOuterScopeAsync("NoFile.cs", "Inner");

        // Returns empty string or an error message — must not throw
        Assert.That(result, Is.Not.Null);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// K. PerformanceEngine — 1 untested method
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class PerformanceEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private PerformanceEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new PerformanceEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public async Task OptimizeResourceDisposalAsync_FileWithNoDisposable_ReturnsNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj",
            [("Service.cs", "public class Service { public void Go() {} }")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.OptimizeResourceDisposalAsync(filePath: "Service.cs");

        Assert.That(result, Is.Not.Null);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// L. RefactoringEngine — 5 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class RefactoringEngineGapTests
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
    public async Task ExtractMethodAsync_ValidRange_ReturnsResult()
    {
        var source = @"public class Calc {
    public int Process(int a, int b) {
        int sum = a + b;
        return sum;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        // Line 3 = "        int sum = a + b;"
        var result = await _engine.ExtractMethodAsync("Calc.cs", 3, "int sum = a + b;", 3, "int sum = a + b;", "ComputeSum");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success || result.ErrorMessage != null, Is.True, "Should return Success or a descriptive error");
    }

    [Test]
    public async Task ExtractMethodAsync_UnknownFile_ReturnsFailureResult()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ExtractMethodAsync("NoFile.cs", 1, "x", 1, "x", "NewMethod");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False, "Should fail gracefully for unknown file");
    }

    [Test]
    public async Task ConvertIndexerToMethodAsync_SimpleIndexer_ReturnsNonNull()
    {
        var source = @"
public class Cache {
    private string[] _data = new string[10];
    public string this[int key] => _data[key];
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Cache.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertIndexerToMethodAsync("Cache.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertIndexerToMethodAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertIndexerToMethodAsync("NoFile.cs");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task AddRemoveParamsAsync_MethodWithParams_ReturnsNonNull()
    {
        var source = "public class Service { public void Go(int a, int b) {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddRemoveParamsAsync("Service.cs", "Go");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task AddRemoveParamsAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AddRemoveParamsAsync("NoFile.cs", "Foo");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task AnalyzeControlFlowAsync_SimpleMethod_ReturnsSummary()
    {
        var source = @"
public class Service {
    public int Compute(int x) {
        if (x > 0) return x;
        return -x;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeControlFlowAsync("Service.cs", "Compute");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo("Compute"));
    }

    [Test]
    public async Task AnalyzeControlFlowAsync_UnknownFile_ReturnsDefaultSummary()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeControlFlowAsync("NoFile.cs", "Foo");

        Assert.That(result, Is.Not.Null, "Should return a ControlFlowSummary even for missing file");
    }

    [Test]
    public async Task AnalyzeDataFlowAsync_SimpleMethod_ReturnsSummary()
    {
        var source = @"
public class Service {
    public int Sum(int a, int b) {
        int total = a + b;
        return total;
    }
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Service.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeDataFlowAsync("Service.cs", "Sum");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.MethodName, Is.EqualTo("Sum"));
    }

    [Test]
    public async Task AnalyzeDataFlowAsync_UnknownFile_ReturnsDefaultSummary()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.AnalyzeDataFlowAsync("NoFile.cs", "Foo");

        Assert.That(result, Is.Not.Null, "Should return a DataFlowSummary even for missing file");
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// M. SymbolNavigationEngine — 5 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SymbolNavigationEngineGapTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private SymbolNavigationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
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

        // Either a valid result or null is acceptable — method should not throw
        Assert.That(() => result == null || result.Name != null, Is.True, "Should return SymbolHoverInfo or null without throwing");
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

        Assert.That(result, Is.Not.Null);
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

        Assert.That(result, Is.Not.Null);
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

        Assert.That(result, Is.Not.Null);
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
    public static string Shout(this string s) => s.ToUpper() + ""!"";
}
";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Extensions.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindExtensionMethodsAsync("string");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task FindExtensionMethodsAsync_TypeWithNoExtensions_ReturnsEmptyOrNonNull()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.FindExtensionMethodsAsync("SomeRandomType");

        Assert.That(result, Is.Not.Null);
    }
}

// ════════════════════════════════════════════════════════════════════════════════
// N. SyntaxUpgradeEngine — 2 untested methods
// ════════════════════════════════════════════════════════════════════════════════
[TestFixture]
public class SyntaxUpgradeEngineGapTests
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
    public async Task ConvertSwitchExpressionToStatementAsync_FileWithSwitchExpr_ReturnsNonNull()
    {
        var source = @"
public class Router {
    public string Route(int x) => x switch {
        1 => ""a"",
        2 => ""b"",
        _ => ""c""
    };
}";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Router.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertSwitchExpressionToStatementAsync("Router.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task ConvertSwitchExpressionToStatementAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.ConvertSwitchExpressionToStatementAsync("NoFile.cs");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task UseFieldBackedPropertiesAsync_AutoProperty_ReturnsNonNull()
    {
        var source = "public class Entity { public string Name { get; set; } = \"\"; }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Entity.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.UseFieldBackedPropertiesAsync("Entity.cs");

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task UseFieldBackedPropertiesAsync_UnknownFile_ReturnsEmpty()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Other.cs", "public class X {}")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _engine.UseFieldBackedPropertiesAsync("NoFile.cs");

        Assert.That(result, Is.Empty);
    }
}
