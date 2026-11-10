#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests;

/// <summary>
/// Regression coverage for <see cref="CompilerErrorLookupHelper"/>'s CS0122 branch.
/// See docs/current/project_cs0122_lookup_helper_proposal.md -> this guards against the
/// member-vs-container accessibility confusion traced in PlanImplementVerify run 1, where a
/// model raised a class from internal to public while leaving the actually-inaccessible method
/// private, then reverted the class without ever touching the method.
/// </summary>
[TestFixture]
[Category("CompilerErrorLookupHelper")] // sentinel:auto-category
public class CompilerErrorLookupHelperTests
{
    private IWorkspaceManager _workspaceManager;
    private SymbolNavigationEngine _symbolNavigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private async Task<DiagnosticReport> CompileAndGetErrorsAsync(params (string name, string content)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);

        var project = solution.Projects.Single();
        var compilation = await project.GetCompilationAsync();
        var diagnostics = compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToInfo())
            .ToList();

        return new DiagnosticReport(!(diagnostics.Count == 0), diagnostics);
    }

    [Test]
    public async Task DescribeAsync_Cs0122PrivateMethod_NamesCurrentAccessibilityAndMemberVsContainerNote()
    {
        var report = await CompileAndGetErrorsAsync(
            ("BlockEditHelpers.cs", """
            namespace TestProj;

            public static class BlockEditHelpers
            {
                private static string ReplaceBlockFormatted(string a, string b, string c) => a + b + c;
            }
            """),
            ("BlockConverter.cs", """
            namespace TestProj;

            public class BlockConverter
            {
                public string Convert() => BlockEditHelpers.ReplaceBlockFormatted("a", "b", "c");
            }
            """));

        Assert.That(!report.IsError, Is.False, "the private call should produce a CS0122");
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0122"));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("ReplaceBlockFormatted"));
        Assert.That(description, Does.Contain("currently private"),
            "the current accessibility must be stated affirmatively, not left for the model to infer from silence");
        Assert.That(description, Does.Contain("BlockConverter"),
            "the caller's enclosing type should be named so the sentence reads as a concrete instruction");
        Assert.That(description.ToLowerInvariant(), Does.Contain("containing type"),
            "must warn that raising the class's accessibility does not change the member's own accessibility");
        Assert.That(description, Does.Contain("ChangeAccessibility"), "the hint must name the accessibility tool");
    }

    /// <summary>
    /// Regression coverage for the CS0101/CS0111 branch. See
    /// docs/current/project_readfile_createfile_path_inconsistency_bug.md -> a model that guesses a
    /// wrong-but-plausible path for an existing file gets a CS0101/CS0111 "already contains a
    /// definition" collision with no pointer to the real file, and (per that doc's transcript) can
    /// burn its whole turn budget unable to tell a genuine duplicate apart from a wrong path.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0111DuplicateMember_NamesTheRealCollidingFilePath()
    {
        var report = await CompileAndGetErrorsAsync(
            ("FixtureHelpers/BlockEditHelpers.cs", """
            namespace TestProj.FixtureHelpers;

            public static class BlockEditHelpers
            {
                public static string ReplaceBlockFormatted(string a, string b, string c) => a + b + c;
            }
            """),
            ("BlockEditHelpers.cs", """
            namespace TestProj.FixtureHelpers;

            public static class BlockEditHelpers
            {
                public static string ReplaceBlockFormatted(string a, string b, string c) => a + b + c;
            }
            """));

        Assert.That(!report.IsError, Is.False, "two files declaring the same type/member should collide");
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0111").Or.Contain("CS0101"));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("FixtureHelpers/BlockEditHelpers.cs"),
            "the real (other) file's path must be named so a model with a wrong path can redirect to it");
        Assert.That(description, Does.Contain("ReplaceBlockFormatted"));
    }

    private const string PricingInterface = """
        namespace TestProj;

        public interface IOrderPricingCalculator
        {
            decimal CalculateDiscountedTotal(decimal amount);
        }
        """;

    /// <summary>
    /// CS0266 from retyping a field to an interface the class does not implement yet (model-eval 2026-11-09 T7/T8): the
    /// hint says to add the interface to the class declaration in the same atomic edit.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0266ClassDoesNotImplementTargetInterface_SaysToAddInterfaceInSameEdit()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", PricingInterface),
            ("StandardOrderPricingCalculator.cs", """
            namespace TestProj;

            public class StandardOrderPricingCalculator
            {
                public decimal CalculateDiscountedTotal(decimal amount) => amount;
            }
            """),
            ("OrderCheckout.cs", """
            namespace TestProj;

            public class OrderCheckout
            {
                private readonly IOrderPricingCalculator _calculator = new StandardOrderPricingCalculator();
            }
            """));

        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0266"), "fixture must produce the CS0266");

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("'StandardOrderPricingCalculator' does not implement 'IOrderPricingCalculator'. Add ': IOrderPricingCalculator' to StandardOrderPricingCalculator's declaration in the same atomic edit as this change."));
    }

    [Test]
    public async Task DescribeAsync_Cs0266NumericNarrowing_HasNoHint()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Narrow.cs", """
            namespace TestProj;

            public class Narrow
            {
                public int F(double d)
                {
                    int i = d;
                    return i;
                }
            }
            """));

        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0266"));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Not.Contain("does not implement"));
        Assert.That(description, Does.Not.Contain("  Add "), "no hint line for a numeric CS0266");
    }

    [Test]
    public async Task DescribeAsync_Cs0266InterfaceDowncastToClass_HasNoHint()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", PricingInterface),
            ("StandardOrderPricingCalculator.cs", """
            namespace TestProj;

            public class StandardOrderPricingCalculator : IOrderPricingCalculator
            {
                public decimal CalculateDiscountedTotal(decimal amount) => amount;
            }
            """),
            ("OrderCheckout.cs", """
            namespace TestProj;

            public class OrderCheckout
            {
                public StandardOrderPricingCalculator Get(IOrderPricingCalculator c)
                {
                    StandardOrderPricingCalculator s = c;
                    return s;
                }
            }
            """));

        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0266"));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Not.Contain("does not implement"));
    }

    /// <summary>CS0737 (model-eval 2026-11-09 T11): an internal member cannot implement an interface member.</summary>
    [Test]
    public async Task DescribeAsync_Cs0737NonPublicImplementingMember_SaysToMakeItPublic()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", PricingInterface),
            ("StandardOrderPricingCalculator.cs", """
            namespace TestProj;

            public class StandardOrderPricingCalculator : IOrderPricingCalculator
            {
                internal decimal CalculateDiscountedTotal(decimal amount) => amount;
            }
            """));

        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0737"), "fixture must produce the CS0737");

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("The implementing member must be public; make 'StandardOrderPricingCalculator.CalculateDiscountedTotal(decimal)' public (interface members are implemented by public members)."));
    }

    /// <summary>
    /// CS0107 (eval run 20261109-233953-684: a whole-file write produced "private static internal", and the model removed the wrong
    /// modifier). The gate's diagnostics carry the source line, which names the modifiers found.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0107WithSourceLine_NamesTheModifiersFoundAndSaysToReplaceNotAdd()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Calc.cs", """
            namespace TestProj;

            public class Calc
            {
                private static internal decimal CalculateDiscountedTotal(decimal a) => a;
            }
            """));
        var cs0107 = report.Diagnostics.Single(d => d.Id == "CS0107");
        var withLine = new DiagnosticReport(true, [cs0107 with { SourceLine = "    private static internal decimal CalculateDiscountedTotal(decimal a) => a;" }]);

        var description = await CompilerErrorLookupHelper.DescribeAsync(withLine, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("A declaration may have only one access modifier (found: private internal). To change accessibility, replace the old modifier with the new one rather than adding a second."));
    }

    [Test]
    public async Task DescribeAsync_Cs0107WithoutSourceLine_KeepsTheAdviceAndOmitsTheFoundList()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Calc.cs", """
            namespace TestProj;

            public class Calc
            {
                private static internal decimal CalculateDiscountedTotal(decimal a) => a;
            }
            """));
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0107"), "fixture must produce the CS0107");

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("A declaration may have only one access modifier. To change accessibility, replace the old modifier with the new one rather than adding a second."));
        Assert.That(description, Does.Not.Contain("found:"));
    }

    /// <summary>
    /// Regression coverage for identical-diagnostic grouping. See
    /// docs/current/blockers/closed/blocking_error_movemember_analysisengine_antipatternengine_friction.md
    /// -> one bad MoveMember callSiteFixups value produced 86 identical CS7036 lines, which read as 86
    /// separate problems. Same Id + same Message must collapse to one line with a count and a capped
    /// location list, in first-occurrence order; a distinct diagnostic keeps its own single-line form.
    /// </summary>
    [Test]
    public async Task DescribeAsync_IdenticalDiagnostics_CollapseIntoOneGroupedLine()
    {
        const string ctorMessage = "There is no argument given that corresponds to the required parameter 'workspaceManager' of 'Target.Target(IWorkspaceManager)'";
        var diagnostics = new List<DiagnosticInfo>();
        for (var i = 1; i <= 13; i++)
        {
            diagnostics.Add(new DiagnosticInfo("CS7036", "Error", ctorMessage, $"C:\\repo\\File{i}.cs", i * 10, 5, i * 10, 20));
        }

        diagnostics.Insert(3, new DiagnosticInfo("CS1002", "Error", "; expected", "C:\\repo\\Other.cs", 7, 1, 7, 2));
        var report = new DiagnosticReport(true, diagnostics);

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);
        var lines = description.Split('\n');

        Assert.That(lines, Has.Length.EqualTo(2), "13 identical CS7036s + 1 CS1002 should render as exactly 2 lines");
        Assert.That(lines[0], Does.StartWith("CS7036 (x13): " + ctorMessage + " at: C:\\repo\\File1.cs:10, C:\\repo\\File2.cs:20"),
            "the grouped line comes first (first-occurrence order) and lists locations in original order");
        Assert.That(lines[0], Does.EndWith("(+3 more)"),
            $"locations beyond the cap of {CompilerErrorLookupHelper.MaxGroupedLocations} must be summarized, not listed");
        Assert.That(lines[0], Does.Not.Contain("File11.cs"), "the 11th+ location is past the cap");
        Assert.That(lines[1], Is.EqualTo("CS1002 at C:\\repo\\Other.cs:7: ; expected"),
            "a non-repeated diagnostic keeps the original single-diagnostic format");
    }

    /// <summary>
    /// SourceLine coverage for the single-item branch: when a diagnostic carries its source line, the
    /// rendered line gets a "\n    > " quote appended so the model sees the offending code inline.
    /// </summary>
    [Test]
    public async Task DescribeAsync_SourceLinePresent_SingleItemAppendsQuotedSourceLine()
    {
        var report = new DiagnosticReport(true, new List<DiagnosticInfo>
        {
            new DiagnosticInfo("CS1002", "Error", "; expected", "C:\\repo\\Other.cs", 7, 1, 7, 2, null, "var x = 1"),
        });

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Is.EqualTo("CS1002 at C:\\repo\\Other.cs:7: ; expected\n    > var x = 1"));
    }

    /// <summary>
    /// SourceLine coverage for the grouped branch: the grouped header (one line for N identical
    /// diagnostics) gets the quoted source line of its first occurrence appended.
    /// </summary>
    [Test]
    public async Task DescribeAsync_SourceLinePresent_GroupedHeaderAppendsQuotedSourceLine()
    {
        const string ctorMessage = "There is no argument given that corresponds to the required parameter 'workspaceManager'";
        var report = new DiagnosticReport(true, new List<DiagnosticInfo>
        {
            new DiagnosticInfo("CS7036", "Error", ctorMessage, "C:\\repo\\A.cs", 10, 5, 10, 20, null, "return Foo(a);"),
            new DiagnosticInfo("CS7036", "Error", ctorMessage, "C:\\repo\\B.cs", 20, 5, 20, 20, null, "return Foo(a);"),
        });

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Is.EqualTo(
            "CS7036 (x2): " + ctorMessage + " at: C:\\repo\\A.cs:10, C:\\repo\\B.cs:20\n    > return Foo(a);"));
    }

    /// <summary>
    /// Regression guard: with SourceLine null (the default ToInfo() path), neither the single-item nor the
    /// grouped rendering may emit a quote line, so output is unchanged from before SourceLine existed.
    /// </summary>
    [Test]
    public async Task DescribeAsync_NullSourceLine_OutputHasNoQuoteLine()
    {
        const string ctorMessage = "There is no argument given that corresponds to the required parameter 'workspaceManager'";
        var report = new DiagnosticReport(true, new List<DiagnosticInfo>
        {
            new DiagnosticInfo("CS1002", "Error", "; expected", "C:\\repo\\Other.cs", 7, 1, 7, 2),
            new DiagnosticInfo("CS7036", "Error", ctorMessage, "C:\\repo\\A.cs", 10, 5, 10, 20),
            new DiagnosticInfo("CS7036", "Error", ctorMessage, "C:\\repo\\B.cs", 20, 5, 20, 20),
        });

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Not.Contain("    > "),
            "a null SourceLine must not add a quote line to either the single or the grouped rendering");
        Assert.That(description, Does.Contain("CS1002 at C:\\repo\\Other.cs:7: ; expected"));
        Assert.That(description, Does.StartWith("CS1002 at C:\\repo\\Other.cs:7: ; expected\n"));
    }

    /// <summary>
    /// Regression guard (throw-typed-exceptions S1a): the best-effort symbol lookups behind a hint must
    /// not swallow cancellation into a "not a known symbol" message; a cancelled token has to surface.
    /// </summary>
    [Test]
    public async Task DescribeAsync_CancelledToken_PropagatesOperationCanceledException()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Broken.cs", """
            namespace TestProj;

            public class Broken
            {
                public int Run() => MissingName;
            }
            """));
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0103"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.CatchAsync<OperationCanceledException>(
            async () => await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, cts.Token));
    }
}
