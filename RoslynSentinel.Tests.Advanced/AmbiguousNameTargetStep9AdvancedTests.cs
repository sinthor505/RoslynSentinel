using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

// Step 9 ambiguity coverage: the Advanced engines below refuse an ambiguous name with ToolAmbiguousMatchException.
[TestFixture]
[Category("AmbiguousNameTarget")]
public class AmbiguousNameTargetStep9AdvancedTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private AdvancedRefactoringEngine _advancedRefactoring;
    private StructuralRefactoringEngine _structural;
    private LogicSimplificationEngine _logicSimplification;
    private SyntaxModernizationEngine _modernization;
    private CodeGenerationEngine _codeGeneration;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = Battery.Basic.TestEngines.SentinelConfiguration();
        _advancedRefactoring = Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager);
        _structural = Battery.Advanced.TestEnginesAdvanced.StructuralRefactoringEngine(_workspaceManager);
        _logicSimplification = Battery.Advanced.TestEnginesAdvanced.LogicSimplificationEngine(_workspaceManager);
        _modernization = Battery.Advanced.TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, _config);
        _codeGeneration = Battery.Advanced.TestEnginesAdvanced.CodeGenerationEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task SyncInterfaceToImplementation_TwoClassesNamedServiceInDifferentNamespaces_ThrowsAmbiguousMatch()
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
            async () => await _advancedRefactoring.SyncInterfaceToImplementationAsync("Test.cs", "Service", "IFoo"));

        Assert.That(ex!.Message, Does.Contain("Service"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task InlineField_SameNamedFieldInTwoClasses_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class First
{
    private static readonly int _x = 1;
    public int Get() => _x;
}

public class Second
{
    private static readonly int _x = 2;
    public int Get() => _x;
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _structural.InlineFieldAsync("Test.cs", "_x"));

        Assert.That(ex!.Message, Does.Contain("_x"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task ImplementInterface_BareNameMatchesTwoArities_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public interface IHandler<T> { void Handle(T item); }
public interface IHandler<T1, T2> { void Handle(T1 a, T2 b); }

public class Handler : IHandler<string>, IHandler<string, int>
{
    public void Handle(string item) { }
    public void Handle(string a, int b) { }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _codeGeneration.ImplementInterfaceAsync("Test.cs", "Handler", "IHandler"));

        Assert.That(ex!.Message, Does.Contain("IHandler"));
        // The interface path reports "matches 2 interfaces" (not "declarations"); asserting the emitted wording.
        Assert.That(ex.Message, Does.Contain("matches 2"));
    }

    [Test]
    public async Task ClassToRecord_TwoClassesNamedDtoInDifferentNamespaces_ThrowsAmbiguousMatch()
    {
        SetSource(@"
namespace A
{
    public class Dto
    {
        public int Id { get; set; }
    }
}

namespace B
{
    public class Dto
    {
        public string Name { get; set; }
    }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _modernization.ClassToRecordAsync("Test.cs", "Dto"));

        Assert.That(ex!.Message, Does.Contain("Dto"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task AddGuardClauses_TwoOverloadsNamedRun_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Runner
{
    public void Run(string a) { }
    public void Run(string a, string b) { }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _logicSimplification.AddGuardClausesAsync("Test.cs", "Run"));

        Assert.That(ex!.Message, Does.Contain("Run"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }

    [Test]
    public async Task ConvertMethodToExpressionBody_OneSingleStatementOverloadAndOneMultiStatement_NarrowsToEligibleOverload()
    {
        SetSource(@"
public class Calculator
{
    public int Calc(int a) { return a + 1; }
    public int Calc(int a, int b) { var s = a + b; return s; }
}", "Test.cs");

        var result = await _modernization.ConvertMethodToExpressionBodyAsync("Test.cs", "Calc");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("=>"));
    }

    [Test]
    public async Task ConvertMethodToExpressionBody_TwoSingleStatementOverloads_ThrowsAmbiguousMatch()
    {
        SetSource(@"
public class Calculator
{
    public int Calc(int a) { return a + 1; }
    public int Calc(int a, int b) { return a + b; }
}", "Test.cs");

        var ex = await Assert.ThrowsAsync<ToolAmbiguousMatchException>(
            async () => await _modernization.ConvertMethodToExpressionBodyAsync("Test.cs", "Calc"));

        Assert.That(ex!.Message, Does.Contain("Calc"));
        Assert.That(ex.Message, Does.Contain("matches 2 declarations"));
    }
}
