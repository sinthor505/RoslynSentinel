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
        Encapsulates a public field into a private backing field + public property.

        FIXES standard 'encapsulate_field' BUG: The standard tool generates code like
          private int SuccessCount;
          public int SuccessCount { get { return SuccessCount; } }
        which causes infinite recursion / a compile error because the backing field and
        property share the same name.

        This tool always renames the backing field to _camelCase:
          private int _successCount;
          public int SuccessCount { get => _successCount; set => _successCount = value; }

        Parameters:
          filePath            - Absolute path to the .cs file.
          fieldName           - Exact name of the field to encapsulate (e.g. "SuccessCount").
          overridePropertyName - Optional: provide a custom property name if the default
                                 (PascalCase of fieldName) would conflict.

        Returns: UpdatedContent with the corrected encapsulation applied.
        """)]
    public async Task<MsAugmentResult> EncapsulateFieldSafe(
        string filePath,
        string fieldName,
        string? overridePropertyName = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("EncapsulateFieldSafe: {Field} in {File}", fieldName, filePath);
        }
        return await _engine.EncapsulateFieldSafeAsync(filePath, fieldName, overridePropertyName);
    }

    // ── 2. AnalyzeSwitchForPatternConversion ─────────────────────────────────

    [McpServerTool]
    [Description("""
        Pre-flight analysis: checks whether a switch statement is safe to convert to a
        switch expression using the standard 'convert_to_pattern_matching' tool.

        WHY YOU NEED THIS: The standard tool silently drops variable assignments in switch
        cases that assign to multiple variables. For example:
          case "g":
            totalOz   = rawValue / 28.3495m;   // ← DROPPED by standard tool
            totalGrams = rawValue;               // ← only this survives
            break;
        This produces broken, data-loss code with no warning.

        This tool returns a full case-by-case analysis, tells you which cases have multiple
        assignments, and gives a clear blocking reason when conversion is unsafe.

        If IsSafeToConvert is true, the standard tool (or ConvertSwitchToPatternSafe) will
        produce correct output.

        Parameters:
          filePath       - Absolute path to the .cs file.
          contextSnippet - A verbatim substring from the switch keyword or governing
                           expression line, e.g. "switch (unit)" or "switch (rawUnit)".
          lineBefore/lineAfter - Verbatim text from the line above/below the target to
                           disambiguate when the snippet matches multiple locations.
        """)]
    public async Task<SwitchConversionAnalysis> AnalyzeSwitchForPatternConversion(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("AnalyzeSwitchForPatternConversion in {File}", filePath);
        }
        return await _engine.AnalyzeSwitchForPatternConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
    }

    // ── 3. ConvertSwitchToPatternSafe ─────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Converts a switch statement to a switch expression safely.

        FIXES standard 'convert_to_pattern_matching' BUG: The standard tool silently
        drops variable assignments for cases that set more than one variable, generating
        incorrect code. This tool rejects those cases with a clear error message instead
        of producing broken output.

        Supports three switch forms:
          1. All cases are assignments to the SAME variable:
               case "g": factor = 1.0; break;
               → factor = unit switch { "g" => 1.0, ... };

          2. All cases are return statements:
               case "g": return 1.0;
               → return unit switch { "g" => 1.0, ... };

          3. All cases are throw statements (or mixed with return).

        Rejects (with a clear error):
          • Cases assigning to MULTIPLE different variables per case
          • Cases assigning to different variables across cases
          • Cases with complex multi-statement bodies

        Run AnalyzeSwitchForPatternConversion first if you are unsure.

        Parameters:
          filePath       - Absolute path to the .cs file.
          contextSnippet - A verbatim substring from the switch keyword or governing
                           expression, e.g. "switch (unit)".
          lineBefore/lineAfter - Verbatim text from the line above/below the target to
                           disambiguate when the snippet matches multiple locations.

        Returns: UpdatedContent on success, or Error describing exactly why conversion
        was rejected.
        """)]
    public async Task<MsAugmentResult> ConvertSwitchToPatternSafe(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ConvertSwitchToPatternSafe in {File}", filePath);
        }
        return await _engine.ConvertSwitchToPatternSafeAsync(filePath, contextSnippet, lineBefore, lineAfter);
    }

    // ── 6. FormatDocumentSafe ─────────────────────────────────────────────────


    // ── 7. AnalyzeForeachForLinqConversion ────────────────────────────────────

    [McpServerTool]
    [Description("""
        ANALYZE_FOREACH_FOR_LINQ_CONVERSION — Pre-flight safety analysis for convert_foreach_linq.

        FIXES MS BUG: The standard convert_foreach_linq tool silently destroys data.
        When a collection is modified BEFORE the foreach (e.g., results.Add("header") before
        the loop), the standard tool re-initializes the variable with 'new List<T>()',
        discarding those pre-loop additions WITHOUT any warning.

        ALWAYS call this tool before using convert_foreach_linq.
        Only proceed with conversion if IsSafeToConvert=true.

        Parameters:
          filePath        — absolute path to the .cs file
          contextSnippet  — a short snippet of the foreach statement (e.g., "foreach (var item in")
          lineBefore      — optional: the line of code immediately before contextSnippet
          lineAfter       — optional: the line of code immediately after contextSnippet

        Returns: IsSafeToConvert, CollectionVariableName, StatementsBeforeForeach, BlockingReason,
                 and a Recommendation.
        """)]
    public async Task<ForeachLinqAnalysis> AnalyzeForeachForLinqConversion(
        string filePath, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("AnalyzeForeachForLinqConversion: {File}", filePath);
        }
        return await _engine.AnalyzeForeachForLinqConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        GET_WORKSPACE_HEALTH — Targeted workspace health check that fixes false negatives.

        Call this tool directly — no prior tool_search step is required to use RoslynSentinel tools.

        FIXES MS BUG: The standard diagnose tool reports healthy:false even when all projects
        load successfully, because it tests MSBuild path existence rather than actual workspace
        state. A workspace with 86/86 projects loaded correctly can be falsely reported as
        unhealthy.

        This tool reads actual solution state directly:
          • IsOperational       — true if workspace itself is functional (not throwing)
          • HasLoadedSolution   — true if a .sln or .csproj is currently loaded
          • LoadedSolutionPath  — absolute path to the loaded .sln file; null when no solution
                                  is loaded
          • ProjectCount/DocumentCount — actual loaded counts from the workspace
          • LoadErrors          — non-fatal warnings that occurred during solution loading
          • Summary             — human-readable status

        Note: IsOperational=true + HasLoadedSolution=false simply means no solution has
        been loaded yet — this is a normal state, not an error.
        """)]
    public async Task<WorkspaceHealthReport> GetWorkspaceHealth()
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("GetWorkspaceHealth called");
        }
        return await _engine.GetWorkspaceHealthAsync();
    }



    // ── 12. ExtractMethodSafe ─────────────────────────────────────────────────

    [McpServerTool, Description("""
        extract_method_safe— extracts selected statements into a new method with the CORRECT return type.

        Fixes a bug in the standard extract_method tool where selections ending with `return <expression>`
        are extracted into a method declared `private void MethodName(...)`. The void return type is
        incorrect and causes a compile error. This tool uses Roslyn's SemanticModel to determine the
        actual type of the returned expression, and DataFlowAnalysis to find the correct parameter list.

        Requires a solution to be loaded first (via set_solution_path or equivalent), because semantic
        analysis is needed to determine types.

        Parameters:
          filePath        — absolute path to the .cs file (must be in the loaded solution)
          newMethodName   — valid C# identifier for the new extracted method
          contextSnippet  — a short unique code snippet that identifies the selection
          lineBefore      — optional: the line immediately before contextSnippet (for disambiguation)
          lineAfter       — optional: the line immediately after contextSnippet (for disambiguation)

        Returns: MsAugmentResult with Success=true and UpdatedContent=the rewritten file,
                 or Success=false and Error with a human-readable explanation.
        """)]
    public async Task<MsAugmentResult> ExtractMethodSafe(
        string filePath, string newMethodName, string contextSnippet,
        string? lineBefore = null, string? lineAfter = null)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("ExtractMethodSafe: {File} method={Name}", filePath, newMethodName);
        }
        return await _engine.ExtractMethodSafeAsync(
            filePath, newMethodName, contextSnippet, lineBefore, lineAfter);
    }
}
