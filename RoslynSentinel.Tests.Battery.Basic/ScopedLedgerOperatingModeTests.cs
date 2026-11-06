// Scoped operation ledger x agent operating mode -> MultiAgent refuses TryOpen; SingleAgent records the
// opener and refuses writes from any other agent; Auto keeps today's behaviour.
// Disk tier: the refusals and the allowed writes are proven against real files in a loaded solution.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

// AgentOperatingModeOptions and ToolCallContext are process-wide statics, so these tests must not run in parallel.
[TestFixture]
[NonParallelizable]
public class ScopedLedgerOperatingModeTests
{
    private PersistentWorkspaceManager _workspaceManager;

    [SetUp]
    public void SetUp()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        ToolCallContext.Set(null);
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        _workspaceManager?.Dispose();
    }

    private IScopedOperationLedger Ledger => _workspaceManager;

    // Two real .cs files: the first is the ledger's tracked file, the second is the unrelated write target.
    private (string Tracked, string Unrelated) PickTwoFiles(TestSolutionFixture fixture)
    {
        var files = Directory
            .EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return (files[0], files[1]);
    }

    // Makes the next tool call run as the given agent; null clears the context entirely.
    private static void CallAs(string? agentId, string toolName = "MoveMember") =>
        ToolCallContext.Set(agentId is null
            ? null
            : new ToolCallContext(agentId, "subagent", toolName, "scoped ledger operating mode test", startSeq: 0));

    private bool OpenTrackedLedger(string trackedFile, out string? rejectionReason)
    {
        var trackedPath = _workspaceManager.ResolveFromWire(trackedFile);
        return Ledger.TryOpen(
            "MoveMember",
            [
                new CallSiteLedgerEntry
                {
                    EntryId = "entry-1",
                    FilePath = trackedPath.Absolute,
                    Line = 1,
                    BrokenExpression = "classCLocalVarA.Foo()",
                    OldStaticType = "ClassA",
                    Status = CallSiteStatus.Ambiguous,
                }
            ],
            out rejectionReason,
            openingChangeId: "move-change-id");
    }

    private async Task<ApplyChangesResult> ProposeAppendAsync(string targetFile, string original)
    {
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [_workspaceManager.ResolveFromWire(targetFile)] = original + Environment.NewLine + "// appended by test" + Environment.NewLine
        };
        return await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: false);
    }

    [Test]
    public async Task MultiAgent_TryOpen_IsRejectedAsync()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=multi-agent" });
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, _) = PickTwoFiles(fixture);
        CallAs("A");

        var opened = OpenTrackedLedger(tracked, out var rejectionReason);

        Assert.Multiple(() =>
        {
            Assert.That(opened, Is.False);
            Assert.That(rejectionReason, Does.Contain("MultiAgent"));
            Assert.That(Ledger.OpenOperationName, Is.Null);
        });
    }

    [Test]
    public async Task SingleAgent_TryOpen_Opens_AndRecordsOpener_ThenOtherAgentWriteIsRefusedAsync()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, unrelated) = PickTwoFiles(fixture);
        var unrelatedOriginal = await File.ReadAllTextAsync(unrelated);
        var trackedOriginal = await File.ReadAllTextAsync(tracked);
        CallAs("A", "MoveMember");
        Assert.That(OpenTrackedLedger(tracked, out var openReason), Is.True, openReason);

        CallAs("B");
        var unrelatedResult = await ProposeAppendAsync(unrelated, unrelatedOriginal);
        var trackedResult = await ProposeAppendAsync(tracked, trackedOriginal);

        Assert.Multiple(() =>
        {
            Assert.That(unrelatedResult.IsError, Is.True);
            Assert.That(unrelatedResult.RefusalCode, Is.EqualTo(ToolErrorCode.ScopedLedgerBlocked));
            Assert.That(unrelatedResult.Summary, Does.Contain("'A'"));
            Assert.That(unrelatedResult.Summary, Does.Contain("MoveMember"));
            Assert.That(unrelatedResult.Summary, Does.Contain("MULTI-AGENT:"));
            Assert.That(trackedResult.IsError, Is.True);
            Assert.That(trackedResult.RefusalCode, Is.EqualTo(ToolErrorCode.ScopedLedgerBlocked));
            Assert.That(trackedResult.Summary, Does.Contain("MULTI-AGENT:"));
        });
        Assert.That(await File.ReadAllTextAsync(unrelated), Is.EqualTo(unrelatedOriginal),
            "the refused write to the unrelated file must not have reached disk");
        Assert.That(await File.ReadAllTextAsync(tracked), Is.EqualTo(trackedOriginal),
            "the refused write to the tracked file must not have reached disk");
    }

    [Test]
    public async Task SingleAgent_OpenerItself_IsNotRefusedByOpenerCheckAsync()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, _) = PickTwoFiles(fixture);
        var trackedOriginal = await File.ReadAllTextAsync(tracked);
        CallAs("A", "MoveMember");
        Assert.That(OpenTrackedLedger(tracked, out var openReason), Is.True, openReason);

        var result = await ProposeAppendAsync(tracked, trackedOriginal);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Summary, Does.Not.Contain("MULTI-AGENT:"));
        });
        Assert.That(await File.ReadAllTextAsync(tracked), Does.Contain("// appended by test"),
            "the opener's write to its own tracked file must reach disk");
    }

    [Test]
    public async Task SingleAgent_NullOpenerId_OtherAgentWriteIsNotRefusedByOpenerCheckAsync()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, _) = PickTwoFiles(fixture);
        var trackedOriginal = await File.ReadAllTextAsync(tracked);
        CallAs(null);
        Assert.That(OpenTrackedLedger(tracked, out var openReason), Is.True, openReason);

        CallAs("B");
        var result = await ProposeAppendAsync(tracked, trackedOriginal);

        Assert.That(result.Summary, Does.Not.Contain("MULTI-AGENT:"),
            "a ledger opened with no agent id must not be refused by the opener check");
    }

    [Test]
    public async Task SingleAgent_OpenerThenNoContextCall_IsNotRefusedByOpenerCheckAsync()
    {
        AgentOperatingModeOptions.Configure(new[] { "--operating-mode=single-agent" });
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, _) = PickTwoFiles(fixture);
        var trackedOriginal = await File.ReadAllTextAsync(tracked);
        CallAs("A", "MoveMember");
        Assert.That(OpenTrackedLedger(tracked, out var openReason), Is.True, openReason);

        CallAs(null);
        var result = await ProposeAppendAsync(tracked, trackedOriginal);

        Assert.That(result.Summary, Does.Not.Contain("MULTI-AGENT:"),
            "a call with no tool-call context must not be refused by the opener check");
    }

    [Test]
    public async Task Auto_ModeNotLatched_OtherAgentWriteIsNotRefusedByOpenerCheckAsync()
    {
        AgentOperatingModeOptions.Configure(Array.Empty<string>());
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, _) = PickTwoFiles(fixture);
        var trackedOriginal = await File.ReadAllTextAsync(tracked);
        CallAs("A", "MoveMember");
        Assert.That(OpenTrackedLedger(tracked, out var openReason), Is.True, openReason);

        CallAs("B");
        var result = await ProposeAppendAsync(tracked, trackedOriginal);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.Summary, Does.Not.Contain("MULTI-AGENT:"));
        });
    }
}
