using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
[Category("MemberRefactoringEngine")]
public class SortMembersNoOpTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private MemberRefactoringEngine _memberRefactoringEngine = null!;
    private SymbolNavigationEngine _symbolNavigationEngine = null!;

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
    public void TearDown() => _workspaceManager?.Dispose();

    private void SetSource(string source, string fileName)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task SortMembers_AlreadySorted_ReturnsAlreadyInTargetState_UnchangedText()
    {
        const string source = @"
public class Sorted
{
    private string _name;
    public void Save() { }
}
";
        SetSource(source, "Sorted.cs");

        var result = await _memberRefactoringEngine.SortMembersAsync("Sorted.cs", "Sorted");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.AlreadyInTargetState));
        Assert.That(result.UpdatedText, Is.EqualTo(source));
    }

    [Test]
    public async Task SortMembers_RunTwice_SecondRunIsAlreadyInTargetState()
    {
        SetSource(@"
public class Repo
{
    public void Save() { }
    private string _name;
}
", "Repo.cs");

        var first = await _memberRefactoringEngine.SortMembersAsync("Repo.cs", "Repo");
        SetSource(first.UpdatedText, "Repo.cs");

        var second = await _memberRefactoringEngine.SortMembersAsync("Repo.cs", "Repo");

        Assert.That(first.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(second.Outcome, Is.EqualTo(EditOutcome.AlreadyInTargetState));
        Assert.That(second.UpdatedText, Is.EqualTo(first.UpdatedText));
    }
}
