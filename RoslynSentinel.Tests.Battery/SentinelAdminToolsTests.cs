// SentinelAdminTools — tests for the "Admin"-mode-gated reconciliation tools.
// See docs/current/ideas/external-drift-hard-blocker.md.

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class SentinelAdminToolsTests
{
    private IWorkspaceManager _workspaceManager;
    private RoslynSentinel.Server.Basic.SentinelAdminTools _tools;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tools = new RoslynSentinel.Server.Basic.SentinelAdminTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void ListExternalDiskChanges_Always_ReturnsList()
    {
        var result = _tools.ListExternalDiskChanges();
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void AcknowledgeExternalFileChanges_Always_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _tools.AcknowledgeExternalFileChanges());
    }
}
