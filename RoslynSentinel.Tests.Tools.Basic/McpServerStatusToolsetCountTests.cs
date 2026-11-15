using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// ToolsetService.ToolListChangedRaised counts only SetEnabled calls that mutated the tool collection, and
/// McpServerStatus reports it. Without a request context, ClientProtocolVersion is null.
/// </summary>
[TestFixture]
public class McpServerStatusToolsetCountTests
{
    private PersistentWorkspaceManager _manager = null!;

    [SetUp]
    public void SetUp() => _manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<IWorkspaceManager>.Instance);

    [TearDown]
    public void TearDown() => _manager.Dispose();

    private static (ToolsetService Service, McpServerPrimitiveCollection<McpServerTool> Collection) CreateService()
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        var options = Options.Create(new McpServerOptions { ToolCollection = collection });
        var service = new ToolsetService(
            options,
            new ServiceCollection().BuildServiceProvider(),
            new HashSet<string> { nameof(TestCategoryTaggingTools) },
            () => new[] { typeof(TestCategoryTaggingTools).Assembly });
        return (service, collection);
    }

    [Test]
    public void FreshService_CountIsZero()
    {
        var (service, _) = CreateService();

        Assert.That(service.ToolListChangedRaised, Is.EqualTo(0));
    }

    [Test]
    public void EnablingAToolsetThatAddsTools_CountsOne()
    {
        var (service, _) = CreateService();

        var change = service.SetEnabled(ToolSetName.testCategories, true);

        Assert.That(change.AddedTools, Is.Not.Empty, "testCategories should add TagTestCategories from this assembly");
        Assert.That(service.ToolListChangedRaised, Is.EqualTo(1));
    }

    [Test]
    public void SwitchingTheToolsetOff_CountsTwo()
    {
        var (service, _) = CreateService();
        service.SetEnabled(ToolSetName.testCategories, true);

        var change = service.SetEnabled(ToolSetName.testCategories, false);

        Assert.That(change.RemovedTools, Is.Not.Empty);
        Assert.That(service.ToolListChangedRaised, Is.EqualTo(2));
    }

    [Test]
    public void SetEnabledThatChangesNoTools_DoesNotCount()
    {
        var (service, _) = CreateService();

        // SubAgent lives in Tools.Advanced, so enabling modelEval on this Basic-only assembly adds nothing.
        service.SetEnabled(ToolSetName.modelEval, true);

        Assert.That(service.ToolListChangedRaised, Is.EqualTo(0));
    }

    [Test]
    public void McpServerStatus_WithoutRequestContext_ReportsNullProtocolVersionAndCount()
    {
        var (service, _) = CreateService();
        service.SetEnabled(ToolSetName.testCategories, true);
        var none = new HashSet<string>();
        var tools = new ServerStatusTools(
            _manager,
            new ActiveToolSurface("test", none, none, new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null),
            toolsets: service);

        var status = ((SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason", McpServerStatusToolListing.none)).SuccessData!;

        Assert.That(status.ClientProtocolVersion, Is.Null);
        Assert.That(status.ToolListChangedRaised, Is.EqualTo(1));
    }
}
