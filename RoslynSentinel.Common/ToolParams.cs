namespace RoslynSentinel.Common;

public static class ToolParams
{
    public const string SessionId =
        "Optional. Only needed if you are explicitly tracking workspace sessions yourself. " +
        "Leave empty/omitted - the server will resolve the symbol fresh from docCommentId " +
        "and projectName without requiring a session round-trip.";

    public const string ProjectName =
        "Project name returned by LocateSymbol in the projectName field. " +
        "Must match exactly - case-sensitive.";

    public const string DocCommentId =
        "Uniquely identifies the symbol across the codebase. " +
        "Use the LocateSymbol tool to obtain this value. " +
        "Do not construct this value - pass it exactly as returned by LocateSymbol.";

    // Validate-and-apply workflow
    public const string AutoStage =
        "true (default) -> validates and writes the result to disk immediately; returns changeId to pass to UndoLastApply. " +
        "false -> returns updated file content without validating or writing.";

    public const string ValidateOnApply =
        "true (default) -> delta-compiles the edited project(s) plus every project that transitively " +
        "references them (so removing/narrowing a public member is caught even if nothing inside the " +
        "edited project itself calls it) before writing; returns errors without touching disk if new " +
        "errors found. " +
        "false -> writes regardless (for intentional intermediate broken-state edits).";

    public const string DryRun =
        "true -> validates only; does not write to disk and returns no changeId. " +
        "false (default) -> validates and writes to disk immediately.";

    public const string ReturnDiff =
        "true -> include a unified-diff-style preview of the change in the response (costs extra context). " +
        "false (default) -> omit the diff to keep the response minimal.";

    // Context disambiguation
    public const string ContextSnippet =
        "Optional. Only needed when the target's name alone is ambiguous (2+ declarations share it). " +
        "A SHORT, UNIQUE fragment is best - a single distinctive line (e.g. the signature or one " +
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

    public const string OldContent =
        "REQUIRED. Verbatim text to find and replace - copied exactly from a prior tool result " +
        "(ReadFile/GetMethodSource/etc.), not retyped from memory. Matched as a literal substring " +
        "first, falling back to whitespace-normalized matching. Must be short: max 60 lines / 2000 " +
        "characters - this tool is for small, localized edits only. If oldContent matches more than " +
        "once in the file, use lineBefore/lineAfter to disambiguate.";

    public const string NewContent =
        "REQUIRED. Verbatim replacement text for oldContent. May be empty (pure deletion) or longer " +
        "than oldContent (net insertion), as long as it stays within the size limit (max 60 lines / " +
        "2000 characters).";

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
        "Required. A brief descriptive phrase (at least 10 characters, must contain a space) for why " +
        "you're calling this tool right now - helps when reviewing agent transcripts later.";


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
