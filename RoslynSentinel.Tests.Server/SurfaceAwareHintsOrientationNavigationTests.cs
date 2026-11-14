// Coverage for the orientation-breaker fallback text (ServiceRegistrationExtensionsBasic.OrientationBreakerFallbackText).
// Golden cases pin today's full-surface wording: with no surface (null) or every tool callable, the text names
// ListAll and ListSolutionItems exactly as before. Lean cases use a claude-lean style callable set and must name only
// the listing tool that is callable. Expected strings are built from ToolCallHint so the rendered call shape is the
// oracle, not a hand-copied literal.
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

[TestFixture]
public class SurfaceAwareHintsOrientationNavigationTests
{
    private static string SearchCall() => ToolCallHint.Call("Search", ("mode", "text"));

    private static string ListAllCall() => ToolCallHint.Call("ListAll", ("kind", "all"));

    private static string ListSolutionItemsCall() => ToolCallHint.Call("ListSolutionItems", ("kind", "all"));

    private static string FullSurfaceText() =>
        SearchCall() + " is DISABLED. You MUST call " + ListAllCall() + " or " + ListSolutionItemsCall() + " now.";

    [Test]
    public void FullSurface_NullIsCallable_NamesBothListingToolsAsToday()
    {
        var text = RoslynSentinelServiceExtensionsBasic.OrientationBreakerFallbackText(null);

        Assert.That(text, Is.EqualTo(FullSurfaceText()));
    }

    [Test]
    public void FullSurface_AllToolsCallable_NamesBothListingToolsAsToday()
    {
        var text = RoslynSentinelServiceExtensionsBasic.OrientationBreakerFallbackText(_ => true);

        Assert.That(text, Is.EqualTo(FullSurfaceText()));
    }

    // The lean claude surface as the fallback sees it: no ListAll (the lean allow-list hides it), ListSolutionItems present.
    private static readonly HashSet<string> LeanCallable = new(StringComparer.Ordinal)
    {
        "ReadFile", "GetFileOutline", "Search", "ListSolutionItems", "GetLargeResult", "ReplaceSnippet", "Member",
        "RenameSymbol", "CreateFile", "DeleteFile", "Build", "RunTest", "GetMethodSource", "UndoLastApply",
        "ChangeSignature", "ChangeAccessibility", "ModifyModifier", "UsingDirective",
    };

    [Test]
    public void Lean_ListAllHidden_NamesOnlyListSolutionItems()
    {
        var text = RoslynSentinelServiceExtensionsBasic.OrientationBreakerFallbackText(name => LeanCallable.Contains(name));

        Assert.That(text, Is.EqualTo(SearchCall() + " is DISABLED. You MUST call " + ListSolutionItemsCall() + " now."));
        Assert.That(text, Does.Not.Contain("ListAll"));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\b([A-Z][A-Za-z]+)\(reason:"))
        {
            Assert.That(LeanCallable, Does.Contain(match.Groups[1].Value));
        }
    }

    [Test]
    public void Lean_NothingCallable_NamesNoListingTool()
    {
        var text = RoslynSentinelServiceExtensionsBasic.OrientationBreakerFallbackText(_ => false);

        Assert.That(text, Is.EqualTo(SearchCall() + " is DISABLED. No listing tool is callable on this surface."));
        Assert.That(text, Does.Not.Contain("ListAll"));
        Assert.That(text, Does.Not.Contain("ListSolutionItems"));
    }
}
