using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelModernizationTools
{
    private readonly ModernizationEngine _modernizationEngine;
    private readonly ModernizationUpgradeEngine _modernizationUpgradeEngine;
    private readonly ModernLoggingEngine _modernLoggingEngine;
    private readonly SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly LogicOptimizationEngine _logicOptimizationEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly CodeHealingEngine _codeHealingEngine;
    private readonly AdvancedLogicEngine _advancedLogicEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelModernizationTools> _logger;

    public SentinelModernizationTools(
        ModernizationEngine modernizationEngine,
        ModernizationUpgradeEngine modernizationUpgradeEngine,
        ModernLoggingEngine modernLoggingEngine,
        SyntaxUpgradeEngine syntaxUpgradeEngine,
        AnalysisEngine analysisEngine,
        LogicOptimizationEngine logicOptimizationEngine,
        CodeStyleEngine codeStyleEngine,
        CodeHealingEngine codeHealingEngine,
        AdvancedLogicEngine advancedLogicEngine,
        PersistentWorkspaceManager workspaceManager,
        SentinelConfiguration config,
        ILogger<SentinelModernizationTools> logger)
    {
        _modernizationEngine = modernizationEngine;
        _modernizationUpgradeEngine = modernizationUpgradeEngine;
        _modernLoggingEngine = modernLoggingEngine;
        _syntaxUpgradeEngine = syntaxUpgradeEngine;
        _analysisEngine = analysisEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _codeStyleEngine = codeStyleEngine;
        _codeHealingEngine = codeHealingEngine;
        _advancedLogicEngine = advancedLogicEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Converts a class to a C# record.")]
    public async Task<string> ClassToRecord(string filePath, string className) 
        => await _modernizationEngine.ClassToRecordAsync(filePath, className);

    [McpServerTool]
    [Description("Converts a C# record back to a standard class.")]
    public async Task<string> RecordToClass(string filePath, string recordName) 
        => await _modernizationEngine.RecordToClassAsync(filePath, recordName);

    [McpServerTool]
    [Description("Simplifies redundant code patterns globally in a file (target-typed new, null-coalescing assignment, etc.).")]
    public async Task<string> SimplifyVerbosity(string filePath) 
        => await _codeStyleEngine.SimplifyVerbosityAsync(filePath);

    [McpServerTool]
    [Description("Simplifies redundant boolean logic globally in a file.")]
    public async Task<string> SimplifyBooleanLogic(string filePath) 
        => await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(filePath);

    [McpServerTool]
    [Description("Upgrades threading patterns (locks, semaphores) to modern/safe versions.")]
    public async Task<string> UpgradeThreadSafety(string filePath) 
        => await _codeStyleEngine.UpgradeThreadSafetyAsync(filePath);

    [McpServerTool]
    [Description("Replaces lock(this) or lock(typeof(T)) with a private readonly object lock.")]
    public async Task<string> FixDangerousLock(string filePath) 
        => await _codeStyleEngine.FixDangerousLockAsync(filePath);

    [McpServerTool]
    [Description("Wraps SemaphoreSlim.WaitAsync calls in a try-finally block with Release().")]
    public async Task<string> EnsureSemaphoreFinally(string filePath) 
        => await _codeStyleEngine.EnsureSemaphoreFinallyAsync(filePath);

    [McpServerTool]
    [Description("Replaces direct DateTime usage with modern TimeProvider abstractions for testability.")]
    public async Task<string> UseTimeProvider(string filePath) 
        => await _codeStyleEngine.UseTimeProviderAsync(filePath);

    [McpServerTool]
    [Description("Replaces generic throw new Exception calls with custom typed exceptions and generates the new class files. If autoStage is true, returns a ChangeId.")]
    public async Task<object> ModernizeExceptions(List<CodeHealingEngine.ExceptionTarget> targets, bool autoStage = true) 
    {
        var changes = await _codeHealingEngine.ModernizeExceptionsAsync(targets);
        if (autoStage) return _workspaceManager.StageChanges(changes, $"Modernize {targets.Count} generic exceptions with custom types.");
        return changes;
    }

    [McpServerTool]
    [Description("Upgrades legacy if-throw guard clauses to modern static Throw helpers (e.g. ArgumentNullException.ThrowIfNull).")]
    public async Task<string> UpgradeToModernGuards(string filePath) 
        => await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(filePath);

    [McpServerTool]
    [Description("Replaces hardcoded string literals with nameof() expressions where applicable.")]
    public async Task<string> UseNameofExpression(string filePath, int line, int column) 
        => await _syntaxUpgradeEngine.UseNameofExpressionAsync(filePath, line, column);

    [McpServerTool]
    [Description("Upgrades properties with manual backing fields to modern C# 14 auto-properties using the 'field' keyword.")]
    public async Task<string> UseFieldBackedProperties(string filePath) 
        => await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(filePath);

    [McpServerTool]
    [Description("Removes redundant .AsSpan() calls that are now handled natively by C# 14 implicit span conversions.")]
    public async Task<string> CleanupImplicitSpans(string filePath) 
        => await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(filePath);

    [McpServerTool]
    [Description("Modernizes logging patterns to use source-generated Log messages or structured logging.")]
    public async Task<string> ModernizeLogging(string filePath, string className) 
        => await _modernLoggingEngine.ConvertToSourceGeneratedLoggingAsync(filePath, className);

    [McpServerTool]
    [Description("Converts an async method to use a Polly-style retry loop.")]
    public async Task<string> AddRetryPolicy(string filePath, int startLine, int endLine, int retryCount = 3) 
        => await _codeHealingEngine.AddRetryPolicyAsync(filePath, startLine, endLine, retryCount);
}
