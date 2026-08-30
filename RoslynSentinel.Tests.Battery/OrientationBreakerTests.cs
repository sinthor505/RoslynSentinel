// Unit-level coverage for the orientation breaker split off ICircuitBreaker (see
// docs/current/plan-orientation-breaker.md): PersistentWorkspaceManager now implements two
// independent breakers — IManualCircuitBreaker (mutating-tools breaker, unchanged behavior,
// manual reset only) and IAutomaticCircuitBreaker (new; trips after repeated zero-match
// SearchSolutionText calls, auto-resets via RecordSearchOutcome/Reset()). Each interface
// redeclares IsTripped()/StateMessage()/Reset() with its own explicit-interface-implementation
// body on PersistentWorkspaceManager, so these tests also confirm the two breakers' state is
// genuinely independent rather than accidentally sharing one implementation.
//
// End-to-end coverage of the request filter that enforces the breaker (short-circuiting
// non-allowlisted tools, calling RecordSearchOutcome/Reset()) lives in
// OrientationBreakerFilterTests.cs, which drives a real MCP client/server round trip.

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Battery;

[TestFixture]
public class OrientationBreakerTests
{
    [Test]
    public void RecordSearchOutcome_BelowThreshold_DoesNotTrip()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var automaticBreaker = (IAutomaticCircuitBreaker)workspaceManager;

        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);

        Assert.That(automaticBreaker.IsTripped(), Is.False);
        Assert.That(automaticBreaker.StateMessage(), Is.Null);
    }

    [Test]
    public void RecordSearchOutcome_ThreeConsecutiveZeroMatches_Trips()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var automaticBreaker = (IAutomaticCircuitBreaker)workspaceManager;

        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);

        Assert.That(automaticBreaker.IsTripped(), Is.True);
        Assert.That(automaticBreaker.StateMessage(), Does.Contain("SearchSolutionText"));
    }

    [Test]
    public void RecordSearchOutcome_NonZeroMatchMidStreak_ResetsStreakAndDoesNotTrip()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var automaticBreaker = (IAutomaticCircuitBreaker)workspaceManager;

        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(5); // resets the streak
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);

        Assert.That(automaticBreaker.IsTripped(), Is.False);
    }

    [Test]
    public void Reset_ClearsTrippedStateAndStreak()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var automaticBreaker = (IAutomaticCircuitBreaker)workspaceManager;

        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);
        Assert.That(automaticBreaker.IsTripped(), Is.True);

        automaticBreaker.Reset();

        Assert.That(automaticBreaker.IsTripped(), Is.False);
        Assert.That(automaticBreaker.StateMessage(), Is.Null);

        // Confirms Reset() cleared the streak counter, not just the open flag — two more
        // zero-match calls (not three) should not be enough to re-trip immediately after reset.
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);
        Assert.That(automaticBreaker.IsTripped(), Is.False);
    }

    [Test]
    public void ManualBreaker_RenameFromResetBreakerToReset_BehavesIdenticallyToBefore()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        IManualCircuitBreaker manualBreaker = workspaceManager;

        // Streak-trip threshold is 8 consecutive all-failure batches (BreakerStreakThreshold).
        for (int i = 0; i < 8; i++)
        {
            manualBreaker.RecordBatchOutcome(succeeded: 0, failed: 1, rolledBack: 0, skipped: 0);
        }

        Assert.That(manualBreaker.CheckBreaker(), Is.Not.Null, "Expected the manual breaker to trip after 8 consecutive all-failure batches.");
        Assert.That(manualBreaker.GetBreakerStatus().Open, Is.True);

        manualBreaker.Reset();

        Assert.That(manualBreaker.CheckBreaker(), Is.Null, "Reset() should clear the manual breaker exactly as ResetBreaker() did before the rename.");
        Assert.That(manualBreaker.GetBreakerStatus().Open, Is.False);
    }

    [Test]
    public void TwoBreakers_AreIndependent_TrippingOneDoesNotAffectTheOther()
    {
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        IManualCircuitBreaker manualBreaker = workspaceManager;
        var automaticBreaker = (IAutomaticCircuitBreaker)workspaceManager;

        // Trip only the automatic (orientation) breaker.
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);
        automaticBreaker.RecordSearchOutcome(0);

        Assert.That(automaticBreaker.IsTripped(), Is.True);
        Assert.That(manualBreaker.CheckBreaker(), Is.Null, "Tripping the orientation breaker must not trip the mutating-tools breaker.");
        Assert.That(manualBreaker.GetBreakerStatus().Open, Is.False);

        // Trip only the manual (mutating-tools) breaker; orientation breaker must stay tripped
        // from above, proving Reset() on one type doesn't reach the other's state either.
        for (int i = 0; i < 8; i++)
        {
            manualBreaker.RecordBatchOutcome(succeeded: 0, failed: 1, rolledBack: 0, skipped: 0);
        }

        Assert.That(manualBreaker.CheckBreaker(), Is.Not.Null);
        Assert.That(automaticBreaker.IsTripped(), Is.True, "The manual breaker tripping must not affect the orientation breaker's already-tripped state.");

        manualBreaker.Reset();
        Assert.That(manualBreaker.CheckBreaker(), Is.Null);
        Assert.That(automaticBreaker.IsTripped(), Is.True, "Resetting the manual breaker must not reset the orientation breaker.");
    }
}
