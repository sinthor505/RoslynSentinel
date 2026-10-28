using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C6-3 characterization tests for <c>MemberRefactoringEngine.ExtractMethodAsync</c>, whose extracted method and
/// standalone call-site snippet previously went through two whole-subtree whitespace helpers (since replaced by
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers). Pins the standalone <c>ExtractedMethodText</c> and
/// <c>CallSiteReplacement</c> snippets and the edited document for LF, CRLF and mixed-ending inputs.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
public class ExtractMethodNormalizeCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _engine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNavigationEngine, Battery.Basic.TestEngines.ValidationEngine(_workspaceManager));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private const string StrayLine = "    private   int _n;";

    // Lines 9-10 are selected: an irregularly spaced declaration and a single-line if block.
    private static readonly string[] Input =
    [
        "namespace TestProj;",
        "public class Calc",
        "{",
        StrayLine,
        "",
        "    public int Run(int a)",
        "    {",
        "        var x = a + 1;",
        "        var y   =  x*2;",
        "        if (y > 3) { y = y -1; }",
        "        return y;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "}",
        "",
    ];

    private static readonly string[] ComplexExpected =
    [
        "namespace TestProj;",
        "public class Calc",
        "{",
        StrayLine,
        "",
        "    public int Run(int a)",
        "    {",
        "        var x = a + 1;",
        "        var y = Doubled(x);",
        "        return y;",
        "    }",
        "",
        "    private int   Helper( ) { return  1; }",
        "",
        "    private int Doubled(int x)",
        "    {",
        "        var y = x * 2;",
        "        if (y > 3) { y = y - 1; }",
        "",
        "        return y;",
        "    }",
        "}",
        "",
    ];

    // Line 9 is selected: a plain statement.
    private static readonly string[] SimpleInput =
    [
        "namespace TestProj;",
        "public class Calc",
        "{",
        StrayLine,
        "",
        "    public int Run(int a)",
        "    {",
        "        var x = a + 1;",
        "        var y = x * 2;",
        "        return y;",
        "    }",
        "}",
        "",
    ];

    private static string Build(string eol, string[] lines, bool stray = false)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append(stray && lines[i] == StrayLine ? (eol == "\n" ? "\r\n" : "\n") : eol);
            }
        }

        return sb.ToString();
    }

    private async Task<ExtractMethodResult> ExtractAsync(string text, int startLine, string startText, int endLine, string endText)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", text)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ExtractMethodAsync("Test.cs", startLine, startText, endLine, endText, "Doubled");
        Assert.That(result.IsError, Is.False, result.ErrorMessage);
        return result;
    }

    private Task<ExtractMethodResult> ExtractComplexAsync(string text) =>
        ExtractAsync(text, 9, "var y   =  x*2;", 10, "if (y > 3) { y = y -1; }");

    private Task<ExtractMethodResult> ExtractSimpleAsync(string text) =>
        ExtractAsync(text, 9, "var y = x * 2;", 9, "var y = x * 2;");

    // ---- standalone snippets: the call site and the extracted method as the result reports them

    [Test]
    public async Task ExtractMethod_SimpleSelection_LfDocument_SnippetsByteExact()
    {
        var result = await ExtractSimpleAsync(Build("\n", SimpleInput));

        Assert.That(result.CallSiteReplacement, Is.EqualTo("var y = Doubled(x);"));
        Assert.That(result.ExtractedMethodText, Is.EqualTo("private int Doubled(int x)\n{\n    var y = x * 2;\n    return y;\n}"));
    }

    [Test]
    public async Task ExtractMethod_SimpleSelection_CrlfDocument_SnippetsCurrentEnding()
    {
        var result = await ExtractSimpleAsync(Build("\r\n", SimpleInput));

        Assert.That(result.CallSiteReplacement, Is.EqualTo("var y = Doubled(x);"));
        Assert.That(result.ExtractedMethodText, Is.EqualTo("private int Doubled(int x)\r\n{\r\n    var y = x * 2;\r\n    return y;\r\n}"));
    }

    [Test]
    public async Task ExtractMethod_ComplexSelection_LfDocument_SnippetsByteExact()
    {
        var result = await ExtractComplexAsync(Build("\n", Input));

        Assert.That(result.CallSiteReplacement, Is.EqualTo("var y = Doubled(x);"));
        Assert.That(result.ExtractedMethodText, Is.EqualTo("private int Doubled(int x)\n{\n    var y = x * 2;\n    if (y > 3) { y = y - 1; }\n\n    return y;\n}"));
    }

    // ---- the edited document

    [Test]
    public async Task ExtractMethod_ComplexSelection_LfDocument_UpdatedSourceByteExact()
    {
        var result = await ExtractComplexAsync(Build("\n", Input));

        Assert.That(result.UpdatedSourceContent, Is.EqualTo(Build("\n", ComplexExpected)));
    }

    [Test]
    public async Task ExtractMethod_ComplexSelection_CrlfDocument_UpdatedSourceIsLfResultInCrlf()
    {
        var lf = await ExtractComplexAsync(Build("\n", Input));

        var crlf = await ExtractComplexAsync(Build("\r\n", Input));

        Assert.That(crlf.UpdatedSourceContent, Is.EqualTo(lf.UpdatedSourceContent!.Replace("\n", "\r\n")));
    }

    [Test]
    public async Task ExtractMethod_ComplexSelection_MixedDocument_StrayCrlfLineKeepsItsTerminator()
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        var lf = await ExtractComplexAsync(Build("\n", Input));

        var mixed = await ExtractComplexAsync(Build("\n", Input, stray: true));

        Assert.That(mixed.UpdatedSourceContent, Is.EqualTo(lf.UpdatedSourceContent!.Replace(StrayLine + "\n", StrayLine + "\r\n")));
    }
}
