using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Member(replace) and Member(remove) on an unknown name throw ToolNotFoundException whose message
/// names the file searched, the nearest declared member names and a GetFileOutline/ReadFile next step
/// (shared text from MemberNotFoundDetail.BuildAsync).
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
public class MemberNotFoundEvidenceTests
{
    private IWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _memberRefactoringEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;

    private const string WidgetSource = """
namespace TestProj;
public class Widget
{
    public int Count { get; set; }
    public void Render() { }
    public void RenderAll() { }
}
""";

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(_workspaceManager, _symbolNavigationEngine, Battery.Basic.TestEngines.ValidationEngine(_workspaceManager));
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", new[] { ("Widget.cs", WidgetSource) }));
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private static void AssertNotFoundEvidence(string message)
    {
        Assert.That(message, Does.Contain("Widget.cs"), "The message names the file searched.");
        Assert.That(message, Does.Contain("Render"), "The message lists the nearest declared member names.");
        Assert.That(message, Does.Contain("GetFileOutline").Or.Contain("ReadFile"), "The message ends with a next call.");
    }

    [Test]
    public async Task Member_ReplaceUnknownName_NotFoundNamesFileContainerAndNearestMembers()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _memberRefactoringEngine.ReplaceMemberAsync("Widget.cs", "Rendr", "public void Rendr() { }"));

        AssertNotFoundEvidence(ex!.Message);
    }

    [Test]
    public async Task Member_RemoveUnknownName_NotFoundNamesFileContainerAndNearestMembers()
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _memberRefactoringEngine.RemoveMemberAsync("Widget.cs", "Rendr"));

        AssertNotFoundEvidence(ex!.Message);
    }
}
