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
    /// CS0144 (10-chain runs 214 and 216, 2026-11-10): a field retyped to an interface kept its target-typed new();
    /// the hint names the interface and says to construct a concrete class.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0144_NamesTheInterfaceAndSaysToNameAConcreteClass()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Checkout.cs", """
            namespace TestProj;

            public interface IPricing { decimal Total(decimal a); }

            public class Checkout
            {
                private readonly IPricing _calculator = new();
            }
            """));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("IPricing cannot be created with new() or new IPricing(). Keep IPricing as the declared type"));
    }

    /// <summary>
    /// CS0144 (eval run 20261112-151531-550 T27): the hint named only a placeholder class. It now names concrete implementers
    /// from the solution - sorted by name, abstract classes left out - and uses the first in the example.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0144InterfaceWithImplementers_NamesConcreteClassesByNameAndSkipsAbstract()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Checkout.cs", """
            namespace TestProj;

            public interface IPricing { decimal Total(decimal a); }

            public abstract class BasePricing : IPricing { public abstract decimal Total(decimal a); }

            public class ZetaPricing : IPricing { public decimal Total(decimal a) => a; }

            public class AlphaPricing : IPricing { public decimal Total(decimal a) => a; }

            public class Checkout
            {
                private readonly IPricing _calculator = new();
            }
            """));

        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0144"), "fixture must produce the CS0144");

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("Concrete classes in the solution that implement IPricing: AlphaPricing, ZetaPricing."));
        Assert.That(description, Does.Contain("_field = new AlphaPricing();"));
        Assert.That(description, Does.Not.Contain("BasePricing"), "an abstract class cannot be created either");
        Assert.That(description, Does.Not.Contain("SomeImplementingClass"));
    }

    [Test]
    public async Task DescribeAsync_Cs0144InterfaceWithoutImplementers_StatesNoImplementerFact()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Checkout.cs", """
            namespace TestProj;

            public interface IPricing { decimal Total(decimal a); }

            public class Checkout
            {
                private readonly IPricing _calculator = new();
            }
            """));

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("No class in the solution implements 'IPricing'"));
        Assert.That(description, Does.Not.Contain("SomeImplementingClass"));
        Assert.That(description, Does.Not.Contain("does not list"));
        Assert.That(description, Does.Not.Contain("Concrete classes in the solution"));
    }

    /// <summary>
    /// CS0144 with no implementer: a class that declares every interface member by name and signature but has no base list is
    /// named as a structural match; a class that declares only some of the members is not.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0144InterfaceWithoutImplementersButStructuralMatch_NamesTheMatchingClass()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Checkout.cs", """
            namespace TestProj;

            public interface IPricing { decimal Total(decimal a); string Label { get; } }

            public class FullPricing
            {
                public decimal Total(decimal a) => a;
                public string Label => "full";
            }

            public class PartialPricing
            {
                public decimal Total(decimal a) => a;
            }

            public class Checkout
            {
                private readonly IPricing _calculator = new();
            }
            """));

        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0144"), "fixture must produce the CS0144");

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("No class in the solution implements 'IPricing'"));
        Assert.That(description, Does.Contain("'FullPricing' declares all of IPricing's members but does not list IPricing as a base type."));
        Assert.That(description, Does.Contain("Listing IPricing as a base type of FullPricing makes new FullPricing() valid for a IPricing-typed field."));
        Assert.That(description, Does.Not.Contain("PartialPricing"));
    }

    /// <summary>
    /// CS0103 on a name used as a call (eval run 20261112-151531-550 T19/T21): the missing method is declared in the same edit
    /// or added first. The call shape comes from the quoted source line, which the write gate attaches.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0103MissingCallee_SaysToDeclareItInTheSameEditOrAddMemberFirst()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Calc.cs", """
            namespace TestProj;

            public class Calc
            {
                public int Run()
                {
                    return ComputeTotal(5);
                }
            }
            """));
        var cs0103 = report.Diagnostics.Single(d => d.Id == "CS0103");
        var withLine = new DiagnosticReport(true, [cs0103 with { SourceLine = "return ComputeTotal(5);" }]);

        var description = await CompilerErrorLookupHelper.DescribeAsync(withLine, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("No symbol named 'ComputeTotal' was found anywhere in the solution"));
        Assert.That(description, Does.Contain("is called here, so the method itself is missing"));
        Assert.That(description, Does.Contain("in the same edit as the code that calls it"));
        Assert.That(description, Does.Contain("newContent"));
        Assert.That(description, Does.Contain("newMemberSource"));
        Assert.That(description, Does.Contain("addMember"));
        Assert.That(description, Does.Not.Contain("needs to be added"));
    }

    [Test]
    public async Task DescribeAsync_Cs0103MissingNameNotCalled_KeepsThePlainNeedsToBeAddedText()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Calc.cs", """
            namespace TestProj;

            public class Calc
            {
                public int Run()
                {
                    return missingValue;
                }
            }
            """));
        var cs0103 = report.Diagnostics.Single(d => d.Id == "CS0103");
        var withLine = new DiagnosticReport(true, [cs0103 with { SourceLine = "return missingValue;" }]);

        var description = await CompilerErrorLookupHelper.DescribeAsync(withLine, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("No symbol named 'missingValue' was found anywhere in the solution. This is likely a typo, or the member genuinely doesn't exist yet and needs to be added."));
        Assert.That(description, Does.Not.Contain("is called here"));
    }

    [Test]
    public async Task DescribeAsync_Cs0117MissingMember_ListsTheTypesMembersAndTheClosestName()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", """
            namespace TestProj;

            public static class ShippingKinds
            {
                public const string Standard = "standard";
                public const string Express = "express";
                public const string Overnight = "overnight";
            }

            public class Order
            {
                public string Pick() => ShippingKinds.Ownight;
            }
            """));
        var cs0117 = report.Diagnostics.Single(d => d.Id == "CS0117");
        var withLine = new DiagnosticReport(true, [cs0117]);

        var description = await CompilerErrorLookupHelper.DescribeAsync(withLine, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("Members of 'ShippingKinds':"));
        Assert.That(description, Does.Contain("Overnight"));
        Assert.That(description, Does.Contain("Closest existing member of 'ShippingKinds': 'Overnight'"));
        Assert.That(description, Does.Contain("(9 letters: O-v-e-r-n-i-g-h-t)"));
        Assert.That(
            description.IndexOf("Closest existing member", StringComparison.Ordinal),
            Is.LessThan(description.IndexOf("This is likely a typo", StringComparison.Ordinal)));
    }

    [Test]
    public async Task DescribeAsync_Cs0117MissingMemberNoCloseName_StillListsMembers()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", """
            namespace TestProj;

            public static class ShippingKinds
            {
                public const string Standard = "standard";
                public const string Express = "express";
                public const string Overnight = "overnight";
            }

            public class Order
            {
                public string Pick() => ShippingKinds.Zzzzzzzz;
            }
            """));
        var cs0117 = report.Diagnostics.Single(d => d.Id == "CS0117");
        var withLine = new DiagnosticReport(true, [cs0117]);

        var description = await CompilerErrorLookupHelper.DescribeAsync(withLine, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("Members of 'ShippingKinds':"));
        Assert.That(description, Does.Not.Contain("Closest existing member"));
    }

    private const string ShippingKindsSource = """
        public static class ShippingKinds
        {
            public const string Standard = "standard";
            public const string Express = "express";
            public const string Overnight = "overnight";
            public const string Pickup = "pickup";
        }
        """;

    /// <summary>Attaches the trimmed source line of the diagnostic, as the write gate does (ToInfo(includeSourceLine: true)).</summary>
    private static DiagnosticInfo WithQuotedSourceLine(DiagnosticInfo diagnostic, string fileContent)
    {
        var line = fileContent.Replace("\r\n", "\n").Split('\n')[diagnostic.StartLine - 1].Trim();
        return diagnostic with { SourceLine = line, SpanText = line };
    }

    /// <summary>
    /// CS0117 on a misspelled ShippingKinds.Overnight (eval: Ownight, Oovernight, Oversight): the text names the closest member and
    /// quotes the rejected line rewritten with it, so the correct spelling appears next to the wrong one.
    /// </summary>
    [TestCase("Ownight")]
    [TestCase("Oovernight")]
    [TestCase("Oversight")]
    public async Task DescribeAsync_Cs0117MisspelledMember_ShowsTheLineWithTheClosestMemberAsync(string typo)
    {
        var orderSource = $$"""
            namespace TestProj;

            public class Order
            {
                public string Pick(string kind)
                {
                    switch (kind)
                    {
                        case ShippingKinds.{{typo}}:
                            return "x";
                    }

                    return "";
                }
            }
            """;
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", "namespace TestProj;\n\n" + ShippingKindsSource),
            ("Order.cs", orderSource));
        var cs0117 = WithQuotedSourceLine(report.Diagnostics.Single(d => d.Id == "CS0117"), orderSource);

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0117]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("Closest member: ShippingKinds.Overnight."));
        Assert.That(description, Does.Contain("The line with it: case ShippingKinds.Overnight:"));
        Assert.That(description, Does.Not.Contain("do not").IgnoreCase.And.Not.Contain("must not").IgnoreCase);
    }

    [Test]
    public async Task DescribeAsync_Cs0117NoCloseMember_LeavesTheTextWithoutACorrectedLineAsync()
    {
        var orderSource = """
            namespace TestProj;

            public class Order
            {
                public string Pick() => ShippingKinds.Zebra;
            }
            """;
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", "namespace TestProj;\n\n" + ShippingKindsSource),
            ("Order.cs", orderSource));
        var cs0117 = WithQuotedSourceLine(report.Diagnostics.Single(d => d.Id == "CS0117"), orderSource);

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0117]), _symbolNavigationEngine);

        Assert.That(description, Does.Not.Contain("Closest member:"));
        Assert.That(description, Does.Not.Contain("The line with it"));
        Assert.That(description, Does.Contain("Members of 'ShippingKinds':"));
    }

    /// <summary>The same misspellings as bare names under <c>using static</c> (CS0103): the closest name in scope and the rewritten line.</summary>
    [TestCase("Ownight")]
    [TestCase("Oovernight")]
    [TestCase("Oversight")]
    public async Task DescribeAsync_Cs0103MisspelledStaticImportMember_ShowsTheLineWithTheClosestNameAsync(string typo)
    {
        var orderSource = $$"""
            using static TestProj.ShippingKinds;

            namespace TestProj;

            public class Order
            {
                public string Pick(string kind)
                {
                    switch (kind)
                    {
                        case {{typo}}:
                            return "x";
                    }

                    return "";
                }
            }
            """;
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", "namespace TestProj;\n\n" + ShippingKindsSource),
            ("Order.cs", orderSource));
        var cs0103 = WithQuotedSourceLine(report.Diagnostics.Single(d => d.Id == "CS0103"), orderSource);

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0103]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("Closest name: Overnight."));
        Assert.That(description, Does.Contain("The line with it: case Overnight:"));
        Assert.That(description, Does.Contain($"No symbol named '{typo}' was found anywhere in the solution."), "the existing fact stays");
    }

    [Test]
    public async Task DescribeAsync_Cs0103NoCloseName_LeavesTheTextWithoutACorrectedLineAsync()
    {
        var orderSource = """
            using static TestProj.ShippingKinds;

            namespace TestProj;

            public class Order
            {
                public string Pick(string kind)
                {
                    switch (kind)
                    {
                        case Zebra:
                            return "x";
                    }

                    return "";
                }
            }
            """;
        var report = await CompileAndGetErrorsAsync(
            ("Shipping.cs", "namespace TestProj;\n\n" + ShippingKindsSource),
            ("Order.cs", orderSource));
        var cs0103 = WithQuotedSourceLine(report.Diagnostics.Single(d => d.Id == "CS0103"), orderSource);

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0103]), _symbolNavigationEngine);

        Assert.That(description, Does.Not.Contain("Closest name:"));
        Assert.That(description, Does.Not.Contain("The line with it"));
        Assert.That(description, Does.Contain("No symbol named 'Zebra' was found anywhere in the solution."));
    }

    [Test]
    public void CorrectedLineHint_RewriteLine_ReplacesOnlyTheQualifyingOccurrence()
    {
        // The name also appears unqualified earlier (CS0117 looks at the member after the dot) and as a part of a longer identifier.
        var memberLine = CorrectedLineHint.RewriteLine(
            "var Ownight = ShippingKinds.Ownight; var OwnightX = 1;", "Ownight", "Overnight", startColumn: 30, memberAccess: true);
        Assert.That(memberLine, Is.EqualTo("var Ownight = ShippingKinds.Overnight; var OwnightX = 1;"));

        var bareLine = CorrectedLineHint.RewriteLine(
            "x = ShippingKinds.Ownight + Ownight;", "Ownight", "Overnight", startColumn: 30, memberAccess: false);
        Assert.That(bareLine, Is.EqualTo("x = ShippingKinds.Ownight + Overnight;"));

        Assert.That(CorrectedLineHint.RewriteLine("return Ownight;", "Ownight", "Overnight", 8, memberAccess: true), Is.Null);
        Assert.That(CorrectedLineHint.RewriteLine(null, "Ownight", "Overnight", 8, memberAccess: false), Is.Null);
    }

    [Test]
    public void CorrectedLineHint_FindClosest_AppliesTheDistanceRuleAndBreaksTiesByOrdinalOrder()
    {
        string[] members = ["Standard", "Express", "Overnight", "Pickup"];

        Assert.That(CorrectedLineHint.FindClosest("Ownight", members), Is.EqualTo("Overnight"));
        Assert.That(CorrectedLineHint.FindClosest("overnight", members), Is.EqualTo("Overnight"), "a case-insensitive exact match counts");
        Assert.That(CorrectedLineHint.FindClosest("Zebra", members), Is.Null);
        Assert.That(CorrectedLineHint.FindClosest("Aaa", ["Aab", "Aac"]), Is.EqualTo("Aab"));
        Assert.That(CorrectedLineHint.FindClosest("Aaa", ["Aac", "Aab"]), Is.EqualTo("Aab"), "same result for either enumeration order");
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

    private const string InterfaceI = "namespace TestProj;\r\n\r\npublic interface IOrderPricingCalculator\r\n{\r\n}\r\n";

    private static string FileName(DiagnosticInfo d) => Path.GetFileName(d.FilePath.ToString());

    /// <summary>
    /// Eval run 20261110-004337-782 T2/T3: CreateFile made IOrderPricingCalculator.cs, then a whole-file edit of
    /// OrderPricingCalculator.cs declared the interface too. The compiler pointed at the unedited file; the hint names both.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0101DiagnosticInUneditedFile_NamesTheEditedFileAndTheUneditedOneAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", InterfaceI),
            ("OrderPricingCalculator.cs", InterfaceI + "\r\npublic class OrderPricingCalculator : IOrderPricingCalculator\r\n{\r\n}\r\n"));
        var cs0101 = report.Diagnostics.First(d => d.Id == "CS0101");
        var diagnosticFile = FileName(cs0101);
        var editedFile = diagnosticFile == "OrderPricingCalculator.cs" ? "IOrderPricingCalculator.cs" : "OrderPricingCalculator.cs";

        var description = await CompilerErrorLookupHelper.DescribeAsync(
            new DiagnosticReport(true, [cs0101]), _symbolNavigationEngine, editedFilePaths: [editedFile]);

        Assert.That(description, Does.Contain(
            $"'IOrderPricingCalculator' is now declared twice: in {editedFile} (changed by this edit) and in {diagnosticFile}. Keep one declaration: {diagnosticFile} already declares it, so remove the 'IOrderPricingCalculator' declaration from your edit."));
    }

    /// <summary>
    /// With DeleteFile callable, the CS0101 hint also offers deleting the unedited file when it is the superseded copy.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0101_DeleteFileActive_OffersDeletingTheUneditedFileAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", InterfaceI),
            ("OrderPricingCalculator.cs", InterfaceI + "\r\npublic class OrderPricingCalculator : IOrderPricingCalculator\r\n{\r\n}\r\n"));
        var cs0101 = report.Diagnostics.First(d => d.Id == "CS0101");
        var diagnosticFile = FileName(cs0101);
        var editedFile = diagnosticFile == "OrderPricingCalculator.cs" ? "IOrderPricingCalculator.cs" : "OrderPricingCalculator.cs";

        var description = await CompilerErrorLookupHelper.DescribeAsync(
            new DiagnosticReport(true, [cs0101]), _symbolNavigationEngine, editedFilePaths: [editedFile], isToolActive: name => name == "DeleteFile");

        Assert.That(description, Does.Contain($"Keep one declaration: {diagnosticFile} already declares it, so remove the 'IOrderPricingCalculator' declaration from your edit."));
        Assert.That(description, Does.Contain($"Or, if {diagnosticFile} is the superseded copy, delete that file with "));
        Assert.That(description, Does.Contain("DeleteFile"));
    }

    /// <summary>
    /// With DeleteFile not callable (or the parameter omitted), the CS0101 hint names no tool.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0101_DeleteFileNotActive_NamesNoToolAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", InterfaceI),
            ("OrderPricingCalculator.cs", InterfaceI + "\r\npublic class OrderPricingCalculator : IOrderPricingCalculator\r\n{\r\n}\r\n"));
        var cs0101 = report.Diagnostics.First(d => d.Id == "CS0101");
        var diagnosticFile = FileName(cs0101);
        var editedFile = diagnosticFile == "OrderPricingCalculator.cs" ? "IOrderPricingCalculator.cs" : "OrderPricingCalculator.cs";
        var diagnostics = new DiagnosticReport(true, [cs0101]);

        var notActive = await CompilerErrorLookupHelper.DescribeAsync(
            diagnostics, _symbolNavigationEngine, editedFilePaths: [editedFile], isToolActive: _ => false);
        var omitted = await CompilerErrorLookupHelper.DescribeAsync(
            diagnostics, _symbolNavigationEngine, editedFilePaths: [editedFile]);

        Assert.That(notActive, Does.Not.Contain("DeleteFile"));
        Assert.That(omitted, Does.Not.Contain("DeleteFile"));
    }

    [Test]
    public async Task DescribeAsync_Cs0101DiagnosticInEditedFile_NamesItAsEditedAndTheCommittedOtherFileAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", InterfaceI),
            ("Duplicate.cs", InterfaceI));
        var cs0101 = report.Diagnostics.First(d => d.Id == "CS0101");
        var diagnosticFile = FileName(cs0101);
        var otherFile = diagnosticFile == "Duplicate.cs" ? "IOrderPricingCalculator.cs" : "Duplicate.cs";

        var description = await CompilerErrorLookupHelper.DescribeAsync(
            new DiagnosticReport(true, [cs0101]), _symbolNavigationEngine, editedFilePaths: [diagnosticFile]);

        Assert.That(description, Does.Contain(
            $"'IOrderPricingCalculator' is now declared twice: in {diagnosticFile} (changed by this edit) and in {otherFile}. Keep one declaration: {otherFile} already declares it, so remove the 'IOrderPricingCalculator' declaration from your edit."));
    }

    [Test]
    public async Task DescribeAsync_Cs0101WithoutEditedPaths_NamesBothFilesAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", InterfaceI),
            ("Duplicate.cs", InterfaceI));
        var cs0101 = report.Diagnostics.First(d => d.Id == "CS0101");

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0101]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("IOrderPricingCalculator.cs").And.Contain("Duplicate.cs").And.Contain("is now declared twice"));
        Assert.That(description, Does.Contain("Keep one declaration"));
    }

    /// <summary>
    /// Eval run 20261111-061005-246 T12: a CS0111 inside a type that a CS0101 says is declared twice got the wrong-path
    /// "did you mean to ReadFile/ApplyDiff" advice; it now points at the duplicate type.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0111InsideDuplicatedType_PointsAtTheDuplicateTypeAsync()
    {
        const string interfaceWithMember = "namespace TestProj;\r\n\r\npublic interface IOrderPricingCalculator\r\n{\r\n    decimal Total(decimal amount);\r\n}\r\n";
        var report = await CompileAndGetErrorsAsync(
            ("IOrderPricingCalculator.cs", interfaceWithMember),
            ("Duplicate.cs", interfaceWithMember));
        Assert.That(report.Diagnostics.Select(d => d.Id), Does.Contain("CS0101").And.Contain("CS0111"), "fixture must produce CS0101 and CS0111");

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, editedFilePaths: ["Duplicate.cs"]);

        Assert.That(description, Does.Contain("This follows from 'IOrderPricingCalculator' being declared twice"));
        Assert.That(description, Does.Not.Contain("did you mean to ReadFile"));
    }

    /// <summary>
    /// Batch G R4 (run 20261111-074907-352): an edit re-declared an interface member already in the same file; the CS0111 hint
    /// said the real location could not be found and the model re-sent the edit four times. It now names the existing line.
    /// </summary>
    [Test]
    public async Task DescribeAsync_Cs0111DuplicateInSameFile_NamesTheExistingLineAsync()
    {
        const string interfaceWithDuplicate = "namespace TestProj;\r\n\r\npublic interface IOrderPricingCalculator\r\n{\r\n    string DescribeOrder(int id, string label);\r\n    string DescribeOrder(int id, string label);\r\n}\r\n";
        var report = await CompileAndGetErrorsAsync(("IOrderPricingCalculator.cs", interfaceWithDuplicate));
        var cs0111 = report.Diagnostics.First(d => d.Id == "CS0111");

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0111]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("'DescribeOrder' collides with an existing declaration: 'IOrderPricingCalculator' already declares it with these parameter types at line 5 of this file"));
        Assert.That(description, Does.Not.Contain("could not be found"));
    }

    [Test]
    public async Task DescribeAsync_Cs0101OtherLocationNotResolvable_GivesTheShortFormAsync()
    {
        var ghost = new DiagnosticInfo("CS0101", "Error",
            "The namespace 'TestProj' already contains a definition for 'Ghost'", "C:\\repo\\Ghost.cs", 3, 14, 3, 19);

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [ghost]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("'Ghost' is declared more than once in this namespace; keep one declaration."));
        Assert.That(description, Does.Not.Contain("is now declared twice"));
    }

    [Test]
    public async Task DescribeAsync_Cs0102MemberDeclaredTwiceInOneFile_GivesTheShortFormForTheTypeAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("Calc.cs", "namespace TestProj;\r\n\r\npublic class Calc\r\n{\r\n    public int Total;\r\n    public int Total;\r\n}\r\n"));
        var cs0102 = report.Diagnostics.First(d => d.Id == "CS0102");

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0102]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("'Total' is declared more than once in 'Calc'; keep one declaration."));
    }

    [Test]
    public async Task DescribeAsync_Cs0102MemberDeclaredInTwoPartialFiles_NamesBothFilesAsync()
    {
        var report = await CompileAndGetErrorsAsync(
            ("CalcA.cs", "namespace TestProj;\r\n\r\npublic partial class Calc\r\n{\r\n    public int Total;\r\n}\r\n"),
            ("CalcB.cs", "namespace TestProj;\r\n\r\npublic partial class Calc\r\n{\r\n    public int Total;\r\n}\r\n"));
        var cs0102 = report.Diagnostics.First(d => d.Id == "CS0102");

        var description = await CompilerErrorLookupHelper.DescribeAsync(new DiagnosticReport(true, [cs0102]), _symbolNavigationEngine);

        Assert.That(description, Does.Contain("'Total' in 'Calc' is now declared twice"));
        Assert.That(description, Does.Contain("CalcA.cs").And.Contain("CalcB.cs"));
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
    /// SpanText coverage: a diagnostic carrying the text of its span gets an "Expression the error points at" line,
    /// so the model can tell which expression the error is on, not only which source line it is on.
    /// </summary>
    [Test]
    public async Task DescribeAsync_DiagnosticWithSpanText_NamesTheExpression()
    {
        var report = new DiagnosticReport(true, new List<DiagnosticInfo>
        {
            new DiagnosticInfo("CS0029", "Error", "Cannot implicitly convert type 'string' to 'bool'", "C:\\repo\\DeliveryEstimator.cs", 7, 53, 7, 76, null,
                "if (ShippingKinds.IsExpedited(kind)) return ShippingKinds.Overnight ? 1 : 2;", "ShippingKinds.Overnight"),
        });

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Contain("Expression the error points at: 'ShippingKinds.Overnight'"));
    }

    /// <summary>
    /// Redundancy guard: when SpanText equals SourceLine the expression line would repeat the quoted source line,
    /// so it must be omitted.
    /// </summary>
    [Test]
    public async Task DescribeAsync_SpanTextEqualsSourceLine_OmitsExpressionLine()
    {
        const string line = "var x = 1";
        var report = new DiagnosticReport(true, new List<DiagnosticInfo>
        {
            new DiagnosticInfo("CS1002", "Error", "; expected", "C:\\repo\\Other.cs", 7, 1, 7, 2, null, line, line),
        });

        var description = await CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine);

        Assert.That(description, Does.Not.Contain("Expression the error points at"));
        Assert.That(description, Is.EqualTo("CS1002 at C:\\repo\\Other.cs:7: ; expected\n    > var x = 1"));
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
