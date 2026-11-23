// Battery #10 -> ValidationEngine / MetricsEngine / DiagnosticEngine / SecurityEngine
// Adds dedicated XxxEngineTests fixture classes for 4 more engine classes.
// All tests run in-memory via AdhocWorkspace (no MSBuild/project-file loading).

using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Advanced;

[TestFixture]
// Runs exclusively: SetUp/TearDown wipe every top-level *.cs in the shared working directory (WorkingDirectoryLeakGuard).
[NonParallelizable]
public class ValidationEngineTests
{
    private IWorkspaceManager _workspaceManager = null!;
    private DiffEngine _diffEngine = null!;
    private ValidationEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
        _workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _diffEngine = Basic.TestEngines.DiffEngine();
        _engine = Basic.TestEngines.ValidationEngine(_workspaceManager, _diffEngine, NullLogger<ValidationEngine>.Instance);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source",
            [("Greeter.cs", "public class Greeter { public string Greet() => \"Hello\"; }")]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager?.Dispose();
        WorkingDirectoryLeakGuard.DeleteLeakedSourceFiles();
    }

    [Test]
    public async Task ValidateChanges_ValidNewContent_SucceedsWithNoErrors()
    {
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper("Greeter.cs")] = "public class Greeter { public string Greet() => \"Hi!\"; }"
        });

        Assert.That(!result.IsError, Is.True, "Syntactically valid replacement should pass");
        Assert.That(result.Diagnostics, Is.Empty, "No compiler errors expected for valid code");
    }

    [Test]
    public async Task ValidateChanges_FileNotFound_PassesThroughWhenNoContainingProject()
    {
        // "DoesNotExist.cs" is a bare relative filename with no project directory as an
        // ancestor, so it can't be attributed to any project in the solution -> there is no
        // compilation to validate it against. This stays pass-through by design; see
        // ValidateChanges_NewFileInKnownProject_* below for the case that IS now validated
        // (a new file whose containing project CAN be inferred).
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper("DoesNotExist.cs")] = "public class X {}"
        });

        Assert.That(!result.IsError, Is.True,
            "A new file with no attributable project can't be compiled, so it passes through");
        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public async Task ValidateChanges_NewFileInKnownProject_WithCompileError_ReturnsFalse()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "TestProj", "Source");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source", Path.Combine(projectDir, "Source.csproj"),
            [("Greeter.cs", "public class Greeter { public string Greet() => \"Hello\"; }", Path.Combine(projectDir, "Greeter.cs"))]);
        _workspaceManager.SetTestSolution(solution);

        var newFilePath = Path.Combine(projectDir, "NewFile.cs");
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper(newFilePath)] = "public class NewFile { public NoSuchType Field; }"
        });

        Assert.That(!result.IsError, Is.False, "A brand-new file with an unresolved type should fail validation");
        Assert.That(result.Diagnostics, Is.Not.Empty, "At least one error diagnostic expected");
    }

    [Test]
    public async Task ValidateChanges_NewFileInKnownProject_Valid_SucceedsWithNoErrors()
    {
        var projectDir = Path.Combine(Path.GetTempPath(), "TestProj", "Source");
        var solution = TestSolutionBuilder.CreateSolutionWithProject("Source", Path.Combine(projectDir, "Source.csproj"),
            [("Greeter.cs", "public class Greeter { public string Greet() => \"Hello\"; }", Path.Combine(projectDir, "Greeter.cs"))]);
        _workspaceManager.SetTestSolution(solution);

        var newFilePath = Path.Combine(projectDir, "NewFile.cs");
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper(newFilePath)] = "public class NewFile { public string Name; }"
        });

        Assert.That(!result.IsError, Is.True, "A syntactically and semantically valid new file should pass");
        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public async Task ValidateChanges_BreakingContent_ReturnsFalseWithCompileError()
    {
        // CS0029: cannot implicitly convert type 'string' to 'int'
        var result = await _engine.ValidateChangesAsync(new Dictionary<FilePathWrapper, string>
        {
            [new FilePathWrapper("Greeter.cs")] = "public class Greeter { void M() { int x = \"not a number\"; } }"
        });

        Assert.That(result, Is.Not.Null, "Should always return a report, never throw");
        Assert.That(!result.IsError, Is.False, "Type-mismatch compile error should fail validation");
        Assert.That(result.Diagnostics, Is.Not.Empty, "At least one error diagnostic expected");
    }
}
