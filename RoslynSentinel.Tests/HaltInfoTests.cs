using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("HaltInfo")]
public class HaltInfoTests
{
    private sealed class StubHealth : IWorkspaceHealthReporter
    {
        public bool Halted { get; set; }

        public bool IsSessionHalted() => Halted;

        public void ClearExternalFileChanges() => throw new NotImplementedException();
        public void ClearExternalFileChanges(IReadOnlyCollection<string> paths) => throw new NotImplementedException();
        public Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IEnumerable<string> GetDiagnostics() => throw new NotImplementedException();
        public List<string> GetExternalFileChanges() => throw new NotImplementedException();
        public HealthComponents GetHealthComponents() => throw new NotImplementedException();
        public List<string> GetWorkspaceLoadErrors() => throw new NotImplementedException();
        public WorkspaceStatus GetWorkspaceStatus() => throw new NotImplementedException();
        public void ClearSessionHalt() => throw new NotImplementedException();
    }

    private StubHealth _health = null!;
    private UnrecoverableCircuitBreaker _unrecoverable = null!;
    private OrientationCircuitBreaker _orientation = null!;
    private MutationCircuitBreaker _mutation = null!;

    [SetUp]
    public void Setup()
    {
        _health = new StubHealth();
        _unrecoverable = new UnrecoverableCircuitBreaker(NullLogger.Instance);
        _orientation = new OrientationCircuitBreaker(NullLogger.Instance, tripThreshold: 2);
        _mutation = new MutationCircuitBreaker(NullLogger.Instance);
    }

    private void TripUnrecoverable() => _unrecoverable.Trip("T", "id", "diag");

    private void TripDrift() => _health.Halted = true;

    private void TripOrientation()
    {
        _orientation.RecordSearchOutcome(0);
        _orientation.RecordSearchOutcome(0);
    }

    private void TripMutation()
    {
        for (var i = 0; i < 8; i++)
        {
            _mutation.RecordBatchOutcome(0, 1, 0, 0);
        }
    }

    private HaltInfo? Resolve(bool resetMutationBreakerActive = false) =>
        HaltInfo.From(_health, _unrecoverable, _orientation, _mutation, resetMutationBreakerActive);

    [Test]
    public void From_NothingTripped_ReturnsNull()
    {
        Assert.That(Resolve(), Is.Null);
    }

    [Test]
    public void From_UnrecoverableAlone_YieldsUnrecoverable()
    {
        TripUnrecoverable();
        var info = Resolve();
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Kind, Is.EqualTo("unrecoverable"));
        Assert.That(info.Reason, Is.EqualTo(_unrecoverable.StateMessage()));
        Assert.That(info.Recovery, Does.Contain("No reset exists"));
        Assert.That(info.Recovery, Does.Contain("operator"));
        Assert.That(info.Recovery, Does.Contain("Read-only"));
    }

    [Test]
    public void From_DriftAlone_YieldsExternalDrift()
    {
        TripDrift();
        var info = Resolve();
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Kind, Is.EqualTo("externalDrift"));
        Assert.That(info.Reason, Is.EqualTo(DriftMessages.DriftHaltReason));
        Assert.That(info.Recovery, Is.EqualTo(DriftMessages.DriftHaltRecovery));
    }

    [Test]
    public void From_OrientationAlone_YieldsOrientation()
    {
        TripOrientation();
        var info = Resolve();
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Kind, Is.EqualTo("orientation"));
        Assert.That(info.Reason, Does.Contain("orientation breaker is tripped"));
    }

    [Test]
    public void From_MutationAlone_YieldsMutation()
    {
        TripMutation();
        var info = Resolve();
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Kind, Is.EqualTo("mutation"));
        Assert.That(info.Reason, Does.Contain("batch-mutation breaker is tripped"));
    }

    [Test]
    public void From_AllTripped_PrecedenceChainUnrecoverableDriftOrientationMutation()
    {
        TripUnrecoverable();
        TripDrift();
        TripOrientation();
        TripMutation();
        Assert.That(Resolve()!.Kind, Is.EqualTo("unrecoverable"));

        // Unrecoverable has no reset; simulate it being absent by using fresh instances for the rest of the chain.
        _unrecoverable = new UnrecoverableCircuitBreaker(NullLogger.Instance);
        Assert.That(Resolve()!.Kind, Is.EqualTo("externalDrift"));

        _health.Halted = false;
        Assert.That(Resolve()!.Kind, Is.EqualTo("orientation"));

        _orientation.Reset();
        Assert.That(Resolve()!.Kind, Is.EqualTo("mutation"));

        _mutation.Reset();
        Assert.That(Resolve(), Is.Null);
    }

    [Test]
    public void From_Mutation_ResetToolActive_RecoveryNamesResetMutationBreaker()
    {
        TripMutation();
        var info = Resolve(resetMutationBreakerActive: true);
        Assert.That(info!.Recovery, Does.Contain("ResetMutationBreaker"));
    }

    [Test]
    public void From_Mutation_ResetToolInactive_RecoveryStopsAndReports()
    {
        TripMutation();
        var info = Resolve(resetMutationBreakerActive: false);
        Assert.That(info!.Recovery, Does.Contain("Stop and report"));
        Assert.That(info.Recovery, Does.Not.Contain("ResetMutationBreaker"));
    }

    [Test]
    public void From_Orientation_RecoveryEqualsBreakerStateMessage()
    {
        TripOrientation();
        var info = Resolve();
        Assert.That(info!.Recovery, Is.EqualTo(_orientation.StateMessage()));
        Assert.That(info.Recovery, Is.Not.Empty);
    }

    [Test]
    public void DriftHaltMessage_NamesRecoveryCallsAndReadOnlyTools()
    {
        Assert.That(DriftMessages.DriftHaltMessage, Does.Contain("ExternalFileDrift"));
        Assert.That(DriftMessages.DriftHaltMessage, Does.Contain("Acknowledge"));
        Assert.That(DriftMessages.DriftHaltMessage, Does.Contain("ConfirmAll"));
        Assert.That(DriftMessages.DriftHaltMessage, Does.Contain("Read-only"));
        Assert.That(DriftMessages.DriftHaltMessage,
            Is.EqualTo(DriftMessages.DriftHaltReason + " " + DriftMessages.DriftHaltRecovery));
    }
}
