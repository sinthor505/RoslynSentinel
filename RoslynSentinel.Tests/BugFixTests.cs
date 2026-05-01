using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for four bug fixes:
/// 1. Diagnose false negative (MSBuild not found on SDK-only systems)
/// 2. ExtractInterface missing namespace/usings in generated file
/// 3. ChangeSignatureAsync was a stub (now reorders params + call sites)
/// 4. ImplementInterfaceAsync generates stubs without 'override'
/// </summary>
[TestFixture]
public class BugFixTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private RefactoringEngine _refactoringEngine;
    private CodeGenerationEngine _codeGenerationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _refactoringEngine = new RefactoringEngine(NullLogger<RefactoringEngine>.Instance, _workspaceManager, _config);
        _codeGenerationEngine = new CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultipleFiles(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 1: Diagnose — MSBuildFound should respect MSBuildLocator.IsRegistered
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetHealthComponents_WhenMsBuildLocatorIsRegistered_ReturnsMsBuildFoundTrue()
    {
        // The test process itself has MSBuildLocator registered (tests use Roslyn workspace)
        var components = _workspaceManager.GetHealthComponents();

        // On any system running .NET SDK (as is the case in CI and developer machines),
        // IsRegistered will be true so MsBuildFound must be true.
        Assert.That(components.MsBuildFound, Is.True,
            "MsBuildFound should be true when MSBuildLocator.IsRegistered is true");
    }

    [Test]
    public async Task Diagnose_WhenWorkspaceLoaded_ReturnsHealthyTrue()
    {
        SetSource("public class Foo { public void Bar() {} }");

        // No solutionPath passed — just uses in-memory test solution
        var diffEngine = new DiffEngine(_workspaceManager);
        var report = await new SentinelWorkspaceTools(
            _workspaceManager,
            new ValidationEngine(NullLogger<ValidationEngine>.Instance, _workspaceManager, diffEngine),
            diffEngine,
            new DiagnosticEngine(_workspaceManager),
            new SolutionManagementEngine(_workspaceManager),
            new StructuralRefinementEngine(_workspaceManager),
            new DependencyEngine(_workspaceManager),
            _config,
            NullLogger<SentinelWorkspaceTools>.Instance
        ).Diagnose();

        Assert.That(report.Healthy, Is.True,
            $"Healthy should be true on an SDK-only system. Errors: {string.Join(", ", report.Errors.Select(e => e.Message))}");
        Assert.That(report.Errors.Any(e => e.Code.Contains("5001")), Is.False,
            "MSBuild-not-found should not be an error (moved to warning)");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 2: ExtractInterface — generated file must have namespace + usings
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ExtractInterface_GeneratedFile_ContainsNamespaceAndUsings()
    {
        const string source = @"using System;
using System.Collections.Generic;

namespace MyApp.Services;

public class OrderService
{
    public List<string> GetOrders() => new();
    public void ProcessOrder(string id) { }
}";

        SetSource(source, "OrderService.cs");

        var result = await _refactoringEngine.ExtractInterfaceAsync("OrderService.cs", "OrderService", "IOrderService");

        Assert.That(result, Has.Count.EqualTo(2), "Should produce two files");

        var ifacePath = result.Keys.First(k => k != "OrderService.cs");
        var ifaceContent = result[ifacePath];

        Assert.That(ifaceContent, Does.Contain("namespace MyApp.Services"),
            "Interface file must include the source namespace");
        Assert.That(ifaceContent, Does.Contain("using System;"),
            "Interface file must include using directives from source");
        Assert.That(ifaceContent, Does.Contain("public interface IOrderService"),
            "Interface declaration must be present");
        Assert.That(ifaceContent, Does.Contain("List<string> GetOrders()"),
            "Method signature must appear in interface");
    }

    [Test]
    public async Task ExtractInterface_OriginalClass_GetsInterfaceInBaseList()
    {
        const string source = @"namespace App;
public class Svc { public void Foo() {} }";

        SetSource(source, "Svc.cs");

        var result = await _refactoringEngine.ExtractInterfaceAsync("Svc.cs", "Svc", "ISvc");

        Assert.That(result["Svc.cs"], Does.Contain(": ISvc"),
            "Original class should declare ISvc in its base list");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 3: ChangeSignatureAsync — was a stub; now reorders params & call sites
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ChangeSignature_ReordersParameters_InDeclaration()
    {
        const string source = @"public class Calculator
{
    public int Add(int a, int b, int c) => a + b + c;
}";

        SetSource(source, "Calculator.cs");

        // Reorder [a, b, c] → [c, a, b] using index permutation [2, 0, 1]
        var result = await _refactoringEngine.ChangeSignatureAsync("Calculator.cs", "Add", new[] { 2, 0, 1 });

        Assert.That(result, Is.Not.Empty, "Should return changed files");
        var content = result["Calculator.cs"];
        // Verify new parameter order: c first, then a, then b
        var cPos = content.IndexOf("int c", StringComparison.Ordinal);
        var aPos = content.IndexOf("int a", StringComparison.Ordinal);
        var bPos = content.IndexOf("int b", StringComparison.Ordinal);
        Assert.That(cPos, Is.LessThan(aPos), "c should come before a after reorder");
        Assert.That(aPos, Is.LessThan(bPos), "a should come before b after reorder");
    }

    [Test]
    public async Task ChangeSignature_WithInvalidOrder_ReturnsEmpty()
    {
        const string source = "public class C { public void M(int a, int b) {} }";
        SetSource(source, "C.cs");

        // Wrong length
        var result = await _refactoringEngine.ChangeSignatureAsync("C.cs", "M", new[] { 0 });
        Assert.That(result, Is.Empty, "Invalid order length should return empty dict");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 4: ImplementInterfaceAsync — stubs must NOT have 'override' keyword
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ImplementInterface_GeneratedStubs_DoNotHaveOverrideKeyword()
    {
        const string ifaceSource = @"namespace App;
public interface IGreeter
{
    string Greet(string name);
    int Count { get; }
}";

        const string classSource = @"namespace App;
public class Greeter : IGreeter
{
}";

        SetMultipleFiles(("IGreeter.cs", ifaceSource), ("Greeter.cs", classSource));

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Greeter.cs", "Greeter", "IGreeter");

        Assert.That(result, Does.Not.Contain("override"),
            "Interface implementations must NOT use 'override' keyword");
        Assert.That(result, Does.Contain("public string Greet"),
            "Should generate Greet method stub");
        Assert.That(result, Does.Contain("NotImplementedException"),
            "Stub body should throw NotImplementedException");
    }

    [Test]
    public async Task ImplementInterface_WhenAllMembersImplemented_ReturnsAlreadyImplementedMessage()
    {
        const string source = @"namespace App;
public interface IFoo { void Bar(); }
public class Foo : IFoo
{
    public void Bar() { }
}";
        SetSource(source, "Foo.cs");

        var result = await _codeGenerationEngine.ImplementInterfaceAsync("Foo.cs", "Foo", "IFoo");

        Assert.That(result, Does.Contain("already implemented"),
            "Should report that all members are already implemented");
    }
}
