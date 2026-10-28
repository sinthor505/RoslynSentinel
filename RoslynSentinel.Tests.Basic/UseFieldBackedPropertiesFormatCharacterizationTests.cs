using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C5c-4 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for <c>SyntaxUpgradeEngine.UseFieldBackedPropertiesAsync</c>, which collapses a backing-field +
/// property pair into an auto-property. The rewritten property is annotated and formatted through
/// <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c>; code the rewrite did not touch (a sibling method with
/// irregular spacing) keeps its text. The CRLF tests are byte-identical to the pre-routing behaviour; the irregular
/// sibling and LF/mixed-EOL tests encode the corrected output (the old code formatted the whole class and leaked
/// formatter-inserted CRLF into LF text).
/// </summary>
[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
public class UseFieldBackedPropertiesFormatCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Basic.TestEngines.SyntaxUpgradeEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Entity.cs")
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

    private static string WithStrayCrlf(string lfText)
    {
        var mixed = lfText.Replace("    public void M() { }\n", "    public void M() { }\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF, on the method line.");
        return mixed;
    }

    private static string[] WellFormedInput() =>
    [
        "public class Entity",
        "{",
        "    private string _name;",
        "",
        "    public string Name",
        "    {",
        "        get => _name;",
        "        set => _name = value;",
        "    }",
        "",
        "    public void M() { }",
        "}",
    ];

    private static string[] WellFormedExpected() =>
    [
        "public class Entity",
        "{",
        "",
        "    public string Name { get; set; }",
        "",
        "    public void M() { }",
        "}",
    ];

    private static string[] IrregularSiblingInput() =>
    [
        "public class   Entity",
        "{",
        "    private string _name;",
        "",
        "    public string Name { get => _name; set => _name = value; }",
        "",
        "    // keep   this   comment",
        "    public void  M( ) { }",
        "}",
    ];

    private static string[] IrregularSiblingExpected() =>
    [
        "public class   Entity",
        "{",
        "",
        "    public string Name { get; set; }",
        "",
        "    // keep   this   comment",
        "    public void  M( ) { }",
        "}",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task UseFieldBackedProperties_WellFormedClass_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, WellFormedInput()));

        var result = await _engine.UseFieldBackedPropertiesAsync("Entity.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, WellFormedExpected())));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UseFieldBackedProperties_IrregularSibling_UntouchedCodeKeepsItsSpacing_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, IrregularSiblingInput()));

        var result = await _engine.UseFieldBackedPropertiesAsync("Entity.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf, IrregularSiblingExpected())));
    }

    [Test]
    public async Task UseFieldBackedProperties_LfDocument_ResultHasNoCarriageReturn()
    {
        SetSource(Src(false, WellFormedInput()));

        var result = await _engine.UseFieldBackedPropertiesAsync("Entity.cs");

        Assert.That(result.UpdatedText, Does.Not.Contain("\r"));
    }

    [Test]
    public async Task UseFieldBackedProperties_CrlfDocument_ResultHasNoBareLineFeed()
    {
        SetSource(Src(true, WellFormedInput()));

        var result = await _engine.UseFieldBackedPropertiesAsync("Entity.cs");

        Assert.That(result.UpdatedText.Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    [Test]
    public async Task UseFieldBackedProperties_MixedEol_StrayCrlfLineOutsideTheEditKeepsItsTerminator()
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;

        SetSource(WithStrayCrlf(Src(false, WellFormedInput())));

        var result = await _engine.UseFieldBackedPropertiesAsync("Entity.cs");

        Assert.That(result.UpdatedText, Is.EqualTo(WithStrayCrlf(Src(false, WellFormedExpected()))));
    }
}
