using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C5c-1 characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pins for the five whole-document <c>Formatter.FormatAsync</c> sites in <see cref="CodeGenerationEngine"/>
/// routed onto <c>RoslynFormattingHelper.FormatAnnotatedSpansAsync</c> (only the generated or rewritten nodes are
/// formatted): <c>GenerateConstructorAsync</c>, <c>GenerateToStringAsync</c>, <c>ImplementInterfaceAsync</c>,
/// <c>ConvertPropertySafeAsync</c> (both directions) and <c>InterpolateStringAsync</c>.
/// <para>
/// The "clean" inputs are already formatted outside the edit, so the CRLF pins are byte-identical to the pre-routing
/// whole-document behaviour. The "irregular" pins (odd spacing in untouched code) and the LF / mixed-EOL pins encode the
/// corrected output: untouched code is no longer re-spaced and the formatter's CRLF no longer leaks into LF text.
/// </para>
/// </summary>
[TestFixture]
[Category("CodeGenerationEngine")] // sentinel:auto-category
public class CodeGenerationEngineFormatCharacterizationTests
{
    private IWorkspaceManager _workspaceManager;
    private CodeGenerationEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new CodeGenerationEngine(_workspaceManager);
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

    private sealed record Site(string[] Input, string[] Expected, string Clean, string Irregular);

    private const string CtorSite = "ctor";
    private const string ToStringSite = "tostring";
    private const string IfaceSite = "iface";
    private const string FullPropSite = "fullprop";
    private const string AutoPropSite = "autoprop";
    private const string InterpSite = "interp";

    private static Site GetSite(string site) => site switch
    {
        CtorSite => new Site(
            [
                "public class C",
                "{",
                "    private int _count;",
                "    private string _name;",
                "",
                "    public int Use() { return _count; }",
                "}",
                "",
            ],
            [
                "public class C",
                "{",
                "    private int _count;",
                "    private string _name;",
                "",
                "    public int Use() { return _count; }",
                "",
                "    public C(int count, string name)",
                "    {",
                "        this._count = count;",
                "        this._name = name;",
                "    }",
                "}",
                "",
            ],
            "    public int Use() { return _count; }",
            "    public   int Use( ) { return  _count; }"),
        ToStringSite => new Site(
            [
                "public class C",
                "{",
                "    public int Id { get; set; }",
                "    public string Name { get; set; }",
                "",
                "    public int Use() { return Id; }",
                "}",
                "",
            ],
            [
                "public class C",
                "{",
                "    public int Id { get; set; }",
                "    public string Name { get; set; }",
                "",
                "    public int Use() { return Id; }",
                "",
                "    public override string ToString()",
                "    {",
                "        return $\"C {{ Id = {Id}, Name = {Name} }}\";",
                "    }",
                "}",
                "",
            ],
            "    public int Use() { return Id; }",
            "    public   int Use( ) { return  Id; }"),
        IfaceSite => new Site(
            [
                "public interface IThing",
                "{",
                "    void Run(int x);",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public int Use() { return 1; }",
                "}",
                "",
            ],
            [
                "public interface IThing",
                "{",
                "    void Run(int x);",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public int Use() { return 1; }",
                "    public void Run(int x)",
                "    {",
                "        throw new NotImplementedException();",
                "    }",
                "}",
                "",
            ],
            "    public int Use() { return 1; }",
            "    public   int Use( ) { return  1; }"),
        FullPropSite => new Site(
            [
                "public class C",
                "{",
                "    public int Count { get; set; }",
                "",
                "    public int Use() { return Count; }",
                "}",
                "",
            ],
            [
                "public class C",
                "{",
                "    private int _count;",
                "",
                "    public int Count { get => _count; set => _count = value; }",
                "",
                "    public int Use() { return Count; }",
                "}",
                "",
            ],
            "    public int Use() { return Count; }",
            "    public   int Use( ) { return  Count; }"),
        AutoPropSite => new Site(
            [
                "public class C",
                "{",
                "    private int _count;",
                "    public int Count { get => _count; set => _count = value; }",
                "",
                "    public int Use() { return Count; }",
                "}",
                "",
            ],
            [
                "public class C",
                "{",
                "    public int Count { get; set; }",
                "",
                "    public int Use() { return Count; }",
                "}",
                "",
            ],
            "    public int Use() { return Count; }",
            "    public   int Use( ) { return  Count; }"),
        InterpSite => new Site(
            [
                "public class C",
                "{",
                "    public string Greet(string n) { return string.Format(\"Hello {0}\", n); }",
                "",
                "    public int Use() { return 1; }",
                "}",
                "",
            ],
            [
                "public class C",
                "{",
                "    public string Greet(string n) { return $\"Hello {n}\"; }",
                "",
                "    public int Use() { return 1; }",
                "}",
                "",
            ],
            "    public int Use() { return 1; }",
            "    public   int Use( ) { return  1; }"),
        _ => throw new ArgumentOutOfRangeException(nameof(site)),
    };

