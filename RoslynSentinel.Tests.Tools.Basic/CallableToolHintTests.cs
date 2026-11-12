using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// FileNotFound and orientation-breaker text must not tell a model to call a tool the server does not serve to it
/// (the claude-lean surface hides ListSolutionItems and ListAll). Each builder takes an optional "is this tool callable"
/// delegate: null keeps the full-surface text, a restricted one drops the tool name, a permissive one keeps it.
/// </summary>
[TestFixture]
[Category("WorkspaceTools")] // sentinel:auto-category
public class CallableToolHintTests
{
    private static readonly string[] ListingTools = ["ListSolutionItems", "ListAll"];

    // Only ReadFile and GetLargeResult can be called: the surface of the ReadFile-only scenario.
    private static readonly Func<string, bool> OnlyReadAndPage = name => name is "ReadFile" or "GetLargeResult";

    // Everything is callable, spelled out as a delegate (as opposed to null = no surface at all).
    private static readonly Func<string, bool> Everything = _ => true;

    private static ActiveToolSurface Surface(IReadOnlySet<string>? allowedToolNames, params string[] activeClasses)
        => new("test", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(activeClasses), null, allowedToolNames);

    private static Solution SolutionWithoutTheRequestedFile()
    {
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P", LanguageNames.CSharp);
        workspace.AddDocument(project.Id, "Other.cs", SourceText.From("class A { }"));
        return workspace.CurrentSolution;
    }

    private static void AssertNamesNoListingTool(string? text)
    {
        foreach (var tool in ListingTools)
        {
            Assert.That(text, Does.Not.Contain(tool), $"text must not name {tool}, which is not callable:\n{text}");
        }
    }

    private static DocumentLookupResult NotFoundWithoutCandidates()
        => new(DocumentLookupStatus.NotFound, "Missing.cs", null, [], 3);

    // ---- ActiveToolSurface.IsCallable ----

    [Test]
    public void IsCallable_UsesTheMcpServerStatusRule_ClassActiveAndNameAllowed()
    {
        var classes = new[] { "WorkspaceTools", "WorkspaceReadNavigationTools" };
        var allowList = new HashSet<string> { "ReadFile", "GetLargeResult" };

        var restricted = Surface(allowList, classes);
        Assert.That(restricted.IsCallable("ReadFile"), Is.True, "on the allow-list and its class is active");
        Assert.That(restricted.IsCallable("GetLargeResult"), Is.True);
        Assert.That(restricted.IsCallable("ListSolutionItems"), Is.False, "class is active but the name is not on the allow-list");
        Assert.That(restricted.IsCallable("ListAll"), Is.False);

        var open = Surface(null, classes);
        Assert.That(open.IsCallable("ListSolutionItems"), Is.True, "no allow-list: every tool of an active class");
        Assert.That(open.IsCallable("NoSuchTool"), Is.False, "a name no class declares is not callable");

        var noClasses = Surface(null);
        Assert.That(noClasses.IsCallable("ReadFile"), Is.False, "name allowed but no declaring class is active");
    }

    // ---- DocumentLookup.Describe ----

    [Test]
    public void DocumentLookupDescribe_OnlyReadFileAndPagingCallable_NamesNoListingTool()
    {
        var text = NotFoundWithoutCandidates().Describe(OnlyReadAndPage);

        AssertNamesNoListingTool(text);
        Assert.That(text, Does.Not.Contain("You MUST call"));
        Assert.That(text, Does.Contain("already seen"));
    }

    [Test]
    public void DocumentLookupDescribe_DefaultAndFullSurface_OffersListSolutionItems()
    {
        var lookup = NotFoundWithoutCandidates();

        var unchanged = lookup.Describe();
        Assert.That(unchanged, Does.Contain("call ListSolutionItems("), "null = every tool callable: the text callers saw before");
        Assert.That(unchanged, Does.Not.Contain("You MUST"));
        Assert.That(unchanged, Does.Contain("does not exist in this solution"));
        Assert.That(lookup.Describe(Everything), Is.EqualTo(unchanged), "a permissive delegate gives the same text as none");
        Assert.That(lookup.Message, Is.EqualTo(unchanged), "the Message property keeps the default text");
    }

