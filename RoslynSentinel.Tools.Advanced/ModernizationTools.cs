using System.ComponentModel;

using Microsoft.Extensions.Logging;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tools.Advanced;

[McpServerToolType]
public class ModernizationTools
{
    private readonly SyntaxModernizationEngine _modernizationEngine;
    private readonly SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private readonly LogicSimplificationEngine _logicOptimizationEngine;
    private readonly CodeStyleEngine _codeStyleEngine;
    private readonly CodeHealingEngine _codeHealingEngine;
    private readonly LogicSimplificationEngine _advancedLogicEngine;
    private readonly IDEStyleEngine _ideStyleEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly ISolutionProvider _workspaceManager;
    private readonly SentinelConfiguration _config;
    private readonly ILogger<ModernizationTools> _logger;
    public ModernizationTools(SyntaxModernizationEngine modernizationEngine, SyntaxUpgradeEngine syntaxUpgradeEngine, LogicSimplificationEngine logicOptimizationEngine, CodeStyleEngine codeStyleEngine, CodeHealingEngine codeHealingEngine, LogicSimplificationEngine advancedLogicEngine, IDEStyleEngine ideStyleEngine, AsyncOptimizationEngine asyncOptimizationEngine, ISolutionProvider workspaceManager, SentinelConfiguration config, ILogger<ModernizationTools> logger)
    {
        _modernizationEngine = modernizationEngine;
        _syntaxUpgradeEngine = syntaxUpgradeEngine;
        _logicOptimizationEngine = logicOptimizationEngine;
        _codeStyleEngine = codeStyleEngine;
        _codeHealingEngine = codeHealingEngine;
        _advancedLogicEngine = advancedLogicEngine;
        _ideStyleEngine = ideStyleEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _workspaceManager = workspaceManager;
        _config = config;
        _logger = logger;
    }

    [McpServerTool(Name = "InvertBooleanLogic")]
    [Produces(DataTag.ResultOnly)]
    [Description("Inverts all usages of a boolean identifier solution-wide (wraps with !, collapses double negations).")]
    public async Task<SentinelCallToolResult<object>> InvertBooleanLogic([Description(ToolParams.Reason)] ToolCallReason reason, [Consumes(DataTag.SourceFilepath, required: true)] string filePath, [Consumes(DataTag.SymbolName, required: true)] string boolName, // RequestContext<CallToolRequestParams> requestParams = null,
    CancellationToken cancellationToken = default)
    {
        FilePathWrapper resolvedFilePath = _workspaceManager.ResolveFromWire(filePath);
        try
        {
            var result = await _logicOptimizationEngine.InvertBooleanLogicAsync(resolvedFilePath, boolName, cancellationToken);
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvertBooleanLogic failed for '{BoolName}' in '{FilePathWrapper}'", boolName, resolvedFilePath);
            return new SentinelCallToolResult<object>
            {
                IsError = true,
                ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "InvertBooleanLogic")
            };
        }
    }
}