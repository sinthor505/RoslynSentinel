using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

// Step 9 coverage: Basic name-keyed refactorings refuse an ambiguous name with ToolAmbiguousMatchException.
[TestFixture]
[Category("AmbiguousNameTarget")]
public class AmbiguousNameTargetStep9Tests
{
    private IWorkspaceManager _workspaceManager;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private MemberRefactoringEngine _memberRefactoringEngine;
    private BasicRefactoringEngine _basicRefactoringEngine;
    private SemanticRefactoringEngine _semanticRefactoringEngine;
    private MsToolAugmentEngine _msToolAugmentEngine;
    private ThreadSafetyEngine _threadSafetyEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(
            _workspaceManager,
            _symbolNavigationEngine,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager));
        _basicRefactoringEngine = Battery.Basic.TestEngines.BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            Battery.Basic.TestEngines.SentinelConfiguration());
        _semanticRefactoringEngine = Battery.Basic.TestEngines.SemanticRefactoringEngine(_workspaceManager);
        _msToolAugmentEngine = Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager);
        _threadSafetyEngine = Battery.Basic.TestEngines.ThreadSafetyEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task MoveMember_TwoClassesNamedTargetInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Source
{
    public int Helper() => 1;
}

namespace A
{
    public class Target
    {
    }
}

namespace B
{
    public class Target
    {
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _memberRefactoringEngine.MoveMemberAsync("Test.cs", "Source", ["Helper"], "Target"));

        Assert.That(ex!.Message, Does.Contain("Target"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task InlineVariable_TwoMethodsDeclareSameLocalName_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Calculator
{
    public int First()
    {
        var tmp = 1;
        return tmp;
    }

    public int Second()
    {
        var tmp = 1;
        return tmp;
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _semanticRefactoringEngine.InlineVariableAsync("Test.cs", "tmp"));

        Assert.That(ex!.Message, Does.Contain("tmp"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task MakeMethodStatic_TwoOverloadsWithSameName_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Calculator
{
    public int Calc()
    {
        return 1;
    }

    public int Calc(int x)
    {
        return x;
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _memberRefactoringEngine.MakeMethodStaticAsync("Test.cs", "Calc"));

        Assert.That(ex!.Message, Does.Contain("Calc"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task EncapsulateFieldSafe_TwoClassesWithSameFieldName_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class First
{
    private int _value;
}

public class Second
{
    private int _value;
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _msToolAugmentEngine.EncapsulateFieldSafeAsync("Test.cs", "_value"));

        Assert.That(ex!.Message, Does.Contain("_value"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task ExtractInterface_TwoClassesNamedServiceInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
namespace A
{
    public class Service
    {
        public void Run() { }
    }
}

namespace B
{
    public class Service
    {
        public void Stop() { }
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _basicRefactoringEngine.ExtractInterfaceAsync("Test.cs", "Service", "IService"));

        Assert.That(ex!.Message, Does.Contain("Service"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_TwoOverloadsBothContainingLock_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Worker
{
    private readonly object _gate = new();
    private int _count;

    public void Run()
    {
        lock (_gate)
        {
            _count++;
        }
    }

    public void Run(int times)
    {
        lock (_gate)
        {
            _count += times;
        }
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _threadSafetyEngine.ConvertLockToSemaphoreSlimAsync("Test.cs", "Run"));

        Assert.That(ex!.Message, Does.Contain("Run"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }
}