    [Test]
    public void DocumentLookupToResultError_PassesTheCheckThrough()
    {
        var error = NotFoundWithoutCandidates().ToResultError(OnlyReadAndPage);

        Assert.That(error.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        AssertNamesNoListingTool(error.Message);
    }

    // ---- WorkspaceReadNavigationImpl.BuildFileNotFoundError (GetMethodSource / GetFileOutline) ----

    [Test]
    public void ReadNavigationFileNotFound_OnlyReadFileAndPagingCallable_NamesNoListingTool()
    {
        var error = WorkspaceReadNavigationImpl.BuildFileNotFoundError(SolutionWithoutTheRequestedFile(), "C:\\x\\Missing.cs", OnlyReadAndPage);

        Assert.That(error.ErrorCode, Is.EqualTo("FileNotFound"));
        AssertNamesNoListingTool(error.Message);
        Assert.That(error.Message, Does.Not.Contain("You MUST call"));
        Assert.That(error.Message, Does.Contain("already seen"));
    }

    [Test]
    public void ReadNavigationFileNotFound_DefaultAndListSolutionItemsCallable_OffersListSolutionItems()
    {
        var solution = SolutionWithoutTheRequestedFile();

        var unchanged = WorkspaceReadNavigationImpl.BuildFileNotFoundError(solution, "C:\\x\\Missing.cs");
        Assert.That(unchanged.Message, Does.Contain("call ListSolutionItems("));
        Assert.That(unchanged.Message, Does.Not.Contain("You MUST"));
        Assert.That(unchanged.Message, Does.Contain("does not exist in this solution"));

        Assert.That(unchanged.Message, Does.Contain("CreateFile("), "every tool callable: the not-yet-created hint is shown");

        var onlyListing = WorkspaceReadNavigationImpl.BuildFileNotFoundError(solution, "C:\\x\\Missing.cs", name => name == "ListSolutionItems");
        Assert.That(onlyListing.Message, Does.Contain("call ListSolutionItems("));
        Assert.That(onlyListing.Message, Does.Not.Contain("CreateFile"), "CreateFile is not callable, so it is not named");
    }

    // ---- WorkspaceFileEditImpl.BuildFileNotFoundError (ReadFile) ----

    [Test]
    public void ReadFileNotFound_OnlyReadFileAndPagingCallable_NamesNoListingTool()
    {
        var error = WorkspaceFileEditImpl.BuildFileNotFoundError(SolutionWithoutTheRequestedFile(), "C:\\x\\Missing.cs", OnlyReadAndPage);

        Assert.That(error.ErrorCode, Is.EqualTo("FileNotFound"));
        AssertNamesNoListingTool(error.Message);
        Assert.That(error.Message, Does.Not.Contain("You MUST call"));
        Assert.That(error.Message, Does.Contain("Retry only with a path you have already seen"));
    }

    [Test]
    public void ReadFileNotFound_DefaultAndListSolutionItemsCallable_OffersListSolutionItems()
    {
        var solution = SolutionWithoutTheRequestedFile();

        var unchanged = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, "C:\\x\\Missing.cs");
        Assert.That(unchanged.Message, Does.Contain("call ListSolutionItems("));
        Assert.That(unchanged.Message, Does.Not.Contain("You MUST"));
        Assert.That(unchanged.Message, Does.Contain("does not exist in this solution"));

        var viaSurface = WorkspaceFileEditImpl.BuildFileNotFoundError(solution, "C:\\x\\Missing.cs",
            Surface(null, "WorkspaceTools").IsCallable);
        Assert.That(viaSurface.Message, Is.EqualTo(unchanged.Message), "a real surface where ListSolutionItems is callable keeps the sentence");
    }

    // ---- OrientationCircuitBreaker.StateMessage ----

    private static OrientationCircuitBreaker TrippedBreaker()
    {
        var breaker = new OrientationCircuitBreaker(NullLogger.Instance, tripThreshold: 2);
        breaker.RecordSearchOutcome(0);
        Assert.That(breaker.RecordSearchOutcome(0), Is.True, "the second zero-match search trips it");
        return breaker;
    }

    [Test]
    public void OrientationStateMessage_OnlyReadFileAndPagingCallable_NamesNoListingTool_AndNoListingCall()
    {
        var text = TrippedBreaker().StateMessage(OnlyReadAndPage);

        Assert.That(text, Is.Not.Null);
        AssertNamesNoListingTool(text);
        Assert.That(text, Does.Not.Contain("GetFileOutline"), "GetFileOutline is not callable either");
        Assert.That(text, Does.Not.Contain("You MUST call"), "no listing tool to call, so no instruction to call one");
        Assert.That(text, Does.Contain("already seen"));
        Assert.That(text, Does.Contain("ReadFile("), "the one callable follow-up tool is still named");
    }

    [Test]
    public void OrientationStateMessage_OneListingToolCallable_NamesOnlyThatOne()
    {
        var text = TrippedBreaker().StateMessage(name => name is "ListAll" or "ReadFile");

        Assert.That(text, Does.Contain("You MUST call ListAll("));
        Assert.That(text, Does.Not.Contain("ListSolutionItems"));
        Assert.That(text, Does.Not.Contain(" or "), "a single browse tool is not joined with 'or'");
    }

    [Test]
    public void OrientationStateMessage_DefaultAndFullSurface_AreTheOriginalText()
    {
        var breaker = TrippedBreaker();

        var unchanged = breaker.StateMessage();
        Assert.That(unchanged, Does.Contain("You MUST call ListAll("));
        Assert.That(unchanged, Does.Contain(" or ListSolutionItems("));
        Assert.That(unchanged, Does.Contain("GetFileOutline and ReadFile("));
        Assert.That(unchanged, Does.EndWith("once you have a real path from that list."));
        Assert.That(breaker.StateMessage(Everything), Is.EqualTo(unchanged));
        Assert.That(breaker.StateMessage(null), Is.EqualTo(unchanged));
    }

    [Test]
    public void OrientationStateMessage_NotTripped_IsNullWhateverTheSurface()
    {
        var breaker = new OrientationCircuitBreaker(NullLogger.Instance, tripThreshold: 2);

        Assert.That(breaker.StateMessage(OnlyReadAndPage), Is.Null);
        Assert.That(breaker.StateMessage(), Is.Null);
    }
}
