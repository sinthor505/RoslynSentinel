using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

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
    private readonly ISolutionProvider _workspaceManager;
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
        ISolutionProvider workspaceManager,
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

    [McpServerTool(Name = "InvertBooleanLogic")]
    [Produces(DataTag.ResultOnly)]
    [Description("Inverts all usages of a boolean identifier solution-wide (wraps with !, collapses double negations).")]
    public async Task<SentinelCallToolResult<object>> InvertBooleanLogic(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Consumes(DataTag.SymbolName, required: true)] string boolName,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _advancedLogicEngine.InvertBooleanLogicAsync(filePath, boolName, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                IsSuccess = true,
                SuccessData =  result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertBooleanLogic failed for '{BoolName}' in '{FilePathWrapper}'", boolName, filePath);
            return new SentinelCallToolResult<object>
            {
                IsSuccess = false,
                ErrorData =  ToolErrorMapper.ToResultError(ex, _workspaceManager, "InvertBooleanLogic")
            };
        }
    }
}
