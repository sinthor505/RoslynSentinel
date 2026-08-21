using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class DiffEngineTests
{
    private DiffEngine _diffEngine;

    [SetUp]
    public void Setup()
    {
        var workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        _diffEngine = new DiffEngine(workspaceManager);
    }

    [Test]
    public void ApplyDiff_SingleHunk_Addition_ShouldSucceed()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From("line1" + nl + "line2" + nl + "line3");
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added\n line2\n line3";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo("line1" + Environment.NewLine + "added" + Environment.NewLine + "line2" + Environment.NewLine + "line3"));
    }

    [Test]
    public void ApplyDiff_MultipleHunks_ShouldSucceed()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From("line1" + nl + "line2" + nl + "line3" + nl + "line4" + nl + "line5");
        // Hunk 1 starts at 1, Hunk 2 starts at 4 (relative to original)
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added1\n line2\n line3\n@@ -4,2 +5,3 @@\n line4\n+added2\n line5";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        var expected = string.Join(Environment.NewLine, new[] { "line1", "added1", "line2", "line3", "line4", "added2", "line5" });
        Assert.That(newText, Is.EqualTo(expected));
    }

    [Test]
    public async Task ValidateProposedDiff_ShouldReturnNoErrors_ForValidDiff()
    {
        var source = "public class C { public void M() {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("C.cs", source) });
        var workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        workspaceManager.SetTestSolution(solution);

        var diffEngine = new DiffEngine(workspaceManager);
        var validationEngine = new ValidationEngine(new NullLogger<ValidationEngine>(), workspaceManager, diffEngine);

        var diff = "@@ -1,1 +1,1 @@\n-public class C { public void M() {} }\n+public class C { public void M() { int x = 1; } }";

        var report = await validationEngine.ValidateDiffAsync("C.cs", diff);

        Assert.That(report.Success, Is.True);
        Assert.That(report.Diagnostics.Count, Is.EqualTo(0));
    }

    [Test]
    public async Task ValidateProposedDiff_ShouldReturnErrors_ForInvalidDiff()
    {
        var source = "public class C { public void M() {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("C.cs", source) });
        var workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        workspaceManager.SetTestSolution(solution);

        var diffEngine = new DiffEngine(workspaceManager);
        var validationEngine = new ValidationEngine(new NullLogger<ValidationEngine>(), workspaceManager, diffEngine);

        // Introducing a syntax error (missing semicolon)
        var diff = "@@ -1,1 +1,1 @@\n-public class C { public void M() {} }\n+public class C { public void M() { int x = 1 } }";

        var report = await validationEngine.ValidateDiffAsync("C.cs", diff);

        Assert.That(report.Success, Is.False);
        Assert.That(report.Diagnostics.Any(d => d.Severity == "Error"), Is.True);
    }

    // ── Regression: line-number drift tolerance (formerly ProposedChange, now ApplyDiff) ──────
    // A caller-authored diff's line numbers routinely go stale after an earlier edit to the same
    // file (e.g. a prior hunk in the same diff shifted everything below it, or the caller composed
    // the diff against a slightly-earlier read of the file). The old behavior trusted the declared
    // line number unconditionally and either silently corrupted unrelated lines or threw a generic
    // out-of-bounds error with no indication of why. These tests confirm the tool now re-anchors a
    // hunk by its own content when the declared position doesn't match.

    [Test]
    [Description("A hunk whose declared line number is stale (content has shifted down by 2 lines "
                 + "due to an earlier insertion elsewhere in the file) must still apply correctly by "
                 + "re-anchoring to where its context/removal lines actually are.")]
    public void ApplyDiff_HunkLineNumberStale_ReanchorsToActualContent()
    {
        var nl = Environment.NewLine;
        // Real file has 2 extra lines at the top compared to what the diff's author assumed.
        var oldText = SourceText.From(string.Join(nl,
            "// unexpected extra line 1", "// unexpected extra line 2", "line1", "line2", "line3"));

        // Diff was authored believing "line2" was at line 2; it's actually at line 4.
        var diff = "@@ -2,1 +2,2 @@\n line2\n+added\n line3";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo(string.Join(nl,
            "// unexpected extra line 1", "// unexpected extra line 2", "line1", "line2", "added", "line3")));
    }

    [Test]
    [Description("A hunk whose declared line number is stale in the other direction (content moved "
                 + "UP relative to the diff's assumption) must also re-anchor correctly.")]
    public void ApplyDiff_HunkLineNumberStaleUpward_ReanchorsToActualContent()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From(string.Join(nl, "line1", "line2", "line3"));

        // Diff was authored believing "line2" was at line 5; it's actually at line 2.
        var diff = "@@ -5,1 +5,2 @@\n line2\n+added\n line3";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo(string.Join(nl, "line1", "line2", "added", "line3")));
    }

    [Test]
    [Description("A hunk whose content genuinely does not exist anywhere near its declared position "
                 + "must fail with a clear, actionable message — not silently corrupt an unrelated "
                 + "line, and not a bare out-of-bounds exception with no context.")]
    public void ApplyDiff_HunkContentNotFoundAnywhereNearby_ThrowsWithActionableMessage()
    {
        var oldText = SourceText.From(string.Join(Environment.NewLine, "line1", "line2", "line3"));
        var diff = "@@ -1,1 +1,1 @@\n-this text does not exist in the file\n+replacement";

        var ex = Assert.Throws<DiffApplyException>(() => _diffEngine.ApplyDiff(oldText, diff));
        Assert.That(ex!.Message, Does.Contain("this text does not exist in the file"));
        Assert.That(ex.Message, Does.Contain("Regenerate the diff"));
    }

    [Test]
    [Description("Exact, non-stale line numbers (the common case) must continue to apply without any "
                 + "re-anchoring search overhead changing the result.")]
    public void ApplyDiff_ExactLineNumbers_StillAppliesUnchanged()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From("line1" + nl + "line2" + nl + "line3");
        var diff = "@@ -1,3 +1,4 @@\n line1\n+added\n line2\n line3";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo("line1" + nl + "added" + nl + "line2" + nl + "line3"));
    }

    [Test]
    [Description("A hunk whose leading context line is a blank file line, but the diff text represents "
                 + "it as a bare empty line with no leading space marker (rather than a single space), "
                 + "must still anchor and apply correctly. A caller-authored diff routinely omits the "
                 + "marker on an otherwise-blank context line; treating that line as absent (rather than "
                 + "an implicit blank context line) desynchronizes the anchor search from the declared "
                 + "line number and produces a spurious 'content not found nearby' failure even though "
                 + "the content is exactly where declared — this reproduces a real failure hit live "
                 + "against RoslynSentinel.Tests.Advanced/BugFixTests.cs (see docs/TODO.md).")]
    public void ApplyDiff_HunkWithUnmarkedBlankContextLine_StillAnchorsCorrectly()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From(string.Join(nl,
            "}\";",
            "",
            "            SetSource(code, \"Service.cs\");",
            "",
            "            var result = await _refactoringEngine.SafeDeleteSymbolAsync(",
            "                \"Service.cs\",",
            "                contextSnippet: \"public string GetValue\",",
            "                lineBefore: null,",
            "                lineAfter: null);"));

        // Hunk body's first line is a bare empty string (no leading space marker) representing the
        // blank line before "SetSource(...)" — the exact malformation that triggered the bug.
        var diff = "@@ -2,4 +2,4 @@\n\n            SetSource(code, \"Service.cs\");\n\n-            var result = await _refactoringEngine.SafeDeleteSymbolAsync(\n+            var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync(";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Does.Contain("var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync("));
        Assert.That(newText, Does.Not.Contain("var result = await _refactoringEngine.SafeDeleteSymbolAsync("));
    }
}
