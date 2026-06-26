using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelGenerationTools
{
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly ApiAutomationEngine _apiAutomationEngine;
    // private readonly AsyncOptimizationEngine _asyncOptimizationEngine;
    // private readonly ApiIntegrationEngine _apiIntegrationEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelGenerationTools> _logger;

    public SentinelGenerationTools(
        CodeGenerationEngine codeGenerationEngine,
        ApiAutomationEngine apiAutomationEngine,
        // AsyncOptimizationEngine asyncOptimizationEngine,
        // ApiIntegrationEngine apiIntegrationEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelGenerationTools> logger)
    {
        _codeGenerationEngine = codeGenerationEngine;
        _apiAutomationEngine = apiAutomationEngine;
        // _asyncOptimizationEngine = asyncOptimizationEngine;
        // _apiIntegrationEngine = apiIntegrationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    [McpServerTool(Name = "GenerateClassesFromJson")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates C# class declarations from a JSON string using rootClassName as the top-level type name under the specified namespace.")]
    public object GenerateClassesFromJson(
        [ExternalInputRequired(DataTag.Json)] string json,
        [ExternalInputRequired(DataTag.ClassName)] string rootClassName,
        [ExternalInputRequired(DataTag.Namespace)] string @namespace,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return _codeGenerationEngine.GenerateClassesFromJson(json, rootClassName, @namespace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateClassesFromJson failed for rootClassName='{RootClassName}'", rootClassName);
            return $"GenerateClassesFromJson failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}";
        }
    }

    [McpServerTool(Name = "GenerateHttpClient")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates a typed HttpClient wrapper for a Web API controller.")]
    public async Task<string> GenerateHttpClient(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [ExternalInputRequired(DataTag.ClassName)] string controllerName,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        var fileIds = _workspaceManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath);
        if (fileIds == null || fileIds.Value.Length == 0)
            return $"GenerateHttpClient: file '{Path.GetFileName(filePath)}' not found in the loaded solution. " +
                   $"Verify the path is correct and the solution is loaded. Loaded projects: {_workspaceManager.ProjectCount}.";

        try
        {
            var result = await _apiAutomationEngine.GenerateHttpClientForControllerAsync(filePath, controllerName);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return $"GenerateHttpClient: controller class '{controllerName}' not found in '{Path.GetFileName(filePath)}'. " +
                       "Verify the class name (case-sensitive). Use get_file_outline to list available classes.";
            }

            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateHttpClient failed for '{ControllerName}' in '{FilePath}'", controllerName, filePath);
            return $"GenerateHttpClient failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded. Details: {ex.Message}";
        }
    }

    [McpServerTool(Name = "GenerateDefaultConfigJson")]
    [Produces(DataTag.ResultOnly)]
    [Description("Scans a project for all config[\"Key\"] and IConfiguration.GetValue<T>(\"Key\") usages and returns a JSON skeleton with all keys and inferred default values.")]
    public async Task<string> GenerateDefaultConfigJson(
        [Consumes(DataTag.ProjectName, required: true)] string projectName,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        var projectExists = _workspaceManager.CurrentSolution?.Projects
            .Any(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase)) ?? false;
        if (!projectExists)
        {
            var loadedProjects = _workspaceManager.CurrentSolution?.Projects
                .Select(p => p.Name).Take(10).ToList() ?? [];
            var projectList = loadedProjects.Count > 0 ? string.Join(", ", loadedProjects) : "none";
            return $"GenerateDefaultConfigJson: project '{projectName}' not found in the loaded solution. " +
                   $"Loaded projects: {projectList}.";
        }

        try
        {
            var result = await _codeGenerationEngine.GenerateDefaultConfigJsonAsync(projectName);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return $"GenerateDefaultConfigJson: no configuration keys found in project '{projectName}'. " +
                       "The project may not use IConfiguration or config[\"Key\"] patterns.";
            }

            return result.UpdatedText;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateDefaultConfigJson failed for project '{ProjectName}'", projectName);
            return $"GenerateDefaultConfigJson failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded. Details: {ex.Message}";
        }
    }

    [McpServerTool(Name = "InterpolateStringSafe")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Converts a string.Format(...) call to an interpolated string ($"...").        
        contextSnippet: verbatim substring identifying the string.Format call to convert (required).
        Provide lineBefore and/or lineAfter when the snippet could match multiple locations.
        Returns the updated file content.
        """)]
    // Unlike the built-in convert_to_interpolated_string, this resolves const string format arguments via the semantic model, so it works even when the format string is a named const rather than a literal. Handles {0:format} format specifiers correctly.
    public async Task<string> InterpolateStringSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null,
        RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        var interpFileIds = _workspaceManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath);
        if (interpFileIds == null || interpFileIds.Value.Length == 0)
            return $"InterpolateStringSafe: file '{Path.GetFileName(filePath)}' not found in the loaded solution. " +
                   $"Verify the path is correct and the solution is loaded. Loaded projects: {_workspaceManager.ProjectCount}.";

        try
        {
            var result = await _codeGenerationEngine.InterpolateStringAsync(filePath, contextSnippet, lineBefore, lineAfter);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return $"InterpolateStringSafe: context snippet did not match or target is not a string.Format() call in '{Path.GetFileName(filePath)}'. " +
                       "Verify the snippet is verbatim text from the file and the call uses string.Format(...) (not interpolation or concatenation already).";
            }

            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InterpolateStringSafe failed in '{FilePath}'", filePath);
            return $"InterpolateStringSafe failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded. Details: {ex.Message}";
        }
    }

    internal static ToolOptionsResult GenerateOptions() => new()
    {
        Description = """
            generate — valid kind values:
              add_benchmark_stub           Adds a BenchmarkDotNet stub class for a method.
                                           Requires filePath, className, methodName.
                                           Returns SourceTransformResult.
              generate_constructor         Generates a constructor from private/readonly fields.
                                           Returns updated file content as a string.
              generate_decorator_class     Generates a Decorator pattern class for an interface.
                                           Pass the interface name as className (filePath not required).
                                           decoratorPrefix: prefix for the decorator class (default "Logging").
                                           projectName: optional project scope.
                                           Returns DecoratorResult.
              generate_equality_overrides  Generates Equals and GetHashCode overrides.
                                           Returns updated file content as a string.
              generate_fluent_builder      Generates a fluent builder class with With{Property}() methods.
                                           Returns FluentBuilderResult.
              generate_path_driven_tests   Generates test stubs for each execution path in a method.
                                           Requires filePath, methodName.
                                           framework: "NUnit" (default), "xunit", or "mstest".
                                           disambiguateLine: line number to resolve overloaded methods.
                                           Returns PathDrivenTestReport.
              generate_repository_interface  Extracts an interface from a class with DI and Moq snippets.
                                           Returns RepositoryInterfaceResult.
              generate_test_scaffold       Generates an xUnit+Moq test scaffold with mock fields and test stubs.
                                           Returns TestScaffoldResult.
              generate_test_skeleton       Generates a test class skeleton with one test stub per public method.
                                           Returns TestSkeletonReport.
              generate_to_string_safe      Generates a ToString() override with correctly escaped interpolated strings.
                                           members: optional comma-separated list of property/field names.
                                           Returns MsAugmentResult.

            Additional parameters:
              filePath: required for all kinds except generate_decorator_class.
              className: target class name; for generate_decorator_class pass the interface name.
              methodName: required for add_benchmark_stub and generate_path_driven_tests.
              members: for generate_to_string_safe — optional comma-separated member list.
              decoratorPrefix: for generate_decorator_class (default "Logging").
              projectName: for generate_decorator_class — optional project scope.
              framework: for generate_path_driven_tests — "NUnit" (default), "xunit", or "mstest".
              disambiguateLine: for generate_path_driven_tests — disambiguates overloaded methods.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["kinds"] = new[] {
                "add_benchmark_stub", "generate_constructor", "generate_decorator_class",
                "generate_equality_overrides", "generate_fluent_builder", "generate_path_driven_tests",
                "generate_repository_interface", "generate_test_scaffold", "generate_test_skeleton",
                "generate_to_string_safe"
            }
        }
    };
}
