namespace RoslynSentinel.Common;

public static class ToolParams
{
    public const string SessionId =
        "Optional. Only needed if you are explicitly tracking workspace sessions yourself. " +
        "Leave empty/omitted — the server will resolve the symbol fresh from docCommentId " +
        "and projectName without requiring a session round-trip.";

    public const string ProjectName =
        "Project name returned by locate_symbol in the projectName field. " +
        "Must match exactly — case-sensitive.";

    public const string DocCommentId =
        "Documentation comment ID returned by locate_symbol in the docCommentId field. " +
        "Uniquely identifies the symbol across tool calls. " +
        "Do not construct this value — pass it exactly as returned by locate_symbol.";

    // Validate-and-apply workflow
    public const string AutoStage =
        "true (default) → validates and writes the result to disk immediately; returns changeId to pass to UndoLastApply. " +
        "false → returns updated file content without validating or writing.";

    public const string ValidateOnApply =
        "true (default) → delta compile before writing; returns errors without touching disk if new errors found. " +
        "false → writes regardless (for intentional intermediate broken-state edits).";

    public const string DryRun =
        "true → validates only; does not write to disk and returns no changeId. " +
        "false (default) → validates and writes to disk immediately.";

    public const string ReturnDiff =
        "true → include a unified-diff-style preview of the change in the response (costs extra context). " +
        "false (default) → omit the diff to keep the response minimal.";

    // Context disambiguation
    public const string ContextSnippet =
        "Verbatim substring from the source line containing the target. " +
        "Must match exactly. Use lineBefore/lineAfter when the snippet is not unique.";

    public const string LineBefore =
        "Line immediately before contextSnippet. Used to disambiguate when the snippet appears multiple times.";

    public const string LineAfter =
        "Line immediately after contextSnippet. Used to disambiguate when the snippet appears multiple times.";

    // Enum value sets
    public const string AccessibilityValues =
        "\"public\"|\"private\"|\"internal\"|\"protected\"|\"protected internal\"|\"private protected\"";

    public const string SymbolKindFilter =
        "\"type\"|\"method\"|\"property\"|\"field\"|\"event\"|\"any\" (default)";

    public const string AddOrRemoveAction =
        "\"add\"|\"remove\"";

    public const string DiagnosticScope =
        "\"file\" (scopeName = filePath) | \"project\" (scopeName = projectName) | \"solution\" (scopeName ignored)";
}
