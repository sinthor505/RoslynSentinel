using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
public class McpServerStatusToolListingTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp() =>
        _manager = new PersistentWorkspaceManager(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>.Instance);

    [TearDown]
    public void TearDown() => _manager.Dispose();

    private McpServerStatusResult Call(
        IEnumerable<string> activeClasses,
        McpServerStatusToolListing listing,
        string? filter = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? classModes = null,
        IEnumerable<string>? excluded = null)
    {
        var active = new HashSet<string>(activeClasses);
        var none = new HashSet<string>();
        var tools = new ServerStatusTools(
            _manager,
            new ActiveToolSurface("test", none, none, new HashSet<string>(excluded ?? []), active, classModes),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatus(listing, filter);
        return result.SuccessData!;
    }

    [Test]
    public void DefaultListing_OmitsTools_ButStillReportsCounts()
    {
        var status = Call(["WorkspaceTools"], McpServerStatusToolListing.none);

        Assert.That(status.AllDeclaredTools, Is.Empty);
        Assert.That(status.DeclaredToolCount, Is.GreaterThan(0));
        Assert.That(status.InactiveToolCount, Is.GreaterThan(0));
    }

    [Test]
    public void AllListing_HasOneEntryPerToolName_PreferringTheActiveClass()
    {
        var status = Call(["WorkspaceTools"], McpServerStatusToolListing.all);

        Assert.That(status.AllDeclaredTools.Select(t => t.Name), Is.Unique,
            "A tool name declared by both a facade and its split class must be reported once.");
        var build = status.AllDeclaredTools.Single(t => t.Name == "Build");
        Assert.That(build.ActiveForThisMode, Is.True);
        Assert.That(build.ClassName, Is.EqualTo("WorkspaceTools"));
    }

    [Test]
    public void McpServerStatus_IsReportedActive_EvenWhenNoClassesAreActive()
    {
        var status = Call([], McpServerStatusToolListing.all);

        var self = status.AllDeclaredTools.Single(t => t.Name == "McpServerStatus");
        Assert.That(self.ActiveForThisMode, Is.True);
        Assert.That(self.EnabledBy, Is.Null);
    }

    [Test]
    public void InactiveListing_ExcludesActiveTools_AndEachEntryExplainsHowToEnableIt()
    {
        var modes = new Dictionary<string, IReadOnlyList<string>> { ["GitTools"] = ["Workspace", "Claude"] };
        var status = Call(["WorkspaceTools"], McpServerStatusToolListing.inactive, classModes: modes);

        Assert.That(status.AllDeclaredTools, Is.Not.Empty);
        Assert.That(status.AllDeclaredTools, Has.None.Matches<McpServerStatusDeclaredTool>(t => t.ActiveForThisMode));
        Assert.That(status.AllDeclaredTools, Has.All.Matches<McpServerStatusDeclaredTool>(t => !string.IsNullOrEmpty(t.EnabledBy)));
        var git = status.AllDeclaredTools.Single(t => t.Name == "Git");
        Assert.That(git.EnabledBy, Is.EqualTo("--mode Workspace or Claude, or --include-tools GitTools"));
    }

    [Test]
    public void ExcludedClass_SaysToRemoveItFromExcludeTools()
    {
        var status = Call([], McpServerStatusToolListing.inactive, filter: "Git", excluded: ["GitTools"]);

        Assert.That(status.AllDeclaredTools.Single(t => t.Name == "Git").EnabledBy,
            Does.Contain("--exclude-tools GitTools"));
    }

    [Test]
    public void ToolNameFilter_MatchesToolOrClassName_CaseInsensitively()
    {
        var status = Call([], McpServerStatusToolListing.all, filter: "gittools");

        Assert.That(status.AllDeclaredTools, Is.Not.Empty);
        Assert.That(status.AllDeclaredTools.Select(t => t.ClassName), Has.All.EqualTo("GitTools"));
    }
}
