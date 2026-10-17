using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Characterization tests for <c>StructuralRefactoringEngine.MoveTypeToOuterScopeAsync</c> (docs/current/TODO.md,
/// "MoveTypeToOuterScope data loss"; whitespace initiative C5b-2d). Three source shapes (block namespace, file-scoped
/// namespace, no namespace) x LF / CRLF / one stray CRLF in an LF document. The original bug removed the nested type
/// and never re-added it at the outer scope, so every test here first asserts the type survives; the byte-exact pins
/// then fix the surrounding text. The source carries irregular spacing and a comment in code the engine does not
/// touch, so a regression that formats the whole document shows up as a diff.
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")] // sentinel:auto-category
public class MoveTypeToOuterScopeFormatCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private StructuralRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new StructuralRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>Joins lines with LF, then converts to CRLF when requested.</summary>
    private static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    // One CRLF terminator on an untouched line of an LF document: the last character of the anchor.
    private const string StrayAnchor = "    private   int _other;\n";

    private static string WithStrayCrlf(string lfText)
    {
        Assert.That(lfText, Does.Contain(StrayAnchor), "Precondition: the anchor line is present.");
        var mixed = lfText.Replace(StrayAnchor, StrayAnchor.Substring(0, StrayAnchor.Length - 1) + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    private async Task<string> MoveAsync()
    {
        var result = await _engine.MoveTypeToOuterScopeAsync("Test.cs", "Inner");
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        return result.UpdatedText!;
    }

    // ---- shape 1: no namespace ----

    private static string[] GlobalInput() =>
    [
        "public class Outer",
        "{",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "    public class Inner",
        "    {",
        "        public int   Value;",
        "    }",
        "",
        "    public void Go() { var y =  new Inner(); }",
        "}",
        "",
    ];

    private static string[] GlobalExpected() =>
    [
        "public class Outer",
        "{",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "    public void Go() { var y =  new Inner(); }",
        "}",
        "",
        "public class Inner",
        "{",
        "    public int Value;",
        "}",
        "",
    ];

    // ---- shape 2: block namespace ----

    private static string[] BlockNsInput() =>
    [
        "namespace   N",
        "{",
        "    public class Outer",
        "    {",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "        public class Inner",
        "        {",
        "            public int   Value;",
        "        }",
        "",
        "        public void Go() { var y =  new Inner(); }",
        "    }",
        "}",
        "",
    ];

    private static string[] BlockNsExpected() =>
    [
        "namespace   N",
        "{",
        "    public class Outer",
        "    {",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "        public void Go() { var y =  new Inner(); }",
        "    }",
        "",
        "    public class Inner",
        "    {",
        "        public int Value;",
        "    }",
        "}",
        "",
    ];

    // ---- shape 3: file-scoped namespace ----

    private static string[] FileScopedInput() =>
    [
        "namespace N;",
        "",
        "public class Outer",
        "{",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "    public class Inner",
        "    {",
        "        public int   Value;",
        "    }",
        "",
        "    public void Go() { var y =  new Inner(); }",
        "}",
        "",
    ];

    private static string[] FileScopedExpected() =>
    [
        "namespace N;",
        "",
        "public class Outer",
        "{",
        "    // keep   this   comment",
        "    private   int _other;",
        "",
        "    public void Go() { var y =  new Inner(); }",
        "}",
        "",
        "public class Inner",
        "{",
        "    public int Value;",
        "}",
        "",
    ];

    private static IEnumerable<(string[] input, string[] expected)> Shapes()
    {
        yield return (GlobalInput(), GlobalExpected());
        yield return (BlockNsInput(), BlockNsExpected());
        yield return (FileScopedInput(), FileScopedExpected());
    }

    public static IEnumerable<TestCaseData> ShapeCases(bool includeCrlf)
    {
        var names = new[] { "NoNamespace", "BlockNamespace", "FileScopedNamespace" };
        var i = 0;
        foreach (var (input, expected) in Shapes())
        {
            yield return new TestCaseData(input, expected, false).SetName($"{{m}}({names[i]},LF)");
            if (includeCrlf)
                yield return new TestCaseData(input, expected, true).SetName($"{{m}}({names[i]},CRLF)");
            i++;
        }
    }

    public static IEnumerable<TestCaseData> AllCases() => ShapeCases(true);
    public static IEnumerable<TestCaseData> LfCases() => ShapeCases(false);

    [TestCaseSource(nameof(AllCases))]
    public async Task MoveTypeToOuterScope_TypeSurvivesAtOuterScope(string[] input, string[] expected, bool crlf)
    {
        SetSource(Src(crlf, input));

        var text = await MoveAsync();

        var root = CSharpSyntaxTree.ParseText(text).GetRoot();
        var inner = root.DescendantNodes().OfType<TypeDeclarationSyntax>().SingleOrDefault(t => t.Identifier.Text == "Inner");
        Assert.That(inner, Is.Not.Null, "Inner must still exist after the move (data loss otherwise).");
        Assert.That(inner!.Ancestors().OfType<TypeDeclarationSyntax>(), Is.Empty, "Inner must no longer be nested in a type.");
        Assert.That(root.DescendantNodes().OfType<FieldDeclarationSyntax>().Count(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "Value")), Is.EqualTo(1), "Inner's body must survive.");
    }

    [TestCaseSource(nameof(AllCases))]
    public async Task MoveTypeToOuterScope_ByteExact(string[] input, string[] expected, bool crlf)
    {
        SetSource(Src(crlf, input));

        Assert.That(await MoveAsync(), Is.EqualTo(Src(crlf, expected)));
    }

    [TestCaseSource(nameof(LfCases))]
    public async Task MoveTypeToOuterScope_LfDocument_ResultHasNoCarriageReturn(string[] input, string[] expected, bool crlf)
    {
        SetSource(Src(false, input));

        Assert.That(await MoveAsync(), Does.Not.Contain("\r"));
    }

    [TestCaseSource(nameof(AllCases))]
    public async Task MoveTypeToOuterScope_CrlfDocument_ResultHasNoBareLineFeed(string[] input, string[] expected, bool crlf)
    {
        if (!crlf)
            Assert.Pass("LF case: covered by the no-carriage-return test.");
        SetSource(Src(true, input));

        Assert.That((await MoveAsync()).Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [TestCaseSource(nameof(LfCases))]
    public async Task MoveTypeToOuterScope_MixedEol_StrayCrlfLineKeepsItsTerminator(string[] input, string[] expected, bool crlf)
    {
        SetSource(WithStrayCrlf(Src(false, input)));

        Assert.That(await MoveAsync(), Is.EqualTo(WithStrayCrlf(Src(false, expected))));
    }

    [Test]
    public async Task MoveTypeToOuterScope_TopLevelType_IsReportedAsAlreadyOuter()
    {
        SetSource(Src(false, GlobalInput()));

        var result = await _engine.MoveTypeToOuterScopeAsync("Test.cs", "Outer");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.That(result.UpdatedText, Is.Null);
    }
}
