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
        duplicates in a single operation, writing the result directly to disk.

        FILLS GAP between two standard tools:
          • sort_usings     → sorts but does NOT remove duplicates
          • remove_unused_usings → removes unused usings, but a duplicate that is
                                   "used" (both copies resolve) won't be removed

        This tool handles the case where a file legitimately has two identical using
        directives (e.g., a merge conflict or copy-paste artifact) and you want both
        sorted AND deduplicated in one step.

        Parameters:
          filePath    — absolute path to the .cs file to clean
          writeToFile — true (default) = writes sorted/deduped content to disk immediately
                        false = preview mode, returns UpdatedContent without writing

        Returns: OriginalCount, RemovedDuplicates, UpdatedContent, WrittenToDisk.
        """)]
    public async Task<UsingsCleanupResult> SortAndDeduplicateUsings(string filePath, bool writeToFile = true)
    {
        _logger.LogInformation("SortAndDeduplicateUsings: {File} (write={Write})", filePath, writeToFile);
        return await _engine.SortAndDeduplicateUsingsAsync(filePath, writeToFile);
    }

    // ── 6. FormatDocumentSafe ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        FORMAT_DOCUMENT_SAFE — Roslyn formatter with true preview support.

        FIXES MS BUG: The standard format_document tool has no preview parameter at all.
        Changes are always applied immediately. There is no way to see what would change
        before committing.

        This tool fixes that by defaulting preview=true:
          • preview=true  (default) → returns formatted content WITHOUT writing to disk
          • preview=false            → writes to disk and updates the in-memory workspace

        Use preview=true first to verify the formatted output is as expected,
        then call again with preview=false to apply.

        Parameters:
          filePath  — absolute path to the .cs file to format
          preview   — true (default) = read-only, false = apply to disk
        """)]
    public async Task<MsAugmentResult> FormatDocumentSafe(string filePath, bool preview = true)
    {
        _logger.LogInformation("FormatDocumentSafe: {File} preview={Preview}", filePath, preview);
        return await _engine.FormatDocumentSafeAsync(filePath, preview);
    }

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
        _logger.LogInformation("AnalyzeForeachForLinqConversion: {File}", filePath);
        return await _engine.AnalyzeForeachForLinqConversionAsync(filePath, contextSnippet, lineBefore, lineAfter);
    }

    // ── 8. GetWorkspaceHealth ─────────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        GET_WORKSPACE_HEALTH — Targeted workspace health check that fixes false negatives.

        FIXES MS BUG: The standard diagnose tool reports healthy:false even when all projects
        load successfully, because it tests MSBuild path existence rather than actual workspace
        state. A workspace with 86/86 projects loaded correctly can be falsely reported as
        unhealthy.

        This tool reads actual solution state directly:
          • IsOperational  — true if workspace itself is functional (not throwing)
          • HasLoadedSolution — true if a .sln or .csproj is currently loaded
          • ProjectCount/DocumentCount — actual loaded counts from the workspace
          • LoadErrors — non-fatal warnings that occurred during solution loading
          • Summary — human-readable status

        Note: IsOperational=true + HasLoadedSolution=false simply means no solution has
        been loaded yet — this is a normal state, not an error.
        """)]
    public async Task<WorkspaceHealthReport> GetWorkspaceHealth()
    {
        _logger.LogInformation("GetWorkspaceHealth called");
        return await _engine.GetWorkspaceHealthAsync();
    }

    // ── 9. PreviewAddMissingUsings ────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        PREVIEW_ADD_MISSING_USINGS — Preview what usings would be added, without modifying the file.

        FIXES MS BUG: The standard add_missing_usings tool with preview:true silently APPLIES
        changes to the file on disk anyway — the preview flag is completely ignored. Files are
        always modified regardless of what preview value you pass.

        This tool performs the analysis in read-only mode:
          • Finds CS0246/CS0103 diagnostics (unresolved type/name errors)
          • Searches all projects in the solution for matching type declarations
          • Returns the list of namespaces that WOULD be added
          • Returns UpdatedContent — the file content WITH the usings added (for review)
          • Does NOT write to disk under any circumstances

        Prerequisites: A solution must be loaded (HasLoadedSolution=true from GetWorkspaceHealth).
        The file must be part of the loaded solution.

        Returns: SolutionRequired, UsingsToAdd, Warning, UpdatedContent.
        """)]
    public async Task<AddUsingsPreview> PreviewAddMissingUsings(string filePath)
    {
        _logger.LogInformation("PreviewAddMissingUsings: {File}", filePath);
        return await _engine.PreviewAddMissingUsingsAsync(filePath);
    }

    // ── 10. ExtractConstantSafe ───────────────────────────────────────────────

    [McpServerTool]
    [Description("""
        EXTRACT_CONSTANT_SAFE — Extract a literal to a named constant using context, not line/column.

        FIXES MS BUG: The standard extract_constant tool requires exact 1-based line/column
        coordinates. When these are even slightly wrong (off by one, trailing whitespace,
        CRLF vs LF differences) it throws a cryptic error: "Column 99 is beyond end of line".
        There is no human-readable guidance on how to fix the coordinates.

        This tool uses contextSnippet to locate the literal — the same approach used by all
        other safe Sentinel tools:
          • No line/column arithmetic required
          • Human-readable error messages when the literal cannot be found
          • Replaces ALL identical literals in the file (not just the one occurrence)
          • Inserts the constant as the first member of the containing type

        Parameters:
          filePath        — absolute path to the .cs file
          contextSnippet  — the literal itself or a short surrounding snippet (e.g., '"hello world"')
          constantName    — valid C# identifier for the constant (e.g., 'GreetingMessage')
          lineBefore      — optional: the line immediately before contextSnippet (for disambiguation)
          lineAfter       — optional: the line immediately after contextSnippet (for disambiguation)

        Returns: MsAugmentResult with Success=true and UpdatedContent=the rewritten file,
                 or Success=false and Error with a human-readable explanation.
        """)]
    public async Task<MsAugmentResult> ExtractConstantSafe(
        string filePath, string contextSnippet, string constantName,
        string? lineBefore = null, string? lineAfter = null)
    {
        _logger.LogInformation("ExtractConstantSafe: {File} constant={Name}", filePath, constantName);
        return await _engine.ExtractConstantSafeAsync(filePath, contextSnippet, constantName, lineBefore, lineAfter);
    }

    // ── 11. GenerateToStringSafe ──────────────────────────────────────────────

    [McpServerTool, Description("""
        generate_tostring_safe — generates a ToString() override with CORRECTLY ESCAPED interpolated strings.

        Fixes a bug in the standard generate_tostring tool where literal { characters in the format section
        are left unescaped, producing broken code like $"Type { Prop = {Prop} }" that fails with
        CS8086 (invalid interpolation hole). This tool always emits {{ and }} for literal braces,
        producing valid code like $"Type {{ Prop = {Prop} }}".

        Parameters:
          filePath  — absolute path to the .cs file
          typeName  — the class/struct to add ToString() to
          members   — optional comma-separated list of property/field names to include;
                      if omitted, all public instance properties and fields are used

        Returns: MsAugmentResult with Success=true and UpdatedContent=the rewritten file,
                 or Success=false and Error with a human-readable explanation.
        """)]
    public async Task<MsAugmentResult> GenerateToStringSafe(
        string filePath, string typeName, string? members = null)
    {
        _logger.LogInformation("GenerateToStringSafe: {File} type={Type}", filePath, typeName);
        IList<string>? memberList = members is null ? null
            : members.Split(',', System.StringSplitOptions.RemoveEmptyEntries)
                     .Select(m => m.Trim())
                     .Where(m => m.Length > 0)
                     .ToList();
        return await _engine.GenerateToStringSafeAsync(filePath, typeName, memberList);
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
        _logger.LogInformation("ExtractMethodSafe: {File} method={Name}", filePath, newMethodName);
        return await _engine.ExtractMethodSafeAsync(
            filePath, newMethodName, contextSnippet, lineBefore, lineAfter);
    }
}
