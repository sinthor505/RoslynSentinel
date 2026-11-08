using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// Caller-supplied path spellings (docs/current/issues/issue_path_comparison_case_sensitivity.md): the same file can reach a tool with a
// different drive-letter or folder case than Document.FilePath holds. Same-file detection must ignore that case.
[TestFixture]
[Category("PathComparison")]
public class PathCaseCallerSuppliedTests
{
    private IWorkspaceManager _workspaceManager = null!;

    private MemberRefactoringEngine _memberRefactoringEngine = null!;

    private SymbolNavigationEngine _symbolNavigationEngine = null!;

    private SolutionStructureEngine _solutionStructureEngine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _memberRefactoringEngine = Battery.Basic.TestEngines.MemberRefactoringEngine(
            _workspaceManager,
            _symbolNavigationEngine,
            Battery.Basic.TestEngines.ValidationEngine(_workspaceManager));
        _solutionStructureEngine = Battery.Basic.TestEngines.SolutionStructureEngine(_workspaceManager, Battery.Basic.TestEngines.SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    [Test]
    public async Task BuildContainerNotFoundMessage_SameFileDifferentCase_DoesNotPointAtItself()
    {
        const string path = "C:/Proj/Order.cs";
        const string source = "public class Order { }";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(path, source)]);

        // Same text, same file, but the tree arrives with the path spelled in upper case.
        var root = CSharpSyntaxTree.ParseText(source, path: path.ToUpperInvariant()).GetRoot();

        var message = await SyntaxTargetResolver.BuildContainerNotFoundMessageAsync(solution, root, "Order", CancellationToken.None);

        Assert.That(message, Does.Not.Contain("is declared in"));
    }

    [Test]
    public async Task RemoveConstructorParameter_CallerPathDifferentCase_StillRemovesTheBackingField()
    {
        const string source = @"
public class Svc
{
    private readonly string _name;

    public Svc(string name)
    {
        _name = name;
    }
}
";
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Svc.cs", source)]);
        _workspaceManager.SetTestSolution(solution);

        // The workspace holds "Svc.cs"; the caller supplies the same file in upper case.
        var result = await _memberRefactoringEngine.RemoveConstructorParameterAsync("SVC.CS", "Svc", "name");

        Assert.That(result.UpdatedText, Does.Not.Contain("_name"));
    }

    [Test]
    public async Task PreviewMoveFileToNamespaceFolder_UpperCaseCallerPath_StaysAlreadyInCorrectFolder()
    {
        // The document sits in the folder its namespace implies, so the correct spelling needs no move.
        var filePath = Path.Combine(Path.GetTempPath(), "TestProj", "Order.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(filePath, "namespace TestProj;\npublic class Order { }\n")]);
        _workspaceManager.SetTestSolution(solution);

        var result = await _solutionStructureEngine.PreviewMoveFileToNamespaceFolderAsync(filePath.ToUpperInvariant());

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.NoCandidatesFound));
    }

    [Test]
    public async Task NamedArgumentsConvertAsync_FileScopeUpperCaseCallerPath_ConvertsTheDocument()
    {
        const string source = "public class C { public void M(int first, int second) { } public void Run() { M(1, 2); } }";
        var filePath = Path.Combine(Path.GetTempPath(), "TestProj", "Calls.cs");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(filePath, source)]);
        _workspaceManager.SetTestSolution(solution);
        var engine = new NamedArgumentsEngine(_workspaceManager);

        // The workspace holds the document in its original case; the caller passes the same file upper-cased.
        var outcome = await engine.ConvertAsync(NamedArgumentsScope.file, filePath.ToUpperInvariant(), null, new NamedArgumentsOptions(), CancellationToken.None);

        Assert.That(outcome.Changes, Has.Count.EqualTo(1));
        Assert.That(outcome.Changes.Values.Single(), Does.Contain("M(first: 1, second: 2)"));
    }
}
