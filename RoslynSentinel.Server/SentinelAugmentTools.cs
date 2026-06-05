using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

namespace RoslynSentinel.Server;

/// <summary>
/// MCP tools that augment or fix known bugs in the standard Microsoft roslyn-mcp server.
/// Each tool documents which standard tool it replaces and exactly what it fixes.
/// </summary>
[McpServerToolType]
public class SentinelAugmentTools
{
    private readonly MsToolAugmentEngine _engine;
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly ILogger<SentinelAugmentTools> _logger;

    public SentinelAugmentTools(
        MsToolAugmentEngine engine,
        PersistentWorkspaceManager workspaceManager,
        ILogger<SentinelAugmentTools> logger)
    {
        _engine = engine;
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    // ── 1. EncapsulateFieldSafe ───────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Encapsulates a public field into a private backing field + public property. FIXES standard encapsulate_field BUG: the standard tool creates a backing field and property with the same name, causing infinite recursion/compile error. This tool always renames the backing field to _camelCase. overridePropertyName provides a custom property name when the default PascalCase would conflict. Returns UpdatedContent.
        """)]
    public async Task<ToolResult<object>> EncapsulateFieldSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.SymbolName, required: true)] string fieldName,
        string? overridePropertyName = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("EncapsulateFieldSafe: {Field} in {File}", fieldName, filePath);
        }
        try
        {
            var result = await _engine.EncapsulateFieldSafeAsync(filePath, fieldName, overridePropertyName);
            return new ToolResult<object>
            {
                Success = result.Success,
                Data = result.UpdatedContent,
                Error = result.Error != null ? new ResultError("EncapsulateFieldSafeFailed", result.Error) : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EncapsulateFieldSafe failed for '{Field}' in '{File}'", fieldName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"EncapsulateFieldSafe failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    // ── 2. AnalyzeSwitchForPatternConversion ─────────────────────────────────

    [McpServerTool]
    [Description("""
        Pre-flight safety check before converting a switch statement to a switch expression. FIXES MS BUG: the standard tool silently drops variable assignments in multi-variable cases. IsSafeToConvert=true means the standard tool or convert_switch_to_pattern_safe will produce correct output. contextSnippet: verbatim substring from the switch keyword line (e.g. "switch (unit)"). lineBefore/lineAfter disambiguate. Call describe_advanced_tool_options("analyze_switch_for_pattern_conversion") for full output field reference.
        """)]
    public async Task<ToolResult<object>> AnalyzeSwitchForPatternConversion(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("AnalyzeSwitchForPatternConversion in {File}", filePath);
        }
        try
        {
            var result = await _engine.AnalyzeSwitchForPatternConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
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
                Error = new ResultError("", $"AnalyzeSwitchForPatternConversion failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    // ── 3. ConvertSwitchToPatternSafe ─────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Converts a switch statement to a switch expression, rejecting unsafe cases instead of silently producing broken code. FIXES MS BUG: the standard tool drops variable assignments when a case sets more than one variable. contextSnippet: verbatim substring from the switch keyword line (e.g. "switch (unit)"). lineBefore/lineAfter disambiguate multiple matches. Run analyze_switch_for_pattern_conversion first if unsure. Returns MsAugmentResult with UpdatedContent on success or Error on rejection. Call describe_advanced_tool_options("convert_switch_to_pattern_safe") for supported switch forms and rejection rules.
        """)]
    public async Task<ToolResult<object>> ConvertSwitchToPatternSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ConvertSwitchToPatternSafe in {File}", filePath);
        }
        try
        {
            var result = await _engine.ConvertSwitchToPatternSafeAsync(filePath, contextSnippet, lineBefore, lineAfter);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ConvertSwitchToPatternSafe failed in '{File}'", filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"ConvertSwitchToPatternSafe failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    // ── 6. FormatDocumentSafe ─────────────────────────────────────────────────

    // ── 7. AnalyzeForeachForLinqConversion ────────────────────────────────────

    [McpServerTool]
    [Description("""
        Pre-flight safety check before convert_foreach_linq. FIXES MS BUG: the standard tool silently destroys data when a collection is modified before the foreach. Only proceed with conversion if IsSafeToConvert=true. contextSnippet: short foreach snippet (e.g. "foreach (var item in"). lineBefore/lineAfter disambiguate multiple matches. Call describe_advanced_tool_options("analyze_foreach_for_linq_conversion") for full output field reference and safety rules.
        """)]
    public async Task<ToolResult<object>> AnalyzeForeachForLinqConversion(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("AnalyzeForeachForLinqConversion: {File}", filePath);
        }
        try
        {
            var result = await _engine.AnalyzeForeachForLinqConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
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
                Error = new ResultError("", $"AnalyzeForeachForLinqConversion failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Targeted workspace health check. FIXES MS BUG: the standard diagnose tool reports healthy:false even when all projects load successfully, because it tests MSBuild path existence rather than actual workspace state. This tool reads workspace state directly. Returns: IsOperational, HasLoadedSolution, LoadedSolutionPath, ProjectCount, DocumentCount, LoadErrors, Summary. IsOperational=true + HasLoadedSolution=false is normal — no solution loaded yet, not an error.
        """)]
    public async Task<ToolResult<object>> GetWorkspaceHealth()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("GetWorkspaceHealth called");
        }
        try
        {
            var result = await _engine.GetWorkspaceHealthAsync();
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetWorkspaceHealth failed");
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"GetWorkspaceHealth failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }

    // ── 12. ExtractMethodSafe ─────────────────────────────────────────────────

    [McpServerTool, Description("""
        extract_method_safe—extracts selected statements into a new method with the CORRECT return type. Fixes MS BUG: where selections ending with "return <expression>" are extracted into a method declared "private void MethodName(...)", causing a compile error. This tool uses Roslyn's SemanticModel to determine the actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list. Requires a loaded solution (via set_solution_path or equivalent). newMethodName must be a valid C# identifier. contextSnippet: short unique code snippet identifying the selection. lineBefore/lineAfter disambiguate. Returns MsAugmentResult with extracted method code or error on rejection.
        """)]
    public async Task<ToolResult<object>> ExtractMethodSafe(
        [Consumes(DataTag.SourceFilepath, required: true)] string filePath,
        string newMethodName,
        [Consumes(DataTag.ContextSnippet, required: true)] string contextSnippet,
        [Consumes(DataTag.LineBefore)] string? lineBefore = null,
        [Consumes(DataTag.LineAfter)] string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ExtractMethodSafe: {File} method={Name}", filePath, newMethodName);
        }
        try
        {
            var result = await _engine.ExtractMethodSafeAsync(
                filePath, newMethodName, contextSnippet, lineBefore, lineAfter);
            return new ToolResult<object>
            {
                Success = true,
                Data = result
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractMethodSafe failed for '{NewMethodName}' in '{FilePath}'", newMethodName, filePath);
            return new ToolResult<object>
            {
                Success = false,
                Error = new ResultError("", $"ExtractMethodSafe failed: {ex.GetType().Name}: {ex.Message}")
            };
        }
    }
}
