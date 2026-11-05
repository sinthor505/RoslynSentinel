// Scoped operation ledger -> the refusal text and code surfaced by ApplyProposedChangesAsync, and the
// ExternalFileDrift LedgerRelease / LedgerFix recovery that makes the unrelated write succeed again.
// Disk tier: the refusal and the recovered write are proven against real files in a loaded solution.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery.Basic;

[TestFixture]
public class ScopedOperationLedgerRefusalTests
{
    private PersistentWorkspaceManager _workspaceManager;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
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

    private bool OpenLedgerTrackingFile(string trackedFile)
    {
        var trackedPath = _workspaceManager.ResolveFromWire(trackedFile);
        var opened = Ledger.TryOpen(
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
            out var rejectionReason,
            openingChangeId: "move-change-id");
        Assert.That(opened, Is.True, rejectionReason);
        return opened;
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
    public async Task OpenLedger_RefusesUnrelatedWrite_WithScopedLedgerBlockedCodeAndRecoveryCallsAsync()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, unrelated) = PickTwoFiles(fixture);
        var unrelatedOriginal = await File.ReadAllTextAsync(unrelated);
        OpenLedgerTrackingFile(tracked);

        var result = await ProposeAppendAsync(unrelated, unrelatedOriginal);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.ScopedLedgerBlocked));
            Assert.That(result.Code, Is.EqualTo(ToolErrorCode.ScopedLedgerBlocked));
            Assert.That(result.Summary, Does.Contain("ExternalFileDrift"));
            Assert.That(result.Summary, Does.Contain("LedgerList"));
            Assert.That(result.Summary, Does.Contain("LedgerRelease"));
        });
        Assert.That(await File.ReadAllTextAsync(unrelated), Is.EqualTo(unrelatedOriginal),
            "the refused write must not have reached disk");
    }

    [Test]
    public async Task LedgerRelease_ThenUnrelatedWrite_SucceedsAsync()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, unrelated) = PickTwoFiles(fixture);
        var unrelatedOriginal = await File.ReadAllTextAsync(unrelated);
        OpenLedgerTrackingFile(tracked);
        Assert.That((await ProposeAppendAsync(unrelated, unrelatedOriginal)).IsError, Is.True,
            "precondition: the write must be refused while the ledger is open");

        var release = AdminTools.LedgerResult(Ledger, AdminTools.ExternalFileDriftOperation.LedgerRelease, null);

        Assert.That(release.IsError, Is.False, release.ErrorData?.Message);
        Assert.That(release.SuccessData, Does.Contain("1 unresolved entry(ies) were abandoned"));
        await AssertUnrelatedWriteLandedAsync(unrelated, unrelatedOriginal);
    }

    [Test]
    public async Task LedgerFixAll_ThenUnrelatedWrite_SucceedsAsync()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var (tracked, unrelated) = PickTwoFiles(fixture);
        var unrelatedOriginal = await File.ReadAllTextAsync(unrelated);
        OpenLedgerTrackingFile(tracked);

        var fix = AdminTools.LedgerResult(Ledger, AdminTools.ExternalFileDriftOperation.LedgerFix, "entry-1");

        Assert.That(fix.IsError, Is.False, fix.ErrorData?.Message);
        Assert.That(fix.SuccessData, Does.Contain("ledger released"));
        await AssertUnrelatedWriteLandedAsync(unrelated, unrelatedOriginal);
    }

    // Proves the write that was refused earlier now reaches disk as a real success.
    private async Task AssertUnrelatedWriteLandedAsync(string unrelated, string unrelatedOriginal)
    {
        var result = await ProposeAppendAsync(unrelated, unrelatedOriginal);
        var absolute = _workspaceManager.ResolveFromWire(unrelated).Absolute;
        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.False, result.Summary);
            Assert.That(result.SucceededFiles, Does.Contain(absolute));
        });
        Assert.That(await File.ReadAllTextAsync(unrelated), Does.Contain("// appended by test"),
            "the write must reach disk once the ledger is released");
    }
}
