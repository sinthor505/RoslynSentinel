using System.Text.Json;
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
        var engine = Battery.Basic.TestEngines.DiagnosticEngine(fake);

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
        var engine = Battery.Basic.TestEngines.DiagnosticEngine(fake);

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
        var engine = Battery.Basic.TestEngines.DiagnosticEngine(fake);

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
        var engine = Battery.Basic.TestEngines.DiagnosticEngine(fake);

        await Assert.ThatAsync(
            () => engine.GetFileDiagnosticsAsync(new FilePathWrapper("Test.cs")),
            Throws.TypeOf<SolutionNotLoadedException>());
    }

    [Test]
    public async Task GetSolutionDiagnostics_MoreErrorFilesThanMaxDetails_ErrorFilePathsListsEveryFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RoslynSentinelDiagnosticEngineTests");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(dir, "TestProj.csproj"),
            [
                ("A.cs", "class A { Missing x; }", Path.Combine(dir, "A.cs")),
                ("B.cs", "class B { Missing x; }", Path.Combine(dir, "B.cs")),
                ("C.cs", "class C { Missing x; }", Path.Combine(dir, "C.cs")),
            ]);

        var fake = FakeWorkspaceManager.ReadOnly();
        fake.SetTestSolution(solution);
        var engine = Battery.Basic.TestEngines.DiagnosticEngine(fake);

        var result = await engine.GetSolutionDiagnosticsAsync(maxDetails: 1);

        Assert.That(result.Data.Errors, Is.EqualTo(3));
        Assert.That(result.Data.Details.Count, Is.LessThanOrEqualTo(2), "one error row plus the SENTINEL-TRUNCATED row");
        Assert.That(result.Data.ErrorFilePaths, Is.Not.Null);
        Assert.That(result.Data.ErrorFilePaths!.Count, Is.EqualTo(3), "every error file is listed, not just the ones under the cap");
        Assert.That(result.Data.ErrorFilePaths!.All(p => Path.IsPathRooted(p)), Is.True, "last-writer lookup needs absolute paths");
        Assert.That(result.Data.ErrorFilePaths!.Select(p => Path.GetFileName(p)), Is.EquivalentTo(new[] { "A.cs", "B.cs", "C.cs" }));
    }

    [Test]
    public void GetSolutionDiagnostics_ErrorFilePaths_NotSerialized()
    {
        var summary = new DiagnosticSummary(1, 0, [], ErrorFilePaths: ["C:/x.cs"]);

        var json = JsonSerializer.Serialize(summary);

        Assert.That(json.Contains("errorfilepaths", StringComparison.OrdinalIgnoreCase), Is.False, json);
        Assert.That(json.Contains("C:/x.cs", StringComparison.Ordinal), Is.False, json);
    }

    [Test]
    public async Task GetSolutionDiagnostics_NoErrors_ErrorFilePathsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RoslynSentinelDiagnosticEngineTests");
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj",
            Path.Combine(dir, "TestProj.csproj"),
            [("Clean.cs", "public class Clean { }", Path.Combine(dir, "Clean.cs"))]);

        var fake = FakeWorkspaceManager.ReadOnly();
        fake.SetTestSolution(solution);
        var engine = Battery.Basic.TestEngines.DiagnosticEngine(fake);

        var result = await engine.GetSolutionDiagnosticsAsync();

        Assert.That(result.Data.Errors, Is.Zero);
        Assert.That(result.Data.ErrorFilePaths, Is.Not.Null);
        Assert.That(result.Data.ErrorFilePaths!, Is.Empty);
    }
}
