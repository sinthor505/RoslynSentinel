using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The not-found text from <see cref="SymbolWireResolver.ResolveAsync"/> with the caller's callable-tool predicate:
/// no predicate (full surface) keeps today's text, and a lean predicate names no tool outside its set.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // Not parallel: shares ToolCallHint's process-wide catalog with the other hint tests.
public class SurfaceAwareHintsSymbolResolverTests
{
    private const string ProjectName = "TestProj";
    private const string MissingId = "M:Missing.Nope.DoThing";

    private static Solution BuildSolution() =>
        TestSolutionBuilder.CreateSolutionWithProject(ProjectName, new[] { (OrderPricingFixture.FileName, OrderPricingFixture.Source) });

    private static async Task<string?> NotFoundTextAsync(Func<string, bool>? isCallable = null)
    {
        var solution = BuildSolution();
        var result = await SymbolWireResolver.ResolveAsync(
            (handle, ct) => Task.FromResult<ISymbol?>(null),
            ct => Task.FromResult(solution),
            ProjectName,
            MissingId,
            CancellationToken.None,
            isCallable: isCallable);
        return result.Error?.Message;
    }

    [Test]
    public async Task ResolveAsync_FullSurface_NotFoundTextIsTheGoldenText()
    {
        Assert.That(await NotFoundTextAsync(), Is.EqualTo(GoldenText()));
    }

    private static string GoldenText() =>
    "Symbol 'DoThing' (method) was not found in project '" + ProjectName + "'. No symbol in the solution has a similar name. To see what exists, call "
    + ToolCallHint.Call("ListAll", ("kind", "method"), ("projectName", ProjectName))
    + ". If it is not in that list, the symbol does not exist in this solution.";

    [Test]
    public async Task ResolveAsync_AllToolsCallable_NotFoundTextIsTheSameGoldenText()
    {
        Assert.That(await NotFoundTextAsync(_ => true), Is.EqualTo(GoldenText()));
    }

    private static readonly string[] LeanTools =
{
        "ReadFile", "GetFileOutline", "Search", "ListSolutionItems", "GetLargeResult", "ReplaceSnippet", "Member",
        "RenameSymbol", "CreateFile", "DeleteFile", "Build", "RunTest", "GetMethodSource", "UndoLastApply",
        "ChangeSignature", "ChangeAccessibility", "ModifyModifier", "UsingDirective",
    };

    [Test]
    public async Task ResolveAsync_LeanSurface_NamesOnlyCallableToolsAndNoListAll()
    {
        var lean = new HashSet<string>(LeanTools);

        var text = await NotFoundTextAsync(name => lean.Contains(name));

        Assert.That(text, Is.Not.Null);
        Assert.That(text, Does.Not.Contain("ListAll"));
        foreach (Match match in Regex.Matches(text!, @"\b([A-Z][A-Za-z]+)\(reason:"))
        {
            Assert.That(lean, Does.Contain(match.Groups[1].Value), "Rendered call to a tool outside the lean surface");
        }
    }
}
