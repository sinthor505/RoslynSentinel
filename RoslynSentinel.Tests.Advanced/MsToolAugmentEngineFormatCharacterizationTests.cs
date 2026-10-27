using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5b-2c characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for three span-scoped <c>Formatter.FormatAsync(doc, annotation)</c> sites in
/// <see cref="MsToolAugmentEngine"/> routed onto <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>:
/// <c>EncapsulateFieldSafeAsync</c>, <c>SortAndDeduplicateUsingsAsync</c> (called with <c>writeToFile: false</c>)
/// and <c>ExtractMethodSafeAsync</c>. The source carries irregular spacing, comments and blank lines in code the
/// engine does not touch, so a regression that formats the whole document shows up as a diff. Written against
/// the unmodified calls (plain <c>Formatter.FormatAsync(doc, annotation)</c>). The CRLF tests are byte-identical
/// to the pre-routing behaviour; the LF and mixed-EOL tests encode the corrected output (the old code leaked
/// hard-coded or formatter-inserted CRLF into LF documents, or flattened a stray CRLF line to LF).
/// <para>
/// Exception: the <c>ExtractMethod</c> CRLF pin is NOT byte-identical to the pre-routing behaviour. That engine
/// hard-codes LF trivia on the call statement and on the new method, so a CRLF document came back with two bare
/// LF terminators; the pin (and the no-bare-LF test) encode the corrected all-CRLF output. The Encapsulate and
/// SortUsings CRLF pins are unchanged by the routing.
/// </para>
/// </summary>
[TestFixture]
[Category("MsToolAugmentEngine")] // sentinel:auto-category
public class MsToolAugmentEngineFormatCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new MsToolAugmentEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetFiles(params (string name, string content)[] files) =>
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", files));

    /// <summary>Joins lines with LF (a trailing newline only if the last line is empty), then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    /// <summary>An LF text with exactly one CRLF terminator: the last character of <paramref name="anchor"/>.</summary>
    private static string WithStrayCrlf(string lfText, string anchor)
    {
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        Assert.That(lfText.Split(anchor).Length, Is.EqualTo(2), "Precondition: the anchor line is unique.");
        var mixed = lfText.Replace(anchor, anchor.Substring(0, anchor.Length - 1) + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    // ================================================================ EncapsulateFieldSafeAsync

    private static string[] EncapsulateInput() =>
    [
        "public class C",
        "{",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "    public int   Count;",
        "",
        "    public int Use() { return  Count  +  1; }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    // Pre-existing quirk pinned as-is (not part of this slice): the backing field and the property are separated by
    // two blank lines (the property carries two leading line breaks on top of the backing field's own).
    private static string[] EncapsulateExpected() =>
    [
        "public class C",
        "{",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "    private int _count;",
        "",
        "",
        "    public int Count { get => _count; set => _count = value; }",
        "",
        "    public int Use() { return  _count  +  1; }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private async Task<string> EncapsulateAsync()
    {
        var result = await _engine.EncapsulateFieldSafeAsync("Test.cs", "Count");
        Assert.That(result.IsError, Is.False, result.Error);
        return result.UpdatedContent!;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EncapsulateField_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Test.cs", Src(crlf, EncapsulateInput())));

        Assert.That(await EncapsulateAsync(), Is.EqualTo(Src(crlf, EncapsulateExpected())));
    }

    [Test]
    public async Task EncapsulateField_LfDocument_ResultHasNoCarriageReturn()
    {
        SetFiles(("Test.cs", Src(false, EncapsulateInput())));

        Assert.That(await EncapsulateAsync(), Does.Not.Contain("\r"));
    }

    [Test]
    public async Task EncapsulateField_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetFiles(("Test.cs", Src(true, EncapsulateInput())));

        Assert.That((await EncapsulateAsync()).Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on: a comment line far from the edit, the line before the field (its break is formatter-owned),
    // the blank line right before the field (leading trivia of the replaced node) and an untouched member. Not on
    // the `Use` line: its text changes (`Count` becomes `_count`), so it counts as a changed line and takes the
    // dominant ending by design (see FormatAnnotatedSpansAsync).
    [TestCase("    // keep   this   comment\n")]
    [TestCase("    private   int _other;\n")]
    [TestCase("    private   int _other;\n\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task EncapsulateField_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetFiles(("Test.cs", WithStrayCrlf(Src(false, EncapsulateInput()), anchor)));

        Assert.That(await EncapsulateAsync(), Is.EqualTo(WithStrayCrlf(Src(false, EncapsulateExpected()), anchor)));
    }

    // ================================================================ SortAndDeduplicateUsingsAsync

    private static string[] UsingsInput() =>
    [
        "// header   comment",
        "using System.Text;",
        "using   System;",
        "using System.Linq;",
        "using System;",
        "",
        "namespace   N",
        "{",
        "    public class   C { }",
        "}",
        "",
    ];

    private static string[] UsingsExpected() =>
    [
        "// header   comment",
        "using System;",
        "using System.Linq;",
        "using System.Text;",
        "",
        "namespace   N",
        "{",
        "    public class   C { }",
        "}",
        "",
    ];

    private async Task<string> SortUsingsAsync()
    {
        var result = await _engine.SortAndDeduplicateUsingsAsync("Test.cs", writeToFile: false);
        Assert.That(result.RemovedDuplicates, Is.EqualTo(1));
        return result.UpdatedContent;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SortUsings_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Test.cs", Src(crlf, UsingsInput())));

        Assert.That(await SortUsingsAsync(), Is.EqualTo(Src(crlf, UsingsExpected())));
    }

    [Test]
    public async Task SortUsings_LfDocument_ResultHasNoCarriageReturn()
    {
        SetFiles(("Test.cs", Src(false, UsingsInput())));

        Assert.That(await SortUsingsAsync(), Does.Not.Contain("\r"));
    }

    [Test]
    public async Task SortUsings_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetFiles(("Test.cs", Src(true, UsingsInput())));

        Assert.That((await SortUsingsAsync()).Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on: the header comment (leading trivia of the first using), the blank line after the usings, an
    // untouched namespace line and an untouched member line.
    [TestCase("// header   comment\n", "// header   comment\n")]
    [TestCase("using System;\n\n", "using System.Text;\n\n")]
    [TestCase("namespace   N\n", "namespace   N\n")]
    [TestCase("    public class   C { }\n", "    public class   C { }\n")]
    public async Task SortUsings_MixedEol_StrayCrlfLineKeepsItsTerminator(string inputAnchor, string expectedAnchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetFiles(("Test.cs", WithStrayCrlf(Src(false, UsingsInput()), inputAnchor)));

        Assert.That(await SortUsingsAsync(), Is.EqualTo(WithStrayCrlf(Src(false, UsingsExpected()), expectedAnchor)));
    }

    // ================================================================ ExtractMethodSafeAsync

    private static string[] ExtractInput() =>
    [
        "public class C",
        "{",
        "    // keep   this   comment",
        "    private   int _n;",
        "",
        "    public void Run(int a)",
        "    {",
        "        // before",
        "        _n  =  a  +  1;",
        "        _n  =  _n * 2;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    // Pre-existing quirk pinned as-is (not part of this slice): the new method lands after the last member behind
    // two blank lines; only the call statement and the new method are re-spaced, untouched code keeps its spacing.
    private static string[] ExtractExpected() =>
    [
        "public class C",
        "{",
        "    // keep   this   comment",
        "    private   int _n;",
        "",
        "    public void Run(int a)",
        "    {",
        "        // before",
        "        Step(a);",
        "        _n  =  _n * 2;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "",
        "",
        "    private void Step(int a)",
        "    {",
        "        _n = a + 1;",
        "    }",
        "}",
        "",
    ];

    private async Task<string> ExtractAsync()
    {
        var result = await _engine.ExtractMethodSafeAsync("Test.cs", "Step", "_n  =  a  +  1;");
        Assert.That(result.IsError, Is.False, result.Error);
        return result.UpdatedContent!;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExtractMethod_UntouchedCodeKeepsIrregularSpacing_ByteExact(bool crlf)
    {
        SetFiles(("Test.cs", Src(crlf, ExtractInput())));

        Assert.That(await ExtractAsync(), Is.EqualTo(Src(crlf, ExtractExpected())));
    }

    [Test]
    public async Task ExtractMethod_LfDocument_ResultHasNoCarriageReturn()
    {
        SetFiles(("Test.cs", Src(false, ExtractInput())));

        Assert.That(await ExtractAsync(), Does.Not.Contain("\r"));
    }

    [Test]
    public async Task ExtractMethod_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetFiles(("Test.cs", Src(true, ExtractInput())));

        Assert.That((await ExtractAsync()).Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on: a field line far from the edit, the method signature line before the annotated block, a
    // comment line inside the block, and an untouched member after the new method.
    [TestCase("    private   int _n;\n")]
    [TestCase("    public void Run(int a)\n")]
    [TestCase("        // before\n")]
    [TestCase("    private int   Helper( ) { return  1; }\n")]
    public async Task ExtractMethod_MixedEol_StrayCrlfLineKeepsItsTerminator(string anchor)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetFiles(("Test.cs", WithStrayCrlf(Src(false, ExtractInput()), anchor)));

        Assert.That(await ExtractAsync(), Is.EqualTo(WithStrayCrlf(Src(false, ExtractExpected()), anchor)));
    }

    // ================================================================ Stray bare LF in a CRLF document

    private static string WithStrayBareLf(string crlfText, string anchor)
    {
        Assert.That(crlfText.Split(anchor + "\r\n").Length, Is.EqualTo(2), "Precondition: the anchor line is unique.");
        var mixed = crlfText.Replace(anchor + "\r\n", anchor + "\n");
        Assert.That(mixed.Count(c => c == '\n') - mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray bare LF.");
        return mixed;
    }

    // Pinned as-is (known limitation): a stray bare LF on an unchanged comment line of a CRLF document survives; the
    // rest of the document keeps CRLF.
    [Test]
    public async Task EncapsulateField_CrlfDocument_StrayBareLf_LineKeepsItsTerminator()
    {
        // Changed lines take the resolved EOL, not the majority one; Crlf keeps this CRLF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Crlf;

        SetFiles(("Test.cs", WithStrayBareLf(Src(true, EncapsulateInput()), "    // keep   this   comment")));

        Assert.That(await EncapsulateAsync(), Is.EqualTo(WithStrayBareLf(Src(true, EncapsulateExpected()), "    // keep   this   comment")));
    }

    // Pinned as-is (known limitation): a stray bare LF on an unchanged comment line of a CRLF document survives; the
    // rest of the document keeps CRLF.
    [Test]
    public async Task SortUsings_CrlfDocument_StrayBareLf_LineKeepsItsTerminator()
    {
        // Changed lines take the resolved EOL, not the majority one; Crlf keeps this CRLF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Crlf;

        SetFiles(("Test.cs", WithStrayBareLf(Src(true, UsingsInput()), "// header   comment")));

        Assert.That(await SortUsingsAsync(), Is.EqualTo(WithStrayBareLf(Src(true, UsingsExpected()), "// header   comment")));
    }

    // Pinned as-is (known limitation): a stray bare LF on an unchanged field line of a CRLF document survives, and the
    // appended method keeps CRLF.
    [Test]
    public async Task ExtractMethod_CrlfDocument_StrayBareLf_LineKeepsItsTerminator()
    {
        // Changed lines take the resolved EOL, not the majority one; Crlf keeps this CRLF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Crlf;

        SetFiles(("Test.cs", WithStrayBareLf(Src(true, ExtractInput()), "    private   int _n;")));

        Assert.That(await ExtractAsync(), Is.EqualTo(WithStrayBareLf(Src(true, ExtractExpected()), "    private   int _n;")));
    }

    // ================================================================ Stray CRLF on a blank line inside the selection

    // Pinned as-is (helper-level behaviour, accepted 2026-10-25, finding #12): the formatter helper pairs blank lines by
    // text, so a stray CRLF on the blank line between two selected statements can land on a different blank line. This
    // only matters for a mixed-EOL input; under plan_eol_policy_and_write_chokepoint Step 5 the chokepoint converts the
    // whole file to the resolved EOL before writing, so the placement never reaches disk. Do not re-investigate.
    [Test]
    public async Task ExtractMethod_MixedEol_StrayCrlfOnBlankLineInsideSelection_MovesWithAppendedMethod()
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        var lines = new[]
        {
            "public class C",
            "{",
            "    public void Run(int a)",
            "    {",
            "        _n  =  a  +  1;",
            "",
            "        _n  =  _n * 2;",
            "    }",
            "}",
            "",
        };
        var text = Src(false, lines).Replace("        _n  =  a  +  1;\n\n", "        _n  =  a  +  1;\n\r\n");
        Assert.That(text.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF on the blank line.");
        SetFiles(("Test.cs", text));

        var result = await _engine.ExtractMethodSafeAsync("Test.cs", "Step", "_n  =  a  +  1;\n\n        _n  =  _n * 2;");

        var expected = Src(false, new[]
        {
            "public class C",
            "{",
            "    public void Run(int a)",
            "    {",
            "        Step(a);",
            "    }",
            "\r",
            "",
            "    private void Step(int a)",
            "    {",
            "        _n = a + 1;",
            "",
            "        _n = _n * 2;",
            "    }",
            "}",
            "",
        });
        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.UpdatedContent, Is.EqualTo(expected));
    }

    // ================================================================ CR-only and U+2028 line endings

    // Separator name "CR" = carriage return only; anything else = U+2028 LINE SEPARATOR.
    private static string Separator(string name) => name == "CR" ? ((char)0x0D).ToString() : ((char)0x2028).ToString();

    // CR-only and U+2028 separators: untouched lines keep their separator, changed lines take the resolved line ending
    // (CRLF here); the chokepoint converts the whole file on write.
    [TestCase("CR")]
    [TestCase("LS")]
    public async Task SortUsings_NonLfSeparator_ChangedLinesTakeResolvedEol(string separator)
    {
        _workspaceManager.EolModeOverride = EolMode.Crlf;
        SetFiles(("Test.cs", string.Join(Separator(separator), UsingsInput())));

        var expected = new[]
        {
            "// header   comment",
            "using System;",
            "using System.Linq;",
            "using System.Text;",
            "",
            "namespace   N",
            "{",
            "    public class   C { }",
            "}",
            "",
        };
        var actual = await SortUsingsAsync();
        Assert.That(actual.Replace("\r\n", Separator(separator)), Is.EqualTo(string.Join(Separator(separator), expected)), "content unchanged apart from the terminators of the changed lines");
        Assert.That(actual, Does.Contain("\r\n"), "changed lines take the resolved line ending");
    }

    // CR-only and U+2028 separators: untouched lines keep their separator, changed lines take the resolved line ending
    // (CRLF here, including the appended method); the chokepoint converts the whole file on write.
    [TestCase("CR")]
    [TestCase("LS")]
    public async Task ExtractMethod_NonLfSeparator_ChangedLinesTakeResolvedEol(string separator)
    {
        _workspaceManager.EolModeOverride = EolMode.Crlf;
        SetFiles(("Test.cs", string.Join(Separator(separator), ExtractInput())));

        var expected = new[]
        {
            "public class C",
            "{",
            "    // keep   this   comment",
            "    private   int _n;",
            "",
            "    public void Run(int a)",
            "    {",
            "        // before",
            "        Step(a);",
            "        _n  =  _n * 2;",
            "    }",
            "",
            "    private int   Helper( ) { return  1; }",
            "",
            "",
            "    private void Step(int a)",
            "    {",
            "        _n = a + 1;",
            "    }",
            "}",
            "",
        };
        var actual = await ExtractAsync();
        Assert.That(actual.Replace("\r\n", Separator(separator)), Is.EqualTo(string.Join(Separator(separator), expected)), "content unchanged apart from the terminators of the changed lines");
        Assert.That(actual, Does.Contain("\r\n"), "changed lines take the resolved line ending");
    }

    // ================================================================ Known trivia loss

    // Comments on usings survive the sort and dedup, attached to their using. The removed duplicate's own trivia goes with it.
    [Test]
    public async Task SortUsings_TrailingAndLeadingComments_DuplicateTrivia_CommentsSurvive()
    {
        var lines = new[]
        {
            "using System.Text; // trailing text",
            "// lead of linq",
            "using   System.Linq;",
            "// lead of dup",
            "using System; // trailing sys",
            "using   System;",
            "",
            "namespace   N",
            "{",
            "    public class   C { }",
            "}",
            "",
        };
        SetFiles(("Test.cs", Src(false, lines)));

        var result = await _engine.SortAndDeduplicateUsingsAsync("Test.cs", writeToFile: false);

        Assert.That(result.UpdatedContent, Is.EqualTo(Src(false, new[]
        {
            "// lead of dup",
            "using System; // trailing sys",
            "// lead of linq",
            "using System.Linq;",
            "using System.Text; // trailing text",
            "",
            "namespace   N",
            "{",
            "    public class   C { }",
            "}",
            "",
        })));
    }

    [Test]
    public async Task ExtractMethod_CommentBetweenSelectedStatements_IsKept()
    {
        var lines = new[]
        {
            "public class C",
            "{",
            "    public void Run(int a)",
            "    {",
            "        _n  =  a  +  1;",
            "        // between   the   statements",
            "        _n  =  _n * 2;",
            "    }",
            "}",
            "",
        };
        SetFiles(("Test.cs", Src(false, lines)));

        var result = await _engine.ExtractMethodSafeAsync("Test.cs", "Step", "_n  =  a  +  1;\n        // between   the   statements\n        _n  =  _n * 2;");

        Assert.That(result.IsError, Is.False, result.Error);
        Assert.That(result.UpdatedContent, Is.EqualTo(Src(false, new[]
        {
            "public class C",
            "{",
            "    public void Run(int a)",
            "    {",
            "        Step(a);",
            "    }",
            "",
            "",
            "    private void Step(int a)",
            "    {",
            "        _n = a + 1;",
            "        // between   the   statements",
            "        _n = _n * 2;",
            "    }",
            "}",
            "",
        })));
    }
}
