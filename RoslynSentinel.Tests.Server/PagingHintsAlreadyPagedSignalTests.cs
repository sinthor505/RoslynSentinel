namespace RoslynSentinel.Tests.Server;

/// <summary>
/// <see cref="PagingHints.ForAlreadyPagedPage"/> is the one place the <c>AlreadyPaged</c> flag is produced, and it raises the
/// per-call <see cref="OversizeUnitSignal"/> that keeps the request-filter backstop from re-storing a page in every marshaller
/// mode (no marshaller is registered in mode off). These cases pin that the signal stays per call and opt-in.
/// </summary>
[TestFixture]
public class PagingHintsAlreadyPagedSignalTests
{
    [Test]
    public void ForAlreadyPagedPage_SetsTheFlag_AndRaisesTheOpenSignal()
    {
        using var signal = OversizeUnitSignal.Open();

        var hints = PagingHints.ForAlreadyPagedPage();

        Assert.That(hints.AlreadyPaged, Is.True);
        Assert.That(hints.RecordsPaths, Is.Empty);
        Assert.That(signal.Raised, Is.True);
    }

    [Test]
    public void OrdinaryPagingHints_NeverRaiseTheSignal()
    {
        using var signal = OversizeUnitSignal.Open();

        var hints = new PagingHints(["successData.items"], PagingOrder.Linear);

        Assert.That(hints.AlreadyPaged, Is.False);
        Assert.That(signal.Raised, Is.False, "a tool that only declares a record sequence must stay subject to the offload backstop");
    }

    [Test]
    public void ForAlreadyPagedPage_WithNoSignalOpen_IsANoOp()
    {
        Assert.DoesNotThrow(() => PagingHints.ForAlreadyPagedPage());
    }

    [Test]
    public async Task ForAlreadyPagedPage_RaisesOnlyTheSignalOfItsOwnCall()
    {
        // Two concurrent "calls", each with its own signal (the filter opens one per tool call): only the call that built
        // an already-paged page is told to leave its response alone.
        async Task<bool> RunCallAsync(bool buildsAlreadyPagedPage)
        {
            using var signal = OversizeUnitSignal.Open();
            await Task.Yield();
            if (buildsAlreadyPagedPage)
            {
                PagingHints.ForAlreadyPagedPage();
            }

            await Task.Yield();
            return signal.Raised;
        }

        var paged = Task.Run(() => RunCallAsync(true));
        var other = Task.Run(() => RunCallAsync(false));

        Assert.That(await paged, Is.True);
        Assert.That(await other, Is.False);
    }
}
