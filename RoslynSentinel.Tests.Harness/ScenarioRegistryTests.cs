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

    [Test]
    public void All_HasNoDuplicateIds()
    {
        var ids = ScenarioRegistry.All.Select(s => s.Id).ToList();

        Assert.That(ids.Select(id => id.ToUpperInvariant()).Distinct().Count(), Is.EqualTo(ids.Count));
    }
}
