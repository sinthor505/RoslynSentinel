using NUnit.Framework;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class ScenarioRegistryTests
{
    [Test]
    public void Find_UnknownId_ReturnsNull()
    {
        Assert.That(ScenarioRegistry.Find("no-such-scenario-id"), Is.Null);
    }

    [Test]
    public void Find_NullOrEmpty_ReturnsNull()
    {
        Assert.That(ScenarioRegistry.Find(null), Is.Null);
        Assert.That(ScenarioRegistry.Find(string.Empty), Is.Null);
    }

    [TestCase(StopAdherenceScenarios.StopAdherenceMissingFileId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingMethodId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingMethodToolErrorId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceControlId)]
    public void Find_ResolvesAllFiveStopFamilyIds(string id)
    {
        var scenario = ScenarioRegistry.Find(id);

        Assert.That(scenario, Is.Not.Null);
        Assert.That(scenario!.Id, Is.EqualTo(id));
    }

    [Test]
    public void All_HasNoDuplicateIds()
    {
        var ids = ScenarioRegistry.All.Select(s => s.Id).ToList();

        Assert.That(ids.Select(id => id.ToUpperInvariant()).Distinct().Count(), Is.EqualTo(ids.Count));
    }

    [TestCase(StopAdherenceScenarios.StopAdherenceMissingFileId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingMethodId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingMethodToolErrorId)]
    [TestCase(StopAdherenceScenarios.StopAdherenceControlId)]
    public void Registry_EveryStopScenarioDeclaresAnAxis(string id)
    {
        var scenario = ScenarioRegistry.Find(id);

        Assert.That(scenario, Is.Not.Null);
        Assert.That(scenario!.PrimaryAxis, Is.Not.EqualTo(EvalAxis.Unassigned), $"{id} does not declare a PrimaryAxis.");
    }

    // Stop-family half of the Decision 6 assignment (the implementation scenarios are pinned in ImplementationScenarioGoldenTests).
    // A re-axis is a deliberate edit of this test.
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingFileId, EvalAxis.InstructionAdherence)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingMethodId, EvalAxis.InstructionAdherence)]
    [TestCase(StopAdherenceScenarios.StopAdherenceControlId, EvalAxis.InstructionAdherence)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingFileToolErrorId, EvalAxis.ErrorRecovery)]
    [TestCase(StopAdherenceScenarios.StopAdherenceMissingMethodToolErrorId, EvalAxis.ErrorRecovery)]
    public void Registry_StopFamilyAxisAssignment_MatchesThePlan(string id, EvalAxis expectedAxis)
    {
        Assert.That(ScenarioRegistry.Find(id)!.PrimaryAxis, Is.EqualTo(expectedAxis), $"{id} is expected on axis {expectedAxis}.");
    }
}
