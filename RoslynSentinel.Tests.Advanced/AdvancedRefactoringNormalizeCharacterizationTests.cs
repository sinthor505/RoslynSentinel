using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// C6-2b characterization tests for the three normalize-helper sites in <see cref="AdvancedRefactoringEngine"/>:
/// <c>OptimizeTaskWaitAsync</c> (existing tree; formats only the rewritten nodes) and the method and property stubs
/// built by <c>SyncInterfaceToImplementationAsync</c> (new nodes; formatted via <c>Formatter.Annotation</c>).
/// Pins byte-exact output for LF, CRLF and a stray-CRLF mixed document, and that an untouched, badly spaced member is
/// left alone. Before C6-2b the OptimizeTaskWait site normalized the whole tree with a LF end-of-line and re-spaced
/// untouched code; those pins were updated when it migrated.
/// </summary>
[TestFixture]
[Category("AdvancedRefactoringEngine")]
public class AdvancedRefactoringNormalizeCharacterizationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private AdvancedRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Advanced.TestEnginesAdvanced.AdvancedRefactoringEngine(_workspaceManager, NullLogger<AdvancedRefactoringEngine>.Instance);
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

    /// <summary>An LF text with exactly one CRLF terminator: the last character of the anchor line.</summary>
    private static string WithStrayCrlf(string lfText, string anchor)
    {
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        Assert.That(lfText.Split(anchor).Length, Is.EqualTo(2), "Precondition: the anchor line is unique.");
        var mixed = lfText.Replace(anchor, anchor.Substring(0, anchor.Length - 1) + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    private const string OptSite = "opt";
    private const string MethodSite = "method";
    private const string PropSite = "prop";

    private static readonly string[] AllSites = [OptSite, MethodSite, PropSite];

    private sealed record Site(string[] Input, string[] Expected, string[] IrregularInput, string[] IrregularExpected, string Anchor);

    private static Site GetSite(string site) => site switch
    {
        OptSite => new Site(
            [
                "using System.Threading.Tasks;",
                "",
                "public class C",
                "{",
                "    private Task<int> _t;",
                "",
                "    public int Use() { return _t.Result; }",
                "}",
                "",
            ],
            [
                "using System.Threading.Tasks;",
                "",
                "public class C",
                "{",
                "    private Task<int> _t;",
                "",
                "    public async Task<int> Use() { return (await _t); }",
                "}",
                "",
            ],
            [
                "using System.Threading.Tasks;",
                "",
                "public class C",
                "{",
                "    private Task<int> _t;",
                "",
                "    public int Use() { return _t.Result; }",
                "    public   int Other( ) { return  2; }",
                "}",
                "",
            ],
            [
                "using System.Threading.Tasks;",
                "",
                "public class C",
                "{",
                "    private Task<int> _t;",
                "",
                "    public async Task<int> Use() { return (await _t); }",
                "    public   int Other( ) { return  2; }",
                "}",
                "",
            ],
            "public class C\n"),
        MethodSite => new Site(
            [
                "public interface IThing",
                "{",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public void Run(int x) { }",
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
                "    public void Run(int x) { }",
                "}",
                "",
            ],
            [
                "public interface IThing",
                "{",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public void Run(int x) { }",
                "    private   int Keep( ) { return  1; }",
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
                "    public void Run(int x) { }",
                "    private   int Keep( ) { return  1; }",
                "}",
                "",
            ],
            "public interface IThing\n"),
        PropSite => new Site(
            [
                "public interface IThing",
                "{",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public int Count { get; set; }",
                "}",
                "",
            ],
            [
                "public interface IThing",
                "{",
                "    int Count { get; set; }",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public int Count { get; set; }",
                "}",
                "",
            ],
            [
                "public interface IThing",
                "{",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public int Count { get; set; }",
                "    private   int Keep( ) { return  1; }",
                "}",
                "",
            ],
            [
                "public interface IThing",
                "{",
                "    int Count { get; set; }",
                "}",
                "",
                "public class C : IThing",
                "{",
                "    public int Count { get; set; }",
                "    private   int Keep( ) { return  1; }",
                "}",
                "",
            ],
            "public interface IThing\n"),
        _ => throw new ArgumentOutOfRangeException(nameof(site)),
    };

    private async Task<string> RunAsync(string site)
    {
        switch (site)
        {
            case OptSite:
                return Text(await _engine.OptimizeTaskWaitAsync("Test.cs"));
            case MethodSite:
            case PropSite:
                return Text(await _engine.SyncInterfaceToImplementationAsync("Test.cs", "C", "IThing"));
            default:
                throw new ArgumentOutOfRangeException(nameof(site));
        }

        static string Text(DocumentEditResult r)
        {
            Assert.That(r.Outcome, Is.EqualTo(EditOutcome.Modified), r.Message);
            return r.UpdatedText;
        }
    }

    [Test]
    public async Task CleanInput_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var s = GetSite(site);
        SetFiles(("Test.cs", Src(crlf, s.Input)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, s.Expected)));
    }

    // Untouched code with irregular spacing is not re-spaced.
    [Test]
    public async Task UntouchedIrregularSpacing_ByteExact([ValueSource(nameof(AllSites))] string site, [Values(false, true)] bool crlf)
    {
        var s = GetSite(site);
        SetFiles(("Test.cs", Src(crlf, s.IrregularInput)));

        Assert.That(await RunAsync(site), Is.EqualTo(Src(crlf, s.IrregularExpected)));
    }

    // Untouched interface members keep their spacing when sync adds a member: `int  A( ) ;`, the blank lines and `// c` stay as written.
    [Test]
    public async Task SyncInterface_SameFile_KeepsIrregularMembers([Values(false, true)] bool crlf)
    {
        SetFiles(("Test.cs", Src(crlf,
            "public interface IThing",
            "{",
            "    int  A( ) ;",
            "",
            "",
            "    // c",
            "    int B();",
            "}",
            "",
            "public class C : IThing",
            "{",
            "    public int A() { return 1; }",
            "    public int B() { return 2; }",
            "    public int Extra() { return 3; }",
            "}",
            "")));

        Assert.That(await RunAsync(MethodSite), Is.EqualTo(Src(crlf,
            "public interface IThing",
            "{",
            "    int  A( ) ;",
            "",
            "",
            "    // c",
            "    int B();",
            "    int Extra();",
            "}",
            "",
            "public class C : IThing",
            "{",
            "    public int A() { return 1; }",
            "    public int B() { return 2; }",
            "    public int Extra() { return 3; }",
            "}",
            "")));
    }

    [Test]
    public async Task SyncInterface_CrossFile_KeepsIrregularInterfaceMembers([Values(false, true)] bool crlf)
    {
        SetFiles(("C.cs", Src(crlf,
            "public class C : IThing",
            "{",
            "    public int A() { return 1; }",
            "    public int Extra() { return 3; }",
            "}",
            "")),
            ("IThing.cs", Src(crlf,
            "public interface IThing",
            "{",
            "    int  A( ) ;",
            "",
            "",
            "    // c",
            "    int B();",
            "}",
            "")));

        var result = await _engine.SyncInterfaceToImplementationAsync("C.cs", "C", "IThing");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That((string)result.FilePath, Does.EndWith("IThing.cs"));
        Assert.That(result.UpdatedText, Is.EqualTo(Src(crlf,
            "public interface IThing",
            "{",
            "    int  A( ) ;",
            "",
            "",
            "    // c",
            "    int B();",
            "    int Extra();",
            "}",
            "")));
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

        Assert.That((await RunAsync(site)).Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
    }

    [TestCase(OptSite)]
    [TestCase(MethodSite)]
    [TestCase(PropSite)]
    public async Task MixedEol_StrayCrlfOnAnchorLine_ByteExact(string site)
    {
        var s = GetSite(site);
        // Changed lines take the resolved EOL, not the majority one; Lf keeps this LF-majority mixed source's pinned expectation.
        _workspaceManager.EolModeOverride = EolMode.Lf;
        SetFiles(("Test.cs", WithStrayCrlf(Src(false, s.Input), s.Anchor)));

        Assert.That(await RunAsync(site), Is.EqualTo(WithStrayCrlf(Src(false, s.Expected), s.Anchor)));
    }
}
