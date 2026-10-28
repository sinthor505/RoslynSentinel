using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// C6 proof slice 1 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins (LF, CRLF and Mixed output) for the four <c>SyntaxUpgradeEngine</c> methods that now end in
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass). Each input has an untouched, badly spaced member
/// (<c>UntouchedLine</c>) that the rewrite does not change. The pins encode the migrated contract: the untouched
/// line keeps its text, changed lines take the document's dominant line ending, and a stray CRLF on an untouched
/// line survives. The pre-migration whole-subtree pass re-spaced the untouched member and emitted LF for every input.
/// </summary>
[TestFixture]
[Category("SyntaxUpgradeEngine")] // sentinel:auto-category
public class SyntaxUpgradeNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private PersistentWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SyntaxUpgradeEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName = "Entity.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>
    /// Joins lines with the requested ending. "LF" and "CRLF" use one ending throughout; "Mixed" is LF except
    /// the untouched line, which ends in a stray CRLF.
    /// </summary>
    private static string Build(string eol, string[] lines)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i == lines.Length - 1)
            {
                break;
            }

            var crlf = eol == "CRLF" || (eol == "Mixed" && lines[i] == UntouchedLine);
            sb.Append(crlf ? "\r\n" : "\n");
        }

        return sb.ToString();
    }

    private static string[] GuardInput() =>
    [
        "public class Entity",
        "{",
        "    public void Set(string name)",
        "    {",
        "        if (name == null) throw new ArgumentNullException(nameof(name));",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] GuardExpected() =>
    [
        "public class Entity",
        "{",
        "    public void Set(string name)",
        "    {",
        "        ArgumentNullException.ThrowIfNull(name);",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] BracesInput() =>
    [
        "public class Entity",
        "{",
        "    public void Set(bool flag)",
        "    {",
        "        if (flag) Work();",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] BracesExpected() =>
    [
        "public class Entity",
        "{",
        "    public void Set(bool flag)",
        "    {",
        "        if (flag)",
        "        {",
        "            Work();",
        "        }",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] PatternInput() =>
    [
        "public class Entity",
        "{",
        "    public int Len(object o)",
        "    {",
        "        if (o is string)",
        "        {",
        "            var s = (string)o;",
        "            return s.Length;",
        "        }",
        "        return 0;",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] PatternExpected() =>
    [
        "public class Entity",
        "{",
        "    public int Len(object o)",
        "    {",
        "        if (o is string s)",
        "        {",
        "            return s.Length;",
        "        }",
        "        return 0;",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] SpanInput() =>
    [
        "public class Entity",
        "{",
        "    public int Len(string s)",
        "    {",
        "        return s.AsSpan().Length;",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    private static string[] SpanExpected() =>
    [
        "public class Entity",
        "{",
        "    public int Len(string s)",
        "    {",
        "        return s.Length;",
        "    }",
        "",
        UntouchedLine,
        "}",
    ];

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UpgradeToModernGuards_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, GuardInput()));

        var result = await _engine.UpgradeToModernGuardsAsync("Entity.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Build(eol, GuardExpected())));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task AddBraces_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, BracesInput()));

        var result = await _engine.AddBracesAsync("Entity.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Build(eol, BracesExpected())));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UpgradePatternMatching_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, PatternInput()));

        var result = await _engine.UpgradePatternMatchingAsync("Entity.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Build(eol, PatternExpected())));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task CleanupImplicitSpans_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol, SpanInput()));

        var result = await _engine.CleanupImplicitSpansAsync("Entity.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Is.EqualTo(Build(eol, SpanExpected())));
    }

    [Test]
    public async Task UseFieldBackedProperties_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UseFieldBackedPropertiesAsync("Missing.cs"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UpgradeToPrimaryConstructor_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UpgradeToPrimaryConstructorAsync("Missing.cs", "Foo"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
