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
    {
        var result = await _apiAutomationEngine.GenerateHttpClientForControllerAsync(filePath, controllerName);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"GenerateHttpClient failed for controller '{controllerName}' in '{filePath}': " +
                "file not found in workspace or controller class not found. Ensure the solution is loaded.");
        }

        return result;
    }

    [McpServerTool]
    [Description("Generates a constructor for a class from its private/readonly fields. Skips if a constructor already exists. Returns the updated file content.")]
    public async Task<string> GenerateConstructor(string filePath, string className)
    {
        var result = await _codeGenerationEngine.GenerateConstructorAsync(filePath, className);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"GenerateConstructor failed for '{className}' in '{filePath}': " +
                "file not found in workspace or class not found. Ensure the solution is loaded.");
        }

        return result;
    }

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
    {
        try
        {
            return await _codeGenerationEngine.GenerateFluentBuilderAsync(filePath, className);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateFluentBuilder failed for '{ClassName}' in '{FilePath}'", className, filePath);
            return new FluentBuilderResult(className, string.Empty, string.Empty, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    [McpServerTool]
    [Description("Generates a Decorator pattern class for a given interface. The decorator wraps any implementation, delegates all interface members to an inner instance, and includes TODO comments for adding cross-cutting concerns (logging, caching, retry). Returns the full .cs file source.")]
    public async Task<DecoratorResult> GenerateDecoratorClass(string interfaceName, string decoratorPrefix = "Logging", string? projectName = null)
    {
        var result = await _codeGenerationEngine.GenerateDecoratorClassAsync(interfaceName, decoratorPrefix, projectName);
        if (result == null)
        {
            throw new InvalidOperationException(
                $"Interface '{interfaceName}' not found in the solution{(projectName != null ? $" project '{projectName}'" : string.Empty)}. " +
                "Ensure the interface name matches exactly (including the leading 'I') and is part of the loaded solution.");
        }

        return result;
    }

    [McpServerTool]
    [Description("Scans a project for all config[\"Key\"] and IConfiguration.GetValue<T>(\"Key\") usages and generates a JSON skeleton with all keys and inferred default values.")]
    public async Task<string> GenerateDefaultConfigJson(string projectName)
    {
        var result = await _codeGenerationEngine.GenerateDefaultConfigJsonAsync(projectName);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"GenerateDefaultConfigJson failed for project '{projectName}': " +
                "project not found in workspace or no configuration keys found. Ensure the solution is loaded.");
        }

        return result;
    }

    [McpServerTool]
    [Description("Generates an async overload for a synchronous method by wrapping it in Task.Run.")]
    public async Task<string> GenerateAsyncOverload(string filePath, string methodName)
    {
        try
        {
            var result = await _asyncOptimizationEngine.GenerateAsyncOverloadAsync(filePath, methodName);
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidOperationException(
                    $"GenerateAsyncOverload failed for '{methodName}' in '{filePath}': " +
                    "file not found in workspace or method not found. Ensure the solution is loaded.");
            }

            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateAsyncOverload unexpected exception for '{MethodName}' in '{FilePath}'", methodName, filePath);
            throw new InvalidOperationException($"GenerateAsyncOverload for '{methodName}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Adds [Required] and [StringLength(100)] data annotations to all string properties in a POCO class.")]
    public async Task<string> AddValidationToPoco(string filePath, string className)
    {
        try
        {
            var result = await _apiIntegrationEngine.AddValidationToPocoAsync(filePath, className);
            if (string.IsNullOrEmpty(result))
            {
                throw new InvalidOperationException(
                    $"AddValidationToPoco failed for '{className}' in '{filePath}': " +
                    "file not found in workspace or class not found. Ensure the solution is loaded.");
            }

            return result;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddValidationToPoco unexpected exception for '{ClassName}' in '{FilePath}'", className, filePath);
            throw new InvalidOperationException($"AddValidationToPoco for '{className}' in '{filePath}' failed: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Generates stub implementations for all unimplemented members of an interface on a class. Unlike the built-in implement_interface, this never adds the 'override' keyword (which is incorrect for interface implementations). Pass filePath of the class file, className of the implementing class, and interfaceName of the interface to implement.")]
    public async Task<string> ImplementInterfaceSafe(string filePath, string className, string interfaceName)
    {
        var result = await _codeGenerationEngine.ImplementInterfaceAsync(filePath, className, interfaceName);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"ImplementInterfaceSafe failed for '{className}' implementing '{interfaceName}' in '{filePath}': " +
                "file not found in workspace, class not found, or interface not found in solution. Ensure the solution is loaded.");
        }

        return result;
    }

    [McpServerTool]
    [Description("""
        Converts a property between auto-property and full property with backing field.
        Unlike the built-in convert_property, this correctly preserves initializers when converting
        ToFullProperty (the initializer moves to the backing field) and handles virtual/override/new
        modifiers without dropping them.
        direction: "ToFullProperty" (auto-prop → backing field + expression-body accessors) or
                   "ToAutoProperty" (full property → auto-prop, moving backing field initializer to property).
        propertyName: the property to convert.
        contextSnippet: optional verbatim substring to disambiguate when multiple properties share a name.
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns the updated file content.
        """)]
    public async Task<string> ConvertPropertySafe(
        string filePath, string propertyName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null)
    {
        var result = await _codeGenerationEngine.ConvertPropertySafeAsync(filePath, propertyName, direction, contextSnippet, lineBefore, lineAfter);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"ConvertPropertySafe failed for property '{propertyName}' ({direction}) in '{filePath}': " +
                "file not found in workspace, property not found, or the specified context snippet did not match. Ensure the solution is loaded.");
        }

        return result;
    }

    [McpServerTool]
    [Description("""
        Converts a string.Format(...) call to an interpolated string ($"...").
        Unlike the built-in convert_to_interpolated_string, this resolves const string format arguments
        via the semantic model, so it works even when the format string is a named const rather than a
        literal. Handles {0:format} format specifiers correctly.
        contextSnippet: verbatim substring identifying the string.Format call to convert (required).
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns the updated file content.
        """)]
    public async Task<string> InterpolateStringSafe(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null)
    {
        var result = await _codeGenerationEngine.InterpolateStringAsync(filePath, contextSnippet, lineBefore, lineAfter);
        if (string.IsNullOrEmpty(result))
        {
            throw new InvalidOperationException(
                $"InterpolateStringSafe failed in '{filePath}': " +
                "file not found in workspace, context snippet did not match, or target is not a string.Format() call. Ensure the solution is loaded.");
        }

        return result;
    }
}
