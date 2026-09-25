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
        "true (default) = write immediately; false = return content without writing.";

    public const string ValidateOnApply =
        "true (default) = reject the write if it introduces a new compiler error.";

    public const string DryRun =
        "true = validate only, don't write.";

    public const string ReturnDiff =
        "true = include a diff preview in the response.";

    // Context disambiguation
    /*
    public const string ContextSnippet =
        "Short unique fragment identifying the target when its name alone is ambiguous, copied verbatim from a prior result.";
    */
    public const string ContextSnippet = "Short unique verbatim disambiguating fragment for identifying the target";

    public const string LineBefore =
        "Line before contextSnippet, to disambiguate repeats.";

    public const string LineAfter =
        "Line after contextSnippet, to disambiguate repeats.";

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
        "Optional. Only needed when the target's name AND contextSnippet are still ambiguous - e.g. " +
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

    // Transcript review        
    public const string Reason =
        "Why you're calling this now (min 10 chars, must contain a space).";
    /*
    // Testing with empty reason to reduce tool schema token usage. Observing model compliance.
    //public const string Reason = ""; // empty reason description caused models to omit the field or be surprised by the constraints, reverted to original.
    */

    // Added by AddMember (expected - used for diagnostics)
    public const string SnippetEdits =
    "Batch form of oldContent/newContent: apply several edits in one call instead of one call per " +
    "edit. Mutually exclusive with filepath/oldContent/newContent - supply either the singular " +
    "params or this array, never both. Every edit is matched against each file's ORIGINAL content " +
    "(not against the result of an earlier edit in this same array), then all matches are spliced " +
    "in together and written as one atomic change - so edits within this call never need to account " +
    "for each other's line-number shifts. Two edits in the same file with overlapping matches are " +
    "rejected before anything is written. Each edit is still bound by the same oldContent/newContent " +
    "size limits as a single-edit call.";

    // Added by InsertMemberAfter (expected - used for diagnostics)
    public const string ModifierEdits =
    "Batch form: apply several add/remove-modifier edits in one call instead of one call per edit. " +
    "Mutually exclusive with filepath/targetName/modifier/action - supply either the singular params " +
    "or this array, never both. Every edit's target is resolved against each file's ORIGINAL syntax " +
    "tree (not against the result of an earlier edit in this same array), then all edits for a file " +
    "are applied together and written as one atomic change. Two edits in the same file that resolve " +
    "to the same target are rejected before anything is written.";

    // Added by InsertMemberAfter (expected - used for diagnostics)
    // Added by ModifyAttribute batch support
    public const string AttributeEdits =
    "Batch form: apply several add/replace/remove-attribute edits in one call instead of one call per " +
    "edit. Mutually exclusive with filepath/targetName/existingAttribute/action/newAttribute - supply " +
    "either the singular params or this array, never both. Every edit's target is resolved against " +
    "each file's ORIGINAL syntax tree (not against the result of an earlier edit in this same array), " +
    "then all edits for a file are applied together and written as one atomic change. Two edits in the " +
    "same file that resolve to the same target are rejected before anything is written.";

    // Added by InsertMemberAfter (expected - used for diagnostics)
    // Added by ModifyBaseType batch support
    public const string BaseTypeEdits =
    "Batch form: apply several add/remove-base-type edits in one call instead of one call per edit. " +
    "Mutually exclusive with filepath/typeName/baseTypeName/action - supply either the singular params " +
    "or this array, never both. Every edit's target is resolved against each file's ORIGINAL syntax " +
    "tree (not against the result of an earlier edit in this same array), then all edits for a file " +
    "are applied together and written as one atomic change. Two edits in the same file that resolve " +
    "to the same target are rejected before anything is written.";
}
