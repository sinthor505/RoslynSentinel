using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using RoslynSentinel.Server;
using Serilog;
using Serilog.Extensions.Logging;
using System.Linq;

var builder = Host.CreateApplicationBuilder(args);

// --- Command Line Argument Parsing ---
var modeArg = args.FirstOrDefault(a => a.StartsWith("--mode="))?.Replace("--mode=", "") ?? "all";
var solutionPath = args.FirstOrDefault(a => a.StartsWith("--solution="))?.Replace("--solution=", "");
var portArg = args.FirstOrDefault(a => a.StartsWith("--port="))?.Replace("--port=", "");
var hostArg = args.FirstOrDefault(a => a.StartsWith("--host="))?.Replace("--host=", "") ?? "localhost";

var activeModes = modeArg.Equals("all", StringComparison.OrdinalIgnoreCase) 
    ? new HashSet<string> { "Workspace", "Intelligence", "Refactor", "Modernize", "Quality", "Generation" }
    : modeArg.Split(',').Select(m => m.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

Console.WriteLine($"--- BUILD STAMP: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC ---");

// Configure Logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(@"E:\source\repos\RoslynSentinel\publish\logs\server.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Services.AddSingleton<ILoggerFactory>(new SerilogLoggerFactory(Log.Logger));
builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

// --- Register Engines ---
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

// --- Configure MCP Server Transport ---
var mcpBuilder = builder.Services.AddMcpServer();

if (!string.IsNullOrEmpty(portArg) && int.TryParse(portArg, out var port))
{
    // SSE Transport (Web Server)
    mcpBuilder.WithSseServerTransport(options => {
        options.Url = $"http://{hostArg}:{port}/mcp";
    });
}
else
{
    // Default Stdio Transport
    mcpBuilder.WithStdioServerTransport();
}

// --- Tool Registration ---
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

// --- Auto-Load Solution if Provided ---
if (!string.IsNullOrEmpty(solutionPath))
{
    var workspaceManager = host.Services.GetRequiredService<PersistentWorkspaceManager>();
    logger.LogInformation("Auto-loading solution: {Path}", solutionPath);
    // Running fire-and-forget but logged. The manager handles its own async initialization.
    _ = workspaceManager.LoadSolutionAsync(solutionPath);
}

logger.LogInformation("Roslyn Sentinel MCP Server starting. Transport: {Transport}. Modes: {Modes}", 
    string.IsNullOrEmpty(portArg) ? "Stdio" : $"SSE ({hostArg}:{portArg})", 
    string.Join(", ", activeModes));

await host.RunAsync();
