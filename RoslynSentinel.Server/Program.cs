using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynSentinel.Server;
using Serilog;
using Serilog.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Parse Modes from CLI (e.g. --mode Workspace,Intelligence or --mode all)
var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Replace("--mode=", "") ?? "all";
var activeModes = modeArg.Equals("all", StringComparison.OrdinalIgnoreCase) 
    ? new HashSet<string> { "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation" }
    : modeArg.Split(',').Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"--- BUILD STAMP: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---");

// Configure Logging with Serilog directly
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(@"E:\source\repos\RoslynSentinel\publish\logs\server.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(Log.Logger));
builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

// Register Infrastructure
builder.Services.AddSingleton<PersistentWorkspaceManager>();
builder.Services.AddSingleton<DiffEngine>();
builder.Services.AddSingleton<ValidationEngine>();
builder.Services.AddSingleton<ImpactAnalyzer>();
builder.Services.AddSingleton<RefactoringEngine>();
builder.Services.AddSingleton<MetricsEngine>();
builder.Services.AddSingleton<CodeHealingEngine>();
builder.Services.AddSingleton<AnalysisEngine>();
builder.Services.AddSingleton<PerformanceEngine>();
builder.Services.AddSingleton<SecurityEngine>();
builder.Services.AddSingleton<TestingEngine>();
builder.Services.AddSingleton<CodeGenerationEngine>();
builder.Services.AddSingleton<ModernizationEngine>();
builder.Services.AddSingleton<DependencyInjectionEngine>();
builder.Services.AddSingleton<ThreadSafetyEngine>();
builder.Services.AddSingleton<ArchitecturalEngine>();
builder.Services.AddSingleton<AdvancedRefactoringEngine>();
builder.Services.AddSingleton<DocumentationEngine>();
builder.Services.AddSingleton<SecurityAndSafetyEngine>();
builder.Services.AddSingleton<ApiIntegrationEngine>();
builder.Services.AddSingleton<InventoryEngine>();
builder.Services.AddSingleton<AsyncOptimizationEngine>();
builder.Services.AddSingleton<InstrumentationEngine>();
builder.Services.AddSingleton<AdvancedTypeEngine>();
builder.Services.AddSingleton<ModernLoggingEngine>();
builder.Services.AddSingleton<CodeFlowEngine>();
builder.Services.AddSingleton<StructuralRefinementEngine>();
builder.Services.AddSingleton<LogicOptimizationEngine>();
builder.Services.AddSingleton<SemanticSearchEngine>();
builder.Services.AddSingleton<ModernizationUpgradeEngine>();
builder.Services.AddSingleton<AsyncSafetyEngine>();
builder.Services.AddSingleton<ProjectStructureEngine>();
builder.Services.AddSingleton<DeadCodeEngine>();
builder.Services.AddSingleton<SyntaxUpgradeEngine>();
builder.Services.AddSingleton<RefinementEngine>();
builder.Services.AddSingleton<DiagnosticEngine>();
builder.Services.AddSingleton<SolutionManagementEngine>();
builder.Services.AddSingleton<MappingEngine>();
builder.Services.AddSingleton<IDEStyleEngine>();
builder.Services.AddSingleton<StandardRefactoringEngine>();
builder.Services.AddSingleton<ImmutabilityEngine>();
builder.Services.AddSingleton<CodeStyleEngine>();
builder.Services.AddSingleton<DependencyEngine>();
builder.Services.AddSingleton<AdvancedLogicEngine>();
builder.Services.AddSingleton<AdvancedStructuralEngine>();
builder.Services.AddSingleton<SemanticRefactoringLibrary>();
builder.Services.AddSingleton<GranularRefactoringEngine>();
builder.Services.AddSingleton<ApiAutomationEngine>();
builder.Services.AddSingleton<ControlFlowEngine>();
builder.Services.AddSingleton<HealthOrchestrationEngine>();

// Configure MCP Server
var mcpBuilder = builder.Services.AddMcpServer().WithStdioServerTransport();

// Focused Registration
if (activeModes.Contains("Workspace")) 
{
    builder.Services.AddSingleton<SentinelWorkspaceTools>();
    mcpBuilder.WithTools<SentinelWorkspaceTools>();
}
if (activeModes.Contains("Intelligence")) 
{
    builder.Services.AddSingleton<SentinelIntelligenceTools>();
    mcpBuilder.WithTools<SentinelIntelligenceTools>();
}
if (activeModes.Contains("Refactor")) 
{
    builder.Services.AddSingleton<SentinelRefactoringTools>();
    mcpBuilder.WithTools<SentinelRefactoringTools>();
}
if (activeModes.Contains("Modernize")) 
{
    builder.Services.AddSingleton<SentinelModernizationTools>();
    mcpBuilder.WithTools<SentinelModernizationTools>();
}
if (activeModes.Contains("Quality")) 
{
    builder.Services.AddSingleton<SentinelQualityTools>();
    mcpBuilder.WithTools<SentinelQualityTools>();
}
if (activeModes.Contains("Generation")) 
{
    builder.Services.AddSingleton<SentinelGenerationTools>();
    mcpBuilder.WithTools<SentinelGenerationTools>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Roslyn Sentinel MCP Server v1.1.0 (Build: {BuildDate}) starting in modes: {Modes}", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC"), string.Join(", ", activeModes));

await host.RunAsync();
