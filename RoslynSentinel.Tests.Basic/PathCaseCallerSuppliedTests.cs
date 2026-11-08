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
}
