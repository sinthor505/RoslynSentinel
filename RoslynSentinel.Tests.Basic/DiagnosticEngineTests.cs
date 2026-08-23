using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Demonstrates testing an engine against a fake IWorkspaceManager instead of the real
/// PersistentWorkspaceManager - no MSBuildLocator warmup, no real workspace, just an
/// in-memory Solution handed straight to the engine under test.
/// </summary>
[TestFixture]
public class DiagnosticEngineTests
{
    [Test]
    public async Task GetFileDiagnostics_OnCleanFile_ReturnsNoErrors()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            [("Test.cs", "public class Foo { public void Bar() { } }")]);

        var fake = new FakeWorkspaceManager();
        fake.SetTestSolution(solution);
        var engine = new DiagnosticEngine(fake);

        var result = await engine.GetFileDiagnosticsAsync(new FilePath("Test.cs"));

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success));
        Assert.That(result.Data.Errors, Is.EqualTo(0));
    }

    [Test]
    public async Task GetFileDiagnostics_OnBrokenFile_ReportsError()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            [("Test.cs", "public class Foo { void Bar() { NoSuchMethod(); } }")]);

        var fake = new FakeWorkspaceManager();
        fake.SetTestSolution(solution);
        var engine = new DiagnosticEngine(fake);

        var result = await engine.GetFileDiagnosticsAsync(new FilePath("Test.cs"));

        Assert.That(result.Data.Errors, Is.GreaterThan(0));
    }

    [Test]
    public async Task GetFileDiagnostics_WithNoSolutionLoaded_ThrowsSolutionNotLoaded()
    {
        var fake = new FakeWorkspaceManager();
        var engine = new DiagnosticEngine(fake);

        await Assert.ThatAsync(
            () => engine.GetFileDiagnosticsAsync(new FilePath("Test.cs")),
            Throws.TypeOf<SolutionNotLoadedException>());
    }
}
