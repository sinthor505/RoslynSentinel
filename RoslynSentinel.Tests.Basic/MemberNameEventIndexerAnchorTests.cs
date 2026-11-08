using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// MemberRefactoringEngine.InsertMemberAfterAsync with event and indexer anchors: SyntaxTargetResolver.GetMemberName names
/// a field-like event, an accessor event and an indexer ("this"), so an insert-after can target each of them.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
[Category("MemberNameEventIndexerAnchor")]
public class MemberNameEventIndexerAnchorTests
{
    private const string Source = "public class C { public event System.EventHandler Changed; public event System.EventHandler Moved { add { } remove { } } public int this[int i] { get { return i; } } public void Last() { } }";

    private IWorkspaceManager _workspaceManager;

    private MemberRefactoringEngine _memberRefactoringEngine;

    private SymbolNavigationEngine _symbolNavigationEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(
            _workspaceManager,
            _symbolNavigationEngine,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager));
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetFiles(params (string FileName, string Source)[] files)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", files);
        _workspaceManager.SetTestSolution(solution);
    }

    private async Task<string> CurrentTextAsync(string fileName)
    {
        var document = _workspaceManager.CurrentSolution!.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);
        return (await document.GetTextAsync()).ToString();
    }

    [Test]
    public async Task InsertAfter_FieldLikeEventAnchor_InsertsAfterEventAndBeforeAccessorEventAsync()
    {
        SetFiles(("C.cs", Source));

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("C.cs", "C", "Changed", "public void Added() { }");

        var text = result.UpdatedText;
        var changed = text.IndexOf("Changed", StringComparison.Ordinal);
        var added = text.IndexOf("public void Added()", StringComparison.Ordinal);
        var moved = text.IndexOf("Moved", StringComparison.Ordinal);
        Assert.That(added, Is.GreaterThan(changed), "Added() follows the field-like event Changed");
        Assert.That(added, Is.LessThan(moved), "Added() precedes the accessor event Moved");
    }

    [Test]
    public async Task InsertAfter_AccessorEventAnchor_InsertsAfterEventBodyAndBeforeIndexerAsync()
    {
        SetFiles(("C.cs", Source));

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("C.cs", "C", "Moved", "public void Added() { }");

        var text = result.UpdatedText;
        var moved = text.IndexOf("Moved", StringComparison.Ordinal);
        var added = text.IndexOf("public void Added()", StringComparison.Ordinal);
        var indexer = text.IndexOf("public int this[int i]", StringComparison.Ordinal);
        Assert.That(added, Is.GreaterThan(moved), "Added() follows the accessor event Moved");
        Assert.That(added, Is.LessThan(indexer), "Added() precedes the indexer");
    }

    [Test]
    public async Task InsertAfter_IndexerAnchorNamedThis_InsertsAfterIndexerAndBeforeLastAsync()
    {
        SetFiles(("C.cs", Source));

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("C.cs", "C", "this", "public void Added() { }");

        var text = result.UpdatedText;
        var indexer = text.IndexOf("public int this[int i]", StringComparison.Ordinal);
        var added = text.IndexOf("public void Added()", StringComparison.Ordinal);
        var last = text.IndexOf("public void Last()", StringComparison.Ordinal);
        Assert.That(added, Is.GreaterThan(indexer), "Added() follows the indexer");
        Assert.That(added, Is.LessThan(last), "Added() precedes Last()");
    }
}
