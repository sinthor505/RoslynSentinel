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
    private readonly ILogger<SentinelGenerationTools> _logger;

    public SentinelGenerationTools(
        CodeGenerationEngine codeGenerationEngine,
        ApiAutomationEngine apiAutomationEngine,
        AsyncOptimizationEngine asyncOptimizationEngine,
        ApiIntegrationEngine apiIntegrationEngine,
        ILogger<SentinelGenerationTools> logger)
    {
        _codeGenerationEngine = codeGenerationEngine;
        _apiAutomationEngine = apiAutomationEngine;
        _asyncOptimizationEngine = asyncOptimizationEngine;
        _apiIntegrationEngine = apiIntegrationEngine;
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

    [McpServerTool]
    [Description("Generates a constructor for a class from its private/readonly fields. Skips if a constructor already exists. Returns the updated file content.")]
    public async Task<string> GenerateConstructor(string filePath, string className)
        => await _codeGenerationEngine.GenerateConstructorAsync(filePath, className);

    [McpServerTool]
    [Description("""
        Generates a public override ToString() for a class based on its public properties.
        
        SECURITY: Sensitive properties are automatically excluded from the output to prevent
        credential leakage in logs. Properties whose names contain any of these substrings
        (case-insensitive) are excluded by default: password, passwd, secret, apikey, token,
        hash, salt, pin, cvv, ssn, creditcard, connectionstring, privatekey, clientsecret.
        
        Use excludeProperties to additionally exclude specific property names by exact name.
        
        Returns: UpdatedContent (the new file source), IncludedProperties, ExcludedProperties,
        and an optional Warning listing which properties were excluded and why.
        Skips generation if a ToString() override already exists.
        """)]
    public async Task<CodeGenerationEngine.GenerateToStringResult> GenerateToString(
        string filePath,
        string className,
        string[]? excludeProperties = null)
        => await _codeGenerationEngine.GenerateToStringAsync(filePath, className, excludeProperties);

    [McpServerTool]
    [Description("Extracts an interface from a concrete class. Returns: the interface source code ready to paste into a new file, a DI registration snippet (services.AddScoped<IFoo, Foo>()), and a Moq mock setup snippet for use in unit tests.")]
    public async Task<RepositoryInterfaceResult> GenerateRepositoryInterface(string filePath, string className)
        => await _codeGenerationEngine.GenerateRepositoryInterfaceAsync(filePath, className);

    [McpServerTool]
    [Description("Generates a fluent builder class for any C# class. The builder provides With{Property}() methods for each public settable property and a Build() method that constructs the target type. Returns the complete builder source, class name, and a usage example.")]
    public async Task<FluentBuilderResult> GenerateFluentBuilder(string filePath, string className)
        => await _codeGenerationEngine.GenerateFluentBuilderAsync(filePath, className);

    [McpServerTool]
    [Description("Generates a Decorator pattern class for a given interface. The decorator wraps any implementation, delegates all interface members to an inner instance, and includes TODO comments for adding cross-cutting concerns (logging, caching, retry). Returns the full .cs file source.")]
    public async Task<DecoratorResult?> GenerateDecoratorClass(string interfaceName, string decoratorPrefix = "Logging", string? projectName = null)
        => await _codeGenerationEngine.GenerateDecoratorClassAsync(interfaceName, decoratorPrefix, projectName);

    [McpServerTool]
    [Description("Scans a project for all config[\"Key\"] and IConfiguration.GetValue<T>(\"Key\") usages and generates a JSON skeleton with all keys and inferred default values.")]
    public async Task<string> GenerateDefaultConfigJson(string projectName)
        => await _codeGenerationEngine.GenerateDefaultConfigJsonAsync(projectName);

    [McpServerTool]
    [Description("Generates an async overload for a synchronous method by wrapping it in Task.Run.")]
    public async Task<string> GenerateAsyncOverload(string filePath, string methodName)
        => await _asyncOptimizationEngine.GenerateAsyncOverloadAsync(filePath, methodName);

    [McpServerTool]
    [Description("Adds [Required] and [StringLength(100)] data annotations to all string properties in a POCO class.")]
    public async Task<string> AddValidationToPoco(string filePath, string className)
        => await _apiIntegrationEngine.AddValidationToPocoAsync(filePath, className);

    [McpServerTool]
    [Description("Generates stub implementations for all unimplemented members of an interface on a class. Unlike the built-in implement_interface, this never adds the 'override' keyword (which is incorrect for interface implementations). Pass filePath of the class file, className of the implementing class, and interfaceName of the interface to implement.")]
    public async Task<string> ImplementInterfaceSafe(string filePath, string className, string interfaceName)
        => await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
}
