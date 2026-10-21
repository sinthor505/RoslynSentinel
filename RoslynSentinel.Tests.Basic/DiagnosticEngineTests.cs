using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Demonstrates testing an engine against a fake IWorkspaceManager instead of the real
/// PersistentWorkspaceManager - no MSBuildLocator warmup, no real workspace, just an
/// in-memory Solution handed straight to the engine under test.
/// </summary>
[TestFixture]
[Category("DiagnosticEngine")] // sentinel:auto-category
[Category("DiagnosticSummary")] // sentinel:auto-category
public class DiagnosticEngineTests
{
    [Test]
    public async Task GetFileDiagnostics_OnCleanFile_ReturnsNoErrors()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            [("Test.cs", "public class Foo { public void Bar() { } }")]);

        var fake = FakeWorkspaceManager.ReadOnly();
        fake.SetTestSolution(solution);
        var engine = new DiagnosticEngine(fake);

        var result = await engine.GetFileDiagnosticsAsync(new FilePathWrapper("Test.cs"));

        Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success));
        Assert.That(result.Data.Errors, Is.Zero);
    }

    [Test]
    public async Task GetFileDiagnostics_OnBrokenFile_ReportsError()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            [("Test.cs", "public class Foo { void Bar() { NoSuchMethod(); } }")]);

        var fake = FakeWorkspaceManager.ReadOnly();
        fake.SetTestSolution(solution);
        var engine = new DiagnosticEngine(fake);

        var result = await engine.GetFileDiagnosticsAsync(new FilePathWrapper("Test.cs"));

        Assert.That(result.Data.Errors, Is.GreaterThan(0));
        var cs0103 = result.Data.Details.Single(d => d.Id == "CS0103");
        Assert.That(cs0103.Severity, Is.EqualTo("Error"));
        Assert.That(cs0103.StartLine, Is.EqualTo(1), "the unresolved call is on the file's only line");
    }

    [Test]
    public async Task GetFileDiagnostics_FileWithOneKnownError_ReportsExactlyOneErrorAndNoWarnings()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            [("Test.cs", "public class Foo { void Bar() { NoSuchMethod(); } }")]);

        var fake = FakeWorkspaceManager.ReadOnly();
        fake.SetTestSolution(solution);
        var engine = new DiagnosticEngine(fake);

        var result = await engine.GetFileDiagnosticsAsync(new FilePathWrapper("Test.cs"));

        Assert.That(result.Data.Errors, Is.EqualTo(1));
        Assert.That(result.Data.Warnings, Is.Zero);
        Assert.That(result.Data.Details, Has.Count.EqualTo(1));
        Assert.That(result.Data.Details[0].Severity, Is.EqualTo("Error"));
    }

    [Test]
    public async Task GetFileDiagnostics_WithNoSolutionLoaded_ThrowsSolutionNotLoaded()
    {
        var fake = FakeWorkspaceManager.ReadOnly();
        var engine = new DiagnosticEngine(fake);

        await Assert.ThatAsync(
            () => engine.GetFileDiagnosticsAsync(new FilePathWrapper("Test.cs")),
            Throws.TypeOf<SolutionNotLoadedException>());
    }
}
