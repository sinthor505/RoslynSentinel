using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

// Tool-level tests for the MoveFile project-edge gate (plan Step 27, rows T27.8-T27.12; D3 toggles, D13 gate, D20 fixture).
// Fixture: a real PersistentWorkspaceManager over a TestSolutionFixture copy of ContosoOrders (ContosoOrders.Tests -> ContosoOrders.Core) plus a THIRD,
// hand-written project, ContosoOrders.Extras (SDK-style csproj, no references, no packages), added to the copied .sln before the solution loads (D20, in-process:
// LoadSolutionAsync loads it like the other two and nothing in this file spawns dotnet). A file moved from Tests to Extras that uses a Core type needs the
// project edge Extras -> Core; a moved type that stays referenced from Tests needs Tests -> Extras. Every row asserts on disk bytes, error codes and finding text.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("WholeFileWriteTools")]
public sealed class MoveFileProjectEdgeTests
{
    private const string CoreFolder = "ContosoOrders.Core";
    private const string TestsFolder = "ContosoOrders.Tests";
    private const string ExtrasFolder = "ContosoOrders.Extras";

    private const string ExtrasProjectGuid = "{C3333333-3333-3333-3333-333333333333}";

    // Written as a person would write it (CRLF, no restore, no package reference).
    private const string ExtrasCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n\r\n  <PropertyGroup>\r\n    <TargetFramework>net10.0</TargetFramework>\r\n    <ImplicitUsings>enable</ImplicitUsings>\r\n    <Nullable>enable</Nullable>\r\n  </PropertyGroup>\r\n\r\n</Project>\r\n";

    // The wire text of the facts the gate reports has no shared constant (only the parameter names do), so these are the stable fragments of the production text.
    private const string ProjectReferenceAddedFragment = "a ProjectReference to 'ContosoOrders.Core'";
    private const string WorkspaceChangedFragment = "the workspace changed during the call";

    // The foreign write must run with no ToolCallContext in this flow, so the fallback flow token decides who holds the lease.
    [SetUp]
    public void ResetCallIdentity() => ToolCallContext.Set(null);

    [TearDown]
    public void ClearCallIdentity() => ToolCallContext.Set(null);

    private static async Task<PersistentWorkspaceManager> LoadAsync(TestSolutionFixture fixture)
    {
        var manager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await manager.LoadSolutionAsync(fixture.SolutionPath, null, TestSolutionFixture.LoadTimeout);
        return manager;
    }

    // The D20 probe made explicit: the real manager must have loaded the hand-written third project next to the other two.
    private static async Task<PersistentWorkspaceManager> LoadWithExtrasAsync(TestSolutionFixture fixture)
    {
        var manager = await LoadAsync(fixture);
        var solution = await manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        Assert.That(solution.Projects.Select(project => project.Name), Is.SupersetOf(new[] { CoreFolder, TestsFolder, ExtrasFolder }), "The third project is part of the loaded solution.");
        return manager;
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager, WorkspaceTools workspaceTools)
    {
        var diffEngine = Battery.Basic.TestEngines.DiffEngine();
        var validationEngine = Battery.Basic.TestEngines.ValidationEngine(manager, diffEngine, NullLogger<ValidationEngine>.Instance);
        return Battery.Basic.TestTools.WholeFileWriteTools(manager, workspaceTools, validationEngine, diffEngine, NullLogger<WholeFileWriteTools>.Instance, Battery.Basic.TestEngines.SymbolNavigationEngine(manager, NullLogger<SymbolNavigationEngine>.Instance));
    }

    private static WholeFileWriteTools BuildTools(IWorkspaceManager manager) => BuildTools(manager, Battery.Basic.TestTools.WorkspaceTools(manager));

    private static Task<SentinelCallToolResult<AppliedChangeSummary>> MoveAsync(WholeFileWriteTools tools, string source, string destination, bool addProjectReferences)
    {
        var moves = new List<FileMovePair> { new() { SourcePath = source, DestinationPath = destination } };
        return tools.MoveFileTool(reason: "test message", moves, MoveFileNamespaceHandling.keep, dryRun: false, addProjectReferences: addProjectReferences, addInternalsVisibleTo: false);
    }

