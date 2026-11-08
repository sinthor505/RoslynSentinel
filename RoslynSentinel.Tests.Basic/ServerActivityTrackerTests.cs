using System.Threading.Tasks;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
public class ServerActivityTrackerTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    [Test]
    public void NewTracker_HasNoInFlightCalls()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        Assert.That(tracker.InFlightCount, Is.EqualTo(0));
    }

    [Test]
    public void NewTracker_LastActivityIsClockAtConstruction_AndUtc()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        Assert.That(tracker.LastActivityUtc, Is.EqualTo(T0));
        Assert.That(tracker.LastActivityUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void Touch_MovesLastActivityToCurrentClock()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        now = T0.AddMinutes(5);
        tracker.Touch();

        Assert.That(tracker.LastActivityUtc, Is.EqualTo(T0.AddMinutes(5)));
    }

    [Test]
    public void Enter_IncrementsCountAndTouches_DisposeDecrementsAndTouches()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        now = T0.AddMinutes(1);
        var scope = tracker.Enter();
        Assert.That(tracker.InFlightCount, Is.EqualTo(1));
        Assert.That(tracker.LastActivityUtc, Is.EqualTo(T0.AddMinutes(1)));

        now = T0.AddMinutes(2);
        scope.Dispose();
        Assert.That(tracker.InFlightCount, Is.EqualTo(0));
        Assert.That(tracker.LastActivityUtc, Is.EqualTo(T0.AddMinutes(2)));
    }

    [Test]
    public void Dispose_CalledTwice_DecrementsOnlyOnce()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        var first = tracker.Enter();
        var second = tracker.Enter();
        Assert.That(tracker.InFlightCount, Is.EqualTo(2));

        first.Dispose();
        first.Dispose();

        Assert.That(tracker.InFlightCount, Is.EqualTo(1));

        second.Dispose();
        Assert.That(tracker.InFlightCount, Is.EqualTo(0));
    }

    [Test]
    public void OverlappingScopes_CountGoesTwoOneZero()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        var outer = tracker.Enter();
        var inner = tracker.Enter();
        Assert.That(tracker.InFlightCount, Is.EqualTo(2));

        inner.Dispose();
        Assert.That(tracker.InFlightCount, Is.EqualTo(1));

        outer.Dispose();
        Assert.That(tracker.InFlightCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ConcurrentEnterAndDispose_EndsWithZeroInFlight()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        await Task.Run(() => Parallel.For(0, 1000, _ =>
        {
            using (tracker.Enter())
            {
            }
        }));

        Assert.That(tracker.InFlightCount, Is.EqualTo(0));
    }

    [Test]
    public void ScopeAroundThrowingAction_StillDecrements()
    {
        var now = T0;
        var tracker = new ServerActivityTracker(() => now);

        Assert.Throws<InvalidOperationException>(() =>
        {
            using (tracker.Enter())
            {
                throw new InvalidOperationException("boom");
            }
        });

        Assert.That(tracker.InFlightCount, Is.EqualTo(0));
    }
}
