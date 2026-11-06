// DTO-EXEMPT: test fixture class, not a DTO.
// MSBuildWorkspace reports an unparseable or missing project file only through its WorkspaceFailed event and still
// returns a solution holding that project with no documents (the open does not throw). These tests pin that such a
// reload is recorded as a sticky reload failure (workspaceStale), that a valid project with no source files is not,
// and that a later clean reload or load clears the failure.

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[NonParallelizable]
public class WorkspaceBrokenProjectReloadTests
{
    private const string CoreProjectName = "ContosoOrders.Core";
    private const string CoreProjectFileName = "ContosoOrders.Core.csproj";

    // Not well-formed XML: the open reports a Failure diagnostic naming the project file.
    private const string BrokenProjectXml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>";

    private static string CoreProjectPath(TestSolutionFixture fixture)
        => Path.Combine(fixture.SolutionDirectory, CoreProjectName, CoreProjectFileName);

    private static int CoreDocumentCount(PersistentWorkspaceManager manager)
        => manager.CurrentSolution!.Projects.Single(p => p.Name == CoreProjectName).Documents.Count();

    private static McpServerStatusResult StatusFor(PersistentWorkspaceManager manager)
    {
        var tools = new ServerStatusTools(
            manager,
            new ActiveToolSurface("test", new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null),
            new StoppedByScriptMarker(WasFound: false, Details: null));

        var result = (SentinelCallToolResult<McpServerStatusResult>)tools.McpServerStatusTool("test reason");
        return result.SuccessData!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static string ValidProjectWithoutSources(string originalProjectXml)
        => originalProjectXml.Replace("</PropertyGroup>", "\t<EnableDefaultCompileItems>false</EnableDefaultCompileItems>\r\n  </PropertyGroup>");

    [Test]
    [NonParallelizable]
    public async Task DebounceReload_BrokenProjectXml_RecordsFailureKeepsOldSolutionAndReportsStale()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        manager.DebounceRetryDelayMs = 200;
        var before = manager.CurrentSolution;
        var coreDocumentsBefore = CoreDocumentCount(manager);
        Assert.That(coreDocumentsBefore, Is.GreaterThan(0), "precondition: the project has documents");
        Assert.That(StatusFor(manager).WorkspaceStale, Is.False, "precondition: a healthy load is not stale");

        await File.WriteAllTextAsync(CoreProjectPath(fixture), BrokenProjectXml);
        await WaitUntilAsync(() => manager.ReloadFailure is not null, TimeSpan.FromSeconds(20));

        var failure = manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "a debounce reload of a syntax-broken project file did not record a failure within 20 s");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ProjectFileChange));
        Assert.That(failure.TriggerFile, Is.EqualTo(CoreProjectFileName));
        Assert.That(failure.OldWorkspaceKept, Is.True);
        Assert.That(failure.Cause, Does.Contain(CoreProjectFileName), "the cause must name the failed project file");
        Assert.That(failure.Cause, Does.Not.Contain(fixture.SolutionDirectory), "the cause must not carry the directory path");
        Assert.That(manager.CurrentSolution, Is.SameAs(before), "the previous complete solution is kept");
        Assert.That(CoreDocumentCount(manager), Is.EqualTo(coreDocumentsBefore));

        var status = StatusFor(manager);
        Assert.That(status.WorkspaceStale, Is.True);
        Assert.That(status.ReloadFailure, Is.Not.Null);
        Assert.That(status.ReloadFailure!.Cause, Does.Contain(CoreProjectFileName));
    }

    [Test]
    [NonParallelizable]
    public async Task DebounceReload_ProjectFileFixedAfterBreak_ClearsFailureAndRestoresDocuments()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        manager.DebounceRetryDelayMs = 200;
        var coreDocumentsBefore = CoreDocumentCount(manager);
        var projectPath = CoreProjectPath(fixture);
        var originalXml = await File.ReadAllTextAsync(projectPath);

        await File.WriteAllTextAsync(projectPath, BrokenProjectXml);
        await WaitUntilAsync(() => manager.ReloadFailure is not null, TimeSpan.FromSeconds(20));
        Assert.That(manager.ReloadFailure, Is.Not.Null, "precondition: the broken project file must record a failure");
        var versionBeforeFix = manager.WorkspaceVersion;

        await File.WriteAllTextAsync(projectPath, originalXml);
        await WaitUntilAsync(() => manager.ReloadFailure is null, TimeSpan.FromSeconds(20));

        Assert.That(manager.ReloadFailure, Is.Null, "the clean debounce reload after the fix did not clear the failure within 20 s");
        Assert.That(manager.WorkspaceVersion, Is.GreaterThan(versionBeforeFix));
        Assert.That(CoreDocumentCount(manager), Is.EqualTo(coreDocumentsBefore));
        Assert.That(StatusFor(manager).WorkspaceStale, Is.False);
    }

    [Test]
    [NonParallelizable]
    public async Task DebounceReload_ValidProjectWithNoSourceFiles_IsNotReportedAsFailure()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath);
        manager.DebounceRetryDelayMs = 200;
        var coreDocumentsBefore = CoreDocumentCount(manager);
        var projectPath = CoreProjectPath(fixture);

        // A well-formed project that globs no sources: the sources stay on disk, so only a real reload can drop the documents.
        var emptiedXml = ValidProjectWithoutSources(await File.ReadAllTextAsync(projectPath));
        Assert.That(emptiedXml, Does.Contain("EnableDefaultCompileItems"), "precondition: the project edit applied");
        await File.WriteAllTextAsync(projectPath, emptiedXml);
        await WaitUntilAsync(() => CoreDocumentCount(manager) < coreDocumentsBefore, TimeSpan.FromSeconds(20));

        Assert.That(CoreDocumentCount(manager), Is.LessThan(coreDocumentsBefore), "the debounce reload of the source-less project did not complete within 20 s");
        var core = manager.CurrentSolution!.Projects.Single(p => p.Name == CoreProjectName);
        Assert.That(core.Documents.Any(d => d.Name == "OrderService.cs"), Is.False, "the sources must no longer be part of the project");
        Assert.That(manager.ReloadFailure, Is.Null, "a valid project with no source files is not a reload failure");
        Assert.That(StatusFor(manager).WorkspaceStale, Is.False);
    }

    [Test]
    [NonParallelizable]
    public async Task LoadSolution_BrokenProjectXml_RecordsExplicitLoadFailureThenCleanLoadClears()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var projectPath = CoreProjectPath(fixture);
        var originalXml = await File.ReadAllTextAsync(projectPath);

        await File.WriteAllTextAsync(projectPath, BrokenProjectXml);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var failure = manager.ReloadFailure;
        Assert.That(failure, Is.Not.Null, "an explicit load that returns a project which failed to load must record a failure");
        Assert.That(failure!.Trigger, Is.EqualTo(WorkspaceReloadTriggerKind.ExplicitLoad));
        Assert.That(failure.OldWorkspaceKept, Is.False);
        Assert.That(failure.Cause, Does.Contain(CoreProjectFileName), "the cause must name the failed project file");
        Assert.That(failure.Cause, Does.Not.Contain(fixture.SolutionDirectory), "the cause must not carry the directory path");
        Assert.That(StatusFor(manager).WorkspaceStale, Is.True);

        await File.WriteAllTextAsync(projectPath, originalXml);
        await manager.LoadSolutionAsync(fixture.SolutionPath);

        Assert.That(manager.ReloadFailure, Is.Null, "a clean explicit load after the fix clears the sticky failure");
        Assert.That(StatusFor(manager).WorkspaceStale, Is.False);
        Assert.That(CoreDocumentCount(manager), Is.GreaterThan(0));
    }

    [Test]
    [NonParallelizable]
    public async Task LoadSolution_ValidProjectWithNoSourceFiles_IsNotReportedAsFailure()
    {
        using var fixture = new TestSolutionFixture();
        using var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var projectPath = CoreProjectPath(fixture);
        await File.WriteAllTextAsync(projectPath, ValidProjectWithoutSources(await File.ReadAllTextAsync(projectPath)));

        await manager.LoadSolutionAsync(fixture.SolutionPath);

        var core = manager.CurrentSolution!.Projects.Single(p => p.Name == CoreProjectName);
        Assert.That(core.Documents.Any(d => d.Name == "OrderService.cs"), Is.False, "precondition: the project has no source files");
        Assert.That(manager.ReloadFailure, Is.Null, "a valid project with no source files is not a reload failure");
        Assert.That(StatusFor(manager).WorkspaceStale, Is.False);
    }
}
