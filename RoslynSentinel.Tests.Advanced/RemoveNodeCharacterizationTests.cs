using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C4a characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for the three private "remove a node and format its container" copies
/// (<c>SemanticRefactoringEngine.InlineVariableAsync</c> unused-variable path,
/// <c>StructuralRefinementEngine.SafeDeleteSymbolAsync</c>, <c>StructuralRefactoringEngine.InlineFieldAsync</c>)
/// run against LF and CRLF documents with comments and blank lines around the removed node, so a later
/// consolidation onto <c>RoslynFormattingHelper.RemoveNodePreservingTriviaAsync</c> shows exactly what changed.
/// </summary>
[TestFixture]
[Category("StructuralRefinementEngine")] // sentinel:auto-category
public class RemoveNodeCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private SemanticRefactoringEngine _semanticEngine;
    private StructuralRefinementEngine _refinementEngine;
    private StructuralRefactoringEngine _structuralEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _semanticEngine = new SemanticRefactoringEngine(_workspaceManager);
        _refinementEngine = new StructuralRefinementEngine(_workspaceManager, new SentinelConfiguration());
        _structuralEngine = new StructuralRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>Joins lines with LF (no trailing newline), then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    // ---------------------------------------------------------------- InlineVariable (unused local removal)

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineVariable_UnusedMiddleStatement_CommentsAndBlankLines_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        var a = 1;",
            "",
            "        // before removed",
            "        var unused = 2; // trailing",
            "",
            "        // after removed",
            "        var b = 3;",
            "        System.Console.WriteLine(a + b);",
            "    }",
            "}"));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "unused");

        Assert.That(result, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        var a = 1;",
            "",
            "        // after removed",
            "        var b = 3;",
            "        System.Console.WriteLine(a + b);",
            "    }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineVariable_UnusedFirstAndLastStatement_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        // first",
            "        var unused = 2;",
            "        var b = 3;",
            "        System.Console.WriteLine(b);",
            "    }",
            "",
            "    void N()",
            "    {",
            "        var c = 3;",
            "        System.Console.WriteLine(c);",
            "        var unusedLast = 4; // last",
            "    }",
            "}"));

        var first = await _semanticEngine.InlineVariableAsync("Test.cs", "unused");
        Assert.That(first, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        var b = 3;",
            "        System.Console.WriteLine(b);",
            "    }",
            "",
            "    void N()",
            "    {",
            "        var c = 3;",
            "        System.Console.WriteLine(c);",
            "        var unusedLast = 4; // last",
            "    }",
            "}")));

        var last = await _semanticEngine.InlineVariableAsync("Test.cs", "unusedLast");
        Assert.That(last, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        // first",
            "        var unused = 2;",
            "        var b = 3;",
            "        System.Console.WriteLine(b);",
            "    }",
            "",
            "    void N()",
            "    {",
            "        var c = 3;",
            "        System.Console.WriteLine(c);",
            "    }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineVariable_UnusedWithNonCanonicalUntouchedSibling_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        int  y=2;",
            "        var unused = 1;",
            "      System.Console.WriteLine( y );",
            "    }",
            "}"));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "unused");

        // The shared helper puts every changed line in the document's dominant ending, so an LF document stays
        // pure LF (the former private copy came back with a stray CRLF between the re-indented statements).
        var expected = Src(crlf, "public class C", "{", "    void M()", "    {", "        int y = 2;", "        System.Console.WriteLine(y);", "    }", "}");
        Assert.That(result, Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineVariable_UnusedInsideIfDirective_KeepsUnbalancedDirective_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        var keep = 1;",
            "#region R",
            "        var unused = 2;",
            "#endregion",
            "        System.Console.WriteLine(keep);",
            "    }",
            "}"));

        var result = await _semanticEngine.InlineVariableAsync("Test.cs", "unused");

        // Pinned current behaviour: the kept directive is re-indented and a blank line is inserted before it.
        // The inserted blank line takes the document's dominant ending (the former private copy wrote a stray CRLF).
        var expected = Src(crlf,
            "public class C",
            "{",
            "    void M()",
            "    {",
            "        var keep = 1;",
            "",
            "        #region R",
            "        #endregion",
            "        System.Console.WriteLine(keep);",
            "    }",
            "}");

        Assert.That(result, Is.EqualTo(expected));
    }

    // ---------------------------------------------------------------- SafeDeleteSymbol

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_UnusedMiddleMethod_DocCommentAndBlankLines_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    public void A() { }",
            "",
            "    /// <summary>Unused.</summary>",
            "    private void Unused() { } // trailing",
            "",
            "    public void B() { }",
            "}"));

        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", "Unused", "private void Unused()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    public void A() { }",
            "",
            "    public void B() { }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_UnusedFirstMethodWithComment_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    // about Unused",
            "    private void Unused() { }",
            "",
            "    public void B() { }",
            "}"));

        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", "Unused", "private void Unused()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "",
            "    public void B() { }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_UnusedLastMethodWithComment_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    public void B() { }",
            "",
            "    // about Unused",
            "    private void Unused() { }",
            "}"));

        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", "Unused", "private void Unused()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    public void B() { }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_UnusedMethodWithNonCanonicalUntouchedSibling_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    public   void  A( ) { }",
            "",
            "    private void Unused() { }",
            "",
            "      public void B(){ }",
            "}"));

        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", "Unused", "private void Unused()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        // Untouched siblings are reformatted, and every changed line takes the document's dominant ending, so an LF
        // document stays pure LF (the former private copy came back with a stray CRLF).
        var expected = Src(crlf, "public class C", "{", "    public void A() { }", "", "    public void B() { }", "}");
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_UnusedTopLevelClassWithComment_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class Keep { }",
            "",
            "// about Unused",
            "public class Unused { }",
            "",
            "public class AlsoKeep { }"));

        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", "Unused", "public class Unused", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class Keep { }",
            "",
            "public class AlsoKeep { }")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_ByLineColumn_UnusedMethod_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    public void A() { }",
            "",
            "    private void Unused() { }",
            "",
            "    public void B() { }",
            "}"));

        // Line 5, column 18 is the identifier "Unused".
        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", 5, 18);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    public void A() { }",
            "",
            "    public void B() { }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeDelete_UnusedMethodInsideIfDirective_KeepsUnbalancedDirective_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    public void A() { }",
            "#region R",
            "    private void Unused() { }",
            "#endregion",
            "    public void B() { }",
            "}"));

        var result = await _refinementEngine.SafeDeleteSymbolAsync("Test.cs", "Unused", "private void Unused()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);

        // Pinned current behaviour: the kept directive is re-indented and a blank line is inserted before it.
        // The inserted blank line takes the document's dominant ending (the former private copy wrote a stray CRLF).
        var expected = Src(crlf,
            "public class C",
            "{",
            "    public void A() { }",
            "",
            "    #region R",
            "    #endregion",
            "    public void B() { }",
            "}");

        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }

    // ---------------------------------------------------------------- InlineField

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineField_CanonicalValue_CommentAndBlankLinesAroundField_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    private int _a = 1;",
            "",
            "    // the label",
            "    private string _label = \"x\"; // trailing",
            "",
            "    public string M()",
            "    {",
            "        return _label;",
            "    }",
            "}"), "Service.cs");

        var result = await _structuralEngine.InlineFieldAsync("Service.cs", "_label");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "    private int _a = 1;",
            "",
            "    public string M()",
            "    {",
            "        return \"x\";",
            "    }",
            "}")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InlineField_UnformattedValueIsFormattedByContainerPass_ByteExact(bool crlf)
    {
        SetSource(Src(crlf,
            "public class C",
            "{",
            "    private int _f = 1+2;",
            "",
            "    public int M()",
            "    {",
            "        return _f;",
            "    }",
            "}"), "Service.cs");

        var result = await _structuralEngine.InlineFieldAsync("Service.cs", "_f");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public class C",
            "{",
            "",
            "    public int M()",
            "    {",
            "        return 1 + 2;",
            "    }",
            "}")));
    }
}
