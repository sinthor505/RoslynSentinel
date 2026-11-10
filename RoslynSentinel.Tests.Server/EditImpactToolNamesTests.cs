using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// <see cref="EditImpactAnalyzer"/> names tools by plain string (Engines cannot see the tool classes) and learns which of them
/// the caller can use from <see cref="ActiveToolSurface.IsCallable"/>, the DI-registered active-tool source. These tests pin
/// both ends: every name the analyzer can emit is a real tool name, and a name hidden by the surface is never emitted.
/// </summary>
[TestFixture]
[Category("EditImpactAnalyzer")]
public class EditImpactToolNamesTests
{
    [Test]
    public void NamedTools_AreAllDeclaredToolNames()
    {
        var declared = McpToolSchemaPatcher
            .DiscoverAllDeclaredTools(typeof(RefactoringSignatureTools).Assembly, typeof(AdvancedRefactoringTools).Assembly)
            .Select(t => t.ToolName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in EditImpactAnalyzer.NamedTools)
        {
            Assert.That(declared, Does.Contain(name), $"EditImpactAnalyzer names '{name}', which no tool class declares - renamed tool?");
        }
    }

    [Test]
    public void ActiveToolSurface_DecidesWhichToolTheNextStepNames()
    {
        // The edit renames CalcDisc in one file; the unedited caller still uses the old name.
        var baseline = new Dictionary<string, string?> { ["Calc.cs"] = "namespace N; class C { public int CalcDisc(int a) => a; }" };
        var candidate = new Dictionary<string, string?> { ["Calc.cs"] = "namespace N; class C { public int CalculateDiscountedTotal(int a) => a; }" };
        var diagnostics = new List<DiagnosticInfo>
        {
            new("CS1061", "Error", "'C' does not contain a definition for 'CalcDisc' and no accessible extension method 'CalcDisc' could be found",
                "Checkout.cs", 9, 1, 9, 5, SourceLine: "return _c.CalcDisc(1);"),
        };
        var none = new HashSet<string>();
        var declaringClasses = new HashSet<string> { "RefactoringSignatureTools", "WorkspaceTools" };

        var open = new ActiveToolSurface("test", none, none, none, declaringClasses);
        var hidden = new ActiveToolSurface("test", none, none, none, declaringClasses, null, new HashSet<string> { "ReplaceSnippet" });

        var withRename = EditImpactAnalyzer.Analyze(baseline, candidate, diagnostics, open.IsCallable);
        var withoutRename = EditImpactAnalyzer.Analyze(baseline, candidate, diagnostics, hidden.IsCallable);

        Assert.That(withRename!.NextStepLine, Does.Contain("use RenameSymbol"));
        Assert.That(withRename.NextStepLine, Does.Contain("newName: \"CalculateDiscountedTotal\""), "with the tools loaded the hint is a schema-valid call");
        Assert.That(withoutRename!.Text, Does.Not.Contain("RenameSymbol"), "hidden by the allow-list, so never named");
        Assert.That(withoutRename.NextStepLine, Does.Contain("batchEdits"));
    }
}
