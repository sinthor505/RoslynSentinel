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
    {
        try
        {
            var result = await _codeHealingEngine.FixThreadSleepAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"FixThreadSleep failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FixThreadSleep unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"FixThreadSleep for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("IDE0011: Adds braces to single-line if, foreach, and while statements.")]
    public async Task<string> AddBraces(string filePath)
    {
        try
        {
            var result = await _syntaxUpgradeEngine.AddBracesAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"AddBraces failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddBraces unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"AddBraces for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("IDE0019/20: Upgrades legacy type checks and casts to modern C# pattern matching (is string s).")]
    public async Task<string> UpgradePatternMatching(string filePath)
    {
        try
        {
            var result = await _syntaxUpgradeEngine.UpgradePatternMatchingAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"UpgradePatternMatching failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpgradePatternMatching unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"UpgradePatternMatching for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("IDE0056: Upgrades manual index calculations (Length - 1) to modern [^1] syntax.")]
    public async Task<string> UseIndexFromEnd(string filePath)
    {
        try
        {
            var result = await _codeStyleEngine.UseIndexFromEndAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"UseIndexFromEnd failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UseIndexFromEnd unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"UseIndexFromEnd for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("IDE0340: Upgrades nameof(List<int>) to modern C# 14 nameof(List<>). Provide contextSnippet: the verbatim nameof expression text. Provide lineBefore and/or lineAfter when the snippet could match multiple locations.")]
    public async Task<string> UpgradeUnboundNameof(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
    {
        var result = await _syntaxUpgradeEngine.UseNameofExpressionAsync(filePath, contextSnippet, lineBefore, lineAfter);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"UpgradeUnboundNameof failed in '{filePath}': file not found in workspace or context snippet did not match. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("C# 14: Upgrades manual backing fields to modern 'field' keyword auto-properties.")]
    public async Task<string> UseFieldBackedProperties(string filePath)
    {
        try
        {
            var result = await _syntaxUpgradeEngine.UseFieldBackedPropertiesAsync(filePath);
            // Return a friendly message when the file was not found or no patterns were present;
            // do NOT throw — the file simply may not be part of the loaded workspace.
            if (string.IsNullOrEmpty(result))
                return $"// No backing-field patterns found or file not in workspace for '{filePath}'.";
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UseFieldBackedProperties unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"UseFieldBackedProperties for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Converts a class to a C# record.")]
    public async Task<string> ClassToRecord(string filePath, string className)
    {
        try
        {
            var result = await _modernizationEngine.ClassToRecordAsync(filePath, className);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"ClassToRecord failed for '{className}' in '{filePath}': " +
                    "file not found in workspace or class not found. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ClassToRecord unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
            throw new InvalidOperationException($"ClassToRecord for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Converts a C# record back to a standard class.")]
    public async Task<string> RecordToClass(string filePath, string recordName)
    {
        try
        {
            var result = await _modernizationEngine.RecordToClassAsync(filePath, recordName);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"RecordToClass failed for '{recordName}' in '{filePath}': " +
                    "file not found in workspace or record not found. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RecordToClass unexpected exception for '{RecordName}' in '{FilePath}'", recordName, filePath);
            throw new InvalidOperationException($"RecordToClass for '{recordName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Simplifies redundant code patterns globally in a file (target-typed new, null-coalescing assignment, etc.).")]
    public async Task<string> SimplifyVerbosity(string filePath)
    {
        try
        {
            var result = await _codeStyleEngine.SimplifyVerbosityAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"SimplifyVerbosity failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimplifyVerbosity unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"SimplifyVerbosity for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Upgrades threading patterns (locks, semaphores) to modern/safe versions.")]
    public async Task<string> UpgradeThreadSafety(string filePath)
    {
        try
        {
            var result = await _codeStyleEngine.FixDangerousLockAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"UpgradeThreadSafety failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpgradeThreadSafety unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"UpgradeThreadSafety for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Replaces direct DateTime usage with modern TimeProvider abstractions for testability.")]
    public async Task<string> UseTimeProvider(string filePath)
    {
        try
        {
            var result = await _codeStyleEngine.UseTimeProviderAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"UseTimeProvider failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UseTimeProvider unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"UseTimeProvider for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Replaces generic throw new Exception calls with custom typed exceptions and generates the new class files.")]
    public async Task<object> ModernizeExceptions(List<CodeHealingEngine.ExceptionTarget> targets, bool autoStage = true)
    {
        try
        {
            var changes = await _codeHealingEngine.ModernizeExceptionsAsync(targets);
            if (autoStage) return _workspaceManager.StageChanges(changes, $"Modernize {targets.Count} generic exceptions.");
            return changes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ModernizeExceptions unexpected exception");
            throw new InvalidOperationException($"ModernizeExceptions failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("ModernGuardClauses: Upgrades if-throw null checks to ArgumentNullException.ThrowIfNull / ArgumentException.ThrowIfNullOrEmpty.")]
    public async Task<string> UpgradeToModernGuards(string filePath)
    {
        var result = await _syntaxUpgradeEngine.UpgradeToModernGuardsAsync(filePath);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"UpgradeToModernGuards failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("IfToSwitch: Converts a switch statement in a method to a switch expression.")]
    public async Task<string> ConvertSwitchToExpression(string filePath, string methodName)
    {
        try
        {
            var result = await _syntaxUpgradeEngine.ConvertSwitchToExpressionAsync(filePath, methodName);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"ConvertSwitchToExpression failed for '{methodName}' in '{filePath}': " +
                    "file not found in workspace, method not found, or no eligible switch statements found. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertSwitchToExpression unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
            throw new InvalidOperationException($"ConvertSwitchToExpression for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("ImplicitSpanCleanup: Removes unnecessary .AsSpan() calls that are implicit in modern C#.")]
    public async Task<string> CleanupImplicitSpans(string filePath)
    {
        try
        {
            var result = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"CleanupImplicitSpans failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CleanupImplicitSpans unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"CleanupImplicitSpans for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Converts LogInformation/LogError/LogWarning calls in a class to high-performance [LoggerMessage] partial methods and makes the class partial.")]
    public async Task<string> ConvertToSourceGeneratedLogging(string filePath, string className)
    {
        var result = await _modernLoggingEngine.ConvertToSourceGeneratedLoggingAsync(filePath, className);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"ConvertToSourceGeneratedLogging failed for '{className}' in '{filePath}': " +
                "file not found in workspace or class not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Simplifies boolean expressions: rewrites 'x == true' to 'x', 'x == false' to '!x', and similar redundant patterns.")]
    public async Task<string> SimplifyBooleanExpressions(string filePath)
    {
        var result = await _logicOptimizationEngine.SimplifyBooleanExpressionsAsync(filePath);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"SimplifyBooleanExpressions failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Removes redundant 'this.' member access qualifiers from an entire file.")]
    public async Task<string> SimplifyMemberAccess(string filePath)
    {
        try
        {
            var result = await _ideStyleEngine.SimplifyMemberAccessAsync(filePath);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"SimplifyMemberAccess failed for '{filePath}': file not found in workspace. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SimplifyMemberAccess unexpected exception for '{FilePath}'", filePath);
            throw new InvalidOperationException($"SimplifyMemberAccess for '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Makes a class immutable: adds 'readonly' to all fields and converts property setters to 'init'.")]
    public async Task<string> MakeClassImmutable(string filePath, string className)
    {
        try
        {
            var result = await _immutabilityEngine.MakeClassImmutableAsync(filePath, className);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"MakeClassImmutable failed for '{className}' in '{filePath}': " +
                    "file not found in workspace or class not found. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MakeClassImmutable unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
            throw new InvalidOperationException($"MakeClassImmutable for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Converts a standard static method into an extension method by prepending 'this' to its first parameter.")]
    public async Task<string> ConvertStaticToExtension(string filePath, string methodName)
    {
        try
        {
            var result = await _advancedLogicEngine.ConvertStaticToExtensionAsync(filePath, methodName);
            if (string.IsNullOrEmpty(result))
                throw new InvalidOperationException(
                    $"ConvertStaticToExtension failed for '{methodName}' in '{filePath}': " +
                    "file not found in workspace or method not found. Ensure the solution is loaded.");
            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertStaticToExtension unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
            throw new InvalidOperationException($"ConvertStaticToExtension for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Inverts all usages of a boolean identifier across the solution: wraps each usage with '!' and removes double negations. Returns a file-to-content map of changed files.")]
    public async Task<Dictionary<string, string>> InvertBooleanLogic(string filePath, string boolName)
        => await _advancedLogicEngine.InvertBooleanLogicAsync(filePath, boolName);

    [McpServerTool]
    [Description("Converts a method's Task/Task<T> return type to ValueTask/ValueTask<T> for reduced heap allocation on hot paths.")]
    public async Task<string> OptimizeToValueTask(string filePath, string methodName)
    {
        var result = await _asyncOptimizationEngine.OptimizeToValueTaskAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"OptimizeToValueTask failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Batches independent sequential await calls in a method into a single Task.WhenAll() for parallel execution.")]
    public async Task<string> OptimizeIndependentAwaits(string filePath, string methodName)
    {
        var result = await _asyncOptimizationEngine.OptimizeIndependentAwaitsAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"OptimizeIndependentAwaits failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Converts a class with a simple assignment-only constructor into a C# 12 primary constructor. Removes the explicit constructor and its corresponding private readonly fields, then rewrites field references to use the parameter names directly. Only succeeds if every constructor statement is a field assignment.")]
    public async Task<string> UpgradeToPrimaryConstructor(string filePath, string className)
    {
        var result = await _syntaxUpgradeEngine.UpgradeToPrimaryConstructorAsync(filePath, className);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"UpgradeToPrimaryConstructor failed for '{className}' in '{filePath}': " +
                "file not found in workspace or class not found. Ensure the solution is loaded.");
        return result;
    }

    [McpServerTool]
    [Description("Finds private static readonly Dictionary<> and HashSet<> fields initialized inline. Suggests using FrozenDictionary/FrozenSet (System.Collections.Frozen) for better read performance.")]
    public async Task<List<AntiPatternFinding>> FindUseFrozenCollections(string? filePath = null, string? projectName = null)
        => await _codeStyleEngine.FindUseFrozenCollectionsAsync(filePath, projectName);

    [McpServerTool]
    [Description("Replaces throw new ArgumentNullException(nameof(x)) with ArgumentNullException.ThrowIfNull(x) and throw new ArgumentOutOfRangeException(nameof(x)) with ArgumentOutOfRangeException.ThrowIfNegative(x) in the specified method.")]
    public async Task<string> UseExceptionExpressions(string filePath, string methodName)
    {
        var result = await _syntaxUpgradeEngine.UseExceptionExpressionsAsync(filePath, methodName);
        if (string.IsNullOrEmpty(result))
            throw new InvalidOperationException(
                $"UseExceptionExpressions failed for '{methodName}' in '{filePath}': " +
                "file not found in workspace or method not found. Ensure the solution is loaded.");
        return result;
    }
}
