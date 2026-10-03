// AdminTools -> tests for the "Admin"-mode-gated reconciliation tools.
// See docs/current/ideas/external-drift-hard-blocker.md.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
public class AdminToolsTests
{
    private IWorkspaceManager _workspaceManager;
    private AdminTools _tools;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tools = new AdminTools(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void ListExternalDiskChanges_Always_ReturnsList()
    {
        var result = _tools.ListExternalDiskChanges(reason: "test message");
        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public void AcknowledgeExternalFileChanges_Always_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => _tools.AcknowledgeExternalFileChanges(reason: "test message"));
    }
}
