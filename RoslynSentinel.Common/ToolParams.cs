namespace RoslynSentinel.Common;

public static class ToolParams
{
    public const string UseScratchDir =
        "Obsolete: true is the same as level=fullBuildThrowaway on Build; RunTest ignores it.";

    public const string BuildLevel =
        "quickBuild: in-memory diagnostics. fullBuildThrowaway (default): `dotnet build` into .roslynsentinel/build/<instance>/, projects' bin/ untouched. fullBuild: builds the projects' own bin/obj; only this level updates bin/ (isServerBinaryStale).";

    public const string BuildDescription =
        "Compiles the solution. level=quickBuild: in-memory Roslyn diagnostics (fast). Default level=fullBuildThrowaway: runs `dotnet build` (slower; also catches NuGet restore, resource and post-build failures) into a per-server output folder. Success returns the outcome, projects compiled and error/warning counts. Failure returns error counts by project/file/code, the first maxDetails errors (root-cause project first), SuppressedDownstreamErrorCount and OmittedErrorCount; FullDiagnosticsResultId holds all diagnostics (read with GetLargeResult). Output tails and warnings need includeOutput / includeWarnings."; // hint-ok: static [Description]/const text, schema budget

    public const string RunTestDescription =
        "Runs `dotnet test` on the solution or one project. Returns TotalCount/PassedCount/FailedCount/SkippedCount, a FailureSummary grouping failures by shared cause, and a Results list (resultsType, default failed; capped by maxDetails; summary=true omits it). Failed tests carry their captured Output. filter goes to `dotnet test --filter`; an unresolvable filter is a distinct error from zero matches. FullResultsId (read with GetLargeResult) holds untruncated rows; OmittedResultCount counts rows beyond maxDetails."; // hint-ok: static [Description]/const text, schema budget

    public const string ProjectName =
        "Project name returned by Search(mode: symbol) in the projectName field. " +
        "Must match exactly - case-sensitive."; // hint-ok: static [Description]/const text, schema budget

    public const string DocCommentId =
        "Uniquely identifies the symbol across the codebase. " +
        "Use the Search(mode: symbol) tool to obtain this value. " +
        "Do not construct this value - pass it exactly as returned by Search(mode: symbol)."; // hint-ok: static [Description]/const text, schema budget

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
    public const string ContextSnippet = "Short verbatim line from the source, unique in the file; add lineBefore/lineAfter if still ambiguous.";

    public const string LineBefore =
        "Line before contextSnippet.";

    public const string LineAfter =
        "Line after contextSnippet.";

    // ReplaceSnippet has no contextSnippet parameter: its anchors disambiguate oldContent. Applied to the
    // emitted schema by McpToolSchemaPatcher.ApplyReplaceSnippetLimits (top-level and batchEdits items).
    public const string ReplaceSnippetAnchorLine =
        "Optional filter: the single line just before (lineBefore) or just after (lineAfter) oldContent.";

    // ReplaceSnippet match selectors: values are printed by the ambiguity error.
    public const string ReplaceSnippetWithinMember =
        "Name of the member containing the intended oldContent match, as printed by the ambiguity error.";

    public const string ReplaceSnippetMatchLine =
        "1-based line where the intended oldContent match starts, as printed by the ambiguity error. Refers to the file before this call.";

    /*
    public const string OldContent =
        "REQUIRED. Verbatim text to find and replace - copied exactly from a prior tool result " +
        "(ReadFile/GetMethodSource/etc.), not retyped from memory. Matched as a literal substring " +
        "first, falling back to whitespace-normalized matching. Must be short (a startup-configured " +
        "line/char limit, reported in the error if exceeded) - this tool is for small, localized " +
        "edits only. If oldContent matches more than once in the file, use lineBefore/lineAfter to " +
        "disambiguate.";

    */
    public const string OldContent = "Verbatim text to find and replace using literal substring match. Default limit of 2000 characters.";

    /*
    public const string NewContent =
        "REQUIRED. Verbatim replacement text for oldContent. May be empty (pure deletion) or longer " +
        "than oldContent (net insertion), as long as it stays within the size limit (a startup-" +
        "configured line/char limit, reported in the error if exceeded).";
    */
    public const string NewContent = "Verbatim replacement text for oldContent. Default limit of 2000 characters.";

    public const string ContainingTypeName =
        "Name of the type that directly declares the target. Only needed when the target's name AND " +
        "contextSnippet are still ambiguous (e.g. identical members on two sibling types).";

