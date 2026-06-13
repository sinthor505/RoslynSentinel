// Battery #35 — New features: extract_class internal caller rewriting, upgrade_to_file_scoped_namespace,
// trace_variable_lifetime, get_type_hierarchy, find_cancellation_token_not_forwarded.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

[TestFixture]
public class BatteryThirtyFiveTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private AdvancedStructuralEngine _advancedStructuralEngine;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private AsyncSafetyEngine _asyncSafetyEngine;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _advancedStructuralEngine = new AdvancedStructuralEngine(_workspaceManager);
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, _config);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _asyncSafetyEngine = new AsyncSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private void SetMultiFile(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    // ======================================================================
    // extract_class — internal caller rewriting
    // ======================================================================

    [Test]
    public async Task ExtractClass_InternalCallers_ThisMethodCallRewritten()
    {
        const string src = @"
public class Order
{
    public decimal Calculate() => 0;
    public string Process()
    {
        var v = this.Calculate();
        return v.ToString();
    }
}";
        SetSource(src, "Order.cs");
        var result = await _advancedStructuralEngine.ExtractClassAsync(
            "Order.cs", "Order", "Calculator", new[] { "Calculate" });

        Assert.That(result, Does.ContainKey("Order.cs"), "Source file must be present in result");
        var source = result["Order.cs"];
        // The internal call this.Calculate() should become Calculator.Calculate()
        Assert.That(source, Does.Contain("Calculator.Calculate"),
            "this.Calculate() should be rewritten to Calculator.Calculate()");
        Assert.That(source, Does.Not.Contain("this.Calculate"),
            "this.Calculate() should have been rewritten");
    }

    [Test]
    public async Task ExtractClass_InternalCallers_BareMethodCallRewritten()
    {
        const string src = @"
public class Processor
{
    public int Compute() => 42;
    public void Run()
    {
        var x = Compute();
        System.Console.WriteLine(x);
    }
}";
        SetSource(src, "Proc.cs");
        var result = await _advancedStructuralEngine.ExtractClassAsync(
            "Proc.cs", "Processor", "Computer", new[] { "Compute" });

        Assert.That(result, Does.ContainKey("Proc.cs"));
        var source = result["Proc.cs"];
        // The bare call Compute() should become Computer.Compute()
        Assert.That(source, Does.Contain("Computer.Compute"),
            "Bare Compute() call should be rewritten to Computer.Compute()");
    }

    [Test]
    public async Task ExtractClass_InternalCallers_PublicPropertyAddedToSource()
    {
        const string src = @"
public class Service
{
    public int DoWork() => 1;
    public void Start() { DoWork(); }
}";
        SetSource(src, "Svc.cs");
        var result = await _advancedStructuralEngine.ExtractClassAsync(
            "Svc.cs", "Service", "Worker", new[] { "DoWork" });

        var source = result["Svc.cs"];
        Assert.That(source, Does.Contain("public Worker Worker"),
            "Source class should expose new class as public auto-property");
    }

    [Test]
    public async Task ExtractClass_InternalCallers_NoExtraMembers_NoCrash()
    {
        // Extracting a method with no internal callers should still work correctly
        const string src = @"
public class Foo
{
    public int Standalone() => 7;
    public int Other() => 99;
}";
        SetSource(src, "Foo.cs");
        var result = await _advancedStructuralEngine.ExtractClassAsync(
            "Foo.cs", "Foo", "StandaloneWorker", new[] { "Standalone" });

        Assert.That(result, Does.ContainKey("Foo.cs"));
        Assert.That(result, Does.ContainKey("StandaloneWorker.cs"));
        Assert.That(result["StandaloneWorker.cs"], Does.Contain("Standalone"));
    }

    // ======================================================================
    // upgrade_to_file_scoped_namespace
    // ======================================================================

    [Test]
    public async Task UpgradeToFileScopedNamespace_BlockForm_ConvertedSuccessfully()
    {
        const string src = @"namespace MyApp
{
    public class Foo
    {
        public int Value { get; set; }
    }
}";
        SetSource(src, "Foo.cs");
        var result = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync("Foo.cs");

        Assert.That(result.UpdatedText!, Does.Not.StartWith("// "),
            "Should return converted code, not an error/message comment");
        Assert.That(result.UpdatedText!, Does.Contain("namespace MyApp;"),
            "Should use file-scoped namespace syntax (semicolon form)");
        Assert.That(result.UpdatedText!, Does.Not.Contain("namespace MyApp\n{").And.Not.Contain("namespace MyApp\r\n{"),
            "Block-form namespace braces should be gone");
        Assert.That(result.UpdatedText!, Does.Contain("class Foo"), "Class body should be preserved");
    }

    [Test]
    public async Task UpgradeToFileScopedNamespace_AlreadyFileScoped_ReturnsMessage()
    {
        const string src = "namespace MyApp;\npublic class Bar {}";
        SetSource(src, "Bar.cs");
        var result = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync("Bar.cs");

        Assert.That(result.UpdatedText!, Does.StartWith("// Already"),
            "Should return message when namespace is already file-scoped");
    }

    [Test]
    public async Task UpgradeToFileScopedNamespace_NoNamespace_ReturnsMessage()
    {
        const string src = "public class Global {}";
        SetSource(src, "Global.cs");
        var result = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync("Global.cs");

        Assert.That(result.UpdatedText!, Does.StartWith("// No block"),
            "Should return a message when no namespace is found");
    }

    [Test]
    public async Task UpgradeToFileScopedNamespace_PreservesUsingsAndMembers()
    {
        const string src = @"using System;
using System.Collections.Generic;
namespace MyApp
{
    public interface IFoo { void Do(); }
    public class FooImpl : IFoo { public void Do() {} }
}";
        SetSource(src, "Multi.cs");
        var result = await _syntaxUpgradeEngine.UpgradeToFileScopedNamespaceAsync("Multi.cs");

        Assert.That(result, Does.Contain("namespace MyApp;"), "File-scoped namespace should be present");
        Assert.That(result, Does.Contain("IFoo"), "Interface should be preserved");
        Assert.That(result, Does.Contain("FooImpl"), "Class should be preserved");
    }

    // ======================================================================
    // trace_variable_lifetime
    // ======================================================================

    [Test]
    public async Task TraceVariableLifetime_FindsDeclarationAndRead()
    {
        const string src = @"
public class Counter
{
    public int Run()
    {
        int count = 0;
        count = count + 1;
        return count;
    }
}";
        SetSource(src, "Counter.cs");
        var report = await _symbolNavigationEngine.TraceVariableLifetimeAsync("Counter.cs", "count", 6);

        Assert.That(report.Error, Is.Null, $"Should not error: {report.Error}");
        Assert.That(report.VariableName, Is.EqualTo("count"));
        Assert.That(report.TypeName, Does.Contain("int"));
        Assert.That(report.Accesses, Is.Not.Empty, "Should find at least the declaration access");
        Assert.That(report.Accesses.Any(a => a.AccessKind == "Declaration"), Is.True,
            "Should include a Declaration access");
    }

    [Test]
    public async Task TraceVariableLifetime_IsInLoop_TrueInsideForEach()
    {
        const string src = @"
using System.Collections.Generic;
public class Looper
{
    public void Process(List<int> items)
    {
        foreach (var item in items)
        {
            var x = item * 2;
        }
    }
}";
        SetSource(src, "Looper.cs");
        var report = await _symbolNavigationEngine.TraceVariableLifetimeAsync("Looper.cs", "x", 9);

        Assert.That(report.Error, Is.Null, $"Should not error: {report.Error}");
        // Declaration of x is inside foreach — IsInLoop should be true
        var decl = report.Accesses.FirstOrDefault(a => a.AccessKind == "Declaration");
        if (decl != null)
        {
            Assert.That(decl.IsInLoop, Is.True, "Variable declared inside foreach should have IsInLoop=true");
        }
    }

    [Test]
    public async Task TraceVariableLifetime_NotFound_ReturnsError()
    {
        const string src = "public class X { public void M() { int y = 1; } }";
        SetSource(src, "X.cs");
        var report = await _symbolNavigationEngine.TraceVariableLifetimeAsync("X.cs", "noSuchVar", 1);

        Assert.That(report.Error, Is.Not.Null.And.Not.Empty,
            "Should return an error when variable not found");
    }

    // ======================================================================
    // get_type_hierarchy
    // ======================================================================

    [Test]
    public async Task GetTypeHierarchy_Class_ReturnsBaseChainAndInterfaces()
    {
        const string src = @"
public interface IShape { double Area(); }
public abstract class BaseShape : IShape { public abstract double Area(); }
public class Circle : BaseShape { public override double Area() => 3.14; }";
        SetSource(src, "Shapes.cs");

        var report = await _symbolNavigationEngine.GetTypeHierarchyAsync("Circle");

        Assert.That(report.Error, Is.Null, $"Should not error: {report.Error}");
        Assert.That(report.TypeName, Does.Contain("Circle"));
        Assert.That(report.BaseClass, Does.Contain("BaseShape"),
            "Direct base class should be BaseShape");
        Assert.That(report.BaseClassChain, Does.Contain("BaseShape"),
            "Base class chain should include BaseShape");
        Assert.That(report.ImplementedInterfaces, Does.Contain("IShape"),
            "Implemented interfaces should include IShape");
        Assert.That(report.IsAbstract, Is.False, "Circle is not abstract");
    }

    [Test]
    public async Task GetTypeHierarchy_Interface_ReturnsImplementingTypes()
    {
        const string src = @"
public interface IWorker { void Work(); }
public class HardWorker : IWorker { public void Work() {} }
public class LazyWorker : IWorker { public void Work() {} }";
        SetSource(src, "Workers.cs");

        var report = await _symbolNavigationEngine.GetTypeHierarchyAsync("IWorker");

        Assert.That(report.Error, Is.Null, $"Should not error: {report.Error}");
        Assert.That(report.IsInterface, Is.True);
        Assert.That(report.ImplementingTypes.Any(e => e.TypeName.Contains("HardWorker")), Is.True,
            "HardWorker should be listed as implementing IWorker");
        Assert.That(report.ImplementingTypes.Any(e => e.TypeName.Contains("LazyWorker")), Is.True,
            "LazyWorker should be listed as implementing IWorker");
    }

    [Test]
    public async Task GetTypeHierarchy_NotFound_ReturnsError()
    {
        SetSource("public class A {}", "A.cs");
        var report = await _symbolNavigationEngine.GetTypeHierarchyAsync("NoSuchType");

        Assert.That(report.Error, Is.Not.Null.And.Not.Empty, "Should error for unknown type");
    }

    [Test]
    public async Task GetTypeHierarchy_AbstractClass_IsAbstractTrue()
    {
        const string src = "public abstract class AbstractBase { public abstract void Do(); }";
        SetSource(src, "Abs.cs");
        var report = await _symbolNavigationEngine.GetTypeHierarchyAsync("AbstractBase");

        Assert.That(report.Error, Is.Null);
        Assert.That(report.IsAbstract, Is.True);
    }

    // ======================================================================
    // find_cancellation_token_not_forwarded (EPC31)
    // ======================================================================

    [Test]
    public async Task FindCancellationTokenNotForwarded_MissingForwarding_Reported()
    {
        const string src = @"
using System.Threading;
using System.Threading.Tasks;
public class DataService
{
    public async Task<int> GetDataAsync(int id, CancellationToken cancellationToken = default)
    {
        await Task.Delay(100);
        return id;
    }

    public async Task<string> ProcessAsync(string input, CancellationToken ct)
    {
        var data = await GetDataAsync(1); // ct not forwarded
        return data.ToString();
    }
}";
        SetSource(src, "Svc.cs");
        var reports = await _asyncSafetyEngine.FindCancellationTokenNotForwardedAsync("Svc.cs");

        Assert.That(reports, Is.Not.Empty, "Should detect unforwarded CT");
        Assert.That(reports.Any(r => r.MethodName == "ProcessAsync"), Is.True,
            "ProcessAsync should be flagged");
        Assert.That(reports.Any(r => r.Reason.Contains("GetDataAsync")), Is.True,
            "Report should mention GetDataAsync as the callee");
    }

    [Test]
    public async Task FindCancellationTokenNotForwarded_ForwardedCt_NoReport()
    {
        const string src = @"
using System.Threading;
using System.Threading.Tasks;
public class GoodService
{
    public async Task<int> FetchAsync(int id, CancellationToken ct = default)
    {
        await Task.Delay(100, ct);
        return id;
    }

    public async Task<string> HandleAsync(string input, CancellationToken ct)
    {
        var data = await FetchAsync(1, ct); // ct properly forwarded
        return data.ToString();
    }
}";
        SetSource(src, "Good.cs");
        var reports = await _asyncSafetyEngine.FindCancellationTokenNotForwardedAsync("Good.cs");

        Assert.That(reports.Where(r => r.MethodName == "HandleAsync"), Is.Empty,
            "HandleAsync properly forwards ct and should not be flagged");
    }

    [Test]
    public async Task FindCancellationTokenNotForwarded_NonAsyncMethod_NotFlagged()
    {
        const string src = @"
using System.Threading;
public class SyncService
{
    public string GetSync(string s, CancellationToken ct)
    {
        return s.ToUpperInvariant(); // sync, no async callees
    }
}";
        SetSource(src, "Sync.cs");
        var reports = await _asyncSafetyEngine.FindCancellationTokenNotForwardedAsync("Sync.cs");

        // Sync method should not be flagged (no awaited calls)
        Assert.That(reports.Where(r => r.MethodName == "GetSync"), Is.Empty,
            "Sync method with CT but no awaited calls should not be flagged");
    }
}
