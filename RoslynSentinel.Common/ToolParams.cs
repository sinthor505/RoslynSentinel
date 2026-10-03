namespace RoslynSentinel.Common;

public static class ToolParams
{
    public const string ProjectName =
        "Project name returned by LocateSymbol in the projectName field. " +
        "Must match exactly - case-sensitive.";

    public const string DocCommentId =
        "Uniquely identifies the symbol across the codebase. " +
        "Use the LocateSymbol tool to obtain this value. " +
        "Do not construct this value - pass it exactly as returned by LocateSymbol.";

    // Validate-and-apply workflow
    public const string AutoStage =
        "false = return content without writing.";

    public const string ValidateOnApply =
        "true = reject the write if it introduces a new compiler error.";

    public const string DryRun =
        "true = validate only, don't write.";

    public const string ReturnDiff =
        "true = include a diff in the response.";

    // The one batch-semantics sentence shared by every batch-edit param (SnippetEdits, ModifierEdits, ...).
    public const string BatchSemantics =
        "Edits resolve against the original file content and apply atomically; two overlapping edits are rejected.";

    // Context disambiguation
    /*
    public const string ContextSnippet =
        "Short unique fragment identifying the target when its name alone is ambiguous, copied verbatim from a prior result.";
    */
    public const string ContextSnippet = "Short unique verbatim disambiguating fragment for identifying the target";

    public const string LineBefore =
        "Line before contextSnippet.";

    public const string LineAfter =
        "Line after contextSnippet.";

    /*
    public const string OldContent =
        "REQUIRED. Verbatim text to find and replace - copied exactly from a prior tool result " +
        "(ReadFile/GetMethodSource/etc.), not retyped from memory. Matched as a literal substring " +
        "first, falling back to whitespace-normalized matching. Must be short (a startup-configured " +
        "line/char limit, reported in the error if exceeded) - this tool is for small, localized " +
        "edits only. If oldContent matches more than once in the file, use lineBefore/lineAfter to " +
        "disambiguate.";

    */
    public const string OldContent = "Verbatim text to find and replace using literal substring match.";

    /*
    public const string NewContent =
        "REQUIRED. Verbatim replacement text for oldContent. May be empty (pure deletion) or longer " +
        "than oldContent (net insertion), as long as it stays within the size limit (a startup-" +
        "configured line/char limit, reported in the error if exceeded).";
    */
    public const string NewContent = "Verbatim replacement text for oldContent.";

    public const string ContainingTypeName =
        "Name of the type that directly declares the target. Only needed when the target's name AND " +
        "contextSnippet are still ambiguous (e.g. identical members on two sibling types).";

    // Enum value sets
    public const string AccessibilityValues =
        "\"public\"|\"private\"|\"internal\"|\"protected\"|\"protected internal\"|\"private protected\"";

    public const string ListAllKindValues =
        "\"all\"|\"namespace\"|\"class\"|\"interface\"|\"method\"|\"property\"|\"struct\"|\"record\"|\"enum\"|\"enum member\"|\"constructor\"|\"field\"";

    public const string SearchModeValues =
    "\"text\"|\"symbol\"|\"references\"|\"all\"|\"namespace\"|\"class\"|\"interface\"|\"method\"|\"property\"|\"struct\"|\"record\"|\"enum\"|\"enum member\"|\"constructor\"|\"field\"";

    public const string SymbolKindFilter =
        "\"type\"|\"method\"|\"property\"|\"field\"|\"event\"|\"any\" (default)";

    public const string AddOrRemoveAction =
        "\"add\"|\"remove\"";

    public const string DiagnosticScope =
        "\"file\" (scopeName = filePath) | \"project\" (scopeName = projectName) | \"solution\" (scopeName ignored)";

    // Transcript review        
    public const string Reason =
        "Why you're calling this now (min 10 chars, must contain a space).";
    /*
    // Testing with empty reason to reduce tool schema token usage. Observing model compliance.
    //public const string Reason = ""; // empty reason description caused models to omit the field or be surprised by the constraints, reverted to original.
    */

    public const string SnippetEdits =
 "Batch form of filePath/oldContent/newContent - use it instead of them, not with them. " + BatchSemantics +
 " Each edit has the same size limits.";

    public const string ModifierEdits =
 "Batch form of filePath/targetName/modifier/action - use it instead of them, not with them. " + BatchSemantics +
 " A file holding an 'add static' conversion must not be targeted by other edits in the same batch; " +
 "two members that only use each other can be listed together.";

    // Added by ModifyAttribute batch support
    public const string AttributeEdits =
    "Batch form of the singular filePath/targetName/existingAttribute/action/newAttribute params - use it instead of them, not with them. " + BatchSemantics;

    // Added by ModifyBaseType batch support
    public const string BaseTypeEdits =
    "Batch form of filePath/typeName/baseTypeName/action - use it instead of them, not with them. " + BatchSemantics;
}
