using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Server;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests;

/// <summary>
/// Tests for stack overflow risk detection (Battery 39):
///  1. Clean method with no recursion — zero findings.
///  2. Unconditional self-call — Definite DirectRecursion.
///  3. Conditional self-call — Suspicious ConditionalRecursion.
///  4. Expression-body property reads itself — Definite PropertySelfRead.
///  5. Block getter reads itself — Definite PropertySelfRead.
///  6. Setter assigns to self — Definite PropertySelfWrite.
///  7. Override calls own name (not base) — Definite OverrideCallsSelf.
///  8. Conditional recursion with unchanged argument — Suspicious ArgumentNotDecreasing.
///  9. Mutual recursion A→B→A — Suspicious MutualRecursion.
/// 10. In-file inheritance cycle: override→base→virtual→same override — InheritanceCycle.
/// 11. Valid recursion (decreasing arg, guarded) — no Definite findings.
/// 12. Inheritance property cycle: override accesses base property that calls overridden method.
/// </summary>
[TestFixture]
public class BatteryThirtyNineTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private StackOverflowEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new StackOverflowEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetProject(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("MyApp.Service", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    private async Task<string> GetDocPath()
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        return solution.Projects.First().Documents.First().FilePath ?? "Test.cs";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 1 — Clean method → no findings
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task CleanMethod_NoRecursion_NoFindings()
    {
        SetProject("""
            public class MathHelper
            {
                public int Add(int a, int b) => a + b;
                public int Multiply(int a, int b) => a * b;
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.DefiniteCount, Is.EqualTo(0), "No definite risks in clean code");
        Assert.That(report.SuspiciousCount, Is.EqualTo(0), "No suspicious risks in clean code");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2 — Unconditional self-call → Definite DirectRecursion
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task UnconditionalSelfCall_IsDefiniteDirectRecursion()
    {
        SetProject("""
            public class Looper
            {
                public void RunForever() { RunForever(); }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.DefiniteCount, Is.GreaterThanOrEqualTo(1), "Should detect definite risk");
        Assert.That(report.Findings.Any(f => f.Kind == "DirectRecursion" && f.Risk == StackOverflowRisk.Definite),
            Is.True, "DirectRecursion Definite finding expected");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3 — Conditional self-call → Suspicious ConditionalRecursion
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConditionalSelfCall_IsSuspiciousConditionalRecursion()
    {
        SetProject("""
            public class Recurser
            {
                public void Go(int n)
                {
                    if (n > 0)
                        Go(n - 1);
                }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "ConditionalRecursion" && f.Risk == StackOverflowRisk.Suspicious),
            Is.True, "ConditionalRecursion Suspicious finding expected for guarded self-call");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4 — Expression-body property reads itself → Definite PropertySelfRead
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task PropertyExpressionBodySelfRead_IsDefinitePropertySelfRead()
    {
        SetProject("""
            public class Widget
            {
                public int Count => Count;
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "PropertySelfRead" && f.Risk == StackOverflowRisk.Definite),
            Is.True, "PropertySelfRead Definite expected for expression-body getter reading itself");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5 — Block getter reads itself → Definite PropertySelfRead
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task BlockGetterSelfRead_IsDefinitePropertySelfRead()
    {
        SetProject("""
            public class Box
            {
                public string Label
                {
                    get { return Label; }
                }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "PropertySelfRead" && f.Risk == StackOverflowRisk.Definite
                                          && f.ContainingMember == "Label.get"),
            Is.True, "PropertySelfRead Definite on Label.get expected");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6 — Setter assigns to self → Definite PropertySelfWrite
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task SetterSelfWrite_IsDefinitePropertySelfWrite()
    {
        SetProject("""
            public class Counter
            {
                public int Value
                {
                    set { Value = value; }
                }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "PropertySelfWrite" && f.Risk == StackOverflowRisk.Definite),
            Is.True, "PropertySelfWrite Definite expected for setter assigning to itself");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7 — Override calls own name instead of base → Definite OverrideCallsSelf
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task OverrideCallsOwnName_IsDefiniteOverrideCallsSelf()
    {
        SetProject("""
            public class Animal
            {
                public virtual string Describe() => "Animal";
            }

            public class Dog : Animal
            {
                public override string Describe() => Describe(); // calls itself, not base
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "OverrideCallsSelf" && f.Risk == StackOverflowRisk.Definite),
            Is.True, "OverrideCallsSelf Definite expected");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8 — Guarded recursion but argument unchanged → Suspicious ArgumentNotDecreasing
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ConditionalRecursionUnchangedArg_IsSuspiciousArgumentNotDecreasing()
    {
        SetProject("""
            public class Buggy
            {
                public int Process(int n)
                {
                    if (n > 0)
                        return Process(n); // n not reduced!
                    return 0;
                }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "ArgumentNotDecreasing" && f.Risk == StackOverflowRisk.Suspicious),
            Is.True, "ArgumentNotDecreasing Suspicious expected when parameter passed unchanged");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9 — Mutual recursion A→B→A → Suspicious MutualRecursion
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task MutualRecursion_TwoMethods_IsSuspiciousMutualRecursion()
    {
        SetProject("""
            public class Ping
            {
                public void A() { B(); }
                public void B() { A(); }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "MutualRecursion" && f.Risk == StackOverflowRisk.Suspicious),
            Is.True, "MutualRecursion Suspicious expected for A→B→A cycle");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10 — In-file inheritance dispatch cycle → InheritanceCycle
    //
    // ConcreteProcessor.OnExecute calls Execute() (base method).
    // Processor.Execute calls OnExecute() (abstract/virtual).
    // Virtual dispatch routes back to ConcreteProcessor.OnExecute → infinite loop.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task InheritanceCycle_OverrideCallsBaseWhichDispatchesToSelf_IsDetected()
    {
        SetProject("""
            public abstract class Processor
            {
                public void Execute() { OnExecute(); }
                protected abstract void OnExecute();
            }

            public class ConcreteProcessor : Processor
            {
                protected override void OnExecute() { Execute(); }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "InheritanceCycle"),
            Is.True, "InheritanceCycle expected: ConcreteProcessor.OnExecute → Execute → OnExecute (virtual) → ConcreteProcessor.OnExecute");
        Assert.That(report.Findings.First(f => f.Kind == "InheritanceCycle").CyclePath,
            Does.Contain("ConcreteProcessor"), "CyclePath should name the derived class");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 11 — Valid recursion: guarded + decreasing argument → no Definite findings
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task ValidRecursion_DecreasingArg_NoDefiniteFindings()
    {
        SetProject("""
            public class Math
            {
                public int Factorial(int n)
                {
                    if (n <= 1) return 1;
                    return n * Factorial(n - 1);
                }
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.DefiniteCount, Is.EqualTo(0),
            "No definite stack overflow risks in properly written factorial");
        Assert.That(report.Findings.Any(f => f.Kind == "ArgumentNotDecreasing"), Is.False,
            "ArgumentNotDecreasing should not fire when n-1 is passed");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 12 — Property chain inheritance cycle
    //
    // MyWidget.GetLabel() override returns the 'Label' property (PascalCase access).
    // Widget.Label getter calls GetLabel() (virtual method).
    // Virtual dispatch → MyWidget.GetLabel → reads Label → Widget.Label → GetLabel → cycle.
    // ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task InheritanceCycle_PropertyChainThroughBase_IsDetected()
    {
        SetProject("""
            public abstract class Widget
            {
                public string Label => GetLabel();
                protected abstract string GetLabel();
            }

            public class MyWidget : Widget
            {
                protected override string GetLabel() => Label;
            }
            """);

        var report = await _engine.AnalyzeStackOverflowRisksAsync(await GetDocPath());

        Assert.That(report.Findings.Any(f => f.Kind == "InheritanceCycle"),
            Is.True, "InheritanceCycle expected: MyWidget.GetLabel → Widget.Label → GetLabel() → MyWidget.GetLabel");
    }
}
