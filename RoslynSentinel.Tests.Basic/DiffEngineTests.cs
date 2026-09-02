using System.Linq;

using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class DiffEngineTests
{
    private DiffEngine _diffEngine = null!;

    [SetUp]
    public void Setup()
    {
        var workspaceManager = new FakeWorkspaceManager();
        _diffEngine = new DiffEngine();
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
    public void ApplyDiff_PredominantlyLfFile_DoesNotReflowUntouchedLinesToCrlf()
    {
        // Regression for docs/TODO.md's "ApplyDiff reflows far more of the file" entry: a file
        // that's all-LF except this fix's own machinery must stay all-LF after a small, targeted
        // hunk — not have every untouched line forced to CRLF just because the join separator was
        // previously chosen once for the whole file based on whether "\r\n" appeared anywhere.
        var oldText = SourceText.From("line1\nline2\nline3\nline4");
        var diff = "@@ -2,1 +2,2 @@\n line2\n+added\n line3";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo("line1\nline2\nadded\nline3\nline4"));
    }

    [Test]
    public void ApplyDiff_MixedLineEndings_PreservesEachSurvivingLinesOwnEnding()
    {
        // A file with genuinely mixed endings (routine after a partial manual edit or a merge)
        // must keep each untouched line's own ending rather than being normalized to one
        // convention. Only the newly-inserted line is expected to take the file's dominant ending.
        var oldText = SourceText.From("line1\r\nline2\nline3\r\nline4");
        var diff = "@@ -2,1 +2,2 @@\n line2\n+added\n line3";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        // line1's own \r\n, line2's own \n, "added" gets the dominant ending (\r\n — 2 vs 1), then
        // line3's own \r\n, then line4 (last line, originally no trailing newline) stays bare.
        Assert.That(newText, Is.EqualTo("line1\r\nline2\nadded\r\nline3\r\nline4"));
    }

    [Test]
    public void ApplyDiff_NoTrailingNewline_InsertionAfterLastLine_AddsRealSeparator()
    {
        // The original last line has no trailing newline (common for a file saved without one). If
        // a hunk appends a new line after it, the formerly-last line needs a real separator now
        // that it's no longer last — otherwise the two lines would run together with no break.
        var oldText = SourceText.From("line1\nline2");
        var diff = "@@ -2,1 +2,2 @@\n line2\n+added";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo("line1\nline2\nadded"));
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
    public void ApplyDiff_LastHunk_TrailingNewlineAfterFinalContextLine_DoesNotPhantomAnchor()
    {
        // Regression for a real model-eval failure (SizeThreshold n60 transcript,
        // docs/current/blockers/blocking_error_searchmode_literal_override_and_iserror_flag.md):
        // the diff text's own trailing newline after the final hunk's last body line was being
        // read by ReadHunkBody as one more (empty) body line, which IsContextOrRemovalLine then
        // treated as an implicit blank context line. ReanchorHunk then required a blank line
        // immediately after "line5" that the real file doesn't have there — defeating an
        // otherwise-exact reanchor match at every position in the search window, even though
        // hunk 1 was purely additive and hunk 2's real content sat exactly where the shifted
        // offset predicted.
        var oldText = SourceText.From(
            "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n" +
            "target1\ntarget2\ntarget3\nafter1\n");
        // Hunk 1 inserts 2 lines after line1 (purely additive). Hunk 2's declared old-start (11)
        // is stale by exactly the 2-line shift hunk 1 introduces, so the exact match at the
        // recomputed declared line should succeed — the phantom trailing "" must not be
        // included as a 4th anchor line requiring a blank line after "target3" that isn't there.
        var diff = "@@ -1,1 +1,3 @@\n line1\n+ins1\n+ins2\n" +
                    "@@ -11,3 +13,3 @@\n target1\n target2\n-target3\n+target3-changed\n";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Does.Contain("target3-changed"));
        Assert.That(newText, Does.Not.Contain("target3\nafter1"));
    }

    [Test]
    public void ApplyDiff_LastHunk_GenuineBlankContextLineAtEnd_StillMatches()
    {
        // A hunk whose real last body line is an intentional blank context line (marked with a
        // leading space, the normal encoding) must still anchor correctly — the fix for the
        // phantom-trailing-newline case above must not eat a genuine blank context line.
        var oldText = SourceText.From("line1\nline2\nline3\n\nline5\n");
        var diff = "@@ -2,3 +2,4 @@\n line2\n+added\n line3\n \n";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo("line1\nline2\nadded\nline3\n\nline5\n"));
    }

    [Test]
    public void ApplyDiff_Size60ModelEvalTranscript_ReplaysRecordedFailingDiff()
    {
        // Full end-to-end reproduction of the exact transcript that surfaced the phantom-anchor
        // bug: a 60-padding-method fixture plus the model's real two-hunk diff (hunk 1 inserts a
        // private helper after the class's opening brace; hunk 2 rewires one call site 329 lines
        // later). Recorded failure: "hunk '@@ -320,7 +329,7 @@' declares line 335, but its
        // content wasn't found there or within 60 lines in either direction."
        var sb = new System.Text.StringBuilder();
        sb.Append("namespace ContosoOrders.Core.FixtureHelpers;\n\n");
        sb.Append("public class BlockConverter\n{\n");
        sb.Append("    private readonly object _unrelatedField = new();\n\n");
        for (var i = 0; i < 60; i++)
        {
            sb.Append($"    public string UnrelatedMethod{i}(  int {(i % 2 == 0 ? " " : "")} value  )\n    {{\n            return $\"unrelated-{i}-{{value}}\";\n    }}\n\n");
        }
        sb.Append("""
                /// <summary>
                /// Converts a "public abstract class Name { ... }" block into a "public interface IName
                /// { ... }" block by rewriting its header and stripping method bodies down to
                /// semicolons. BUG: rebuilds the whole file's text via ReformatWholeFile(), which
                /// re-indents every line in the file, not just the converted block.
                /// </summary>
                public string ConvertAbstractClassToInterface(string fileText, string className)
                {
                    var oldHeader = $"public abstract class {className}";
                    if (!fileText.Contains(oldHeader, StringComparison.Ordinal))
                    {
                        return fileText;
                    }

                    var newHeader = $"public interface I{className}";
                    var rewritten = fileText.Replace(oldHeader, newHeader, StringComparison.Ordinal);
                    return ReformatWholeFile(rewritten);
                }

                private static string ReformatWholeFile(string fileText)
                {
                    var lines = fileText.Split('\n');
                    var normalized = lines.Select(line => line.TrimEnd());
                    return string.Join("\n", normalized);
                }


            """);
        sb.Append("    public string UnrelatedMethodAfter(  string   s  )\n");
        sb.Append("    {\n");
        sb.Append("            return s?.Trim() ?? \"\";\n");
        sb.Append("    }\n");
        sb.Append("}\n");

        var oldText = SourceText.From(sb.ToString());

        var diff =
            "@@ -3,6 +3,15 @@ namespace ContosoOrders.Core.FixtureHelpers;\n" +
            "\n" +
            " public class BlockConverter\n" +
            " {\n" +
            "+    private static string ReplaceBlockFormatted(string fileText, string oldBlock, string newBlock)\n" +
            "+    {\n" +
            "+        var index = fileText.IndexOf(oldBlock, StringComparison.Ordinal);\n" +
            "+        if (index < 0)\n" +
            "+        {\n" +
            "+            throw new InvalidOperationException(\"oldBlock not found in fileText.\");\n" +
            "+        }\n" +
            "+\n" +
            "+        var lineStart = fileText.LastIndexOf('\\n', Math.Max(0, index - 1)) + 1;\n" +
            "+        var indent = fileText[lineStart..index];\n" +
            "+\n" +
            "+        var formattedNewBlock = string.Join(\"\\n\" + indent, newBlock.Split('\\n').Select(line => line.TrimEnd()));\n" +
            "+\n" +
            "+        return fileText[..index] + formattedNewBlock + fileText[(index + oldBlock.Length)..];\n" +
            "+    }\n" +
            "     private readonly object _unrelatedField = new();\n" +
            "\n" +
            "@@ -320,7 +329,7 @@ namespace ContosoOrders.Core.FixtureHelpers;\n" +
            "         var newHeader = $\"public interface I{className}\";\n" +
            "         var rewritten = fileText.Replace(oldHeader, newHeader, StringComparison.Ordinal);\n" +
            "-        return ReformatWholeFile(rewritten);\n" +
            "+        return ReplaceBlockFormatted(fileText, oldHeader, newHeader);\n";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Does.Contain("private static string ReplaceBlockFormatted"));
        Assert.That(newText, Does.Contain("return ReplaceBlockFormatted(fileText, oldHeader, newHeader);"));
        Assert.That(newText, Does.Not.Contain("return ReformatWholeFile(rewritten);"));
    }

    [Test]
    public async Task ValidateProposedDiff_ShouldReturnNoErrors_ForValidDiff()
    {
        var source = "public class C { public void M() {} }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("C.cs", source) });
        var workspaceManager = new FakeWorkspaceManager();
        workspaceManager.SetTestSolution(solution);

        var diffEngine = new DiffEngine();
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
        var workspaceManager = new FakeWorkspaceManager();
        workspaceManager.SetTestSolution(solution);

        var diffEngine = new DiffEngine();
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
    [Description("A hunk whose context lines span BOTH sides of a +-only insertion block must reanchor "
                 + "successfully when every anchor line (leading and trailing) is valid at the shifted "
                 + "position — not just the leading ones. Regression for a live DiffApplyFailed hit during "
                 + "the 2026-09-01 external-drift-hard-blocker session: see docs/current/blockers.")]
    public void ApplyDiff_ContextSpansBothSidesOfInsertionBlock_ReanchorsUsingAllAnchorLines()
    {
        var nl = Environment.NewLine;
        // Real file has 5 extra lines inserted above (by an earlier, unrelated hunk) compared to
        // what this hunk's author assumed, shifting everything below down by 5.
        var oldText = SourceText.From(string.Join(nl,
            "// extra 1", "// extra 2", "// extra 3", "// extra 4", "// extra 5",
            "    while (x) { }", "}", "", "existing member"));

        // Diff was authored believing "while (x) { }" was at line 1; it's actually at line 6.
        // Context lines appear both BEFORE the +-only insertion block ("while...", "}", blank) and
        // AFTER it ("existing member") — both sides must be checked at the same shifted position.
        var diff = "@@ -1,3 +1,10 @@\n     while (x) { }\n }\n \n+    public void NewMember()\n+    {\n+    }\n+\n existing member";

        var newText = _diffEngine.ApplyDiff(oldText, diff).ToString();

        Assert.That(newText, Is.EqualTo(string.Join(nl,
            "// extra 1", "// extra 2", "// extra 3", "// extra 4", "// extra 5",
            "    while (x) { }", "}", "",
            "    public void NewMember()", "    {", "    }", "",
            "existing member")));
    }

    [Test]
    [Description("When a hunk's trailing context (after a +-only insertion block) is stale — e.g. copied "
                 + "from a different part of the file — the error must name the actual divergence point, "
                 + "not just the hunk's first anchor line, which would misleadingly suggest the start "
                 + "position itself is wrong when it's actually correct.")]
    public void ApplyDiff_TrailingContextAfterInsertionIsStale_ErrorNamesActualDivergencePoint()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From(string.Join(nl,
            "    while (x) { }", "}", "", "existing member", "// unrelated trailer"));

        // Leading context ("while...", "}", blank) is correct, but the trailing context line after
        // the +-only block is stale — "wrong trailer" doesn't exist anywhere in the file.
        var diff = "@@ -1,3 +1,10 @@\n     while (x) { }\n }\n \n+    public void NewMember()\n+    {\n+    }\n+\n wrong trailer";

        var ex = Assert.Throws<DiffApplyException>(() => _diffEngine.ApplyDiff(oldText, diff));
        Assert.That(ex!.Message, Does.Contain("Matched the first 3 anchor line(s)"));
        Assert.That(ex.Message, Does.Contain("wrong trailer"));
        Assert.That(ex.Message, Does.Contain("stale"));
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

    [Test(Description =
        "Reproduces the silent-no-op-with-false-success bug: a hunk header of bare \"@@\" (no " +
        "-oldStart,oldCount +newStart,newCount numbers) matched none of the header regexes, so the " +
        "entire hunk — including every \"+\" line — was skipped as ordinary body text with no error " +
        "and no content changed. The caller (ApplyProposedChangesAsync) then saw newContent == " +
        "preImage and reported a clean no-op success, even though the requested insertion never " +
        "happened. See docs/current/blockers/blocking_error_applydiff_silent_noop_false_success.md.")]
    public void ApplyDiff_BareAtAtHeaderWithNoLineNumbers_ThrowsInsteadOfSilentNoOp()
    {
        var nl = Environment.NewLine;
        var oldText = SourceText.From(string.Join(nl,
            "[JsonConverter(typeof(JsonStringEnumConverter))]",
            "public enum BuildVerifyLevel",
            "{",
            "    noBuild, quickBuild, fullBuild",
            "}",
            "",
            "// ── Content hashing ───────────────────────────────────────────────────────────"));

        var diff = "@@\n [JsonConverter(typeof(JsonStringEnumConverter))]\n public enum BuildVerifyLevel\n {\n     noBuild, quickBuild, fullBuild\n }\n \n+// ── RunTest ───────────────────────────────────────────────────────────────────\n+\n+public enum TestOutcome\n+{\n+    Passed, Failed, Skipped, NotExecuted\n+}\n+\n+[JsonConverter(typeof(JsonStringEnumConverter))]\n+public enum TestResultsFilter\n+{\n+    all, failed, skipped\n+}\n+\n // ── Content hashing ───────────────────────────────────────────────────────────";

        var ex = Assert.Throws<DiffApplyException>(() => _diffEngine.ApplyDiff(oldText, diff));
        Assert.That(ex!.Message, Does.Contain("Malformed hunk header"));
    }
}
