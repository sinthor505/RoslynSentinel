using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

[McpServerToolType]
public class SentinelGenerationTools
{
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly ApiAutomationEngine _apiAutomationEngine;
    private readonly ILogger<SentinelGenerationTools> _logger;

    public SentinelGenerationTools(
        CodeGenerationEngine codeGenerationEngine,
        ApiAutomationEngine apiAutomationEngine,
        ILogger<SentinelGenerationTools> logger)
    {
        _codeGenerationEngine = codeGenerationEngine;
        _apiAutomationEngine = apiAutomationEngine;
        _logger = logger;
    }

    [McpServerTool]
    [Description("Generates C# classes from a JSON string.")]
    public GenerationResult GenerateClassesFromJson(string json, string rootClassName, string @namespace) 
        => _codeGenerationEngine.GenerateClassesFromJson(json, rootClassName, @namespace);

    [McpServerTool]
    [Description("Generates a typed HttpClient for a Web API controller.")]
    public async Task<string> GenerateHttpClient(string filePath, string controllerName) 
        => await _apiAutomationEngine.GenerateHttpClientForControllerAsync(filePath, controllerName);
}
