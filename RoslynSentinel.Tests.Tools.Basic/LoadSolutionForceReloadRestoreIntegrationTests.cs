// End to end (punchlist #24): a scratch solution is loaded, a second project (referencing the first) is added with
// `dotnet sln add` and never restored, then LoadSolution(forceReload) with the REAL restore must load it. Starts dotnet
// processes, so it is excluded from the fast suite.

using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class LoadSolutionForceReloadRestoreIntegrationTests
{
    private string _root = null!;

    [SetUp]
    public void CreateScratchDirectory()
    {
        _root = Path.Combine(ScratchRoot.Resolve(), "LoadSolutionForceReloadRestoreIntegrationTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteScratchDirectory()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A lingering MSBuild node can hold a handle; a leftover scratch folder is not a test failure.
        }
    }

    [Test]
    public async Task LoadSolution_ForceReloadAfterProjectAddedMidSession_LoadsItWithoutUnableToFindProject()
    {
        WriteProject("A", referencesA: false);
        RunDotnet("new sln --name Demo --format sln --output .");
        var solutionPath = Path.Combine(_root, "Demo.sln");
        RunDotnet("sln Demo.sln add A/A.csproj");
        var firstRestore = await SolutionRestore.RestoreAsync(solutionPath, TimeSpan.FromMinutes(3), CancellationToken.None);
        Assert.That(firstRestore.Succeeded, Is.True, firstRestore.OutputTail);

        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager);
        var firstLoad = await tool.LoadSolution(new ToolCallReason("loads the scratch solution with project A"), solutionPath, timeoutSeconds: 120);
        Assert.That(firstLoad.IsError, Is.False, firstLoad.ErrorData?.Message);

        // The project added mid-session: no restore has run for it, so it has no obj/project.assets.json.
        WriteProject("B", referencesA: true);
        RunDotnet("sln Demo.sln add B/B.csproj");
        Assert.That(File.Exists(Path.Combine(_root, "B", "obj", "project.assets.json")), Is.False, "precondition: B is unrestored");

        var reload = await tool.LoadSolution(new ToolCallReason("reloads after adding project B"), solutionPath, forceReload: true, timeoutSeconds: 120);

        Assert.That(reload.IsError, Is.False, reload.ErrorData?.Message);
        Assert.That(reload.SuccessData?.ToString(), Does.Contain("Restored the solution first"));
        Assert.That(workspaceManager.GetWorkspaceLoadErrors(), Has.None.Contain("Unable to find project"));
        var solution = await workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        Assert.That(solution!.Projects.Select(p => p.Name), Is.SupersetOf(new[] { "A", "B" }));
        Assert.That(File.Exists(Path.Combine(_root, "B", "obj", "project.assets.json")), Is.True, "the forced reload must have restored B");
    }

    private void WriteProject(string name, bool referencesA)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var reference = referencesA ? "<ItemGroup><ProjectReference Include=\"..\\A\\A.csproj\" /></ItemGroup>" : "";
        File.WriteAllText(
            Path.Combine(directory, name + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{reference}</Project>");
        File.WriteAllText(Path.Combine(directory, name + "Type.cs"), $"namespace {name}; public class {name}Type {{ }}");
    }

    private void RunDotnet(string arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        // The test host pins an MSBuild toolset in its environment; the child resolves its own.
        foreach (var variable in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly", "DOTNET_HOST_PATH", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR", "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR" })
        {
            startInfo.EnvironmentVariables.Remove(variable);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.That(process.WaitForExit(TimeSpan.FromMinutes(2)), Is.True, $"dotnet {arguments} did not finish");
        Assert.That(process.ExitCode, Is.Zero, $"dotnet {arguments} failed: {output.Result} {error.Result}");
    }

    private static WorkspaceProjectManagementImpl CreateImpl(PersistentWorkspaceManager workspaceManager)
    {
        var config = Battery.Basic.TestEngines.SentinelConfiguration();
        return new WorkspaceProjectManagementImpl(
            workspaceManager, Battery.Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config), NullLogger<WorkspaceProjectManagementImpl>.Instance, Battery.Basic.TestEngines.SolutionStructureEngine(workspaceManager, config));
    }
}
