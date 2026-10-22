// Battery #22 -> IntelligenceTools
// Tests all 45 public methods of IntelligenceTools in-memory via TestSolutionBuilder.
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tests.Support;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
public class BatteryTwentyTwoTests
{
    private IWorkspaceManager _workspaceManager;
    private SentinelConfiguration _config;
    private ImpactAnalyzer _impactAnalyzer;
    private DiscoveryEngine _semanticSearchEngine;
    private MetricsEngine _metricsEngine;
    private InventoryEngine _inventoryEngine;
    private DeadCodeEngine _deadCodeEngine;
    private DocumentationEngine _documentationEngine;
    private DependencyEngine _dependencyEngine;
    private SolutionStructureEngine _solutionStructureEngine;
    private AsyncAnalysisEngine _asyncSafetyEngine;
    private HealthOrchestrationEngine _healthOrchestrationEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;
    private DependencyInjectionEngine _dependencyInjectionEngine;
    private DiscoveryEngine _discoveryEngine;
    private IntelligenceTools _tools;
    private SymbolRelationshipTools _symbolRelationshipTools;
    private SymbolNavigationTools _symbolNavigationTools;
    private ScanTools _scanTools;
    private static readonly string RichSource = Sources.RichOrderService;
    private const string GenericSource = """
        namespace TestProj;

        public class EngineResultWrapper<T>
        {
            public T? Value { get; set; }
        }

        public class PairWrapper<TKey, TValue>
        {
            public TKey? Key { get; set; }
        }

        public class PlainType
        {
            public int Count { get; set; }
        }
        """;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _config = new SentinelConfiguration();
        _impactAnalyzer = new ImpactAnalyzer(_workspaceManager, NullLogger<ImpactAnalyzer>.Instance);
        _semanticSearchEngine = new DiscoveryEngine(_workspaceManager);
        _metricsEngine = new MetricsEngine(_workspaceManager);
        _inventoryEngine = new InventoryEngine(_workspaceManager);
        _deadCodeEngine = new DeadCodeEngine(_workspaceManager, _config);
        _documentationEngine = new DocumentationEngine(_workspaceManager);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _solutionStructureEngine = new SolutionStructureEngine(_workspaceManager, _config);
        _asyncSafetyEngine = new AsyncAnalysisEngine(_workspaceManager);
        _healthOrchestrationEngine = new HealthOrchestrationEngine(_workspaceManager, _solutionStructureEngine, _config, new PerformanceEngine(_workspaceManager), new AntiPatternEngine(_workspaceManager, _config));
        _antiPatternEngine = new AntiPatternEngine(_workspaceManager, _config);
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _dependencyInjectionEngine = new DependencyInjectionEngine(_workspaceManager);
        _discoveryEngine = new DiscoveryEngine(_workspaceManager, _symbolNavigationEngine);
        _tools = BatteryToolFactory.CreateIntelligenceTools(_workspaceManager);
        // Symbol-level tools moved to SymbolNavigationTools (Basic) in the server split.
        _symbolRelationshipTools = BatteryToolFactory.CreateSymbolRelationshipTools(_workspaceManager);
        _symbolNavigationTools = BatteryToolFactory.CreateSymbolNavigationTools(_workspaceManager);
        // GetPublicApiSurfaceTool moved to ScanTools (Advanced).
        _scanTools = BatteryToolFactory.CreateScanTools(_workspaceManager);

        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrappers.cs", RichSource)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();
    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // --- RunScanDetectorTool ---
    [Test]
    [Category("DetectorId")] // sentinel:auto-category
    [Category("ScanTools")] // sentinel:auto-category
    public async Task RunScanDetector_UnusedReferencesWithoutProjectScope_ReturnsInvalidArgument()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _scanTools.RunScanDetectorTool(reason: "test message", detector: ScanTools.DetectorId.unused_references, scope: ToolScope.file, filePath: "Test.cs");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Is.EqualTo("This detector requires scope=\"project\" with a valid projectName as scopeName. Received scope=\"file\", scopeName=null."));
    }

    // --- GetComprehensiveHealthReportTool ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetComprehensiveHealthReport_ValidSolution_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetComprehensiveHealthReportTool(reason: "test message", timeoutSeconds: 5);
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
        var report = (ComprehensiveHealthReport)result.SuccessData!;
        Assert.That(report.ProjectSummaries, Has.Count.EqualTo(1));
        Assert.That(report.HasMoreData, Is.False);
        Assert.That(report.NextProjectOffset, Is.Null);
    }

    // --- GetBlastRadius (via InspectSymbolTool) ---
    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task GetBlastRadius_FieldWithObjectCreationInitializer_ResolvesFieldNotConstructor()
    {
        const string fieldSource = "using System.Collections.Generic;\nclass C { public static readonly IReadOnlySet<string> Names = new HashSet<string>(new[] { \"a\" }); void M() { var n = Names; } }";
        SetSource(fieldSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "public static readonly IReadOnlySet<string> Names = new HashSet<string>(", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
        var report = (ImpactReport)result.SuccessData!;
        Assert.That(report.Error, Is.Null);
        Assert.That(report.SymbolKind, Is.EqualTo("Field"));
        Assert.That(report.SymbolName, Does.Contain("Names"));
        Assert.That(report.TotalCallSites, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task GetBlastRadius_ValidMethod_ReturnsReport()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "ProcessAsync", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
        var report = (ImpactReport)result.SuccessData!;
        Assert.That(report.Error, Is.Null);
        Assert.That(report.SymbolName, Does.Contain("ProcessAsync"));
    }

    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task GetBlastRadius_ValidMethod_ReportHasNoErrorAndNamesSymbol()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "ProcessAsync", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
        var report = (ImpactReport)result.SuccessData!;
        Assert.That(report.Error, Is.Null);
        Assert.That(report.SymbolName, Does.Contain("ProcessAsync"));
    }

    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task GetBlastRadius_UnresolvableSnippet_ReturnsNotFoundError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "ThisTextDoesNotExistAnywhere", InspectSymbolAspect.blastRadius);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound.ToString()));
        Assert.That(result.ErrorData!.Message, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task InspectSymbol_UnresolvableSnippet_InfoAndBlastRadiusAgree()
    {
        SetSource(RichSource, "Test.cs");
        var resultInfo = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "ThisTextDoesNotExistAnywhere", InspectSymbolAspect.info);
        var resultBlastRadius = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "ThisTextDoesNotExistAnywhere", InspectSymbolAspect.blastRadius);
        Assert.That(resultInfo.IsError, Is.True);
        Assert.That(resultBlastRadius.IsError, Is.True);
        Assert.That(resultInfo.ErrorData, Is.Not.Null);
        Assert.That(resultBlastRadius.ErrorData, Is.Not.Null);
        Assert.That(resultInfo.ErrorData!.Message, Is.Not.Null.And.Not.Empty);
        Assert.That(resultBlastRadius.ErrorData!.Message, Is.Not.Null.And.Not.Empty);
        Assert.That(resultInfo.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound.ToString()));
        Assert.That(resultBlastRadius.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound.ToString()));
    }

    // --- FindMethodsByReturnType (via QuerySymbolRelationshipsTool) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindMethodsByReturnType_ValidType_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "List<string>", FindUsagesSearchKind.methodsByReturnType);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.EqualTo(1));
    }

    // --- GetSolutionMetricsTool ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetSolutionMetrics_LoadedSolution_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetricsTool(reason: "test message");
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
        var metrics = (SolutionMetrics)result.SuccessData!;
        Assert.That(metrics.ProjectCount, Is.EqualTo(1));
        Assert.That(metrics.TotalFiles, Is.EqualTo(1));
        Assert.That(metrics.TotalTypes, Is.EqualTo(3));
        Assert.That(metrics.Projects, Has.Count.EqualTo(1));
        Assert.That(metrics.TotalMethods, Is.EqualTo(9));
        Assert.That(metrics.TotalLines, Is.EqualTo(72));
    }

    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetSolutionMetrics_WithProjectName_ReturnsMetrics()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetSolutionMetricsTool(reason: "test message", "TestProj");
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
        var metrics = (SolutionMetrics)result.SuccessData!;
        Assert.That(metrics.ProjectCount, Is.EqualTo(1));
        Assert.That(metrics.TotalFiles, Is.EqualTo(1));
        Assert.That(metrics.TotalTypes, Is.EqualTo(3));
        Assert.That(metrics.Projects, Has.Count.EqualTo(1));
    }

    // --- GetCodeInventoryTool ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetCodeInventory_ValidFile_ReturnsInventory()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCodeInventoryTool(reason: "test message", "Test.cs");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.TotalRecords, Is.GreaterThanOrEqualTo(5));
    }

    // --- FindUnusedPrivateMembers (via DeadCodeEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task FindUnusedPrivateMembers_ValidClass_ReturnsList()
    {
        SetSource("public class Order\n{\n    private void Unused() { }\n    public int Id { get; set; }\n}", "Test.cs");
        var result = await _deadCodeEngine.FindUnusedPrivateMembersAsync("Test.cs", "Order");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Single().SymbolName, Is.EqualTo("Unused"));
        Assert.That(result.Single().Type, Is.EqualTo("UnusedPrivateMember"));
        // TODO Engine only reports private methods/properties, not fields (DeadCodeEngine.cs:37)
    }

    // --- DetectUnusedPrivateFields (via DeadCodeEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task DetectUnusedPrivateFields_ValidFile_ReturnsList()
    {
        SetSource("public class Order\n{\n    private int _unused;\n}", "Test.cs");
        var result = await _deadCodeEngine.DetectUnusedPrivateFieldsAsync("Test.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Single().SymbolName, Is.EqualTo("_unused"));
    }

    // --- DetectUnusedLocalVariables (via DeadCodeEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task DetectUnusedLocalVariables_ValidFile_ReturnsList()
    {
        SetSource("public class Order\n{\n    public void M()\n    {\n        var unusedLocal = 1;\n    }\n}", "Test.cs");
        var result = await _deadCodeEngine.DetectUnusedLocalVariablesAsync("Test.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Single().SymbolName, Is.EqualTo("unusedLocal"));
    }

    // --- DetectLongParameterLists (via AnalysisEngine) ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task DetectLongParameterLists_ValidSolution_ReturnsList()
    {
        SetSource("public class Order\n{\n    public void M(int a, int b, int c, int d, int e, int f) { }\n}", "Test.cs");
        var result = await _antiPatternEngine.DetectLongParameterListsAsync();
        Assert.That(result, Has.Count.EqualTo(1));
        //Assert.That(result.Count > 0);
    }

    // --- FindUninstantiatedTypes (via AnalysisEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task FindUninstantiatedTypes_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUninstantiatedTypesAsync();
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("Type 'TestProj.Order' in Test.cs is never instantiated."));
        Assert.That(result, Does.Contain("Type 'TestProj.OrderService' in Test.cs is never instantiated."));
        //Assert.That(result.Count > 0);
    }

    // --- FindCircularDependencies (no params, via AnalysisEngine) ---
    [Test]
    [Category("SolutionStructureEngine")] // sentinel:auto-category
    public async Task FindCircularDependencies_NoParams_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _solutionStructureEngine.FindCircularDependenciesAsync();
        Assert.That(result, Is.Empty);
        // the engine detects type-level cycles, not project-reference cycles (see FindCircularDependencies_TypeCycle_ReportsBothTypes)
    }

    [Test]
    [Category("SolutionStructureEngine")] // sentinel:auto-category
    public async Task FindCircularDependencies_TypeCycle_ReportsBothTypes()
    {
        var source = """
            namespace TestProj;

            public class A
            {
                public B? Partner { get; set; }
            }

            public class B
            {
                public A? Partner { get; set; }
            }
            """;
        SetSource(source, "Cycle.cs");
        var result = await _solutionStructureEngine.FindCircularDependenciesAsync();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Cycle, Is.EqualTo(new[] { "TestProj.A", "TestProj.B", "TestProj.A" }));
        Assert.That(result[0].CycleType, Is.EqualTo("Direct"));
    }

    // --- GenerateCallTree (via GetCallGraphTool "tree") ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GenerateCallTree_ValidMethod_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraphTool(reason: "test message", "Test.cs", "ProcessAsync", "tree");
        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData!.ToString(), Does.Contain("ProcessAsync"));
        //Assert.That(result.TotalRecords > 0);
    }

    // --- DocumentPocoFields (via DocumentationEngine) ---
    [Test]
    [Category("DocumentationEngine")] // sentinel:auto-category
    public async Task DocumentPocoFields_ValidClass_ReturnsString()
    {
        SetSource("public class Order\n{\n    public int OrderId { get; set; }\n}", "Test.cs");
        var result = await _documentationEngine.DocumentPocoFieldsAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("[Description(\"Gets or sets the OrderId.\")]"));
        // TODO DocumentPocoFieldsAsync annotates only properties; fields are ignored (RichSource Order.OrderId and CustomerName are fields)
    }

    // --- GenerateEqualityOverrides (via AnalysisEngine) ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task GenerateEqualityOverrides_ValidClass_ReturnsString()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _antiPatternEngine.GenerateEqualityOverridesAsync("Test.cs", "Order");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("GetHashCode"));
    }

    // --- FindUnusedReferences (via DependencyEngine) ---
    [Test]
    [Category("DependencyEngine")] // sentinel:auto-category
    public async Task FindUnusedReferences_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _dependencyEngine.FindUnusedReferencesAsync("TestProj");
        Assert.That(result, Is.Empty);
    }

    [Test]
    [Category("DependencyEngine")] // sentinel:auto-category
    public async Task FindUnusedReferences_UnreferencedProjectReference_IsReported()
    {
        var projectIdA = ProjectId.CreateNewId();
        var projectIdB = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(projectIdA, "ProjA", "ProjA", LanguageNames.CSharp)
            .AddProject(projectIdB, "ProjB", "ProjB", LanguageNames.CSharp)
            .AddProjectReference(projectIdA, new ProjectReference(projectIdB));
        _workspaceManager.SetTestSolution(solution);
        var result = await _dependencyEngine.FindUnusedReferencesAsync("ProjA");
        Assert.That(result, Is.EqualTo(new[] { "ProjB" }));
    }

    // --- CheckPackageInconsistency (via DependencyEngine) ---
    // Reads project.FilePathWrapper directly off disk (regexes <PackageReference> out of the raw
    // .csproj XML -> Roslyn's in-memory Project model has no NuGet-version API), so unlike the
    // rest of this battery it can't run against TestSolutionBuilder's in-memory fake project
    // path. Uses a real on-disk solution via TestSolutionFixture instead.
    [Test]
    [Category("DependencyEngine")] // sentinel:auto-category
    public async Task CheckPackageInconsistency_ValidSolution_ReturnsList()
    {
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var result = await dependencyEngine.CheckPackageInconsistencyAsync();
        Assert.That(result, Is.Empty);
    }

    [Test]
    [Category("DependencyEngine")] // sentinel:auto-category
    public async Task CheckPackageInconsistency_MismatchedVersions_IsReported()
    {
        using var fixture = new TestSolutionFixture();
        var csprojPaths = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.csproj", SearchOption.AllDirectories).Order().ToArray();
        Assert.That(csprojPaths, Has.Length.GreaterThanOrEqualTo(2));
        const string packageId = "Contoso.Fixture.Probe";
        var firstText = await File.ReadAllTextAsync(csprojPaths[0]);
        await File.WriteAllTextAsync(csprojPaths[0], firstText.Replace("</Project>", $"  <ItemGroup>\n    <PackageReference Include=\"{packageId}\" Version=\"13.0.1\" />\n  </ItemGroup>\n</Project>"));
        var secondText = await File.ReadAllTextAsync(csprojPaths[1]);
        await File.WriteAllTextAsync(csprojPaths[1], secondText.Replace("</Project>", $"  <ItemGroup>\n    <PackageReference Include=\"{packageId}\" Version=\"12.0.3\" />\n  </ItemGroup>\n</Project>"));
        using var workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);
        var dependencyEngine = new DependencyEngine(workspaceManager);
        var result = await dependencyEngine.CheckPackageInconsistencyAsync();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0], Does.StartWith($"Package '{packageId}' has multiple versions: "));
        Assert.That(result[0], Does.Contain($"{Path.GetFileNameWithoutExtension(csprojPaths[0])} (13.0.1)"));
        Assert.That(result[0], Does.Contain($"{Path.GetFileNameWithoutExtension(csprojPaths[1])} (12.0.3)"));
    }

    // --- FindUnusedInterfaces (via AnalysisEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task FindUnusedInterfaces_ValidSolution_ReturnsList()
    {
        SetSource("namespace TestProj;\npublic interface IFoo { }\npublic class Bar { }", "Test.cs");
        var result = await _deadCodeEngine.FindUnusedInterfacesAsync();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Does.Contain("Interface 'IFoo' has no implementations."));
        //Assert.That(result.Count > 0);
    }

    // --- FindInternalClassesThatCouldBePrivate (via AnalysisEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task FindInternalClassesThatCouldBePrivate_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindInternalClassesThatCouldBePrivateAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindLargeSwitchStatements (via AnalysisEngine) ---
    [Test]
    [Category("AntiPatternEngine")] // sentinel:auto-category
    public async Task FindLargeSwitchStatements_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _antiPatternEngine.FindLargeSwitchStatementsAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindStructuralSmells (via SolutionStructureEngine) ---
    [Test]
    [Category("SolutionStructureEngine")] // sentinel:auto-category
    public async Task FindStructuralSmells_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _solutionStructureEngine.FindStructuralSmellsAsync();
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count > 0);
    }

    // --- FindUnusedConstructors (via DeadCodeEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task FindUnusedConstructors_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _deadCodeEngine.FindUnusedConstructorsAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- CheckForUnusedEventSubscriptions (via DeadCodeEngine) ---
    [Test]
    [Category("DeadCodeEngine")] // sentinel:auto-category
    public async Task CheckForUnusedEventSubscriptions_ValidFile_ReturnsList()
    {
        SetSource("public class Publisher\n{\n    public event System.EventHandler Changed;\n}\npublic class Listener\n{\n    public Listener(Publisher p)\n    {\n        p.Changed += OnChanged;\n    }\n    private void OnChanged(object s, System.EventArgs e) { }\n}", "Test.cs");
        var result = await _deadCodeEngine.CheckForUnusedEventSubscriptionsAsync("Test.cs");
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.Single().SymbolName, Is.EqualTo("p.Changed"));
        //Assert.That(result, Is.Not.Null);
    }

    // --- GetSymbolInfo (via InspectSymbolTool) ---
    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task GetSymbolInfo_ValidSymbolSnippet_ReturnsInfo()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.InspectSymbolTool(reason: "test message", "Test.cs", "ProcessAsync", InspectSymbolAspect.info);
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindAllImplementations (via QuerySymbolRelationshipsTool) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindAllImplementations_ValidInterface_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "IOrderService", FindUsagesSearchKind.implementorsOf);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords > 0);
    }

    // --- FindReadonlyFieldCandidates (via SymbolNavigationEngine) ---
    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task FindReadonlyFieldCandidates_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationEngine.FindReadonlyFieldCandidatesAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindDiRegistrations (now GetDiRegistrationsTool) ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task FindDiRegistrations_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetDiRegistrationsTool(reason: "test message");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- GetTypeMembersDetail (via GetTypeInfoTool) ---
    [Test]
    [Category("SymbolNavigationTools")] // sentinel:auto-category
    public async Task GetTypeMembersDetail_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolNavigationTools.GetTypeInfoTool(reason: "test message", "Order", include: TypeInfoInclude.members);
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindExtensionMethods (via QuerySymbolRelationshipsTool) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindExtensionMethods_ValidType_ReturnsList()
    {
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "Order", FindUsagesSearchKind.extensionsFor);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords > 0);
    }

    // --- AnalyzeTypeCohesion (via MetricsEngine) ---
    [Test]
    [Category("MetricsEngine")] // sentinel:auto-category
    public async Task AnalyzeTypeCohesion_ValidFile_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _metricsEngine.AnalyzeTypeCohesionAsync("Test.cs");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count > 0);
    }

    // --- FindCircularDependencies (with projectName, via SolutionStructureEngine) ---
    [Test]
    [Category("SolutionStructureEngine")] // sentinel:auto-category
    public async Task FindCircularDependencies_WithProjectName_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _solutionStructureEngine.FindCircularDependenciesAsync("TestProj");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- GetCallGraphTool ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraphTool(reason: "test message", "Test.cs", "ProcessAsync");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetCallGraph_NonExistentMethod_ReturnsStructuredError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraphTool(reason: "test message", "Test.cs", "NoSuchMethod99");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
    }

    // --- GetReverseCallGraph (via GetCallGraphTool "reverse") ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetReverseCallGraph_ValidMethod_ReturnsCallGraph()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraphTool(reason: "test message", "Test.cs", "GetStatus", "reverse");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task GetReverseCallGraph_NonExistentMethod_ReturnsStructuredError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.GetCallGraphTool(reason: "test message", "Test.cs", "NoSuchMethod99", "reverse");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
    }

    // --- MoveFileToNamespaceFolder ---
    [Test]
    [Category("IntelligenceTools")] // sentinel:auto-category
    public async Task PreviewMoveFileToNamespaceFolder_ValidFile_ReturnsPreview()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _tools.PreviewMoveFileToNamespaceFolderTool(reason: "test message", "Test.cs");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.SuccessData, Is.Not.Null);
        Assert.That(result.IsError, Is.False);
    }

    // --- FindAllThrowSites (via DiscoveryEngine) ---
    [Test]
    [Category("DiscoveryEngine")] // sentinel:auto-category
    public async Task FindAllThrowSites_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _discoveryEngine.FindAllThrowSitesAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindObjectCreationSites (via FindByName) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindObjectCreationSites_ValidType_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "Order", FindUsagesSearchKind.objectCreations);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalRecords, Is.EqualTo(1));
    }

    // --- GetPublicApiSurfaceTool ---
    [Test]
    [Category("ScanTools")] // sentinel:auto-category
    public async Task GetPublicApiSurface_ValidProject_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _scanTools.GetPublicApiSurfaceTool("TestProj");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindServicesNotRegistered (via DependencyInjectionEngine) ---
    [Test]
    [Category("DependencyInjectionEngine")] // sentinel:auto-category
    public async Task FindServicesNotRegistered_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _dependencyInjectionEngine.FindServicesNotRegisteredAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- FindBestInsertionPoint (now GetBestInsertionPointTool) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindBestInsertionPoint_ValidClass_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.GetBestInsertionPointTool(reason: "test message", "Test.cs", "Order", InsertionMemberKind.method);
        Assert.That(result, Is.Not.Null);
        Assert.That(!result.IsError, Is.True);
    }

    // --- FindTodoFixmeComments (via DiscoveryEngine) ---
    [Test]
    [Category("DiscoveryEngine")] // sentinel:auto-category
    public async Task FindTodoFixmeComments_ValidSolution_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _discoveryEngine.FindTodoFixmeCommentsAsync();
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.Count > 0);
    }

    // --- PreviewRenameImpactTool ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task PreviewRenameImpact_ValidSymbol_ReturnsPreview()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.PreviewRenameImpactTool(reason: "test message", filePath: "Test.cs", symbolName: "ProcessAsync");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindCallersSafe (via FindReferencesTool) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindCallersSafe_ValidSymbol_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.FindReferencesTool(reason: "test message", "ProcessAsync", FindReferencesKind.callers, filePath: "Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindImplementationsSafe (via FindReferencesTool) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindImplementationsSafe_ValidInterface_ReturnsList()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.FindReferencesTool(reason: "test message", "IOrderService", FindReferencesKind.implementations, filePath: "Test.cs");
        Assert.That(result, Is.Not.Null);
        //Assert.That(result.TotalRecords > 0);
    }

    // --- FindReferencesTool(kind: all) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task FindReferences_KindAll_ReturnsBothCallersAndImplementations()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.FindReferencesTool(reason: "test message", "ProcessAsync", FindReferencesKind.all, filePath: "Test.cs");
        Assert.That(result.IsError, Is.False);
        Assert.That(result.SuccessData, Is.Not.Null);
        var json = System.Text.Json.JsonSerializer.Serialize(result.SuccessData);
        Assert.That(json, Does.Contain("callers"));
        Assert.That(json, Does.Contain("implementations"));
    }

    // --- QuerySymbolRelationshipsTool (renamed from FindUsages) ---
    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task QuerySymbolRelationships_ObjectCreationsForRealType_ReturnsResult()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "Order", FindUsagesSearchKind.objectCreations);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsError, Is.False);
    }

    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task QuerySymbolRelationships_ObjectCreationsForMethodName_ReturnsSemanticGuardError()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "ProcessAsync", FindUsagesSearchKind.objectCreations);
        Assert.That(!result.IsError, Is.False, "objectCreations against a method name must be rejected, not silently return [].");
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("Search(mode: callers"), "The guard should point the caller at Search(mode: callers) instead of objectCreations for a member name.");
    }

    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task QuerySymbolRelationships_EmptyTargetedKind_BroadensAndFindsUnderAnotherKind()
    {
        SetSource(RichSource, "Test.cs");
        // "IOrderService" has zero attribute usages, but a real implementor exists (OrderService) ->
        // broaden-on-empty should surface that under 'implementorsOf' instead of just returning [].
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "IOrderService", FindUsagesSearchKind.attributeUsages);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.WarningDetails, Is.Not.Null.And.Contains("Broadened"));
        Assert.That(result.WarningDetails, Does.Contain("implementorsOf"));
    }

    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task QuerySymbolRelationships_EmptyUnderAllKinds_ReturnsPlainNotFoundSignal()
    {
        SetSource(RichSource, "Test.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "ThisNameAppearsNowhereInTheSolution", FindUsagesSearchKind.attributeUsages);
        Assert.That(result.IsError, Is.False);
        Assert.That(result.WarningDetails, Does.Contain("nothing found under any kind"));
    }


    [Test]
    [Category("SymbolRelationshipTools")] // sentinel:auto-category
    public async Task QuerySymbolRelationships_AttributeUsages_TopLevelClassTarget_ReturnsMatchNotCrash()
    {
        // Regression test for blocking_error_findattributeusages_first_throws_on_unresolved_target.md:
        // FindAttributeUsagesAsync used to pass containingType: "" (empty string) for every top-level
        // type declaration, but LocateSymbolAsync's containingType filter only activates when the
        // argument is non-null - and a top-level type's own symbol.ContainingType is null, so "" != null
        // always excluded it, leaving LocateSymbolAsync's result empty and an unguarded .First() call
        // throwing InvalidOperationException for every single top-level attribute usage.
        const string source = @"
using System;
namespace TestProj;

public class ProbeAttribute : Attribute { }

[Probe]
public class AttributedTarget { }
";
        SetSource(source, "AttributeProbe.cs");
        var result = await _symbolRelationshipTools.QuerySymbolRelationshipsTool(reason: "test message", "Probe", FindUsagesSearchKind.attributeUsages);
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        var sites = (result.SuccessData as System.Collections.IEnumerable)?.Cast<object>().ToList();
        Assert.That(sites, Is.Not.Null.And.Count.EqualTo(1));
    }

    public AntiPatternEngine _antiPatternEngine;
}