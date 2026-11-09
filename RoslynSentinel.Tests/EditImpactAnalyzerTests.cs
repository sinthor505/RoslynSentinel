using Microsoft.CodeAnalysis;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests;

/// <summary>
/// Fixture tests (no model) for <see cref="EditImpactAnalyzer"/>, built on the OrderPricingCalculator / OrderCheckout shape
/// from the 2026-11-09 10-chain evals: a single-file text edit renames <c>CalcDisc</c>, the compile gate then reports errors
/// in the untouched <c>OrderCheckout.cs</c>. The reports come from the real gate (<see cref="ValidationEngine"/>), not from
/// hand-built diagnostics.
/// </summary>
[TestFixture]
[Category("EditImpactAnalyzer")]
public class EditImpactAnalyzerTests
{
    private const string CalculatorFile = "OrderPricingCalculator.cs";
    private const string CheckoutFile = "OrderCheckout.cs";

    private const string Calculator = """
        namespace TestProj;

        public class OrderPricingCalculator
        {
            private readonly object _unrelatedField = new();

            public string DescribeOrder(int id, string label)
            {
                return $"Order {id}: {label}";
            }

            public decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer)
            {
                if (isPreferredCustomer)
                {
                    var discount = amount * rate * 1.1m;
                    return amount - discount;
                }

                var standardDiscount = amount * rate;
                return amount - standardDiscount;
            }
        }
        """;

    private const string Checkout = """
        namespace TestProj;

        public class OrderCheckout
        {
            private readonly OrderPricingCalculator _calculator = new();

            public decimal GetFinalPrice(decimal amount, decimal rate, bool isPreferredCustomer)
            {
                return _calculator.CalcDisc(amount, rate, isPreferredCustomer);
            }
        }
        """;

    private static Func<string, bool> Active(params string[] names) => name => names.Contains(name);

    private static Solution Baseline() => TestSolutionBuilder.CreateSolutionWithProject("TestProj",
        [(CalculatorFile, Calculator), (CheckoutFile, Checkout)]);

    /// <summary>Runs the real gate over the proposed texts, then the analyzer over its report.</summary>
    private static async Task<(EditImpactResult? Result, DiagnosticReport Report)> GateAndAnalyzeAsync(
        Dictionary<string, string> edits, Func<string, bool> isToolActive)
    {
        var baseline = Baseline();
        var changes = edits.ToDictionary(kv => (FilePathWrapper)kv.Key, kv => kv.Value);
        var report = await ValidationEngine.ValidateChangesAsync(baseline, changes);
        var result = await EditImpactAnalyzer.AnalyzeAsync(baseline, changes, report, isToolActive);
        return (result, report);
    }

    private static Dictionary<string, string> Texts(params (string Path, string Text)[] files)
        => files.ToDictionary(f => f.Path, f => f.Text, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> Single(string path, string text)
        => new Dictionary<string, string?> { [path] = text };

    // ---- the four cases from the first-slice spec ----

    [Test]
    public async Task SingleFileRename_RenameSymbolActive_NamesRenameAndCallerFile_NextStepIsRenameSymbol()
    {
        var renamed = Calculator.Replace("CalcDisc", "CalculateDiscountedTotal");

        var (result, report) = await GateAndAnalyzeAsync(Texts((CalculatorFile, renamed)), Active("RenameSymbol", "ReplaceSnippet"));

        Assert.That(report.IsError, Is.True, "the untouched caller must break, otherwise the fixture proves nothing");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Text, Does.StartWith("Next step:"), "the action comes first");
        Assert.That(result.NextStepLine, Does.Contain("use RenameSymbol"));
        Assert.That(result.Text, Does.Contain("CalcDisc").And.Contain("CalculateDiscountedTotal"));
        Assert.That(result.Text, Does.Contain("renamed CalcDisc -> CalculateDiscountedTotal"));
        Assert.That(result.Text, Does.Contain($"{CheckoutFile}:9"), "the caller's file and line");
        Assert.That(result.Text, Does.Contain("_calculator.CalcDisc(amount, rate, isPreferredCustomer)"), "the offending source line is quoted");
        Assert.That(result.Text.Split('\n').Length, Is.LessThanOrEqualTo(10));
        Assert.That(result.UnattributedDiagnostics, Is.Empty);
    }

