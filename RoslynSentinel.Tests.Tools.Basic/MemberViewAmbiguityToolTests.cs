// Tool-layer coverage for Member(view) ambiguity: viewing an overloaded member name without a
// contextSnippet returns ErrorCode Ambiguous with structured candidates, and each candidate's
// ContextSnippet, fed back with its FilePath, resolves the view successfully (round-trip).

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
[Category("RefactoringStructuralImpl")]
public class MemberViewAmbiguityToolTests
{
    private const string Source = "public class Holder\n{\n    public void Run(int x) { }\n    public void Run(string s) { }\n}";
    private const string FileName = "MemberViewAmbig.cs";

    private IWorkspaceManager _workspaceManager = null!;
    private RefactoringStructuralImpl _impl = null!;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var symbolNavigationEngine = TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validationEngine = TestEngines.ValidationEngine(_workspaceManager);
        _impl = TestTools.RefactoringStructuralImpl(
            TestEngines.BasicRefactoringEngine(_workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, TestEngines.SentinelConfiguration()),
            TestEngines.MemberRefactoringEngine(_workspaceManager, symbolNavigationEngine, validationEngine),
            TestEngines.StructuralRefinementEngine(_workspaceManager),
            symbolNavigationEngine,
            _workspaceManager,
            validationEngine,
            NullLogger<RefactoringStructuralImpl>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(FileName, Source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    [Test]
    public async Task Member_View_AmbiguousOverloadWithoutSnippet_StructuredCandidatesRoundTrip()
    {
        var ambiguous = await _impl.Member(
            reason: "ambiguity tool-layer test: view overloaded member Run without contextSnippet",
            filePath: FileName,
            operation: MemberAction.view,
            memberName: "Run");

        Assert.That(ambiguous.IsError, Is.True);
        Assert.That(ambiguous.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(ambiguous.ErrorData.StructuredDetail, Is.Not.Null);
        Assert.That(ambiguous.ErrorData.StructuredDetail!, Has.Count.EqualTo(2));
        Assert.That(ambiguous.ErrorData.StructuredDetail!.All(d => d is AmbiguousCandidate), Is.True);

        var candidates = ambiguous.ErrorData.StructuredDetail!.Cast<AmbiguousCandidate>().ToList();
        var pick = candidates[0];
        Assert.That(pick.ContextSnippet, Is.Not.Empty);

        var retry = await _impl.Member(
            reason: "ambiguity tool-layer test: retry view with candidate filePath and snippet",
            filePath: pick.FilePath,
            operation: MemberAction.view,
            memberName: "Run",
            contextSnippet: pick.ContextSnippet);

        Assert.That(retry.IsError, Is.False, retry.ErrorData?.ErrorCode + ": " + retry.ErrorData?.Message);
    }
}
