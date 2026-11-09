// Observed-content ledger (read-ledger plan step 2): the FakeWorkspaceManager store must behave like the
// PersistentWorkspaceManager store, both must compose IObservedContentLedger through IWorkspaceManager, and
// the StaleRead error code is pinned to its wire string.

#pragma warning disable CS8618

using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests;

[TestFixture]
public class ObservedContentLedgerFakeParityTests
{
    private FilePathWrapper _file;

    [SetUp]
    public void Setup()
    {
        _file = new FilePathWrapper(Path.Combine(Path.GetTempPath(), "observed-ledger-parity.cs"));
        ToolCallContext.Set(null);
    }

    [TearDown]
    public void TearDown() => ToolCallContext.Set(null);

    private static ToolCallContext AgentContext(string agentId) =>
        new(agentId, "test-agent", "TestTool", "observed-ledger parity test", 0);

    [Test]
    public void RecordObserved_FakeAndPersistent_YieldEqualHashForSameText()
    {
        const string text = "parity text";
        using var fake = FakeWorkspaceManager.ReadWrite();
        using var persistent = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);

        fake.RecordObserved(_file, text);
        persistent.RecordObserved(_file, text);

        Assert.That(fake.TryGetObservedHash(_file, out var fakeHash), Is.True);
        Assert.That(persistent.TryGetObservedHash(_file, out var persistentHash), Is.True);
        Assert.That(fakeHash, Is.EqualTo(persistentHash));
        Assert.That(fakeHash, Is.EqualTo(ContentFingerprint.Compute(text)));
    }

    [Test]
    public void FakeWorkspaceManager_PerAgentIsolation_EachAgentAndNoContextHasOwnEntry()
    {
        const string text1 = "fake agent A content";
        const string text2 = "fake agent B content";
        const string text3 = "fake no-context content";
        using var fake = FakeWorkspaceManager.ReadWrite();

        Assert.That(fake.TryGetObservedHash(_file, out _), Is.False);

        ToolCallContext.Set(AgentContext("A"));
        fake.RecordObserved(_file, text1);

        ToolCallContext.Set(AgentContext("B"));
        Assert.That(fake.TryGetObservedHash(_file, out _), Is.False,
            "Agent B must not see a hash recorded by agent A.");
        fake.RecordObserved(_file, text2);

        ToolCallContext.Set(AgentContext("A"));
        Assert.That(fake.TryGetObservedHash(_file, out var hashA), Is.True);
        Assert.That(hashA, Is.EqualTo(ContentFingerprint.Compute(text1)),
            "Agent A's entry must survive agent B's write.");

        ToolCallContext.Set(null);
        Assert.That(fake.TryGetObservedHash(_file, out _), Is.False,
            "The null (no-context) key is separate from every agent key.");
        fake.RecordObserved(_file, text3);
        Assert.That(fake.TryGetObservedHash(_file, out var hashNull), Is.True);
        Assert.That(hashNull, Is.EqualTo(ContentFingerprint.Compute(text3)));
    }

    [Test]
    public void IWorkspaceManager_FakeAndPersistent_ComposeIObservedContentLedger()
    {
        using var fake = FakeWorkspaceManager.ReadWrite();
        using var persistent = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        IWorkspaceManager fakeManager = fake;
        IWorkspaceManager persistentManager = persistent;

        Assert.That(fakeManager, Is.InstanceOf<IObservedContentLedger>());
        Assert.That(persistentManager, Is.InstanceOf<IObservedContentLedger>());
        Assert.That(() => ((IObservedContentLedger)fakeManager).RecordObserved(_file, "fake cast"), Throws.Nothing);
        Assert.That(() => ((IObservedContentLedger)persistentManager).RecordObserved(_file, "persistent cast"), Throws.Nothing);
    }

    [Test]
    public void ToolErrorCode_StaleRead_EqualsWireString()
    {
        Assert.That(ToolErrorCode.StaleRead, Is.EqualTo("StaleRead"));
    }
}