    [Test]
    public async Task SingleFileRename_RenameSymbolNotActive_NextStepIsTheBatchLine()
    {
        var renamed = Calculator.Replace("CalcDisc", "CalculateDiscountedTotal");

        var (result, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, renamed)), Active("ReplaceSnippet"));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.NextStepLine, Does.Contain("batchEdits").And.Contain("one item per file"));
        Assert.That(result.Text, Does.Not.Contain("RenameSymbol"), "a tool that is not active must never be named");
        Assert.That(result.Text, Does.Contain("renames CalcDisc to CalculateDiscountedTotal"), "the diff still names the rename");
    }

    [Test]
    public async Task SingleFileRename_NeitherToolActive_NextStepIsGenericAndNamesNoTool()
    {
        var renamed = Calculator.Replace("CalcDisc", "CalculateDiscountedTotal");

        var (result, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, renamed)), Active());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.NextStepLine, Does.Contain("ONE call"));
        Assert.That(result.Text, Does.Not.Contain("RenameSymbol").And.Not.Contain("ReplaceSnippet"));
    }

    [Test]
    public async Task CorrectTwoFileBatch_PassesTheGate_AndProducesNoReport()
    {
        var edits = Texts(
            (CalculatorFile, Calculator.Replace("CalcDisc", "CalculateDiscountedTotal")),
            (CheckoutFile, Checkout.Replace("CalcDisc", "CalculateDiscountedTotal")));

        var (result, report) = await GateAndAnalyzeAsync(edits, Active("RenameSymbol", "ReplaceSnippet"));

        Assert.That(report.IsError, Is.False, "a rename applied to both files is a correct batch");
        Assert.That(result, Is.Null, "a correct batch must not be flagged for breaking its own callers");

        // The batch diff still sees the rename; it just has no unedited file left to blame.
        var diff = EditImpactAnalyzer.DiffSymbols(
            new Dictionary<string, string?> { [CalculatorFile] = Calculator, [CheckoutFile] = Checkout },
            edits.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
        Assert.That(diff.Single(c => c.Kind == SymbolChangeKind.Renamed).NewName, Is.EqualTo("CalculateDiscountedTotal"));
    }

    [Test]
    public async Task RemovedAndAddedWithDifferentParameterTypes_ReportsRemovedAndAdded_ClaimsNoRename()
    {
        var edited = Calculator.Replace(
            "public decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer)",
            "public decimal CalculateDiscountedTotal(double amount, double rate, bool isPreferredCustomer)")
            .Replace("var discount = amount * rate * 1.1m;", "var discount = (decimal)(amount * rate * 1.1);")
            .Replace("return amount - discount;", "return (decimal)amount - discount;")
            .Replace("var standardDiscount = amount * rate;", "var standardDiscount = (decimal)(amount * rate);")
            .Replace("return amount - standardDiscount;", "return (decimal)amount - standardDiscount;");

        var (result, report) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("RenameSymbol", "ReplaceSnippet"));

        Assert.That(report.IsError, Is.True);
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Text, Does.Contain("removed CalcDisc").And.Contain("added CalculateDiscountedTotal"));
        Assert.That(result.Text.ToLowerInvariant(), Does.Not.Contain("renam"), "different parameter types: no rename claim");
        Assert.That(result.Changes.Select(c => c.Kind), Does.Not.Contain(SymbolChangeKind.Renamed));
        Assert.That(result.NextStepLine, Does.Contain("removes CalcDisc"));
        Assert.That(result.NextStepLine, Does.Not.Contain("RenameSymbol"), "RenameSymbol would not fix a removal");
    }

    [Test]
    public async Task NameAndAccessibilityChangedTogether_IsARename_NextStepIsRenameSymbolThenChangeAccessibility()
    {
        // Same shape as the model's edit: rename plus public -> internal in one text edit.
        var edited = Calculator.Replace(
            "public decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer)",
            "internal decimal CalculateDiscountedTotal(decimal amount, decimal rate, bool isPreferredCustomer)");

        var (result, report) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("RenameSymbol", "ChangeAccessibility", "ReplaceSnippet"));

        Assert.That(report.IsError, Is.True);
        Assert.That(result, Is.Not.Null);
        var rename = result!.Changes.Single(c => c.Kind == SymbolChangeKind.Renamed);
        Assert.That((rename.OldName, rename.NewName, rename.NewAccessibility), Is.EqualTo(("CalcDisc", "CalculateDiscountedTotal", "internal")));
        Assert.That(result.Changes.Select(c => c.Kind), Does.Not.Contain(SymbolChangeKind.Removed));
        Assert.That(result.NextStepLine, Does.Contain("use RenameSymbol"));
        Assert.That(result.NextStepLine, Does.Contain("internal").And.Contain("ChangeAccessibility"), "the accessibility change follows the rename");
        Assert.That(result.NextStepLine, Does.Not.Contain("removes CalcDisc"));
    }

    [Test]
    public async Task NameAndAccessibilityChangedTogether_ChangeAccessibilityNotCallable_NamesOnlyRenameSymbol()
    {
        var edited = Calculator.Replace(
            "public decimal CalcDisc(decimal amount, decimal rate, bool isPreferredCustomer)",
            "internal decimal CalculateDiscountedTotal(decimal amount, decimal rate, bool isPreferredCustomer)");

        var (result, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("RenameSymbol", "ReplaceSnippet"));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.NextStepLine, Does.Contain("use RenameSymbol"));
        Assert.That(result.Text, Does.Not.Contain("ChangeAccessibility"), "a tool that is not callable must never be named");
    }

    [Test]
    public void DiffSymbols_RenameWithAccessibilityParameterNameAndExtraHelper_IsStillARename()
    {
        // The extra added method has a different shape, so it does not make the pairing ambiguous.
        var diff = EditImpactAnalyzer.DiffSymbols(
            Single(CalculatorFile, "class C { public decimal CalcDisc(decimal a, decimal rate, bool b) => a; }"),
            Single(CalculatorFile, "class C { internal decimal CalculateDiscountedTotal(decimal a, decimal discountRate, bool b) => a; private decimal Raw(decimal a, decimal r) => a; }"));

        var rename = diff.Single(c => c.Kind == SymbolChangeKind.Renamed);
        Assert.That((rename.OldName, rename.NewName, rename.NewAccessibility), Is.EqualTo(("CalcDisc", "CalculateDiscountedTotal", "internal")));
        Assert.That(diff.Where(c => c.Kind == SymbolChangeKind.Added).Select(c => c.OldName), Is.EquivalentTo(new[] { "Raw" }));
    }

    [Test]
    public void DiffSymbols_NameAndAccessibilityAndParameterTypeChanged_IsNotARename()
    {
        var diff = EditImpactAnalyzer.DiffSymbols(
            Single(CalculatorFile, "class C { public decimal CalcDisc(decimal a) => a; }"),
            Single(CalculatorFile, "class C { internal decimal CalculateDiscountedTotal(double a) => 0; }"));

        Assert.That(diff.Select(c => c.Kind), Does.Not.Contain(SymbolChangeKind.Renamed));
        Assert.That(diff.Select(c => (c.Kind, c.OldName)), Is.EquivalentTo(new[]
        {
            (SymbolChangeKind.Removed, "CalcDisc"), (SymbolChangeKind.Added, "CalculateDiscountedTotal"),
        }));
    }

    // ---- the other attributed kinds from the spec table ----

    [Test]
    public async Task TypeRename_IsAttributedToCs0246_AndMembersOfTheRenamedTypeAreNotReportedAsRemoved()
    {
        var edited = Calculator.Replace("OrderPricingCalculator", "StandardOrderPricingCalculator");

        var (result, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("RenameSymbol", "ReplaceSnippet"));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.NextStepLine, Does.Contain("renames OrderPricingCalculator to StandardOrderPricingCalculator").And.Contain("use RenameSymbol"));
        var renamed = result.Changes.Where(c => c.Kind == SymbolChangeKind.Renamed).ToList();
        Assert.That(renamed, Has.Count.EqualTo(1));
        Assert.That(renamed[0].DeclarationKind, Is.EqualTo("class"));
        Assert.That(result.Changes.Count, Is.EqualTo(1), "its members are the same members under the new type name");
    }

    [Test]
    public async Task ParameterAdded_IsAttributedToCs7036_AndPointsAtSignatureTools()
    {
        var edited = Calculator.Replace("bool isPreferredCustomer)", "bool isPreferredCustomer, int extra)");

        var (result, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("ChangeSignature", "MethodSignature", "ReplaceSnippet"));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Changes.Single(c => c.Kind == SymbolChangeKind.SignatureChanged).OldName, Is.EqualTo("CalcDisc"));
        Assert.That(result.NextStepLine, Does.Contain("ChangeSignature or MethodSignature"));

        var (withoutTools, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("ReplaceSnippet"));
        Assert.That(withoutTools!.NextStepLine, Does.Contain("batchEdits"));
        Assert.That(withoutTools.NextStepLine, Does.Not.Contain("ChangeSignature"));
    }

    [Test]
    public async Task AccessibilityNarrowed_IsAttributedToCs0122_AndPointsAtChangeAccessibility()
    {
        var edited = Calculator.Replace("public decimal CalcDisc", "private decimal CalcDisc");

        var (result, _) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("ChangeAccessibility", "ReplaceSnippet"));

        Assert.That(result, Is.Not.Null);
        var change = result!.Changes.Single(c => c.Kind == SymbolChangeKind.AccessibilityChanged);
        Assert.That(change.AccessibilityNarrowed, Is.True);
        Assert.That(result.NextStepLine, Does.Contain("use ChangeAccessibility"));
    }

    // ---- no report ----

    [Test]
    public async Task ErrorsOnlyInTheEditedFile_ProduceNoReport()
    {
        var edited = Calculator.Replace("return amount - standardDiscount;", "return amount - missingName;");

        var (result, report) = await GateAndAnalyzeAsync(Texts((CalculatorFile, edited)), Active("RenameSymbol"));

        Assert.That(report.IsError, Is.True);
        Assert.That(result, Is.Null, "nothing in an unedited file to attribute; the caller keeps its existing text");
    }

    [Test]
    public async Task ErrorInUneditedFileWithNoMatchingDiffEntry_ProducesNoReport()
    {
        // The edit only adds a member; the caller's error names something the edit never touched.
        var baseline = Baseline();
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [CalculatorFile] = Calculator.Replace(
                "private readonly object _unrelatedField = new();",
                "private readonly object _unrelatedField = new();\n    public int Added() => 1;"),
        };
        var unrelated = new DiagnosticReport(true, [new DiagnosticInfo("CS1061", "Error",
            "'OrderPricingCalculator' does not contain a definition for 'SomethingElse'", CheckoutFile, 9, 1, 9, 5)]);

        var result = await EditImpactAnalyzer.AnalyzeAsync(baseline, changes, unrelated, Active("RenameSymbol"));

        Assert.That(result, Is.Null);
    }

    // ---- the diff on its own ----

    [Test]
    public void DiffSymbols_TwoRemovedOneAdded_IsNotARename()
    {
        var before = Single(CalculatorFile, "namespace N; class C { void A() {} void B() {} }");
        var after = Single(CalculatorFile, "namespace N; class C { void X() {} }");

        var diff = EditImpactAnalyzer.DiffSymbols(before, after);

        Assert.That(diff.Select(c => (c.Kind, c.OldName)), Is.EquivalentTo(new[]
        {
            (SymbolChangeKind.Removed, "A"), (SymbolChangeKind.Removed, "B"), (SymbolChangeKind.Added, "X"),
        }));
    }

    [Test]
    public void DiffSymbols_OneRemovedOneAddedEqualSignature_IsARename()
    {
        var before = Single(CalculatorFile, "namespace N; class C { int F; void A(int x) {} }");
        var after = Single(CalculatorFile, "namespace N; class C { int F; void Renamed(int x) {} }");

        var diff = EditImpactAnalyzer.DiffSymbols(before, after);

        var change = diff.Single();
        Assert.That((change.Kind, change.OldName, change.NewName, change.DeclarationKind), Is.EqualTo((SymbolChangeKind.Renamed, "A", "Renamed", "method")));
    }

    [Test]
    public void DiffSymbols_SameNameDifferentReturnType_IsSignatureChanged()
    {
        var diff = EditImpactAnalyzer.DiffSymbols(
            Single(CalculatorFile, "class C { int Get() => 1; }"),
            Single(CalculatorFile, "class C { long Get() => 1; }"));

        Assert.That(diff.Single().Kind, Is.EqualTo(SymbolChangeKind.SignatureChanged));
    }

    [Test]
    public void DiffSymbols_BodyOnlyEditAndAccessibilityWidening_IsNotNarrowing()
    {
        var diff = EditImpactAnalyzer.DiffSymbols(
            Single(CalculatorFile, "class C { int Get() => 1; private int Hidden() => 1; }"),
            Single(CalculatorFile, "class C { int Get() => 2; public int Hidden() => 1; }"));

        var change = diff.Single();
        Assert.That(change.Kind, Is.EqualTo(SymbolChangeKind.AccessibilityChanged));
        Assert.That(change.AccessibilityNarrowed, Is.False);
    }

    [Test]
    public void DiffSymbols_MoveOfATypeToAnotherEditedFile_IsNoChange()
    {
        var diff = EditImpactAnalyzer.DiffSymbols(
            new Dictionary<string, string?> { ["A.cs"] = "namespace N; class C { void M() {} }", ["B.cs"] = "namespace N; class D {}" },
            new Dictionary<string, string?> { ["A.cs"] = "namespace N; class D {}", ["B.cs"] = "namespace N; class C { void M() {} }" });

        Assert.That(diff, Is.Empty);
    }
}
