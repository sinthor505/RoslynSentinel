using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Battery.Advanced;

// CreateProject registers the files it writes (csproj, solution, template .cs, EOL pass) as its own writes, so the
// drift guard does not read them as external edits. Uses a real disk-backed temp solution (TestSolutionFixture) and a
// real watcher, never the RoslynSentinel repo solution. Spawns dotnet new / dotnet sln, hence NonParallelizable.
[TestFixture]
[Category("SolutionManagementEngine")] // sentinel:auto-category
[NonParallelizable]
public class CreateProjectOwnWritesTests
{
    // No positive signal says "no more watcher events are coming", so the settle window is the bound. It is several
    // times the 500 ms debounce; the poll steps keep it from being one fixed sleep.
    private static async Task WaitForWatcherToSettleAsync(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }

    [Test]
    public async Task CreateProject_OwnWrites_AreNotDriftAndFollowUpApplyDoesNotHaltAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var engine = Basic.TestEngines.SolutionManagementEngine(workspaceManager);

        await engine.CreateProjectAsync("Extra", "classlib");

        var projectDir = Path.Combine(fixture.SolutionDirectory, "Extra");
        var csprojPath = Path.Combine(projectDir, "Extra.csproj");
        var templateClass = Path.Combine(projectDir, "Class1.cs");
        Assert.That(File.Exists(csprojPath), Is.True, "dotnet new should have written the project file");
        Assert.That(File.Exists(templateClass), Is.True, "dotnet new classlib should have written Class1.cs");

        await WaitForWatcherToSettleAsync(TimeSpan.FromSeconds(3));
        Assert.That(workspaceManager.GetExternalFileChanges(), Is.Empty,
            "CreateProject's own writes must not be reported as external drift");

        // Follow-up write of the project file plus delete of the template file. A drift halt would throw
        // SessionHaltedException here, so DoesNotThrowAsync names that failure explicitly.
        var newCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
        ApplyChangesResult? applied = null;
        await Assert.DoesNotThrowAsync(async () =>
            applied = await workspaceManager.ApplyProposedChangesAsync(
                new()
                {
                    [csprojPath] = newCsproj
                },
                deletePaths: [templateClass]));

        Assert.That(applied!.IsError, Is.False, applied.Summary);
        Assert.That(workspaceManager.IsSessionHalted(), Is.False);
    }

    [Test]
    public async Task CreateProject_UnrelatedExternalEdit_StaysFlaggedAndProjectPathsAreNotAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var engine = Basic.TestEngines.SolutionManagementEngine(workspaceManager);

        var unrelated = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        await File.WriteAllTextAsync(unrelated, await File.ReadAllTextAsync(unrelated) + "\n// unrelated external edit\n");

        // Poll for the watcher to flag the edit (same pattern as DeleteFile_DriftedFile_RefusesDeleteAsync).
        var flagDeadline = DateTime.UtcNow.AddSeconds(5);
        while (!workspaceManager.GetExternalFileChanges().Contains(unrelated, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow < flagDeadline)
        {
            await Task.Delay(25);
        }

        Assert.That(workspaceManager.GetExternalFileChanges(), Does.Contain(unrelated).IgnoreCase,
            "precondition: the watcher must flag the unrelated edit before CreateProject runs");

        await engine.CreateProjectAsync("Extra", "classlib");
        await WaitForWatcherToSettleAsync(TimeSpan.FromSeconds(3));

        var changes = workspaceManager.GetExternalFileChanges();
        Assert.That(changes, Does.Contain(unrelated).IgnoreCase,
            "CreateProject must not clear drift on paths it did not write");

        var projectDir = Path.Combine(fixture.SolutionDirectory, "Extra");
        Assert.That(changes.Where(p => p.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase)), Is.Empty,
            "CreateProject's own project files must not be flagged");
        Assert.That(changes, Does.Not.Contain(fixture.SolutionPath).IgnoreCase,
            "the solution file CreateProject edited via dotnet sln add must be registered");
    }

    [Test]
    public async Task CreateProject_UnknownTemplate_ThrowsAndRegistersNothingAsync()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var engine = Basic.TestEngines.SolutionManagementEngine(workspaceManager);

        var wouldBeProjectDir = Path.Combine(fixture.SolutionDirectory, "Ghost");
        var ex = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () =>
            await engine.CreateProjectAsync("Ghost", "nosuchtemplate"));
        Assert.That(ex!.Message, Does.Contain("was not created"));

        await WaitForWatcherToSettleAsync(TimeSpan.FromSeconds(3));
        Assert.That(workspaceManager.GetExternalFileChanges().Where(p => p.StartsWith(wouldBeProjectDir, StringComparison.OrdinalIgnoreCase)), Is.Empty,
            "a failed CreateProject must not register or flag anything under its project directory");
    }
}
