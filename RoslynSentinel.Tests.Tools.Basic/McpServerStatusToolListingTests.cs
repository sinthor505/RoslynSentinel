using System.Text.Json;
using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Category("McpServerStatusDeclaredTool")] // sentinel:auto-category
[Category("McpServerStatusResult")] // sentinel:auto-category
[Category("McpServerStatusToolListing")] // sentinel:auto-category
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

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatus("test reason", listing, filter);
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
    public void Status_ReportsServerBuildIdentity_WithAbsoluteBinaryPath()
    {
        var status = Call([], McpServerStatusToolListing.none);

        Assert.That(status.ServerVersion, Is.EqualTo(ServerBuildInfo.Version));
        Assert.That(status.ServerBuildTimeUtc, Is.EqualTo(ServerBuildInfo.BuildTimeUtc));
        Assert.That(status.ServerBinaryPath, Is.EqualTo(ServerBuildInfo.BinaryPath));
        Assert.That(status.ServerPid, Is.EqualTo(Environment.ProcessId));
    }

    [Test]
    public void McpServerStatus_ReportsSolutionLoadStatusAsString()
    {
        var status = Call([], McpServerStatusToolListing.none);

        Assert.That(status.SolutionLoadStatus, Is.EqualTo(SolutionLoadStatus.NotLoaded));

        var json = JsonSerializer.Serialize(status, SharedJsonOptions.Default);
        using var doc = JsonDocument.Parse(json);
        // SharedJsonOptions.Default has no naming policy (PascalCase on the wire from this serializer),
        // so match the name case-insensitively; the assertion is that the enum is a string, not a number.
        var property = doc.RootElement.EnumerateObject()
            .Single(p => string.Equals(p.Name, "solutionLoadStatus", StringComparison.OrdinalIgnoreCase))
            .Value;

        Assert.That(property.ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(property.GetString(), Is.EqualTo("NotLoaded"));
    }

    [Test]
    public void ResponseEnvelope_DoesNotCarryPerResponseServerInfo()
    {
        // Build identity lives in McpServerStatus only, to keep every other response small.
        var envelope = typeof(SentinelCallToolResult<,>);
        Assert.That(envelope.GetProperty("ServerInfo"), Is.Null);
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

    [Test]
    [Category("ServerStatusTools")] // sentinel:auto-category
    public void ClaudeLeanAllowList_MarksToolsOutsideItInactive_AndHintsAtWiderModes()
    {
        // claude-lean activates WorkspaceTools/GitTools/... as classes, but only the allow-listed tools.
        var modes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["WorkspaceTools"] = ["Workspace", "Claude", "claude-lean"],
            ["WorkspaceProjectManagementTools"] = ["Workspace", "Claude"],
            ["GitTools"] = ["Workspace", "Claude", "claude-lean"],
            ["DocumentationTools"] = ["Workspace", "Claude"],
        };
        var none = new HashSet<string>();
        var tools = new ServerStatusTools(
            _manager,
            new ActiveToolSurface(
                "claude-lean",
                new HashSet<string> { "claude-lean" },
                none,
                none,
                new HashSet<string> { "WorkspaceTools", "GitTools" },
                modes,
                allowedToolNames: new HashSet<string> { "Build", "Git" }),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var status = ((SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatus("test reason", McpServerStatusToolListing.all, null)).SuccessData!;
        McpServerStatusDeclaredTool Tool(string name) => status.AllDeclaredTools.Single(t => t.Name == name);

        Assert.That(Tool("Build").ActiveForThisMode, Is.True);
        Assert.That(Tool("Git").ActiveForThisMode, Is.True);

        var createProject = Tool("CreateProject");
        Assert.That(createProject.ActiveForThisMode, Is.False, "Class is active but the tool is not allow-listed.");
        Assert.That(createProject.EnabledBy, Does.Contain("claude-lean").And.Contain("--mode Workspace or Claude"));
        Assert.That(createProject.EnabledBy, Does.Not.Contain("--include-tools"));

        // Under an allow-list every non-listed tool gets the claude-lean hint, even if its class is not active,
        // because --include-tools cannot widen an allow-list.
        Assert.That(Tool("ProjectDoc").EnabledBy, Does.Contain("claude-lean").And.Contain("--mode Workspace or Claude"));
        Assert.That(Tool("ProjectDoc").EnabledBy, Does.Not.Contain("--include-tools"));
        Assert.That(status.InactiveToolCount, Is.GreaterThan(2));
    }

    [Test]
    [Category("ServerStatusTools")] // sentinel:auto-category
    public void NoAllowList_InactiveClassKeepsOrdinaryClassLevelHint()
    {
        var modes = new Dictionary<string, IReadOnlyList<string>>
        {
            ["WorkspaceTools"] = ["Workspace", "Claude"],
            ["DocumentationTools"] = ["Workspace", "Claude"],
        };
        var none = new HashSet<string>();
        var tools = new ServerStatusTools(
            _manager,
            new ActiveToolSurface(
                "Workspace",
                new HashSet<string> { "Workspace" },
                none,
                none,
                new HashSet<string> { "WorkspaceTools" },
                modes),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var status = ((SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatus("test reason", McpServerStatusToolListing.all, null)).SuccessData!;

        Assert.That(
            status.AllDeclaredTools.Single(t => t.Name == "ProjectDoc").EnabledBy,
            Is.EqualTo("--mode Workspace or Claude, or --include-tools DocumentationTools"));
    }
}
