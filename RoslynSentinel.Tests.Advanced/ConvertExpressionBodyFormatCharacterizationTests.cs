using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5c-5 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <see cref="StructuralRefactoringEngine.ConvertExpressionBodyAsync"/> in both directions, for
/// methods and properties. The rewrite is routed onto <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c> so only
/// the converted member is formatted and line endings follow the pre-rewrite document.
/// <para>
/// Pre-migration behaviour (whole-document format) that these pins replaced: untouched irregular lines were re-spaced,
/// the blank line before the converted member was dropped, and LF documents received bare CRLF on the lines the rewrite
/// touched. CRLF output for well-formatted input was already byte-identical and stays so.
/// </para>
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")]
public class ConvertExpressionBodyFormatCharacterizationTests
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

    private void SetFiles(params (string name, string content)[] files) =>
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", files));

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

    private const string MethodToExpr = "method-to-expr";
    private const string MethodToBlock = "method-to-block";
    private const string PropToExpr = "prop-to-expr";
    private const string PropToBlock = "prop-to-block";

    private static readonly string[] AllSites = [MethodToExpr, MethodToBlock, PropToExpr, PropToBlock];

    // Untouched, badly formatted member placed in the same class as the converted member.
    private const string IrregularLine = "    public  int   Other( )   {   return   7; }";

    // A blank line separates the field from the converted member (leading trivia of the member).
    private static string[] Class(string[] member, bool irregular, bool blankBeforeMember)
    {
        var lines = new List<string> { "public class C", "{", "    private int _count;" };
        if (irregular)
        {
            lines.Add(IrregularLine);
        }

        if (blankBeforeMember)
        {
            lines.Add("");
        }

        lines.AddRange(member);
        lines.Add("}");
        lines.Add("");
        return lines.ToArray();
    }

    private static (string[] Input, string[] Expected) Lines(string site, bool irregular = false, bool blankBeforeMember = false)
    {
        var (inMember, outMember) = site switch
        {
            MethodToExpr =>
            (
                new[] { "    public int Use()", "    {", "        return _count;", "    }" },
                new[] { "    public int Use() => _count;" }
            ),
            MethodToBlock =>
            (
                new[] { "    public int Use() => _count;" },
                new[] { "    public int Use()", "    {", "        return _count;", "    }" }
            ),
            PropToExpr =>
            (
                new[] { "    public int Count", "    {", "        get", "        {", "            return _count;", "        }", "    }" },
                new[] { "    public int Count => _count;" }
            ),
            PropToBlock =>
            (
                new[] { "    public int Count => _count;" },
                new[] { "    public int Count", "    {", "        get", "        {", "            return _count;", "        }", "    }" }
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(site)),
        };
        return (Class(inMember, irregular, blankBeforeMember), Class(outMember, irregular, blankBeforeMember));
    }

    private async Task<string> RunAsync(string site)
    {
        var member = site is MethodToExpr or MethodToBlock ? "Use" : "Count";
        var direction = site is MethodToExpr or PropToExpr ? "ToExpressionBody" : "ToBlockBody";
        var r = await _engine.ConvertExpressionBodyAsync("Test.cs", member, direction);
        Assert.That(r.Outcome, Is.EqualTo(EditOutcome.Modified), r.Message);
        return r.UpdatedText;
    }

    [Test]
    public async Task CleanInput_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var (input, expected) = Lines(site);
        SetFiles(("Test.cs", Src(crlf, input)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, expected)));
    }

    // Untouched code with irregular spacing is not re-spaced by the conversion.
    [Test]
    public async Task UntouchedIrregularSpacing_Preserved_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var (input, expected) = Lines(site, irregular: true);
        SetFiles(("Test.cs", Src(crlf, input)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, expected)));
    }

    // The blank line before the converted member is untouched code and is kept.
    [Test]
    public async Task BlankLineBeforeMember_Preserved_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var (input, expected) = Lines(site, blankBeforeMember: true);
        SetFiles(("Test.cs", Src(crlf, input)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, expected)));
    }

    [Test]
    public async Task LfDocument_ResultHasNoCarriageReturn([ValueSource(nameof(AllSites))] string site)
    {
        SetFiles(("Test.cs", Src(false, Lines(site).Input)));

        Assert.That(await RunAsync(site), Does.Not.Contain("\r"));
    }

    [Test]
    public async Task CrlfDocument_ResultHasNoBareLineFeed([ValueSource(nameof(AllSites))] string site)
    {
        SetFiles(("Test.cs", Src(true, Lines(site).Input)));

        Assert.That((await RunAsync(site)).Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on a line the conversion does not touch keeps its own terminator; changed lines take the document's ending.
    [TestCase(MethodToExpr, "public class C\n")]
    [TestCase(MethodToExpr, "    private int _count;\n")]
    [TestCase(MethodToBlock, "public class C\n")]
    [TestCase(MethodToBlock, "    private int _count;\n")]
    [TestCase(PropToExpr, "public class C\n")]
    [TestCase(PropToExpr, "    private int _count;\n")]
    [TestCase(PropToBlock, "public class C\n")]
    [TestCase(PropToBlock, "    private int _count;\n")]
    public async Task MixedEol_StrayCrlfLineKeepsItsTerminator(string site, string anchor)
    {
        var (input, expected) = Lines(site);
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, input), anchor)));

        Assert.That(await RunAsync(site), Is.EqualTo(WithStrayCrlf(Src(false, expected), anchor)));
    }
}
