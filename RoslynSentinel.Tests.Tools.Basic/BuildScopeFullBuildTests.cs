using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// KNOWN FLAKE on full/parallel runs (spawns child processes through ChildProcessCapture): disregard a failure here when the change did not touch process spawning; re-run the class alone first.
// Suspected cause: ChildProcessCapture's pool-backed async pipe reads starved under load. Reference fix to copy if confirmed: GitProcess.TryRun dedicated reader threads (docs/current/issues/issue_process_spawn_test_flake.md).
[TestFixture]
[Category("BuildEngine")]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class BuildScopeFullBuildTests
{
    private static void WriteProject(string root, string name, string source)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, name + "Value.cs"), source);
    }

    private static void WriteFixtureSolution(string root)
    {
        File.WriteAllText(Path.Combine(root, "ScopeFixture.slnx"), """
            <Solution>
              <Project Path="Good/Good.csproj" />
              <Project Path="Bad/Bad.csproj" />
            </Solution>
            """);

        WriteProject(root, "Good", """
            namespace Scope.Good;

            public static class GoodValue
            {
                public static string Name => "good";
            }
            """);

        WriteProject(root, "Bad", """
            namespace Scope.Bad;

            public static class BadValue
            {
                public static string Name => "bad";

                // Deliberate compile error inside a method body; the public surface stays valid.
                public static int Broken()
                {
                    int x = "text";
                    return x;
                }
            }
            """);
    }

    // Characterises RunFullBuildAsync scope handling: scope=project and scope=file build only the scoped
    // project plus its project references. Good and Bad are independent (no ProjectReference between them),
    // so a scoped build of Good must not compile Bad and must not inherit Bad's error.
    [Test]
    [NonParallelizable]
    public async Task FullBuild_ProjectScopeGood_SucceedsAndOnlyListsGoodProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelScope_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "ScopeFixture.slnx"));
            var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
                level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Good");

            Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
            Assert.That(result.Data, Is.Not.Null, result.Error?.Message);
            Assert.That(result.Data!.Outcome, Is.EqualTo(BuildOutcome.Succeeded), result.Data.StdoutTail);
            Assert.That(result.Data.ProjectsCompiled, Is.EqualTo(new[] { "Good" }));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    [NonParallelizable]
    public async Task FullBuild_SolutionScope_FailsBecauseOfBadProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelScope_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "ScopeFixture.slnx"));
            var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
                level: BuildVerifyLevel.fullBuildThrowaway);

            Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
            Assert.That(result.Data, Is.Not.Null, result.Error?.Message);
            Assert.That(result.Data!.Outcome, Is.EqualTo(BuildOutcome.Failed), result.Data.StdoutTail);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    [NonParallelizable]
    public async Task FullBuild_ProjectScopeBad_Fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelScope_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "ScopeFixture.slnx"));
            var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
                level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Bad");

            Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
            Assert.That(result.Data, Is.Not.Null, result.Error?.Message);
            Assert.That(result.Data!.Outcome, Is.EqualTo(BuildOutcome.Failed), result.Data.StdoutTail);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    [NonParallelizable]
    public async Task FullBuild_FileScope_BuildsContainingProjectOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelScope_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "ScopeFixture.slnx"));
            var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));
            var goodFile = Path.Combine(root, "Good", "GoodValue.cs");

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
                level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.file, scopeName: goodFile);

            Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.Success), result.Error?.Message);
            Assert.That(result.Data, Is.Not.Null, result.Error?.Message);
            Assert.That(result.Data!.Outcome, Is.EqualTo(BuildOutcome.Succeeded), result.Data.StdoutTail);
            Assert.That(result.Data.ProjectsCompiled, Is.EqualTo(new[] { "Good" }));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    [NonParallelizable]
    public async Task FullBuild_UnknownProject_ReturnsBuildNotRunWithListAllHint()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelScope_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "ScopeFixture.slnx"));
            var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
                level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: "Nope");

            Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.InvalidInput));
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(EngineErrorCode.BuildNotRun));
            Assert.That(result.Error.Message, Does.Contain("ListAll"));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    [NonParallelizable]
    public async Task FullBuild_ProjectScopeWithoutScopeName_ReturnsInvalidInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "RoslynSentinelScope_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            WriteFixtureSolution(root);
            using var workspaceManager = TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            await workspaceManager.LoadSolutionAsync(Path.Combine(root, "ScopeFixture.slnx"));
            var buildEngine = TestEngines.BuildEngine(workspaceManager, TestEngines.DiagnosticEngine(workspaceManager));

            var result = await buildEngine.RunFullBuildAsync(CancellationToken.None, maxDetails: 50,
                level: BuildVerifyLevel.fullBuildThrowaway, scope: ToolScope.project, scopeName: null);

            Assert.That(result.Outcome, Is.EqualTo(EngineOutcome.InvalidInput));
            Assert.That(result.Error, Is.Not.Null);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a build worker node may still hold a handle briefly.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
