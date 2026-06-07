using System.ComponentModel;

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
    [Produces(DataTag.ResultOnly)]
    [Description("Inverts all usages of a boolean identifier across the solution: wraps each usage with ! and removes double negations. Returns a file → content map of changed files.")]
    public async Task<ToolResult<object>> InvertBooleanLogic(
        [Consumes(DataTag.SourceFilepath, required: true)] string rawFilePath,
        [Consumes(DataTag.SymbolName, required: true)] string boolName)
    {
        FilePath filePath = FilePath.FromWire(rawFilePath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _advancedLogicEngine.InvertBooleanLogicAsync(filePath, boolName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertBooleanLogic failed for '{BoolName}' in '{FilePath}'", boolName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"InvertBooleanLogic failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }
}
