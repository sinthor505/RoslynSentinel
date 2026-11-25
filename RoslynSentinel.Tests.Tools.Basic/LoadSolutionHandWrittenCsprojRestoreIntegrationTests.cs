// D5 probe (plan_multi_file_movefile_and_namespace_change_tool, Step 6): a hand-written B.csproj that an existing project A
// references is added with `dotnet sln add` and never restored. LoadSolution must load it, with forceReload and without.
// Starts dotnet processes, so it is excluded from the fast suite.

using System.Diagnostics;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class LoadSolutionHandWrittenCsprojRestoreIntegrationTests
{
    private string _root = null!;

    [SetUp]
    public void CreateScratchDirectory()
    {
        _root = Path.Combine(ScratchRoot.Resolve(), "LoadSolutionHandWrittenCsprojRestoreIntegrationTests", Guid.NewGuid().ToString("N"));
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
    public async Task ForceReload_RestoresHandWrittenProjectReferencedByExistingProject()
    {
        var solutionPath = CreateSolutionWithHandWrittenProjectB();
        Assert.That(File.Exists(Path.Combine(_root, "B", "obj", "project.assets.json")), Is.False, "precondition: B is unrestored");

        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager);
        var reload = await tool.LoadSolution(new ToolCallReason("force-reloads a solution whose referenced project B was never restored"), solutionPath, forceReload: true, timeoutSeconds: 120);

        Assert.That(reload.IsError, Is.False, reload.ErrorData?.Message);
        Assert.That(workspaceManager.GetWorkspaceLoadErrors(), Has.None.Contain("Unable to find project"));
        var solution = await workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        Assert.That(solution!.Projects.Select(p => p.Name), Is.SupersetOf(new[] { "A", "B" }));
    }

    [Test]
    public async Task LoadWithoutForceReload_RestoresHandWrittenProjectWhenAssetsAreMissing()
    {
        var solutionPath = CreateSolutionWithHandWrittenProjectB();
        Assert.That(File.Exists(Path.Combine(_root, "B", "obj", "project.assets.json")), Is.False, "precondition: B is unrestored");

        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tool = CreateImpl(workspaceManager);
        var load = await tool.LoadSolution(new ToolCallReason("loads a solution whose referenced project B has no assets file"), solutionPath, timeoutSeconds: 120);

        Assert.That(load.IsError, Is.False, load.ErrorData?.Message);
        Assert.That(workspaceManager.GetWorkspaceLoadErrors(), Has.None.Contain("Unable to find project"));
        var solution = await workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        Assert.That(solution!.Projects.Select(p => p.Name), Is.SupersetOf(new[] { "A", "B" }));
    }

    private string CreateSolutionWithHandWrittenProjectB()
    {
        WriteClassLibrary("A");
        WriteHandWrittenProjectB();

        // A references B by editing A's csproj text, as a user would; nothing runs restore for B.
        var aCsproj = Path.Combine(_root, "A", "A.csproj");
        var aText = File.ReadAllText(aCsproj);
        File.WriteAllText(aCsproj, aText.Replace("</Project>", "<ItemGroup><ProjectReference Include=\"..\\B\\B.csproj\" /></ItemGroup></Project>"));

        RunDotnet("new sln --name Demo --format sln --output .");
        RunDotnet("sln Demo.sln add A/A.csproj");
        RunDotnet("sln Demo.sln add B/B.csproj");
        return Path.Combine(_root, "Demo.sln");
    }

    private void WriteClassLibrary(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(directory, name + "Type.cs"), $"namespace {name}; public class {name}Type {{ }}");
    }

    private void WriteHandWrittenProjectB()
    {
        // Written as a person would write it: no dotnet new, no tool, no restore.
        var directory = Path.Combine(_root, "B");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "B.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(directory, "BType.cs"), "namespace B; public class BType { }");
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
        BuildIsolation.RemoveInheritedMsBuildPins(startInfo);

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
            workspaceManager, Battery.Basic.TestEngines.StructuralRefinementEngine(workspaceManager, config), NullLogger<WorkspaceProjectManagementImpl>.Instance, Battery.Basic.TestEngines.SolutionStructureEngine(workspaceManager, config),
            SolutionRestore.RestoreAsync); // opts in to the real restore: the test-host ambient default is a no-op
    }
}
