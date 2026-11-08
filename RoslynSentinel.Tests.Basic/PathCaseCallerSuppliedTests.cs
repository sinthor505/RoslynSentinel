using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// Caller-supplied path spellings (docs/current/issues/issue_path_comparison_case_sensitivity.md): the same file can reach a tool with a
// different drive-letter or folder case than Document.FilePath holds. Same-file detection must ignore that case.
[TestFixture]
[Category("PathComparison")]
public class PathCaseCallerSuppliedTests
{
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
}
