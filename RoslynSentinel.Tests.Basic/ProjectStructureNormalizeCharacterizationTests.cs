using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice 5b characterization tests (docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md).
/// Byte-exact pin (LF, CRLF and Mixed output) for <c>SolutionStructureEngine.ConvertToBackgroundServiceAsync</c>, which now ends in
/// the EOL-aware <c>RoslynFormattingHelper</c> helpers (formerly a whole-subtree whitespace pass). The input has an untouched, badly spaced member
/// (<c>UntouchedLine</c>) that the rewrite does not change. "Mixed" is LF except the untouched line, which ends in a
/// stray CRLF. The pin encodes the migrated contract (annotated-span formatting): the untouched line keeps its text,
/// changed lines take the document's dominant line ending, and a stray CRLF on an untouched line survives. The
/// pre-migration whole-subtree pass emitted LF for every input and split the untouched member over three lines.
/// </summary>
[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
public class ProjectStructureNormalizeCharacterizationTests
{
    private const string UntouchedLine = "    public void  Untouched( )  { }";

    private IWorkspaceManager _workspaceManager;
    private SolutionStructureEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new SolutionStructureEngine(_workspaceManager, new SentinelConfiguration());
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
    public async Task ConvertToBackgroundService_ByteExact(string eol)
    {
        SetSource(Build(eol,
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "",
            "public class Worker",
            "{",
            UntouchedLine,
            "}"));

        var result = await _engine.ConvertToBackgroundServiceAsync("Test.cs", "Worker");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        AssertPinned(result.UpdatedText, Build(eol,
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "using Microsoft.Extensions.Hosting;",
            "",
            "public class Worker : BackgroundService",
            "{",
            UntouchedLine,
            "",
            "    protected override async Task ExecuteAsync(CancellationToken stoppingToken)",
            "    {",
            "        while (!stoppingToken.IsCancellationRequested)",
            "        {",
            "            await Task.Delay(1000, stoppingToken);",
            "        }",
            "    }",
            "}"));
    }
}