    private static string PathIn(TestSolutionFixture fixture, params string[] segments) =>
        Path.Combine(new[] { fixture.SolutionDirectory }.Concat(segments).ToArray());

    // Adds ContosoOrders.Extras to the fixture copy: the csproj, its source files, and the .sln entries (project line plus the four configuration lines).
    // Must run before the solution loads.
    private static void AddExtrasProject(TestSolutionFixture fixture, params (string FileName, string Text)[] files)
    {
        var directory = PathIn(fixture, ExtrasFolder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ExtrasFolder + ".csproj"), ExtrasCsproj);
        foreach (var (fileName, text) in files)
        {
            var path = Path.Combine(directory, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        var solutionText = File.ReadAllText(fixture.SolutionPath);
        var projectBlock = $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{ExtrasFolder}\", \"{ExtrasFolder}\\{ExtrasFolder}.csproj\", \"{ExtrasProjectGuid}\"\r\nEndProject\r\n";
        var projectMarker = solutionText.IndexOf("Project(\"{2150E333", StringComparison.Ordinal);
        Assert.That(projectMarker, Is.GreaterThanOrEqualTo(0), "The fixture .sln keeps its Solution Items project, the anchor for the new project line.");
        solutionText = solutionText.Insert(projectMarker, projectBlock);

        var configurations = string.Concat(
            $"\t\t{ExtrasProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n",
            $"\t\t{ExtrasProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n",
            $"\t\t{ExtrasProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU\r\n",
            $"\t\t{ExtrasProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU\r\n");
        var configurationMarker = solutionText.IndexOf("\tEndGlobalSection\r\n\tGlobalSection(SolutionProperties)", StringComparison.Ordinal);
        Assert.That(configurationMarker, Is.GreaterThanOrEqualTo(0), "The fixture .sln keeps its SolutionProperties section, the anchor for the configuration lines.");
        solutionText = solutionText.Insert(configurationMarker, configurations);
        File.WriteAllText(fixture.SolutionPath, solutionText);
    }

    private static string WriteInTests(TestSolutionFixture fixture, string fileName, string text)
    {
        var path = PathIn(fixture, TestsFolder, fileName);
        File.WriteAllText(path, text);
        return path;
    }

    // A Tests file whose only outside dependency is the Core type OrderLine: moved to Extras it needs a ProjectReference Extras -> Core and nothing else.
    private const string UsesCoreText = "namespace ContosoOrders.Tests;\r\n\r\npublic class EdgeHelper\r\n{\r\n    public ContosoOrders.Core.OrderLine? Line { get; set; }\r\n}\r\n";

    private static string ProbeText(int value) => $"namespace ContosoOrders.Core;\r\n\r\npublic class EdgeGateProbe\r\n{{\r\n    public int X() => {value};\r\n}}\r\n";

    // The shared scenario of rows T27.8, T27.9 and T27.12: EdgeHelper.cs (Tests, uses a Core type) moves into the Extras project, which has no reference to Core.
    // The probe file in Core is what the T27.12 foreign writes change.
    private sealed record EdgeScenario(string Source, string Destination, string ExtrasCsprojPath, string ProbePath);

    private static EdgeScenario BuildEdgeScenario(TestSolutionFixture fixture)
    {
        AddExtrasProject(fixture);
        var source = WriteInTests(fixture, "EdgeHelper.cs", UsesCoreText);
        var probe = PathIn(fixture, CoreFolder, "EdgeGateProbe.cs");
        File.WriteAllText(probe, ProbeText(1));
        return new EdgeScenario(source, PathIn(fixture, ExtrasFolder, "Moved", "EdgeHelper.cs"), PathIn(fixture, ExtrasFolder, ExtrasFolder + ".csproj"), probe);
    }

    // A structural (project file) write starts a background reload; it must end before the manager is disposed and before a reloaded solution is read.
    private static async Task WaitForReloadAsync(PersistentWorkspaceManager manager)
    {
        var status = await manager.WaitForLoadAsync(TimeSpan.FromSeconds(120), CancellationToken.None);
        Assert.That(status, Is.EqualTo(SolutionLoadStatus.Loaded), "The background reload after the project file write ended and loaded.");
    }

    // Runs the work on a thread-pool task whose ExecutionContext has flow suppressed, so its flow carries no lease token and no ToolCallContext:
    // it is a foreign writer that waits for the lease like any other (same technique as EdgeGateLeaseTests.StartFromOtherFlow).
    private static Task<ApplyChangesResult> ForeignWriteAsync(PersistentWorkspaceManager manager, string file, int value)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(() => manager.ApplyProposedChangesAsync(new Dictionary<FilePathWrapper, string> { [file] = ProbeText(value) }, retryCount: 0, validateChanges: false));
        }
    }

    // Every text the caller can read from a result: the error message and the findings on either the error or the envelope.
    private static string AllText(SentinelCallToolResult<AppliedChangeSummary> result)
    {
        var parts = new List<string>();
        if (result.ErrorData is { } error)
        {
            parts.Add(error.Message);
            parts.AddRange((error.Findings ?? []).Select(finding => finding.Message));
        }

        parts.AddRange((result.Findings ?? []).Select(finding => finding.Message));
        return string.Join("\n", parts);
    }

    // The compile gate's refusal message names only the files its errors bind to; the diagnostics (with their CS ids) ride on StructuredDetail.
    private static string DetailText(SentinelCallToolResult<AppliedChangeSummary> result) =>
        string.Join("\n", (result.ErrorData?.StructuredDetail ?? []).Select(item => System.Text.Json.JsonSerializer.Serialize(item, item.GetType())));

    private static IEnumerable<string> FindingTexts(SentinelCallToolResult<AppliedChangeSummary> result) =>
        (result.Findings ?? []).Concat(result.ErrorData?.Findings ?? []).Select(finding => finding.Message);

    // Heavy fixture: the csproj write, the shared ChangeId and UndoChange are chokepoint behaviour, so the real manager and a loaded third project are needed.
    [Test]
    public async Task AddProjectReferences_On_WritesCsprojUnderSameChangeId_UndoRevertsItAsync()
    {
        using var fixture = new TestSolutionFixture();
        var scenario = BuildEdgeScenario(fixture);
        using var manager = await LoadWithExtrasAsync(fixture);
        var workspaceTools = Battery.Basic.TestTools.WorkspaceTools(manager);
        var tools = BuildTools(manager, workspaceTools);
        var csprojBefore = File.ReadAllBytes(scenario.ExtrasCsprojPath);
        var sourceBefore = File.ReadAllBytes(scenario.Source);
        Assert.That(File.ReadAllText(scenario.ExtrasCsprojPath), Does.Not.Contain("ProjectReference"), "Precondition: Extras references nothing.");

        var result = await MoveAsync(tools, scenario.Source, scenario.Destination, addProjectReferences: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var changeId = result.SuccessData!.ChangeId;
        Assert.That(changeId, Is.Not.Null.And.Not.Empty, "One change covers the move and the project file.");
        var csprojAfter = File.ReadAllText(scenario.ExtrasCsprojPath);
        Assert.That(csprojAfter, Does.Contain("<ProjectReference Include=\"..\\ContosoOrders.Core\\ContosoOrders.Core.csproj\""), "The csproj gained the item with the relative path to Core.");
        Assert.That(File.Exists(scenario.Source), Is.False, "The source is gone.");
        Assert.That(File.ReadAllBytes(scenario.Destination), Is.EqualTo(sourceBefore), "The destination holds the original bytes.");
        Assert.That(result.SuccessData.AffectedFiles.Select(file => Path.GetFullPath(file.Absolute)), Has.Some.EqualTo(Path.GetFullPath(scenario.ExtrasCsprojPath)).IgnoreCase, "The csproj is listed as affected by the same change.");
        Assert.That(FindingTexts(result), Has.Some.Contain(ProjectReferenceAddedFragment), "A CsprojEdgeAdded fact names the added edge.");

        await WaitForReloadAsync(manager);
        var reloaded = await manager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None);
        var extras = reloaded.Projects.Single(project => project.Name == ExtrasFolder);
        var core = reloaded.Projects.Single(project => project.Name == CoreFolder);
        Assert.That(extras.ProjectReferences.Select(reference => reference.ProjectId), Does.Contain(core.Id), "The reloaded workspace sees the new reference.");

        var undone = await workspaceTools.UndoChangeTool(reason: "test message", changeId!);

        Assert.That(undone.IsError, Is.False, undone.ErrorData?.Message);
        Assert.That(File.ReadAllBytes(scenario.ExtrasCsprojPath), Is.EqualTo(csprojBefore), "Undo restores the csproj's original bytes.");
        Assert.That(File.ReadAllBytes(scenario.Source), Is.EqualTo(sourceBefore), "Undo restores the moved file.");
        Assert.That(File.Exists(scenario.Destination), Is.False, "Undo removes the destination.");
        await WaitForReloadAsync(manager);
    }

    // Heavy fixture: same real-manager scenario as T27.8 with the toggle off. The edge is absent, so the compile gate refuses the move (the moved file's Core type no longer
    // resolves in Extras) and the fact is carried by the refusal; csproj and files stay as they were. The fact is asserted in whichever of the two outcomes surfaces it.
    [Test]
    public async Task AddProjectReferences_Off_ReportsFactNamingParameterAsync()
    {
        using var fixture = new TestSolutionFixture();
        var scenario = BuildEdgeScenario(fixture);
        using var manager = await LoadWithExtrasAsync(fixture);
        var tools = BuildTools(manager);
        var csprojBefore = File.ReadAllBytes(scenario.ExtrasCsprojPath);
        var sourceBefore = File.ReadAllBytes(scenario.Source);

        var result = await MoveAsync(tools, scenario.Source, scenario.Destination, addProjectReferences: false);

        var surfaced = AllText(result);
        Assert.That(surfaced, Does.Contain(MoveFileParameterNames.AddProjectReferences), "The needed edge is reported as a fact that names the parameter (MissingProjectReference).");
        Assert.That(surfaced, Does.Contain("needs a ProjectReference to 'ContosoOrders.Core'"), "The fact names the edge.");
        Assert.That(File.ReadAllBytes(scenario.ExtrasCsprojPath), Is.EqualTo(csprojBefore), "A toggle that is off never edits the project file.");
        if (result.IsError)
        {
            Assert.That(File.ReadAllBytes(scenario.Source), Is.EqualTo(sourceBefore), "A refused move leaves the source.");
            Assert.That(File.Exists(scenario.Destination), Is.False, "A refused move writes no destination.");
        }
    }

    // Heavy fixture: a refusal for a reason that is NOT the edge. EdgeClash.cs needs the edge Extras -> Core (so the gate runs with the toggle on) AND declares a type the Extras
    // project already declares (CS0101 in the destination), so the compile with the edge still fails. CS0246 would mean the edge was not part of the compile.
    [Test]
    public async Task GateRefusal_CsprojNotWrittenAsync()
    {
        using var fixture = new TestSolutionFixture();
        AddExtrasProject(fixture, ("Clash.cs", "namespace ContosoOrders.Shared;\r\n\r\npublic class Clash\r\n{\r\n}\r\n"));
        var source = WriteInTests(fixture, "EdgeClash.cs", "namespace ContosoOrders.Shared;\r\n\r\npublic class Clash\r\n{\r\n    public ContosoOrders.Core.OrderLine? Line { get; set; }\r\n}\r\n");
        var destination = PathIn(fixture, ExtrasFolder, "Moved", "EdgeClash.cs");
        var extrasCsproj = PathIn(fixture, ExtrasFolder, ExtrasFolder + ".csproj");
        using var manager = await LoadWithExtrasAsync(fixture);
        var tools = BuildTools(manager);
        var csprojBefore = File.ReadAllBytes(extrasCsproj);
        var sourceBefore = File.ReadAllBytes(source);

        var result = await MoveAsync(tools, source, destination, addProjectReferences: true);

        Assert.That(result.IsError, Is.True, "The duplicate type breaks the build.");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(DetailText(result), Does.Contain("CS0101"), "The refusal names the duplicate definition.");
        Assert.That(DetailText(result), Does.Not.Contain("CS0246"), "OrderLine resolved, so the compile ran with the edge added.");
        Assert.That(File.ReadAllBytes(extrasCsproj), Is.EqualTo(csprojBefore), "The csproj is written only when the gate passes.");
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBefore), "The source is untouched.");
        Assert.That(File.Exists(destination), Is.False, "Nothing reached the destination.");
    }

    // Heavy fixture: Dup is declared in Core and in Extras. EdgeMover moves into Extras and EdgeUser (stays in Tests) uses it, so the gate adds Tests -> Extras; Tests then
    // sees Dup twice (CS0433), an error the new reference itself causes. The gate must refuse it and write nothing.
    [Test]
    public async Task EdgeInducedError_IsRefusedAndNothingWrittenAsync()
    {
        using var fixture = new TestSolutionFixture();
        AddExtrasProject(fixture, ("Dup.cs", "namespace ContosoOrders.Shared;\r\n\r\npublic class Dup\r\n{\r\n}\r\n"));
        File.WriteAllText(PathIn(fixture, CoreFolder, "Dup.cs"), "namespace ContosoOrders.Shared;\r\n\r\npublic class Dup\r\n{\r\n}\r\n");
        var source = WriteInTests(fixture, "EdgeMover.cs", "namespace ContosoOrders.Tests;\r\n\r\npublic class EdgeMover\r\n{\r\n}\r\n");
        var user = WriteInTests(fixture, "EdgeUser.cs", "using ContosoOrders.Shared;\r\n\r\nnamespace ContosoOrders.Tests;\r\n\r\npublic class EdgeUser\r\n{\r\n    public EdgeMover? Mover { get; set; }\r\n\r\n    public Dup? Duplicate { get; set; }\r\n}\r\n");
        var destination = PathIn(fixture, ExtrasFolder, "Moved", "EdgeMover.cs");
        var testsCsproj = PathIn(fixture, TestsFolder, TestsFolder + ".csproj");
        var extrasCsproj = PathIn(fixture, ExtrasFolder, ExtrasFolder + ".csproj");
        using var manager = await LoadWithExtrasAsync(fixture);
        var tools = BuildTools(manager);
        var testsCsprojBefore = File.ReadAllBytes(testsCsproj);
        var extrasCsprojBefore = File.ReadAllBytes(extrasCsproj);
        var sourceBefore = File.ReadAllBytes(source);
        var userBefore = File.ReadAllBytes(user);

        var result = await MoveAsync(tools, source, destination, addProjectReferences: true);

        Assert.That(result.IsError, Is.True, "The new reference makes Dup ambiguous in Tests.");
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.ValidationFailed));
        Assert.That(DetailText(result), Does.Contain("CS0433"), "The refusal names the ambiguity the edge causes.");
        Assert.That(File.ReadAllBytes(testsCsproj), Is.EqualTo(testsCsprojBefore), "The Tests project file is unchanged.");
        Assert.That(File.ReadAllBytes(extrasCsproj), Is.EqualTo(extrasCsprojBefore), "The Extras project file is unchanged.");
        Assert.That(File.ReadAllBytes(source), Is.EqualTo(sourceBefore), "The source is untouched.");
        Assert.That(File.ReadAllBytes(user), Is.EqualTo(userBefore), "The remaining file is untouched.");
        Assert.That(File.Exists(destination), Is.False, "Nothing reached the destination.");
    }

    // Heavy fixture: the retry exists to cover a workspace change between the gate's compile and its apply, so the real chokepoint, the lease and the seam are needed.
    // Attempt 1: the seam performs a foreign write (lease MaxWait short, so it proceeds after the bound while the holder waits for it inside the seam), the apply is refused
    // because the committed solution moved, attempt 2 recomputes the augmented solution from the new committed one and applies.
    [Test]
    [CancelAfter(180000)]
    public async Task WorkspaceMovesBetweenCompileAndApply_RetrySucceedsAsync()
    {
        using var fixture = new TestSolutionFixture();
        var scenario = BuildEdgeScenario(fixture);
        using var manager = await LoadWithExtrasAsync(fixture);
        manager.Lease.MaxWait = TimeSpan.FromMilliseconds(200);
        var tools = BuildTools(manager);
        var csprojBefore = File.ReadAllText(scenario.ExtrasCsprojPath);
        var sourceBefore = File.ReadAllBytes(scenario.Source);
        var seamCalls = 0;
        ApplyChangesResult? foreign = null;
        tools.BetweenEdgeGateAndApply = async _ =>
        {
            seamCalls++;
            if (seamCalls == 1)
            {
                foreign = await ForeignWriteAsync(manager, scenario.ProbePath, 2);
            }
        };

        var result = await MoveAsync(tools, scenario.Source, scenario.Destination, addProjectReferences: true);

        Assert.That(seamCalls, Is.EqualTo(2), "The first apply was a window miss, so the gate ran its second (and last) attempt.");
        Assert.That(foreign, Is.Not.Null);
        Assert.That(foreign!.IsError, Is.False, foreign.Summary);
        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.That(result.SuccessData!.ChangeId, Is.Not.Null.And.Not.Empty, "The retry's apply recorded its own change.");
        Assert.That(File.ReadAllText(scenario.ExtrasCsprojPath), Is.Not.EqualTo(csprojBefore).And.Contain("<ProjectReference Include=\"..\\ContosoOrders.Core\\ContosoOrders.Core.csproj\""), "The retry wrote the project file.");
        Assert.That(File.Exists(scenario.Source), Is.False, "The source is gone.");
        Assert.That(File.ReadAllBytes(scenario.Destination), Is.EqualTo(sourceBefore), "The destination holds the original bytes.");
        Assert.That(File.ReadAllText(scenario.ProbePath), Does.Contain("=> 2"), "The foreign write stands.");
        Assert.That(AllText(result), Does.Not.Contain(WorkspaceChangedFragment), "A retry that succeeded reports no window-miss fact.");
        Assert.That(manager.Lease.IsHeld, Is.False, "The gate released the lease.");
        await WaitForReloadAsync(manager);
    }

    // Both attempts are window misses: the seam performs a foreign write each time, so the gate gives up after its single retry. The refusal is the safe one (nothing of the
    // move or the csproj written) and carries the WorkspaceChangedDuringGate fact in its message and findings.
    [Test]
    [CancelAfter(180000)]
    public async Task WorkspaceMovesBetweenCompileAndApply_SecondMiss_ReturnsRefusalWithWorkspaceChangedFactAsync()
    {
        using var fixture = new TestSolutionFixture();
        var scenario = BuildEdgeScenario(fixture);
        using var manager = await LoadWithExtrasAsync(fixture);
        manager.Lease.MaxWait = TimeSpan.FromMilliseconds(200);
        var tools = BuildTools(manager);
        var csprojBefore = File.ReadAllBytes(scenario.ExtrasCsprojPath);
        var sourceBefore = File.ReadAllBytes(scenario.Source);
        var seamCalls = 0;
        var foreignResults = new List<ApplyChangesResult>();
        tools.BetweenEdgeGateAndApply = async _ =>
        {
            seamCalls++;
            foreignResults.Add(await ForeignWriteAsync(manager, scenario.ProbePath, 1 + seamCalls));
        };

        var result = await MoveAsync(tools, scenario.Source, scenario.Destination, addProjectReferences: true);

        Assert.That(seamCalls, Is.EqualTo(2), "The gate tries exactly twice.");
        Assert.That(foreignResults.Select(foreign => foreign.IsError), Is.All.False, "Both foreign writes landed.");
        Assert.That(result.IsError, Is.True, "A second miss is refused.");
        Assert.That(result.ErrorData!.ErrorCode, Is.AnyOf(ToolErrorCode.ValidationFailed, ToolErrorCode.ConcurrentEditRefused), "The refusal comes from the apply that saw a moved workspace.");
        Assert.That(result.ErrorData.Message, Does.Contain(WorkspaceChangedFragment), "The message says the check ran without the added references.");
        Assert.That(FindingTexts(result), Has.Some.Contain(WorkspaceChangedFragment), "The WorkspaceChangedDuringGate fact is a finding on the refusal.");
        Assert.That(File.ReadAllBytes(scenario.ExtrasCsprojPath), Is.EqualTo(csprojBefore), "The csproj is unchanged.");
        Assert.That(File.ReadAllBytes(scenario.Source), Is.EqualTo(sourceBefore), "The source is unchanged.");
        Assert.That(File.Exists(scenario.Destination), Is.False, "Nothing reached the destination.");
        Assert.That(manager.Lease.IsHeld, Is.False, "The gate released the lease.");
    }
}
