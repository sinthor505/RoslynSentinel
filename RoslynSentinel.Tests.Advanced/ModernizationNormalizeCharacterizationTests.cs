using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Slice 5b characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins (LF, CRLF and Mixed output) for the eight <c>SyntaxModernizationEngine</c> edit paths that now end in
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass). Each input has an untouched, badly spaced member
/// (<c>UntouchedLine</c>) that the rewrite does not change. "Mixed" is LF except the untouched line, which ends in a
/// stray CRLF. The pins encode the migrated contract (annotated-span formatting): the untouched line keeps its text,
/// blank lines between members and types survive, changed lines take the document's dominant line ending, and a stray
/// CRLF on an untouched line survives. The pre-migration whole-subtree pass emitted LF for every input, split the
/// untouched member over three lines, dropped the blank line after a positional record and after an expression-bodied
/// method, and inserted one between an if block and the next statement.
/// </summary>
[TestFixture]
[Category("SyntaxModernizationEngine")] // sentinel:auto-category
public class ModernizationNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private PersistentWorkspaceManager _workspaceManager;
    private SyntaxModernizationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Advanced.TestEnginesAdvanced.SyntaxModernizationEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    /// <summary>Joins lines without a trailing newline: LF, CRLF, or "Mixed" (LF, CRLF after <c>UntouchedLine</c> only).</summary>
    private static string Build(string eol, params string[] lines)
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

    private static void AssertPinned(string? actual, string expected) =>
        Assert.That(actual, Is.EqualTo(expected).NoClip);

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ClassToRecord_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Person",
            "{",
            "    public string Name { get; set; }",
            "    public int Age { get; set; }",
            "}",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}"));

        var result = await _engine.ClassToRecordAsync("Test.cs", "Person");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public record Person(string Name, int Age);",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task RecordToClass_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public record Person(string Name, int Age);",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}"));

        var result = await _engine.RecordToClassAsync("Test.cs", "Person");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Person",
            "{",
            "    public string Name { get; init; }",
            "    public int Age { get; init; }",
            "}",
            "",
            "public class Other",
            "{",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertMethodToExpressionBody_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public int Twice(int x)",
            "    {",
            "        return x * 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertMethodToExpressionBodyAsync("Test.cs", "Twice");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public int Twice(int x) => x * 2;",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertToPattern_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public bool Check(string s, int x)",
            "    {",
            "        if (s == null)",
            "        {",
            "            return false;",
            "        }",
            "        return x == 1 || x == 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToPatternAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public bool Check(string s, int x)",
            "    {",
            "        if (s is null)",
            "        {",
            "            return false;",
            "        }",
            "        return x is 1 or 2;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseSpanForParsing_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public string Cut(string s)",
            "    {",
            "        return s.Substring(1, 2);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.UseSpanForParsingAsync("Test.cs", "Cut");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public string Cut(string s)",
            "    {",
            "        return s.AsSpan(1, 2).ToString();",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task UseThrowExpressions_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public string Get(string s)",
            "    {",
            "        var x = s;",
            "        if (x == null) throw new System.ArgumentNullException(nameof(x));",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.UseThrowExpressionsAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public string Get(string s)",
            "    {",
            "        var x = s ?? throw new System.ArgumentNullException(nameof(x));",
            "        return x;",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ConvertToSourceGeneratedLogging_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Worker",
            "{",
            "    public void Run(ILogger logger, int id)",
            "    {",
            "        logger.LogInformation(\"Started {Id}\", id);",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToSourceGeneratedLoggingAsync("Test.cs", "Worker");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public partial class Worker",
            "{",
            "    public void Run(ILogger logger, int id)",
            "    {",
            "        LogInformationEvent1(logger, id);",
            "    }",
            "",
            UntouchedLine,
            "",
            "    [LoggerMessage(1, LogLevel.Information, \"Started {Id}\")]",
            "    public static partial void LogInformationEvent1(ILogger logger, object p1);",
            "}"));
    }

    [TestCase("LF")]
    [TestCase("CRLF")]
    [TestCase("Mixed")]
    public async Task ReplaceStringConcatWithInterpolation_ByteExact(string eol)
    {
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        if (eol == "Mixed")
        {
            _workspaceManager.EolModeOverride = EolMode.Lf;
        }

        SetSource(Build(eol,
            "public class Sample",
            "{",
            "    public string Greet(string name)",
            "    {",
            "        return \"Hello \" + name + \"!\";",
            "    }",
            "",
            UntouchedLine,
            "}"));

        var result = await _engine.ReplaceStringConcatWithInterpolationAsync("Test.cs");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "public class Sample",
            "{",
            "    public string Greet(string name)",
            "    {",
            "        return $\"Hello {name}!\";",
            "    }",
            "",
            UntouchedLine,
            "}"));
    }

    [Test]
    public async Task ClassToRecord_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ClassToRecordAsync("Missing.cs", "Person"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task ConvertMethodToExpressionBody_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.ConvertMethodToExpressionBodyAsync("Missing.cs", "Twice"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public async Task UseSpanForParsing_MissingFile_ThrowsNotFound()
    {
        SetSource("public class C { }");
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () => await _engine.UseSpanForParsingAsync("Missing.cs", "Cut"));
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }
}