    private async Task<string> RunAsync(string site)
    {
        switch (site)
        {
            case CtorSite:
                return Text(await _engine.GenerateConstructorAsync("Test.cs", "C"));
            case ToStringSite:
                var ts = await _engine.GenerateToStringAsync("Test.cs", "C");
                Assert.That(ts.Warning, Is.Null.Or.Empty, ts.Warning);
                return ts.UpdatedContent;
            case IfaceSite:
                return Text(await _engine.ImplementInterfaceAsync("Test.cs", "C", "IThing"));
            case FullPropSite:
                return Text(await _engine.ConvertPropertySafeAsync("Test.cs", "Count", "ToFullProperty"));
            case AutoPropSite:
                return Text(await _engine.ConvertPropertySafeAsync("Test.cs", "Count", "ToAutoProperty"));
            case InterpSite:
                return Text(await _engine.InterpolateStringAsync("Test.cs", "string.Format(\"Hello {0}\", n)"));
            default:
                throw new ArgumentOutOfRangeException(nameof(site));
        }

        static string Text(DocumentEditResult r)
        {
            Assert.That(r.Outcome, Is.EqualTo(EditOutcome.Modified), r.Message);
            return r.UpdatedText!;
        }
    }

    private static readonly string[] AllSites = [CtorSite, ToStringSite, IfaceSite, FullPropSite, AutoPropSite, InterpSite];

    [Test]
    public async Task CleanInput_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var s = GetSite(site);
        SetFiles(("Test.cs", Src(crlf, s.Input)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, s.Expected)));
    }

    // Untouched code with irregular spacing is not re-spaced (a whole-document format would normalize it).
    [Test]
    public async Task UntouchedIrregularSpacing_Preserved_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var s = GetSite(site);
        SetFiles(("Test.cs", Src(crlf, s.Input).Replace(s.Clean, s.Irregular)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, s.Expected).Replace(s.Clean, s.Irregular)));
    }

    [Test]
    public async Task LfDocument_ResultHasNoCarriageReturn([ValueSource(nameof(AllSites))] string site)
    {
        SetFiles(("Test.cs", Src(false, GetSite(site).Input)));

        Assert.That(await RunAsync(site), Does.Not.Contain("\r"));
    }

    [Test]
    public async Task CrlfDocument_ResultHasNoBareLineFeed([ValueSource(nameof(AllSites))] string site)
    {
        SetFiles(("Test.cs", Src(true, GetSite(site).Input)));

        Assert.That((await RunAsync(site)).Replace("\r\n", ""), Does.Not.Contain("\n"));
    }

    // Stray CRLF on the line before the edit (formatter-owned break) and on the first line.
    [TestCase(CtorSite, "    public int Use() { return _count; }\n")]
    [TestCase(CtorSite, "public class C\n")]
    [TestCase(ToStringSite, "    public int Use() { return Id; }\n")]
    [TestCase(ToStringSite, "public class C\n")]
    [TestCase(IfaceSite, "    public int Use() { return 1; }\n")]
    [TestCase(IfaceSite, "public interface IThing\n")]
    [TestCase(FullPropSite, "    public int Use() { return Count; }\n")]
    [TestCase(FullPropSite, "public class C\n")]
    [TestCase(AutoPropSite, "    public int Use() { return Count; }\n")]
    [TestCase(AutoPropSite, "public class C\n")]
    [TestCase(InterpSite, "    public int Use() { return 1; }\n")]
    [TestCase(InterpSite, "public class C\n")]
    public async Task MixedEol_StrayCrlfLineKeepsItsTerminator(string site, string anchor)
    {
        var s = GetSite(site);
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, s.Input), anchor)));

        Assert.That(await RunAsync(site), Is.EqualTo(WithStrayCrlf(Src(false, s.Expected), anchor)));
    }
}
