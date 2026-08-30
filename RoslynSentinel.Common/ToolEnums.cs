using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

// ── Git ───────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitOperation
{
    status, log, diff, stage, add, commit, revert
}

// ── Workspace ─────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToolScope
{
    file, project, solution
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeaturesAction
{
    list, get, update
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SolutionItemsKind
{
    projects, files, dependencies, solutionItems,
    /// <summary>Aggregates projects, solutionItems, and every project's files and dependencies in one call. Ignores projectName.</summary>
    all
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProposedChangeAction
{
    apply, validate,

    // confirmationCode was removed from the live ApplyDiff tool — it reliably caused model
    // hallucination (agents fabricated a confirmationCode and called action=confirmationCode
    // even when the real problem was unrelated; see docs/current/overnight-run-2026-08-30.md
    // section 5b) and was never used correctly in practice. Commented out (not deleted) so the
    // value can't appear in ApplyDiff's JSON schema at all, while keeping the old mechanism
    // available to reintroduce later — see the commented-out ApplyDiffWithConfirmationCode in
    // SentinelWorkspaceTools.cs, which depends on this value and is commented out alongside it.
    // confirmationCode

}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangesetFormat
{
    files, diff
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextSearchMode
{
    regex, literal
}

// ── Symbols ───────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SymbolKindFilter
{
    type, method, property, field, @event, any
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InspectSymbolAspect
{
    info, blastRadius
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindUsagesSearchKind
{
    implementorsOf, attributeUsages, objectCreations, extensionsFor, typesWithAttribute, methodsByReturnType
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindReferencesKind
{
    callers, implementations, all
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypeInfoInclude
{
    hierarchy, members, both
}

// ── ListAll ───────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ListAllKind
{
    all, @namespace, @class, @interface, method, property, @struct, record, @enum,
    [JsonStringEnumMemberName("enum member")] enumMember,
    constructor, field,
}

// ── Refactoring ───────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AddRemoveAction
{
    add, remove
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessibilityLevel
{
    @public, @private, @internal, @protected,
    [JsonStringEnumMemberName("protected internal")] protectedInternal,
    [JsonStringEnumMemberName("private protected")] privateProtected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttributeModifyAction
{
    add, replace, remove
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypedMemberKind
{
    property, field
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AddRemoveViewAction
{
    add, remove, view
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MemberAction
{
    add, remove, replace, view
}

// ── Documentation ─────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocAction
{
    read, write, append, list
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocType
{
    plan, handoff, completed_work, documentation, state
}

// ── Asyncify ──────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AsyncMigrationPattern
{
    AsyncBridgeCandidate, HandlerExtractCandidate, HandlerToAsyncCandidate, AsyncCallerUpliftCandidate
}

// ── Build ─────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BuildVerifyLevel
{
    noBuild, quickBuild, fullBuild
}

// ── Content hashing ───────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentHashPurpose
{
    Comment
}
