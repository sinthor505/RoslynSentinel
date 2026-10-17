using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Whitespace initiative C5c-3 characterization tests for the raw format sites of <c>BasicRefactoringEngine</c>
/// (ChangeSignatureAsync, MoveTypeToFileAsync, MoveAllTypesToFilesAsync, ExtractInterfaceAsync and the
/// simplifyExisting tail of AddUsingDirectiveAsync). Each site is pinned byte-exact for LF, CRLF and an LF document with
/// one stray CRLF. The CRLF expectations were captured against the unrouted code and are unchanged by the routing; the
/// tests whose names say "Preserves" or "Stray" encode behaviour the routing fixed (untouched code no longer re-spaced,
/// formatter-added CRLF no longer leaked into LF files). The legitimate format-document tools stay unrouted.
/// </summary>
[TestFixture]
[Category("BasicRefactoringEngine")] // sentinel:auto-category
public class BasicRefactoringEngineFormatCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            new SentinelConfiguration());
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

    private static string WithStrayCrlf(string lfText, string anchorLine)
    {
        var anchor = anchorLine + "\n";
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        var mixed = lfText.Replace(anchor, anchorLine + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    private static string ValueOfKeyContaining(Dictionary<FilePathWrapper, string> changes, string fragment)
    {
        var matches = changes.Where(kv => kv.Key.Contains(fragment)).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), $"Expected exactly one changed file containing '{fragment}'.");
        return matches[0].Value;
    }

    // ---- ChangeSignatureAsync ----

    private const string CalcStray = "    private int _other;";

    private static string[] CalcInput(string other = CalcStray) =>
    [
        "public class Calc",
        "{",
        "    // keep this comment",
        other,
        "",
        "    public int Add(int a, int b)",
        "    {",
        "        return a - b;",
        "    }",
        "",
        "    public int Use()",
        "    {",
        "        return Add(1, 2);",
        "    }",
        "}",
        "",
    ];

    private static string[] CalcExpected(string other = CalcStray) =>
    [
        "public class Calc",
        "{",
        "    // keep this comment",
        other,
        "",
        "    public int Add(int b, int a)",
        "    {",
        "        return a - b;",
        "    }",
        "",
        "    public int Use()",
        "    {",
        "        return Add(2, 1);",
        "    }",
        "}",
        "",
    ];

    private async Task<string> SwapAddParametersAsync()
    {
        var result = await _engine.ChangeSignatureAsync("Test.cs", "Add", new SignatureParameterSpec[] { new ExistingParameterSpec(1), new ExistingParameterSpec(0) });
        Assert.That(result.Changes, Is.Not.Empty, "ChangeSignature returned no changes.");
        return result.Changes["Test.cs"];
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangeSignature_FormattedSource_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, CalcInput()));

        var content = await SwapAddParametersAsync();

        Assert.That(content, Is.EqualTo(Src(crlf, CalcExpected())));
    }

    [Test]
    public async Task ChangeSignature_StrayCrlfOnUntouchedLine_KeepsItsTerminator()
    {
        SetSource(WithStrayCrlf(Src(false, CalcInput()), CalcStray));

        var content = await SwapAddParametersAsync();

        Assert.That(content, Is.EqualTo(WithStrayCrlf(Src(false, CalcExpected()), CalcStray)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangeSignature_Preserves_IrregularSpacingInUntouchedCode(bool crlf)
    {
        const string irregular = "    private   int   _other ;";
        SetSource(Src(crlf, CalcInput(irregular)));

        var content = await SwapAddParametersAsync();

        Assert.That(content, Is.EqualTo(Src(crlf, CalcExpected(irregular))));
    }

    // ---- MoveTypeToFileAsync / MoveAllTypesToFilesAsync ----

    private const string KeepStray = "        private int _other;";

    private static string[] SplitInput() =>
    [
        "using System;",
        "",
        "namespace Demo",
        "{",
        "    public class Keep",
        "    {",
        KeepStray,
        "    }",
        "",
        "    public class Moved",
        "    {",
        "        public int Value;",
        "    }",
        "}",
        "",
    ];

    private static string[] SplitOrigExpected() =>
    [
        "using System;",
        "",
        "namespace Demo",
        "{",
        "    public class Keep",
        "    {",
        KeepStray,
        "    }",
        "}",
        "",
    ];

    private static string[] SplitNewExpected() =>
    [
        "using System;",
        "",
        "namespace Demo",
        "{",
        "    public class Moved",
        "    {",
        "        public int Value;",
        "    }",
        "}",
    ]; // no trailing newline: the scaffolded file ends at the closing brace, as it did before the routing

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveTypeToFile_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, SplitInput()));

        var result = await _engine.MoveTypeToFileAsync("Test.cs", "Moved");

        Assert.That(ValueOfKeyContaining(result, "Test.cs"), Is.EqualTo(Src(crlf, SplitOrigExpected())), "original file");
        Assert.That(ValueOfKeyContaining(result, "Moved.cs"), Is.EqualTo(Src(crlf, SplitNewExpected())), "new file");
    }

    [Test]
    public async Task MoveTypeToFile_StrayCrlfOnUntouchedLine_KeepsItsTerminatorAndNewFileTakesDominantEol()
    {
        SetSource(WithStrayCrlf(Src(false, SplitInput()), KeepStray));

        var result = await _engine.MoveTypeToFileAsync("Test.cs", "Moved");

        Assert.That(ValueOfKeyContaining(result, "Test.cs"), Is.EqualTo(WithStrayCrlf(Src(false, SplitOrigExpected()), KeepStray)), "original file");
        Assert.That(ValueOfKeyContaining(result, "Moved.cs"), Is.EqualTo(Src(false, SplitNewExpected())), "new file");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MoveAllTypesToFiles_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, SplitInput()));

        var result = await _engine.MoveAllTypesToFilesAsync("Test.cs");

        Assert.That(ValueOfKeyContaining(result, "Test.cs"), Is.EqualTo(Src(crlf, SplitOrigExpected())), "original file");
        Assert.That(ValueOfKeyContaining(result, "Moved.cs"), Is.EqualTo(Src(crlf, SplitNewExpected())), "new file");
    }

    [Test]
    public async Task MoveAllTypesToFiles_StrayCrlfOnUntouchedLine_KeepsItsTerminatorAndNewFileTakesDominantEol()
    {
        SetSource(WithStrayCrlf(Src(false, SplitInput()), KeepStray));

        var result = await _engine.MoveAllTypesToFilesAsync("Test.cs");

        Assert.That(ValueOfKeyContaining(result, "Test.cs"), Is.EqualTo(WithStrayCrlf(Src(false, SplitOrigExpected()), KeepStray)), "original file");
        Assert.That(ValueOfKeyContaining(result, "Moved.cs"), Is.EqualTo(Src(false, SplitNewExpected())), "new file");
    }

    // ---- ExtractInterfaceAsync (original file) ----

    private const string SvcStray = "        private int _other;";

    private static string[] SvcInput(string other = SvcStray) =>
    [
        "using System;",
        "",
        "namespace Demo",
        "{",
        "    public class Svc",
        "    {",
        other,
        "",
        "        public int Value { get; set; }",
        "",
        "        public void Run()",
        "        {",
        "        }",
        "    }",
        "}",
        "",
    ];

    private static string[] SvcExpected(string other = SvcStray) =>
    [
        "using System;",
        "",
        "namespace Demo",
        "{",
        // Pinned as-is: AddBaseListTypes leaves the colon on its own line at column 0 (a separate, pre-existing
        // layout defect, reported in the C5c-3 slice notes); the routing must not change it.
        "    public class Svc",
        ": ISvc",
        "    {",
        other,
        "",
        "        public int Value { get; set; }",
        "",
        "        public void Run()",
        "        {",
        "        }",
        "    }",
        "}",
        "",
    ];

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExtractInterface_OriginalFile_ByteExact(bool crlf)
    {
        SetSource(Src(crlf, SvcInput()));

        var result = await _engine.ExtractInterfaceAsync("Test.cs", "Svc", "ISvc");

        Assert.That(ValueOfKeyContaining(result, "Test.cs"), Is.EqualTo(Src(crlf, SvcExpected())));
    }

    [Test]
    public async Task ExtractInterface_StrayCrlfOnUntouchedLine_KeepsItsTerminator()
    {
        SetSource(WithStrayCrlf(Src(false, SvcInput()), SvcStray));

        var result = await _engine.ExtractInterfaceAsync("Test.cs", "Svc", "ISvc");

        Assert.That(ValueOfKeyContaining(result, "Test.cs"), Is.EqualTo(WithStrayCrlf(Src(false, SvcExpected()), SvcStray)));
    }

    // ---- AddUsingDirectiveAsync with simplifyExisting (whole-document tail) ----

    [Test]
    public async Task AddUsingDirective_SimplifyExisting_StrayCrlfOnUntouchedLine_KeepsItsTerminator()
    {
        const string anchor = "        private int _other;";
        var source = WithStrayCrlf(Src(false, SvcInput()), anchor);
        SetSource(source);

        var result = await _engine.AddUsingDirectiveAsync("Test.cs", "System.Linq", simplifyExisting: true);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        var expected = WithStrayCrlf(Src(false,
            "using System;",
            "using System.Linq;",
            "",
            "namespace Demo",
            "{",
            "    public class Svc",
            "    {",
            anchor,
            "",
            "        public int Value { get; set; }",
            "",
            "        public void Run()",
            "        {",
            "        }",
            "    }",
            "}",
            ""), anchor);
        Assert.That(result.UpdatedText, Is.EqualTo(expected));
    }
}
