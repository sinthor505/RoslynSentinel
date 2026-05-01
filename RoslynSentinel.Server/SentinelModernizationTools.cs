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
    private readonly IDEStyleEngine _ideStyleEngine;
    private readonly ImmutabilityEngine _immutabilityEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
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
        IDEStyleEngine ideStyleEngine,
        ImmutabilityEngine immutabilityEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
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
        _ideStyleEngine = ideStyleEngine;
        _immutabilityEngine = immutabilityEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _workspaceManager = workspaceManager;
        _config = config;
        _logger = logger;
    }

    [McpServerTool]
    [Description("EPC33: Replaces Thread.Sleep with Task.Delay in async methods.")]
    public async Task<string> FixThreadSleep(string filePath) 
        => await _codeHealingEngine.FixThreadSleepAsync(filePath);

    [McpServerTool]
    [Description("IDE0011: Adds braces to single-line if, foreach, and while statements.")]
    public async Task<string> AddBraces(string filePath) 
        => await _syntaxUpgradeEngine.AddBracesAsync(filePath);

    [McpServerTool]
    [Description("IDE0019/20: Upgrades legacy type checks and casts to modern C# pattern matching (is string s).")]
    public async Task<string> UpgradePatternMatching(string filePath) 
        => await _syntaxUpgradeEngine.UpgradePatternMatchingAsync(filePath);

    [McpServerTool]
    [Description("IDE0056: Upgrades manual index calculations (Length - 1) to modern [^1] syntax.")]
    public async Task<string> UseIndexFromEnd(string filePath) 
        => await _codeStyleEngine.UseIndexFromEndAsync(filePath);

    [McpServerTool]
    [Description("IDE0340: Upgrades nameof(List<int>) to modern C# 14 nameof(List<>).")]
    public async Task<string> UpgradeUnboundNameof(string filePath, int line, int column) 
        => await _syntaxUpgradeEngine.UseNameofExpressionAsync(filePath, line, column);

    [McpServerTool]
    [Description("C# 14: Upgrades manual backing fields to modern 'field' keyword auto-properties.")]
    public async Task<string> UseFieldBackedProperties(string filePath) 
        => await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(filePath);

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
    [Description("Upgrades threading patterns (locks, semaphores) to modern/safe versions.")]
    public async Task<string> UpgradeThreadSafety(string filePath) 
        => await _codeStyleEngine.FixDangerousLockAsync(filePath);

    [McpServerTool]
    [Description("Replaces direct DateTime usage with modern TimeProvider abstractions for testability.")]
    public async Task<string> UseTimeProvider(string filePath) 
        => await _codeStyleEngine.UseTimeProviderAsync(filePath);

    [McpServerTool]
    [Description("Replaces generic throw new Exception calls with custom typed exceptions and generates the new class files.")]
    public async Task<object> ModernizeExceptions(List<CodeHealingEngine.ExceptionTarget> targets, bool autoStage = true) 
    {
        var changes = await _codeHealingEngine.ModernizeExceptionsAsync(targets);
        if (autoStage) return _workspaceManager.StageChanges(changes, $"Modernize {targets.Count} generic exceptions.");
        return changes;
    }

    [McpServerTool]
    [Description("ModernGuardClauses: Upgrades if-throw null checks to ArgumentNullException.ThrowIfNull / ArgumentException.ThrowIfNullOrEmpty.")]
    public async Task<string> UpgradeToModernGuards(string filePath)
        => await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(filePath);

    [McpServerTool]
    [Description("IfToSwitch: Converts a switch statement in a method to a switch expression.")]
    public async Task<string> ConvertSwitchToExpression(string filePath, string methodName)
        => await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync(filePath, methodName);

    [McpServerTool]
    [Description("ImplicitSpanCleanup: Removes unnecessary .AsSpan() calls that are implicit in modern C#.")]
    public async Task<string> CleanupImplicitSpans(string filePath)
        => await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(filePath);

    [McpServerTool]
    [Description("Converts LogInformation/LogError/LogWarning calls in a class to high-performance [LoggerMessage] partial methods and makes the class partial.")]
    public async Task<string> ConvertToSourceGeneratedLogging(string filePath, string className)
        => await _modernLoggingEngine.ConvertToSourceGeneratedLoggingAsync(filePath, className);

    [McpServerTool]
    [Description("Simplifies boolean expressions: rewrites 'x == true' to 'x', 'x == false' to '!x', and similar redundant patterns.")]
    public async Task<string> SimplifyBooleanExpressions(string filePath)
        => await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(filePath);

    [McpServerTool]
    [Description("Removes redundant 'this.' member access qualifiers from an entire file.")]
    public async Task<string> SimplifyMemberAccess(string filePath)
        => await _ideStyleEngine.SimplifyMemberAccessAsync(filePath);

    [McpServerTool]
    [Description("Makes a class immutable: adds 'readonly' to all fields and converts property setters to 'init'.")]
    public async Task<string> MakeClassImmutable(string filePath, string className)
        => await _immutabilityEngine.MakeClassImmutableAsync(filePath, className);

    [McpServerTool]
    [Description("Converts a standard static method into an extension method by prepending 'this' to its first parameter.")]
    public async Task<string> ConvertStaticToExtension(string filePath, string methodName)
        => await _advancedLogicEngine.ConvertStaticToExtensionAsync(filePath, methodName);

    [McpServerTool]
    [Description("Inverts all usages of a boolean identifier across the solution: wraps each usage with '!' and removes double negations. Returns a file-to-content map of changed files.")]
    public async Task<Dictionary<string, string>> InvertBooleanLogic(string filePath, string boolName)
        => await _advancedLogicEngine.InvertBooleanLogicAsync(filePath, boolName);

    [McpServerTool]
    [Description("Converts a method's Task/Task<T> return type to ValueTask/ValueTask<T> for reduced heap allocation on hot paths.")]
    public async Task<string> OptimizeToValueTask(string filePath, string methodName)
        => await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, methodName);

    [McpServerTool]
    [Description("Batches independent sequential await calls in a method into a single Task.WhenAll() for parallel execution.")]
    public async Task<string> OptimizeIndependentAwaits(string filePath, string methodName)
        => await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync(filePath, methodName);

    [McpServerTool]
    [Description("Converts a class with a simple assignment-only constructor into a C# 12 primary constructor. Removes the explicit constructor and its corresponding private readonly fields, then rewrites field references to use the parameter names directly. Only succeeds if every constructor statement is a field assignment.")]
    public async Task<string> UpgradeToPrimaryConstructor(string filePath, string className)
        => await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync(filePath, className);
}
