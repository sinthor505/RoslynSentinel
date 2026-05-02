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
        _logger.LogInformation("EncapsulateFieldSafe: {Field} in {File}", fieldName, filePath);
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
        _logger.LogInformation("AnalyzeSwitchForPatternConversion in {File}", filePath);
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
        _logger.LogInformation("ConvertSwitchToPatternSafe in {File}", filePath);
        return await _engine.ConvertSwitchToPatternSafeAsync(filePath, contextSnippet, lineBefore, lineAfter);
    }

    // ── 4. ConvertStringFormatToInterpolatedSmart ─────────────────────────────

    [McpServerTool]
    [Description("""
        Converts a string.Format() call to an interpolated string.

        FIXES standard 'convert_to_interpolated_string' LIMITATION: The standard tool
        fails with "First argument must be a string literal" when the format string is a
        named constant or static field, e.g.:
          string.Format(CacheKeyFmt, userId, date)
          string.Format(ErrorMessages.NotFound, id)

        This tool uses the semantic model to resolve the constant value at compile time,
        then builds the interpolated string from the resolved format pattern.

        Supported format string sources:
          • Inline string literal:   string.Format("User {0}", id)
          • const field/property:    string.Format(MyConst, id)
          • Static readonly string:  string.Format(Templates.Key, id) (if const)

        Supports format specifiers:   string.Format("{0:yyyy-MM-dd}", date) → $"{date:yyyy-MM-dd}"

        Parameters:
          filePath       - Absolute path to the .cs file.
          contextSnippet - A verbatim substring from the string.Format call, e.g.
                           "string.Format(CacheKeyFmt" or "Format(MyErrorTemplate,".
          lineBefore/lineAfter - Verbatim text from the line above/below the target to
                           disambiguate when the snippet matches multiple locations.

        Returns: UpdatedContent with the interpolated string, or Error if the format
        string could not be resolved to a compile-time constant.
        """)]
    public async Task<MsAugmentResult> ConvertStringFormatToInterpolatedSmart(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null)
    {
        _logger.LogInformation("ConvertStringFormatToInterpolatedSmart in {File}", filePath);
        return await _engine.ConvertStringFormatToInterpolatedSmartAsync(filePath, contextSnippet, lineBefore, lineAfter);
    }

    // ── 5. SortAndDeduplicateUsings ───────────────────────────────────────────

    [McpServerTool]
    [Description("""
        Sorts using directives (System.* first, then alphabetical) AND removes exact
        duplicates in a single operation.

        FILLS GAP between two standard tools:
          • sort_usings     → sorts but does NOT remove duplicates
          • remove_unused_usings → removes unused usings, but a duplicate that is
                                   "used" (both copies resolve) won't be removed

        This tool handles the case where a file legitimately has two identical using
        directives (e.g., a merge conflict or copy-paste artifact) and you want both
        sorted AND deduplicated in one step.

        Returns: OriginalCount, RemovedDuplicates, and UpdatedContent.
        """)]
    public async Task<UsingsCleanupResult> SortAndDeduplicateUsings(string filePath)
    {
        _logger.LogInformation("SortAndDeduplicateUsings: {File}", filePath);
        return await _engine.SortAndDeduplicateUsingsAsync(filePath);
    }
}
