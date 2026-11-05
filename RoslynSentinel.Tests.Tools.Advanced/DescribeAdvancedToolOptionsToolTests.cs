using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Tests.Battery.Advanced;
using RoslynSentinel.Tools.Advanced;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
[Category("QualityTools")]
public class DescribeAdvancedToolOptionsToolTests
{
    private IWorkspaceManager _workspaceManager;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void DescribeAdvancedToolOptions_KnownTool_ReturnsDescriptionWithoutError()
    {
        var tools = TestToolsAdvanced.QualityTools(_workspaceManager);

        var result = tools.DescribeAdvancedToolOptionsTool(reason: "test message", "scan");

        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.SuccessData.Description, Is.Not.Empty);
        Assert.That(result.SuccessData.Error, Is.Null);
    }

    [Test]
    public void DescribeAdvancedToolOptions_UnknownTool_ReturnsNoFurtherDocumentationNotError()
    {
        var tools = TestToolsAdvanced.QualityTools(_workspaceManager);

        var result = tools.DescribeAdvancedToolOptionsTool(reason: "test message", "no_such_tool");

        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData.Error, Is.Not.Null);
        Assert.That(result.SuccessData.Error.ErrorCode, Is.EqualTo("NoFurtherDocumentation"));
        Assert.That(result.StatusMessage, Is.EqualTo(result.SuccessData.Error.Message));
    }
}