    // Enum value sets
    public const string AccessibilityValues =
        "\"public\"|\"private\"|\"internal\"|\"protected\"|\"protected internal\"|\"private protected\"";

    public const string ListAllKindValues =
        "\"all\"|\"namespace\"|\"class\"|\"interface\"|\"method\"|\"property\"|\"struct\"|\"record\"|\"enum\"|\"enum member\"|\"constructor\"|\"field\"";

    public const string SearchModeValues =
    "\"text\"|\"symbol\"|\"references\"|\"callers\"|\"implementations\"|\"attribute\"|\"all\"|\"namespace\"|\"class\"|\"interface\"|\"method\"|\"property\"|\"struct\"|\"record\"|\"enum\"|\"enum member\"|\"constructor\"|\"field\"";

    public const string SymbolKindFilter =
        "\"type\"|\"method\"|\"property\"|\"field\"|\"event\"|\"any\" (default)";

    public const string AddOrRemoveAction =
        "\"add\"|\"remove\"";

    public const string DiagnosticScope =
        "\"file\" (scopeName = filePath) | \"project\" (scopeName = projectName) | \"solution\" (scopeName ignored)";

    // Transcript review        
    public const string Reason =
        "Why you call this (min 10 chars, has a space).";
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

    // ---- Merged-tool shared text ----
    // In claude-lean, ParameterEdit replaces MethodSignature + ConstructorParameter, and Declaration replaces
    // ModifyModifier + ChangeAccessibility + ModifyAttribute + ModifyBaseType. A clause that both a merged tool and a
    // source tool emit lives here ONCE and is referenced from both, so editing it changes both. The wrapper text around
    // each clause (operation prefixes, per-tool examples) intentionally differs and stays on the tools.
    // MergedToolDescriptionSyncTests pins that both sides still emit these clauses.

    // ParameterEdit <-> MethodSignature / ConstructorParameter: defaultValue = Lead + examples + Tail.
    public const string DefaultValueLead =
        "add only. Default value for the new parameter (e.g. ";

    public const string DefaultValueTail =
        "); omit for a required parameter. To default to null use nullDefault:true, not the string \"null\". Mutually exclusive with nullDefault.";

    // nullDefault: ParameterEdit = Lead + "." + Tail; MethodSignature/ConstructorParameter = Lead + " (use instead of ...)." + Tail.
    public const string NullDefaultLead =
        "add only. Sets the new parameter's default to the null literal";

    public const string NullDefaultTail =
        " Mutually exclusive with defaultValue.";

    // fieldName: ConstructorParameter = "add only. " + clause; ParameterEdit = "constructor, add only. " + clause.
    public const string FieldNameOverride =
        "Overrides the derived field name (_camelCase); paramName and its underscore-prefixed form both resolve to '_paramName'.";

    // operation: the remove clause, followed by " the operation refuse." (MethodSignature) or " it refuse." (ParameterEdit).
    public const string RemoveLastParameter =
        "removes only the LAST parameter, so paramName must match it; positional call sites are updated, but named-argument call sites make";

    // callSiteFixups: Targets, then Keys, then the per-tool middle, then Fallback + " key." (ParameterEdit) or " exact key." (ConstructorParameter).
    public const string CallSiteTargets =
        "(new, target-typed new, `: this(...)`, `: base(...)`)";

    public const string CallSiteKeys =
        "so the parameter can be required instead of defaulted. Key: \"FilePath:Line\" (full path, 1-based line), \"FilePath:*\" (every call site in that file) or \"*\" (every call site);";

    public const string CallSiteFallback =
        "A site with no matching key falls back to defaultValue/nullDefault, else the call is rejected listing each unresolved site's";

    // Declaration <-> ModifyModifier / ModifyAttribute / ModifyBaseType.
    public const string AddStaticUnsupported =
        "auto-properties, init accessors, virtual/override/abstract members, interface implementations, members of generic types";

    // existingAttribute: ModifyAttribute = "The " + clause; Declaration = "attribute: the " + clause.
    public const string AttributeNameClause =
        "attribute to add/replace/remove, with or without [ ] brackets.";

    // newAttribute: ModifyAttribute = "action=replace: " + clause; Declaration = "attribute, action=replace: " + clause.
    public const string NewAttributeClause =
        "the attribute to replace existingAttribute with.";

    // baseTypeName: ModifyBaseType = "The " + clause; Declaration = "baseType: the " + clause.
    public const string BaseTypeNameClause =
        "base type or interface name.";
}
