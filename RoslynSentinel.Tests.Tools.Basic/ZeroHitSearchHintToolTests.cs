// DTO-EXEMPT: test fixture class, not a DTO.
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

// Zero-hit text Search (ForZeroHitSearch) adds an EditAttribution note when another agent's recent write removed the
// term from disk. Each test uses a real PersistentWorkspaceManager rooted in its own temp directory.
[TestFixture]
public class ZeroHitSearchHintToolTests
{
    private const string Original = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";
    private const string ForeignBefore = "namespace T;\r\npublic class Zorblax\r\n{\r\n}\r\n";

    private string _root;

    [SetUp]
    public void Setup()
    {
        ToolCallContext.Set(new ToolCallContext("me", null, "Search", null, 0) { SessionId = "s1" });
        _root = Path.Combine(Path.GetTempPath(), "ZeroHitSearchHint_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string FileIn(string name) => Path.Combine(_root, name);

    // The solution carries a file path under _root: GetSolutionRoot() derives the root (and so the operation blob folder
    // the tool searches) from the solution's own FilePath; SetTestSolutionRoot only feeds the write lock.
    private Solution SolutionWith(string file, string text)
    {
        var workspace = new AdhocWorkspace();
        workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(), Path.Combine(_root, "T.slnx")));
        var project = workspace.AddProject("ZeroHitSearchHint", LanguageNames.CSharp);
        return project.AddDocument(Path.GetFileName(file), SourceText.From(text), filePath: file).Project.Solution;
    }

