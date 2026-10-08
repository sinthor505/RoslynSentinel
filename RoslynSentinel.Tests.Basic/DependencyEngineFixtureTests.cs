using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Fixture-based tests for DependencyEngine using in-memory workspaces with seeded data
/// instead of real solutions. Replaces vacuous integration smoke tests.
/// </summary>
[TestFixture]
[Category("DependencyEngine")] // sentinel:auto-category
[Category("FixtureBased")] // sentinel:auto-category
public class DependencyEngineFixtureTests
{
    [Test]
    public async Task GetProjectDependencies_WithProjectReferences_ReturnsExactReferences()
    {
        // Arrange: Create a two-project solution where Downstream references Upstream
        var solution = TestSolutionBuilder.CreateTwoProjectSolution(
            "UpstreamLib",
            [("Helper.cs", "public class Helper { public void DoWork() { } }")],
            "DownstreamApp",
            [("Consumer.cs", "public class Consumer { public Helper h; }")]);

        var fake = new FakeWorkspaceManager();
        fake.SetTestSolution(solution);
        var engine = new DependencyEngine(fake);

        var downstreamProject = solution.Projects.FirstOrDefault(p => p.Name == "DownstreamApp");
        Assert.That(downstreamProject, Is.Not.Null, "Downstream project must exist in solution.");

        // Act
        var result = await engine.GetProjectDependenciesAsync(downstreamProject!.Name, CancellationToken.None);

        // Assert: Verify exact references
        Assert.That(result, Is.Not.Null, "ProjectDependencyReport must not be null.");
        Assert.That(result.ProjectReferences, Is.Not.Null, "ProjectReferences list must not be null.");
        Assert.That(result.ProjectReferences, Contains.Item("UpstreamLib"), "ProjectReferences must contain UpstreamLib.");
        Assert.That(result.PackageReferences, Is.Not.Null, "PackageReferences list must not be null.");
    }

    [Test]
    public async Task FindUnusedReferences_WithUnusedProjectReference_IdentifiesUnused()
    {
        // Arrange: Create a solution where DownstreamApp references UpstreamLib but never uses it
        var solution = TestSolutionBuilder.CreateTwoProjectSolution(
            "UpstreamLib",
            [("Unused.cs", "public class UnusedHelper { }")],
            "DownstreamApp",
            [("Consumer.cs", "public class Consumer { public void Work() { System.Console.WriteLine(\"hello\"); } }")]);

        var fake = new FakeWorkspaceManager();
        fake.SetTestSolution(solution);
        var engine = new DependencyEngine(fake);

        var downstreamProject = solution.Projects.FirstOrDefault(p => p.Name == "DownstreamApp");
        Assert.That(downstreamProject, Is.Not.Null);

        // Act
        var unused = await engine.FindUnusedReferencesAsync(downstreamProject!.Name);

        // Assert: Verify UpstreamLib appears in unused list
        Assert.That(unused, Is.Not.Null, "Result list must not be null.");
        Assert.That(unused, Contains.Item("UpstreamLib"), "Unused references must include UpstreamLib which is not used by any consumer.");
    }
}
/// <summary>
/// Fixture-based tests for SolutionStructureEngine using in-memory workspaces.
/// </summary>
[TestFixture]
[Category("SolutionStructureEngine")] // sentinel:auto-category
[Category("FixtureBased")] // sentinel:auto-category
public class SolutionStructureEngineFixtureTests
{
    [Test]
    public async Task FindCircularDependencies_WithTypeCycle_DetectsAndReportsCycle()
    {
        // The engine detects cycles between types (Tarjan SCC over type references), not project references.
        // A project-reference cycle cannot be built in Roslyn without crashing compilation.
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "CycleProject",
            [
                ("A.cs", "public class ClassA { public ClassB B; }"),
                ("B.cs", "public class ClassB { public ClassC C; }"),
                ("C.cs", "public class ClassC { public ClassA A; }"),
                ("D.cs", "public class ClassD { public ClassA A; }")
            ]);

        var fake = new FakeWorkspaceManager();
        fake.SetTestSolution(solution);
        var engine = new SolutionStructureEngine(fake, new SentinelConfiguration());

        var cycles = await engine.FindCircularDependenciesAsync();

        Assert.That(cycles, Has.Count.EqualTo(1), "Exactly one cycle (A -> B -> C -> A) must be reported.");
        Assert.That(cycles[0].Cycle, Is.EqualTo(new[] { "ClassA", "ClassB", "ClassC", "ClassA" }));
        Assert.That(cycles[0].Cycle, Does.Not.Contain("ClassD"), "ClassD depends on the cycle but is not part of it.");
    }
}
