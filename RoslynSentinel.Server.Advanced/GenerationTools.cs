using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class GenerationTools
{
    private readonly CodeGenerationEngine _codeGenerationEngine;
    private readonly ApiAutomationEngine _apiAutomationEngine;
    private readonly MappingEngine _mappingEngine;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;
    private readonly ValidationEngine _validationEngine;
    private readonly IWorkspaceManager _workspaceManager;
    private readonly ILogger<GenerationTools> _logger;

    public GenerationTools(
    IWorkspaceManager workspaceManager,
    ILogger<GenerationTools> logger)
    {
        _codeGenerationEngine = new CodeGenerationEngine(workspaceManager);
        _apiAutomationEngine = new ApiAutomationEngine(workspaceManager);
        _mappingEngine = new MappingEngine(workspaceManager);
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
        _validationEngine = new ValidationEngine(workspaceManager);
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    public GenerationTools(
        CodeGenerationEngine codeGenerationEngine,
        ApiAutomationEngine apiAutomationEngine,
        MappingEngine mappingEngine,
        SymbolNavigationEngine symbolNavigationEngine,
        ValidationEngine validationEngine,
        IWorkspaceManager workspaceManager,
        ILogger<GenerationTools> logger)
    {
        _codeGenerationEngine = codeGenerationEngine;
        _apiAutomationEngine = apiAutomationEngine;
        _mappingEngine = mappingEngine;
        _symbolNavigationEngine = symbolNavigationEngine;
        _validationEngine = validationEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    /// <summary>
    /// Validates proposed changes against the current in-memory solution and, unless
    /// <paramref name="dryRun"/> is set, writes them straight to disk (write-through -> no
    /// intermediate staging step). Rolls back any already-written files if a multi-file change
    /// partially fails, so a change never lands half-applied.
    /// </summary>
    private Task<ApplyOutcome> ValidateAndApplyAsync(
        Dictionary<FilePathWrapper, string> changes,
        string description,
        string operationName,
        bool dryRun = false,
        bool returnDiff = false,
        IProgress<ProgressNotificationValue>? progress = default,
        IReadOnlyCollection<FilePathWrapper>? removePaths = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<FilePathWrapper>? deletePaths = null) =>
        ValidateAndApplyHelper.ValidateAndApplyAsync(
            _validationEngine, _workspaceManager, _logger, changes, operationName,
            dryRun, returnDiff, progress, removePaths, cancellationToken, deletePaths,
            describeValidationFailure: (report, ct) => CompilerErrorLookupHelper.DescribeAsync(report, _symbolNavigationEngine, ct));

    [McpServerTool(Name = "GenerateClassesFromJson")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates C# class declarations from a JSON string using rootClassName as the top-level type name under the specified namespace.")]
    public object GenerateClassesFromJson(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [ExternalInputRequired(DataTag.Json)] string json,
        [ExternalInputRequired(DataTag.ClassName)] string rootClassName,
        [ExternalInputRequired(DataTag.Namespace)] string @namespace
        // RequestContext<CallToolRequestParams> requestParams = null,
        // CancellationToken cancellationToken = default
        )
    {
        try
        {
            return _codeGenerationEngine.GenerateClassesFromJson(json, rootClassName, @namespace);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateClassesFromJson failed for rootClassName='{RootClassName}'", rootClassName);
            return ToolErrorMapper.ToErrorMessage(ex, _workspaceManager, "GenerateClassesFromJson");
        }
    }

    [McpServerTool(Name = "GenerateHttpClient")]
    [Produces(DataTag.ResultOnly)]
    [Description("Generates a typed HttpClient wrapper for a Web API controller.")]
    public async Task<string> GenerateHttpClient(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [ExternalInputRequired(DataTag.ClassName)] string controllerName,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        var fileIds = _workspaceManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath);
        if (fileIds == null || fileIds.Value.Length == 0)
            return $"GenerateHttpClient: file '{Path.GetFileName(filePath)}' not found in the loaded solution. " +
                   $"Verify the path is correct and the solution is loaded. Loaded projects: {_workspaceManager.ProjectCount}.";

        try
        {
            var result = await _apiAutomationEngine.GenerateHttpClientForControllerAsync(filePath, controllerName, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return $"GenerateHttpClient: controller class '{controllerName}' not found in '{Path.GetFileName(filePath)}'. " +
                       "Verify the class name (case-sensitive). Use GetFileOutline to list available classes.";
            }

            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateHttpClient failed for '{ControllerName}' in '{FilePathWrapper}'", controllerName, filePath);
            return ToolErrorMapper.ToErrorMessage(ex, _workspaceManager, "GenerateHttpClient");
        }
    }

    [McpServerTool(Name = "GenerateDefaultConfigJson")]
    [Produces(DataTag.ResultOnly)]
    [Description("Scans a project for all config[\"Key\"] and IConfiguration.GetValue<T>(\"Key\") usages and returns a JSON skeleton with all keys and inferred default values.")]
    public async Task<string> GenerateDefaultConfigJson(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.ProjectName, required: true)] string projectName,
        // RequestContext<CallToolRequestParams> requestParams = null,
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
            var result = await _codeGenerationEngine.GenerateDefaultConfigJsonAsync(projectName, cancellationToken);
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
            return ToolErrorMapper.ToErrorMessage(ex, _workspaceManager, "GenerateDefaultConfigJson");
        }
    }

    [McpServerTool(Name = "InterpolateStringSafe")]
    [Produces(DataTag.ResultOnly)]
    [Description("Converts a string.Format(...) call to an interpolated string. Resolves const string format arguments via the semantic model (works even when the format string is a named const, not just a literal) and handles {0:format} specifiers correctly.")]
    public async Task<string> InterpolateStringSafe(
        [Description(ToolParams.Reason)] ToolCallReason reason,
        [Consumes(DataTag.SourceFilepath, required: true)] FilePathWrapper filepath,
        [Description("Verbatim substring identifying the string.Format call to convert.")]
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Description(ToolParams.LineBefore)]
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Description(ToolParams.LineAfter)]
        [Consumes(DataTag.LineAfter)] string? lineAfter = null,
        // RequestContext<CallToolRequestParams> requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePath = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        var interpFileIds = _workspaceManager.CurrentSolution?.GetDocumentIdsWithFilePath(filePath);
        if (interpFileIds == null || interpFileIds.Value.Length == 0)
            return $"InterpolateStringSafe: file '{Path.GetFileName(filePath)}' not found in the loaded solution. " +
                   $"Verify the path is correct and the solution is loaded. Loaded projects: {_workspaceManager.ProjectCount}.";

        try
        {
            var result = await _codeGenerationEngine.InterpolateStringAsync(filePath, contextSnippet, lineBefore, lineAfter, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
            {
                return $"InterpolateStringSafe: context snippet did not match or target is not a string.Format() call in '{Path.GetFileName(filePath)}'. " +
                       "Verify the snippet is verbatim text from the file and the call uses string.Format(...) (not interpolation or concatenation already).";
            }

            return result.ToJsonSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InterpolateStringSafe failed in '{FilePathWrapper}'", filePath);
            return ToolErrorMapper.ToErrorMessage(ex, _workspaceManager, "InterpolateStringSafe");
        }
    }

    public async Task<SentinelCallToolResult<object>> GenerateMapping(
        FilePathWrapper filepath,
        string fromType,
        string toType,
        bool dryRun = false,
        bool returnDiff = false,
        RequestContext<CallToolRequestParams>? requestParams = null,
        CancellationToken cancellationToken = default)
    {
        FilePathWrapper filePathResolved = FilePathWrapper.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            ProgressToken progressToken = requestParams?.Params?.ProgressToken ?? new ProgressToken();
            IProgress<ProgressNotificationValue> progress = new Progress<ProgressNotificationValue>(msg => requestParams?.Server?.NotifyProgressAsync(progressToken, new ProgressNotificationValue() { Progress = 10.0f }, null, cancellationToken));

            var result = await _mappingEngine.GenerateMappingAsync(filePathResolved, fromType, toType, cancellationToken);
            if (string.IsNullOrEmpty(result.UpdatedText))
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = new ResultError(ToolErrorCode.Exception, $"GenerateMapping produced no output for '{fromType}' -> '{toType}' in '{filePathResolved}'. Ensure both types exist in the solution.") };

            var changes = new Dictionary<FilePathWrapper, string> { [filePathResolved] = result.UpdatedText };
            var apply = await ValidateAndApplyAsync(changes, $"Generate mapping from '{fromType}' to '{toType}'.", "GenerateMapping", dryRun, returnDiff, progress, cancellationToken: cancellationToken);
            if (apply.Error is not null)
                return new SentinelCallToolResult<object> { IsSuccess = false, ErrorData = apply.Error };
            return new SentinelCallToolResult<object> { IsSuccess = true, SuccessData = new AppliedChangeSummary(apply.ChangeId, [filePathResolved], $"Generated mapping from '{fromType}' to '{toType}' in {Path.GetFileName(filePathResolved)}.", apply.DryRun, apply.Diff) };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateMapping failed for '{FromType}' to '{ToType}' in '{FilePathWrapper}'", fromType, toType, filePathResolved);
            return new SentinelCallToolResult<object>() { IsSuccess = false, ErrorData = ToolErrorMapper.ToResultError(ex, _workspaceManager, "GenerateMapping") };
        }
    }

    internal static ToolOptionsResult GenerateOptions() => new()
    {
        Description = """
            generate - valid kind values:
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
              members: for generate_to_string_safe - optional comma-separated member list.
              decoratorPrefix: for generate_decorator_class (default "Logging").
              projectName: for generate_decorator_class - optional project scope.
              framework: for generate_path_driven_tests - "NUnit" (default), "xunit", or "mstest".
              disambiguateLine: for generate_path_driven_tests - disambiguates overloaded methods.
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
