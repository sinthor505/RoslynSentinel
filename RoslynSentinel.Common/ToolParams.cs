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
        "true (default) → delta-compiles the edited project(s) plus every project that transitively " +
        "references them (so removing/narrowing a public member is caught even if nothing inside the " +
        "edited project itself calls it) before writing; returns errors without touching disk if new " +
        "errors found. " +
        "false → writes regardless (for intentional intermediate broken-state edits).";

    public const string DryRun =
        "true → validates only; does not write to disk and returns no changeId. " +
        "false (default) → validates and writes to disk immediately.";

    public const string ReturnDiff =
        "true → include a unified-diff-style preview of the change in the response (costs extra context). " +
        "false (default) → omit the diff to keep the response minimal.";

    // Context disambiguation
    public const string ContextSnippet =
        "Optional. Only needed when the target's name alone is ambiguous (2+ declarations share it). " +
        "A SHORT, UNIQUE fragment is best — a single distinctive line (e.g. the signature or one " +
        "statement) is usually enough. Do NOT paste the whole member/type body: a longer excerpt is " +
        "MORE likely to fail (any formatting difference from the real file breaks the match) for no " +
        "added benefit, since only uniqueness among same-named candidates is required, not an exact " +
        "reproduction of the target. Must be copied verbatim from a prior tool result (ReadFile/" +
        "GetMethodSource/etc.), not retyped from memory. Use lineBefore/lineAfter if a short fragment " +
        "still isn't unique.";

    public const string LineBefore =
        "Line immediately before contextSnippet. Used to disambiguate when the snippet appears multiple times.";

    public const string LineAfter =
        "Line immediately after contextSnippet. Used to disambiguate when the snippet appears multiple times.";

    public const string ContainingTypeName =
        "Optional. Only needed when the target's name AND contextSnippet are still ambiguous — e.g. " +
        "two sibling types in the same file declare a same-named member with identical text (identical " +
        "auto-properties on two records). Name of the type (class/struct/record/enum) that directly " +
        "declares the target; narrows candidates before contextSnippet matching runs.";

    // Enum value sets
    public const string AccessibilityValues =
        "\"public\"|\"private\"|\"internal\"|\"protected\"|\"protected internal\"|\"private protected\"";

    public const string ListAllKindValues =
        "\"all\"|\"namespace\"|\"class\"|\"interface\"|\"method\"|\"property\"|\"struct\"|\"record\"|\"enum\"|\"enum member\"|\"constructor\"|\"field\"";

    public const string SymbolKindFilter =
        "\"type\"|\"method\"|\"property\"|\"field\"|\"event\"|\"any\" (default)";

    public const string AddOrRemoveAction =
        "\"add\"|\"remove\"";

    public const string DiagnosticScope =
        "\"file\" (scopeName = filePath) | \"project\" (scopeName = projectName) | \"solution\" (scopeName ignored)";
}