    // The document holds `documentText` in memory; the test writes whatever disk should hold before calling the tool.
    private PersistentWorkspaceManager NewManager(string file, string documentText)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        manager.EolModeOverride = EolMode.Crlf;
        manager.SetTestSolution(SolutionWith(file, documentText));
        manager.SetTestSolutionRoot(_root);
        return manager;
    }

    // Writes a RenameSymbol blob from another agent whose BeforeSource held `before`, then restores this test's context.
    private async Task WriteForeignBlobAsync(string before)
    {
        var context = new ToolCallContext("other-agent", "implementer", "RenameSymbol", "drop it", 0) { SessionId = "s1" };
        context.MarkWritten(DateTime.UtcNow);
        ToolCallContext.Set(context);
        try
        {
            await OperationBlobWriter.WriteAsync(
                "RenameSymbol",
                Guid.NewGuid().ToString("N")[..8],
                [new OperationItemRecord { FilePath = Path.Combine(_root, "Old.cs"), Outcome = ItemRecordOutcome.Succeeded, BeforeSource = before }],
                _root);
        }
        finally
        {
            ToolCallContext.Set(new ToolCallContext("me", null, "Search", null, 0) { SessionId = "s1" });
        }
    }

    private static void SetMe() => ToolCallContext.Set(new ToolCallContext("me", null, "Search", null, 0) { SessionId = "s1" });

    [Test]
    [CancelAfter(30000)]
    public async Task SearchText_ZeroHits_ForeignRemovalBlob_AddsInfoEditAttributionFinding()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var result = await readNav.SearchSolutionText(reason: "test zero hit search note for a foreign removal", pattern: "Zorblax");

        Assert.That(result.TotalRecords, Is.EqualTo(0));
        var notes = result.Findings.Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Severity, Is.EqualTo(FindingSeverity.Info));
        Assert.That(notes[0].Message, Does.Contain("Zorblax").And.Contain("other-agent"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task SearchText_WithHits_NoEditAttributionFinding()
    {
        var text = Original + "// Zorblax\r\n";
        var file = FileIn("A.cs");
        File.WriteAllText(file, text);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, text);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var result = await readNav.SearchSolutionText(reason: "test zero hit note is skipped when hits exist", pattern: "Zorblax");

        Assert.That(result.TotalRecords, Is.GreaterThan(0));
        Assert.That(result.Findings.Where(f => f.Source == "EditAttribution"), Is.Empty);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task SearchText_ZeroHits_NoBlobs_NoEditAttributionFinding()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var result = await readNav.SearchSolutionText(reason: "test zero hit search with no blobs on disk", pattern: "Zorblax");

        Assert.That(result.TotalRecords, Is.EqualTo(0));
        Assert.That(result.Findings.Where(f => f.Source == "EditAttribution"), Is.Empty);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task AddZeroHitEvidence_SymbolMode_ZeroRecords_ForeignBlob_AddsInfoFinding()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var input = new SentinelCallToolResult<object> { IsError = false, TotalRecords = 0, Findings = [new Finding("X", "pre", FindingSeverity.Info)] };
        var result = readNav.AddZeroHitEvidence(SearchMode.symbol, "Zorblax", input);

        Assert.That(result.Findings, Has.Count.EqualTo(2));
        Assert.That(result.Findings.Where(f => f.Source == "X").ToList(), Has.Count.EqualTo(1));
        var notes = result.Findings.Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Severity, Is.EqualTo(FindingSeverity.Info));
        Assert.That(notes[0].Message, Does.Contain("Zorblax").And.Contain("other-agent"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task AddZeroHitEvidence_WithRecords_Unchanged()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var input = new SentinelCallToolResult<object> { IsError = false, TotalRecords = 3, Findings = [new Finding("X", "pre", FindingSeverity.Info)] };
        var result = readNav.AddZeroHitEvidence(SearchMode.symbol, "Zorblax", input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Findings, Has.Count.EqualTo(1));
        Assert.That(result.Findings.Where(f => f.Source == "EditAttribution"), Is.Empty);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task AddZeroHitEvidence_TextMode_Unchanged()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var input = new SentinelCallToolResult<object> { IsError = false, TotalRecords = 0 };
        var result = readNav.AddZeroHitEvidence(SearchMode.text, "Zorblax", input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Findings, Is.Null.Or.Empty);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task AddZeroHitEvidence_BlankQuery_Unchanged()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var input = new SentinelCallToolResult<object> { IsError = false, TotalRecords = 0 };
        var result = readNav.AddZeroHitEvidence(SearchMode.@class, "", input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Findings, Is.Null.Or.Empty);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task AddZeroHitEvidence_IsError_Unchanged()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        SetMe(); var input = new SentinelCallToolResult<object> { IsError = true, TotalRecords = 0 };
        var result = readNav.AddZeroHitEvidence(SearchMode.symbol, "Zorblax", input);

        Assert.That(result, Is.SameAs(input));
        Assert.That(result.Findings, Is.Null.Or.Empty);
    }

    [Test]
    [CancelAfter(30000)]
    public async Task AddZeroHitEvidence_ReferencesMode_NullTotalRecordsZeroReferences_ForeignBlob_AddsInfoFinding()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync(ForeignBefore);
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        // The shape FindReferences returns for a zero-reference success: TotalRecords unset, empty lists, a zero ListSummary.
        SetMe(); var summary = SummarizeListResult.Build(new List<string>(), p => p);
        var input = new SentinelCallToolResult<object> { IsError = false, SuccessData = new ReferencesSearchResult([], [], summary), ListSummary = summary, StatusMessage = summary.ToSummaryMessage("hit") };
        var result = readNav.AddZeroHitEvidence(SearchMode.references, "Zorblax", input);

        var notes = result.Findings.Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Severity, Is.EqualTo(FindingSeverity.Info));
        Assert.That(notes[0].Message, Does.Contain("Zorblax").And.Contain("other-agent"));
    }

    [Test]
    [CancelAfter(30000)]
    public async Task SearchText_LiteralQueryWithParen_ZeroHits_ForeignRemovalBlob_AddsInfoEditAttributionFinding()
    {
        var file = FileIn("A.cs");
        File.WriteAllText(file, Original);
        await WriteForeignBlobAsync("public void Foo(int x)\r\n{\r\n}\r\n");
        using var manager = NewManager(file, Original);
        var readNav = Battery.Basic.TestTools.WorkspaceReadNavigationImpl(manager);

        // "Foo(" is not a valid regex, so the zero-hit note must match it as a literal substring.
        SetMe(); var result = await readNav.SearchSolutionText(reason: "test zero hit search note for a literal query containing a paren", pattern: "Foo(");

        Assert.That(result.TotalRecords, Is.EqualTo(0));
        var notes = result.Findings.Where(f => f.Source == "EditAttribution").ToList();
        Assert.That(notes, Has.Count.EqualTo(1));
        Assert.That(notes[0].Message, Does.Contain("Foo(").And.Contain("other-agent"));
    }
}
