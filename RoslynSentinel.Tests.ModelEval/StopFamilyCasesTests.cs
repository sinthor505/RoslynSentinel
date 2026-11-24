using RoslynSentinel.Common;
using RoslynSentinel.Tests.ModelEval.Fixtures;
using RoslynSentinel.Tests.ModelEval.LiveModel;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class StopFamilyCasesTests
{
    // Model-free guard: every definition with a PrimaryAxis has exactly one stop-family case, carrying both categories.
    // It lives outside LiveModel because the assembly SetUpFixture Assert.Ignore()s that namespace when no model is set.
    [Test]
    public void EveryAxisDefinition_HasACase()
    {
        var cases = ScenarioAgentTests.StopFamilyCases().ToList();
        var caseIds = cases.Select(c => (string)c.Arguments[0]!).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var expectedIds = ScenarioRegistry.All
            .Where(d => d.PrimaryAxis != EvalAxis.Unassigned)
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.That(caseIds, Is.EqualTo(expectedIds), "StopFamilyCases must have exactly one case per definition that declares a PrimaryAxis.");

        foreach (var testCase in cases)
        {
            var id = (string)testCase.Arguments[0]!;
            var definition = ScenarioRegistry.Find(id)!;
            var categories = testCase.Properties["Category"].Cast<object>().Select(c => c.ToString()).ToList();
            Assert.That(categories, Does.Contain(EvalAxisCategory.Name(definition.PrimaryAxis)), $"{id} is missing its axis category.");
            Assert.That(categories, Does.Contain(ScenarioAgentTests.StopFamilyCategory), $"{id} is missing {ScenarioAgentTests.StopFamilyCategory}.");
        }
    }
}
