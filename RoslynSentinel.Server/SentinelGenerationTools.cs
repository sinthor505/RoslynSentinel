using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelGenerationTools
{
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly ApiAutomationEngine _apiAutomationEngine;
    private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    private readonly ApiIntegrationEngine _apiIntegrationEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelGenerationTools> _logger;

    public SentinelGenerationTools(
        CodeGenerationEngine codeGenerationEngine,
        ApiAutomationEngine apiAutomationEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        ApiIntegrationEngine apiIntegrationEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelGenerationTools> logger)
    {
        _codeGenerationEngine = codeGenerationEngine;
        _apiAutomationEngine = apiAutomationEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _apiIntegrationEngine = apiIntegrationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates C# class declarations from a JSON string using rootClassName as the top-level type name under the specified namespace.")]
    public object GenerateClassesFromJson(
        [ExternalInputRequired(DataTag.Json)] string json,
        [ExternalInputRequired(DataTag.ClassName)] string rootClassName,
        [ExternalInputRequired(DataTag.Namespace)] string @namespace)
    {
        try
        {
            return _codeGenerationEngine.GenerateClassesFromJson(json, rootClassName, @namespace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateClassesFromJson failed for rootClassName='{RootClassName}'", rootClassName);
            return $"GenerateClassesFromJson failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates a typed HttpClient wrapper for a Web API controller.")]
    public async Task<string> GenerateHttpClient(
        [Consumes(DataTag.SourceFilepath, required: true)] string rawFilePath,
        [ExternalInputRequired(DataTag.ClassName)] string controllerName)
    {
        FilePath filePath = FilePath.FromWire(rawFilePath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _apiAutomationEngine.GenerateHttpClientForControllerAsync(filePath, controllerName);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return (
                    $"GenerateHttpClient failed for controller '{controllerName}' in '{filePath}': " +
                    "file not found in workspace or controller class not found. Ensure the solution is loaded.");
            }

            return result.Outcome.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateHttpClient failed for '{ControllerName}' in '{FilePath}'", controllerName, filePath);
            return $"GenerateHttpClient failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Produces(DataTag.ResultOnly)]
    [Description("Scans a project for all config[\"Key\"] and IConfiguration.GetValue<T>(\"Key\") usages and returns a JSON skeleton with all keys and inferred default values.")]
    public async Task<string> GenerateDefaultConfigJson(
        [Consumes(DataTag.ProjectName, required: true)] string projectName)
    {
        try
        {
            var result = await _codeGenerationEngine.GenerateDefaultConfigJsonAsync(projectName);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return (
                    $"GenerateDefaultConfigJson failed for project '{projectName}': " +
                    "project not found in workspace or no configuration keys found. Ensure the solution is loaded.");
            }

            return result.UpdatedText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateDefaultConfigJson failed for project '{ProjectName}'", projectName);
            return $"GenerateDefaultConfigJson failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Converts a string.Format(...) call to an interpolated string ($"...").        
        contextSnippet: verbatim substring identifying the string.Format call to convert (required).
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns the updated file content.
        """)]
    // Unlike the built-in convert_to_interpolated_string, this resolves const string format arguments via the semantic model, so it works even when the format string is a named const rather than a literal. Handles {0:format} format specifiers correctly.
    public async Task<string> InterpolateStringSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string rawFilePath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        FilePath filePath = FilePath.FromWire(rawFilePath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _codeGenerationEngine.InterpolateStringAsync(filePath, contextSnippet, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return (
                    $"InterpolateStringSafe failed in '{filePath}': " +
                    "file not found in workspace, context snippet did not match, or target is not a string.Format() call. Ensure the solution is loaded.");
            }

            return result.Outcome.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InterpolateStringSafe failed in '{FilePath}'", filePath);
            return $"InterpolateStringSafe failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
