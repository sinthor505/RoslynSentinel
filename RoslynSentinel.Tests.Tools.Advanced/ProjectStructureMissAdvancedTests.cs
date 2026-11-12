using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

// Not-found text of the project lookups in SolutionStructureEngine and DiscoveryEngine whose hosting tool is an Advanced tool (ScanTools):
// plan_not_found_error_orientation, Step 5e slice 1. A near-miss project name gets the ranked "Did you mean" candidate with its retry and the
// loaded-project list, an unrelated name gets the no-similar-name fact, and a refused call writes nothing (these tools are read-only, so the
// file text is compared before and after). The in-memory workspace names its only project "InMemoryProject".
// Producers covered: FindUnusedReferencesAsync (RunScanDetector unused_references), DetectLayerViolationsAsync (RunScanDetector layer_violations),
// SolutionStructureEngine.GetPublicApiSurfaceAsync (GetPublicApiSurface persistBaseline: true), DiscoveryEngine.GetPublicApiSurfaceAsync
// (GetPublicApiSurface persistBaseline: false, the tool prefixes "GetPublicApiSurface failed: ").
// The Tools.Basic project hosts are covered in Tests.Tools.Basic (ProjectStructureMissTests, DiscoveryMissTests).
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("ScanTools")]
public sealed class ProjectStructureMissAdvancedTests
{
    private const string FileName = "Types.cs";

    private const string Source = "public class Alpha\r\n{\r\n}\r\n";

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private static void AssertCandidate(string message)
    {
        Assert.That(message, Does.Contain("Did you mean 'InMemoryProject'? Retry with projectName: 'InMemoryProject'."));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        AssertFactsAndOptionsOnly(message);
    }

    private static void AssertNoCandidate(string message, string wanted)
    {
        Assert.That(message, Does.Contain($"No loaded project has a similar name to '{wanted}'."));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        Assert.That(message, Does.Contain("Loaded projects: InMemoryProject."));
        AssertFactsAndOptionsOnly(message);
    }

    // Runs one refused call: a NotFound error and the file untouched. Returns the error text.
    private static async Task<string> RunMissAsync(Func<ScanTools, Task<SentinelCallToolResult<object>>> call)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, Source));
        var before = workspace.ReadText(FileName);
        var result = await call(BatteryToolFactory.CreateScanTools(workspace.Manager));
        Assert.That(result.IsError, Is.True, result.StatusMessage);
        Assert.That(result.ErrorData?.ErrorCode.ToString(), Is.EqualTo(nameof(ToolErrorCode.NotFound)), result.ErrorData?.Message);
        Assert.That(workspace.ReadText(FileName), Is.EqualTo(before), "A refused call must not write the file.");
        return result.ErrorData?.Message ?? string.Empty;
    }

    // ---- RunScanDetector unused_references: FindUnusedReferencesAsync ----

    [Test]
    public async Task RunScanDetectorUnusedReferences_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await RunMissAsync(t => t.RunScanDetectorTool(reason: "test message", detector: ScanTools.DetectorId.unused_references, scope: ToolScope.project, scopeName: "InMemoryProjct"));
        AssertCandidate(message);
    }

    [Test]
    public async Task RunScanDetectorUnusedReferences_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(t => t.RunScanDetectorTool(reason: "test message", detector: ScanTools.DetectorId.unused_references, scope: ToolScope.project, scopeName: "Zzz.Missing"));
        AssertNoCandidate(message, "Zzz.Missing");
    }

    // ---- RunScanDetector layer_violations: DetectLayerViolationsAsync ----

    [Test]
    public async Task RunScanDetectorLayerViolations_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await RunMissAsync(t => t.RunScanDetectorTool(reason: "test message", detector: ScanTools.DetectorId.layer_violations, scope: ToolScope.project, scopeName: "InMemoryProjct"));
        AssertCandidate(message);
    }

    [Test]
    public async Task RunScanDetectorLayerViolations_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(t => t.RunScanDetectorTool(reason: "test message", detector: ScanTools.DetectorId.layer_violations, scope: ToolScope.project, scopeName: "Zzz.Missing"));
        AssertNoCandidate(message, "Zzz.Missing");
    }

    // ---- GetPublicApiSurface persistBaseline: true: SolutionStructureEngine.GetPublicApiSurfaceAsync ----

    [Test]
    public async Task GetPublicApiSurfaceBaseline_MisspeltProject_OffersTheNearProjectAsync()
    {
        var message = await RunMissAsync(t => t.GetPublicApiSurfaceTool(reason: "test message", projectName: "InMemoryProjct", persistBaseline: true));
        AssertCandidate(message);
    }

    [Test]
    public async Task GetPublicApiSurfaceBaseline_NoSimilarProject_StatesTheNoCandidateFactAsync()
    {
        var message = await RunMissAsync(t => t.GetPublicApiSurfaceTool(reason: "test message", projectName: "Zzz.Missing", persistBaseline: true));
        AssertNoCandidate(message, "Zzz.Missing");
    }

    // ---- GetPublicApiSurface persistBaseline: false: DiscoveryEngine.GetPublicApiSurfaceAsync ----

    [Test]
    public async Task GetPublicApiSurface_MisspeltProject_OffersTheNearProjectAndKeepsTheToolPrefixAsync()
    {
        var message = await RunMissAsync(t => t.GetPublicApiSurfaceTool(reason: "test message", projectName: "InMemoryProjct"));
        Assert.That(message, Does.StartWith("GetPublicApiSurface failed: Project 'InMemoryProjct' was not found among the "));
        AssertCandidate(message);
    }

    [Test]
    public async Task GetPublicApiSurface_NoSimilarProject_StatesTheNoCandidateFactAndKeepsTheToolPrefixAsync()
    {
        var message = await RunMissAsync(t => t.GetPublicApiSurfaceTool(reason: "test message", projectName: "Zzz.Missing"));
        Assert.That(message, Does.StartWith("GetPublicApiSurface failed: Project 'Zzz.Missing' was not found among the "));
        AssertNoCandidate(message, "Zzz.Missing");
    }
}
