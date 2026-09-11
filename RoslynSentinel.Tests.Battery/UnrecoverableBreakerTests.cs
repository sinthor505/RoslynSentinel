// IUnrecoverableBreaker — the halt that no agent action can clear.
//
// A warning alone was judged insufficient for a failed operation-blob write: agents routinely read
// one and carry on, which run 20260910-013550-398 demonstrates over 60 turns. A failed blob write
// is a *server* bug, so work must not continue on an unundoable footing and the agent must have no
// lever to clear it. IManualCircuitBreaker was rejected as the carrier precisely because
// ResetMutationBreaker is an exposed tool that clears it — see IUnrecoverableBreaker's remarks.
//
// The most valuable test here is ResetMutationBreaker_DoesNotClearTheUnrecoverableHalt: it proves
// the halt is out of the agent's reach. Note the no-Reset() design makes "the agent clears it"
// largely unrepresentable in test code at all, which is the point — the interface has no such
// member to call, so these tests guard the tool/write surface rather than the interface.

using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class UnrecoverableBreakerTests
{
    private TestSolutionFixture _fixture;
    private PersistentWorkspaceManager _workspaceManager;

    [SetUp]
    public async Task SetUpAsync()
    {
        _fixture = new TestSolutionFixture();
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await _workspaceManager.LoadSolutionAsync(_fixture.SolutionPath);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        _fixture?.Dispose();
    }

    private IUnrecoverableBreaker Breaker => _workspaceManager;

    [Test]
    public void NotTrippedByDefault()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Breaker.IsTripped(), Is.False);
            Assert.That(Breaker.StateMessage(), Is.Null);
        });
    }

    [Test]
    public void Trip_MessageNamesTheToolChangeIdAndDiagnostic()
    {
        Breaker.Trip("WrapRange_region", "abc12345", "blob write failed: DirectoryNotFoundException");

        var message = Breaker.StateMessage();
        Assert.That(Breaker.IsTripped(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(message, Does.Contain("WrapRange_region"));
            Assert.That(message, Does.Contain("abc12345"));
            Assert.That(message, Does.Contain("DirectoryNotFoundException"));
            Assert.That(message, Does.Contain("restart"),
                "the message must state the only available remedy, since the agent has none");
        });
    }

    [Test]
    public void Trip_IsIdempotentAndKeepsTheFirstDiagnostic()
    {
        // The first fault is closest to the root cause; a later one is almost certainly downstream.
        Breaker.Trip("Inline_method", "first111", "the original cause");
        Breaker.Trip("MoveType_ownFile", "second22", "a later consequence");

        Assert.That(Breaker.StateMessage(), Does.Contain("the original cause"));
        Assert.That(Breaker.StateMessage(), Does.Not.Contain("a later consequence"));
    }

    [Test]
    public async Task Tripped_RefusesEveryWriteAtTheChokepointAsync()
    {
        // Enforced in ApplyProposedChangesAsync rather than from a list of mutating tool names:
        // every .cs write routes through there, so no tool — including one added later — can slip
        // past. That is the whole reason the chokepoint was chosen over a filter deny-list.
        var targetFile = Directory
            .EnumerateFiles(_fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .First(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase));
        var original = await File.ReadAllTextAsync(targetFile);

        Breaker.Trip("Member_add", "aaa11111", "blob write failed");

        var changes = new Dictionary<FilePathWrapper, string>
        {
            [_workspaceManager.SetFilePath(targetFile)] = original + "\n// appended by test\n"
        };

        Assert.That(
            async () => await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: false),
            Throws.TypeOf<SessionHaltedException>());

        Assert.That(await File.ReadAllTextAsync(targetFile), Is.EqualTo(original),
            "the refused write must not have reached disk");
    }

    [Test]
    public async Task NotTripped_WritesStillSucceedAsync()
    {
        // Guards against the halt being on by default or tripping spuriously — a false positive
        // here would disable every mutating tool on a healthy server.
        var targetFile = Directory
            .EnumerateFiles(_fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories)
            .First(f => !f.Contains("obj", StringComparison.OrdinalIgnoreCase));
        var original = await File.ReadAllTextAsync(targetFile);

        var changes = new Dictionary<FilePathWrapper, string>
        {
            [_workspaceManager.SetFilePath(targetFile)] = original + "\n// appended by test\n"
        };

        var result = await _workspaceManager.ApplyProposedChangesAsync(changes, validateChanges: false);

        Assert.That(result.Success, Is.True, result.Summary);
    }

    [Test]
    public void ResetMutationBreaker_DoesNotClearTheUnrecoverableHalt()
    {
        // The regression that matters most. ResetMutationBreaker is an exposed MCP tool; if it
        // reached this breaker the agent would have a reset button for a server defect.
        Breaker.Trip("SyncInterface_sync", "bbb22222", "blob write failed");

        ((IManualCircuitBreaker)_workspaceManager).Reset();

        Assert.That(Breaker.IsTripped(), Is.True,
            "the unrecoverable halt must survive a mutation-breaker reset — it is not part of " +
            "IManualCircuitBreaker, so ResetMutationBreaker cannot reach it");
    }

    [Test]
    public void OrientationBreakerReset_DoesNotClearTheUnrecoverableHalt()
    {
        // The orientation breaker auto-resets on any successful allowlisted call, so it resets far
        // more often than the manual one — an accidental shared slot here would be cleared almost
        // immediately and the halt would look like it simply didn't work.
        Breaker.Trip("ExtractMembers_interface", "ccc33333", "blob write failed");

        ((IAutomaticCircuitBreaker)_workspaceManager).Reset();

        Assert.That(Breaker.IsTripped(), Is.True);
    }

    [Test]
    public void MutationBreakerStatus_IsUnaffectedByTheUnrecoverableHalt()
    {
        // GetMutationBreakerStatus describes batch-failure rate. Reporting a server-integrity
        // fault through it would make the tool report a state its own description doesn't cover.
        Breaker.Trip("Inline_field", "ddd44444", "blob write failed");

        var status = _workspaceManager.GetBreakerStatus();

        Assert.That(status.Open, Is.False,
            "the batch-failure breaker is a separate breaker and must not appear tripped");
    }

    [Test]
    public void UnrecoverableBreakerHasNoResetMember()
    {
        // Unresettability is a compile-time guarantee, not a convention: ICircuitBreaker no longer
        // declares Reset(), so IUnrecoverableBreaker cannot inherit one. Asserted reflectively
        // because the corresponding negative — calling Reset() — is not expressible in C# here,
        // which is exactly the property being claimed.
        var members = typeof(IUnrecoverableBreaker).GetMembers().Select(m => m.Name).ToList();

        Assert.That(members, Does.Not.Contain("Reset"));
        Assert.That(typeof(ICircuitBreaker).GetMethod("Reset"), Is.Null,
            "Reset() must stay off the base interface, or IUnrecoverableBreaker inherits one");
    }
}
