using NUnit.Framework;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tool-naming tails of the ListAll truncation warning and the SearchSolutionText zero-match warning. Null (or an
/// all-true delegate) must reproduce today's full-surface text exactly; a restricted surface names only callable tools.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class SurfaceAwareHintsNavigationEngineTests
{
    private static readonly string FullTruncationAdvice =
        "For all callers/implementations of a symbol use " + ToolCallHint.Call("Search", ("mode", "references")) + " or " + ToolCallHint.Call("QuerySymbolRelationships") + "; for orientation use " + ToolCallHint.Call("ListAll") + " or " + ToolCallHint.Call("GetFileOutline") + ".";

    private static readonly string FullNoMatchAdvice =
        "Use " + ToolCallHint.Call("ListAll") + " to browse the solution's structure, ProjectDoc to read plan/handoff/documentation files directly, or GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file.";

    // The lean ModelEval surface: only these 18 tools are callable.
    private static readonly HashSet<string> LeanTools = new()
    {
        "ReadFile", "GetFileOutline", "Search", "ListSolutionItems", "GetLargeResult", "ReplaceSnippet", "Member", "RenameSymbol", "CreateFile",
        "DeleteFile", "Build", "RunTest", "GetMethodSource", "UndoLastApply", "ChangeSignature", "ChangeAccessibility", "ModifyModifier", "UsingDirective",
    };

    private static readonly Func<string, bool> LeanSurface = name => LeanTools.Contains(name);

    [Test]
    public void TruncationRecoveryAdvice_NullSurface_MatchesHeadText()
    {
        Assert.That(WorkspaceReadNavigationImpl.TruncationRecoveryAdvice(null), Is.EqualTo(FullTruncationAdvice));
    }

    [Test]
    public void TruncationRecoveryAdvice_EverythingCallable_MatchesHeadText()
    {
        Assert.That(WorkspaceReadNavigationImpl.TruncationRecoveryAdvice(_ => true), Is.EqualTo(FullTruncationAdvice));
    }

    [Test]
    public void NoMatchBrowseAdvice_NullSurface_MatchesHeadText()
    {
        Assert.That(WorkspaceReadNavigationImpl.NoMatchBrowseAdvice(null), Is.EqualTo(FullNoMatchAdvice));
    }

    [Test]
    public void NoMatchBrowseAdvice_EverythingCallable_MatchesHeadText()
    {
        Assert.That(WorkspaceReadNavigationImpl.NoMatchBrowseAdvice(_ => true), Is.EqualTo(FullNoMatchAdvice));
    }

    [Test]
    public void TruncationRecoveryAdvice_LeanSurface_NamesOnlyCallableTools()
    {
        var text = WorkspaceReadNavigationImpl.TruncationRecoveryAdvice(LeanSurface);
        var expected = "For all callers/implementations of a symbol use " + ToolCallHint.Call("Search", ("mode", "references")) + "; for orientation use " + ToolCallHint.Call("ListSolutionItems") + " or " + ToolCallHint.Call("GetFileOutline") + ".";

        Assert.That(text, Is.EqualTo(expected));
        Assert.That(text, Does.Not.Contain("ListAll"));
        Assert.That(text, Does.Not.Contain("QuerySymbolRelationships"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\b([A-Z][A-Za-z]+)\(reason:"))
        {
            Assert.That(LeanTools, Does.Contain(m.Groups[1].Value));
        }
    }

    [Test]
    public void NoMatchBrowseAdvice_LeanSurface_NamesOnlyCallableTools()
    {
        var text = WorkspaceReadNavigationImpl.NoMatchBrowseAdvice(LeanSurface);
        var expected = "Use " + ToolCallHint.Call("ListSolutionItems") + " to browse the solution's structure, or GetFileOutline to get the constructors, members, enums, fields, properties, etc of a file.";

        Assert.That(text, Is.EqualTo(expected));
        Assert.That(text, Does.Not.Contain("ListAll"));
        Assert.That(text, Does.Not.Contain("QuerySymbolRelationships"));
        Assert.That(text, Does.Not.Contain("ProjectDoc"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\b([A-Z][A-Za-z]+)\(reason:"))
        {
            Assert.That(LeanTools, Does.Contain(m.Groups[1].Value));
        }
    }

    [Test]
    public void Advice_NothingCallable_ReturnsEmptyText()
    {
        Assert.That(WorkspaceReadNavigationImpl.TruncationRecoveryAdvice(_ => false), Is.EqualTo(""));
        Assert.That(WorkspaceReadNavigationImpl.NoMatchBrowseAdvice(_ => false), Is.EqualTo(""));
    }
}
