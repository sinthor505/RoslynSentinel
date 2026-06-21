using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

[McpServerToolType]
public class SentinelQualityTools
{
    private readonly TestingEngine _testingEngine;
    private readonly ControlFlowEngine _controlFlowEngine;
    private readonly AnalysisEngine _analysisEngine;
    private readonly AntiPatternEngine _antiPatternEngine;
    private readonly ThreadSafetyEngine _threadSafetyEngine;
    private readonly DiagnosticEngine _diagnosticEngine;
    private readonly CodeStyleAnalysisEngine _codeStyleAnalysisEngine;
    private readonly StackOverflowEngine _stackOverflowEngine;
    private readonly MsToolAugmentEngine _msToolAugmentEngine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelQualityTools> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public SentinelQualityTools(
        TestingEngine testingEngine,
        ControlFlowEngine controlFlowEngine,
        AnalysisEngine analysisEngine,
        AntiPatternEngine antiPatternEngine,
        ThreadSafetyEngine threadSafetyEngine,
        DiagnosticEngine diagnosticEngine,
        CodeStyleAnalysisEngine codeStyleAnalysisEngine,
        StackOverflowEngine stackOverflowEngine,
        MsToolAugmentEngine toolAugmentEngine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelQualityTools> logger)
    {
        _testingEngine = testingEngine;
        _controlFlowEngine = controlFlowEngine;
        _analysisEngine = analysisEngine;
        _antiPatternEngine = antiPatternEngine;
        _threadSafetyEngine = threadSafetyEngine;
        _diagnosticEngine = diagnosticEngine;
        _codeStyleAnalysisEngine = codeStyleAnalysisEngine;
        _stackOverflowEngine = stackOverflowEngine;
        _msToolAugmentEngine = toolAugmentEngine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── describe_advanced_tool_options ─────────────────────────────────────────────────

    [McpServerTool(Name = "DescribeAdvancedToolOptions")]
    [Produces(DataTag.Report)]
    [Description("""
        Returns reference documentation for a named tool's valid input values — transform/kind/detector catalogues and parameter defaults. Only covers tools whose valid values cannot be inferred from the schema alone. Covered tools: scan, apply_file_codemod, apply_method_codemod, apply_class_codemod, generate, convert_switch_to_pattern_safe, analyze_switch_for_pattern_conversion, analyze_foreach_for_linq_conversion. Returns ErrorCode="NoFurtherDocumentation" if the tool is not in the covered set — this does not mean the tool is invalid, only that its schema is self-describing.
        """)]
    public ToolOptionsResult DescribeAdvancedToolOptions(
        [ToolOption(ToolOptionTag.ToolName, required: true)] string toolName,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        return toolName switch
        {
            "scan" => SentinelScanTools.ScanOptions(),
            "apply_file_codemod" => SentinelCodemodTools.ApplyFileCodemodOptions(),
            "apply_method_codemod" => SentinelCodemodTools.ApplyMethodCodemodOptions(),
            "apply_class_codemod" => SentinelCodemodTools.ApplyClassCodemodOptions(),
            "generate" => SentinelGenerationTools.GenerateOptions(),
            "convert_switch_to_pattern_safe" => ConvertSwitchOptions(),
            "analyze_switch_for_pattern_conversion" => AnalyzeSwitchOptions(),
            "analyze_foreach_for_linq_conversion" => AnalyzeForeachOptions(),
            _ => new ToolOptionsResult
            {
                Description = $"'{toolName}' is not in the describe_advanced_tool_options covered set. " +
                               "This does not mean the tool is invalid — its parameter schema fully " +
                               "describes its inputs. Covered tools: scan, apply_file_codemod, " +
                               "apply_method_codemod, apply_class_codemod, generate, " +
                               "convert_switch_to_pattern_safe, analyze_switch_for_pattern_conversion, " +
                               "analyze_foreach_for_linq_conversion.",
                Error = new ResultError(
                    "NoFurtherDocumentation",
                    $"'{toolName}' has no registered options table. See Description for the covered tool list.")
            }
        };
    }

    [McpServerTool(Name = "GetTestCoverageMap")]
    [Produces(DataTag.Report)]
    [Description("Returns execution paths to cover and test methods that exercise a production method. Finds covering tests by name convention (test method name contains production method name) and by direct call-site presence. Returns BranchesToTest, CoveringTests (test file, method, line), and HasAnyCoverage flag.")]
    public async Task<ToolResult<object>> GetTestCoverageMap(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        try
        {
            var result = await _controlFlowEngine.GetTestCoverageMapAsync(filePath, methodName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTestCoverageMap failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"GetTestCoverageMap failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    [McpServerTool(Name = "GetMethodComplexity")]
    [Produces(DataTag.Report)]
    [Description("Calculates cyclomatic complexity of a method: 1 + one per if/else/case/while/for/foreach/catch/&&/||/?? branch. Returns complexity score and contributing conditionals. Guide: 1–4 = Low, 5–7 = Medium, 8–10 = High (refactoring candidate), >10 = Very High.")]
    public async Task<ToolResult<object>> GetMethodComplexity(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.SymbolName, required: true)] string methodName,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        try
        {
            var result = await _testingEngine.CalculateComplexityAsync(filePath, methodName);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMethodComplexity failed for '{MethodName}' in '{FilePath}'", methodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"GetMethodComplexity failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    // ── 7. AnalyzeForeachForLinqConversion ────────────────────────────────────

    [McpServerTool(Name = "AnalyzeForeachForLinqConversion")]
    [Produces(DataTag.ResultOnly)]
    [Description("""
        Pre-flight safety check before convert_foreach_linq. contextSnippet: short foreach snippet (e.g. "foreach (var item in"). lineBefore/lineAfter disambiguate multiple matches. Call describe_advanced_tool_options("analyze_foreach_for_linq_conversion") for full output field reference and safety rules.
        """)]
    // FIXES MS BUG: the standard tool produces incorrect code when the foreach loop body mutates the collection being iterated (e.g. adding/removing items from a List<T>), which is a common pattern. This tool uses Roslyn's ControlFlowAnalysis and DataFlowAnalysis to detect mutations to the collection variable within the loop body, and rejects conversion if any are found.
    public async Task<ToolResult<object>> AnalyzeForeachForLinqConversion(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("AnalyzeForeachForLinqConversion: {File}", filePath);
        }
        try
        {
            var result = await _msToolAugmentEngine.AnalyzeForeachForLinqConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnalyzeForeachForLinqConversion failed in '{File}'", filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"AnalyzeForeachForLinqConversion failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }


    // ── 2. AnalyzeSwitchForPatternConversion ─────────────────────────────────

    [McpServerTool(Name = "AnalyzeSwitchForPatternConversion")]
    [Produces(DataTag.Analysis)]
    [Description("""
        Pre-flight safety check before converting a switch statement to a switch expression. contextSnippet: verbatim substring from the switch keyword line (e.g. "switch (unit)"). lineBefore/lineAfter disambiguate. Call describe_advanced_tool_options("analyze_switch_for_pattern_conversion") for full output field reference.
        """)]
    // FIXES MS BUG: the standard tool silently drops variable assignments in multi-variable cases. This tool uses Roslyn's ControlFlowAnalysis and DataFlowAnalysis to detect all variables assigned within the switch, and rejects conversion if any are assigned in more than one case arm, or if their assigned value is read later in the method (indicating a likely dependency on the variable retaining its value across cases). IsSafeToConvert=true means the standard tool or convert_switch_to_pattern_safe will produce correct output.
    public async Task<ToolResult<object>> AnalyzeSwitchForPatternConversion(
        [Consumes(DataTag.SourceFilepath, required: true)] string filepath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [ExternalInputRequired(DataTag.LineBefore)] string? lineBefore = null,
        [ExternalInputRequired(DataTag.LineAfter)] string? lineAfter = null,
        Progress<string>? progress = null,
        CancellationToken? cancellationToken = default)
    {
        FilePath filePath = FilePath.FromWire(filepath, _workspaceManager.GetSolutionRoot());

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("AnalyzeSwitchForPatternConversion in {File}", filePath);
        }
        try
        {
            var result = await _msToolAugmentEngine.AnalyzeSwitchForPatternConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnalyzeSwitchForPatternConversion failed in '{File}'", filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError(ToolErrorCode.Exception, $"AnalyzeSwitchForPatternConversion failed unexpectedly ({ex.GetType().Name}). Check that the solution is loaded and the file path is valid. Details: {ex.Message}")
            };
        }
    }

    private static ToolOptionsResult ConvertSwitchOptions() => new()
    {
        Description = """
            convert_switch_to_pattern_safe — supported switch forms and rejection rules:

            SUPPORTED forms:
              1. All cases assign to the SAME variable:
                   case "g": factor = 1.0; break;
                   → factor = unit switch { "g" => 1.0, ... };
              2. All cases are return statements:
                   case "g": return 1.0;
                   → return unit switch { "g" => 1.0, ... };
              3. All cases are throw statements (or mixed with return).

            REJECTED (returned as error, not silently dropped):
              • Cases assigning to MULTIPLE different variables per case
              • Cases assigning to different variables across cases
              • Cases with complex multi-statement bodies

            Parameters:
              filePath       — absolute path to the .cs file.
              contextSnippet — verbatim substring from the switch keyword line, e.g. "switch (unit)".
              lineBefore/lineAfter — disambiguate when the snippet matches multiple locations.

            Run analyze_switch_for_pattern_conversion first if you are unsure whether conversion is safe.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["supportedForms"] = new[] { "single-variable assignment", "return statements", "throw statements" },
            ["rejectedForms"] = new[] { "multiple-variable assignment per case", "different variables across cases", "complex multi-statement bodies" }
        }
    };

    private static ToolOptionsResult AnalyzeSwitchOptions() => new()
    {
        Description = """
            analyze_switch_for_pattern_conversion — pre-flight analysis output fields:

            Returns SwitchConversionAnalysis with:
              IsSafeToConvert     — true when the standard tool or convert_switch_to_pattern_safe will produce correct output.
              CaseCount           — total number of cases analysed.
              Cases[]             — per-case detail: CaseLabel, AssignmentCount, VariablesAssigned[], IsSafe, BlockingReason.
              BlockingReason      — human-readable reason why IsSafeToConvert is false (null when safe).
              Recommendation      — suggested next step.

            WHY THIS TOOL EXISTS: The standard 'convert_to_pattern_matching' tool silently drops
            variable assignments in switch cases that assign to more than one variable, producing
            broken code without any warning. This tool detects that condition before conversion.

            Parameters:
              filePath       — absolute path to the .cs file.
              contextSnippet — verbatim substring from the switch keyword line.
              lineBefore/lineAfter — optional disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["outputFields"] = new[] { "IsSafeToConvert", "CaseCount", "Cases", "BlockingReason", "Recommendation" }
        }
    };

    private static ToolOptionsResult AnalyzeForeachOptions() => new()
    {
        Description = """
            analyze_foreach_for_linq_conversion — pre-flight analysis output fields:

            Returns ForeachLinqAnalysis with:
              IsSafeToConvert          — true when convert_foreach_linq will produce correct output.
              CollectionVariableName   — the collection variable being built by the foreach.
              StatementsBeforeForeach  — list of statements that modify the collection BEFORE the loop
                                         (these would be discarded by the standard tool if present).
              BlockingReason           — human-readable reason when IsSafeToConvert is false.
              Recommendation           — suggested next step.

            WHY THIS TOOL EXISTS: The standard 'convert_foreach_linq' tool silently destroys data.
            When a collection is modified before the foreach (e.g., results.Add("header")), the
            standard tool re-initialises the variable with 'new List<T>()', discarding those
            pre-loop additions WITHOUT any warning.

            ALWAYS call this before convert_foreach_linq. Only proceed if IsSafeToConvert=true.

            Parameters:
              filePath        — absolute path to the .cs file.
              contextSnippet  — short snippet of the foreach statement (e.g., "foreach (var item in").
              lineBefore/lineAfter — optional disambiguation.
            """,
        StructuredOptions = new Dictionary<string, object>
        {
            ["outputFields"] = new[] { "IsSafeToConvert", "CollectionVariableName", "StatementsBeforeForeach", "BlockingReason", "Recommendation" }
        }
    };
}
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)