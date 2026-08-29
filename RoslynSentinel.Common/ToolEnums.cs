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
    projects, files, dependencies, solutionItems
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProposedChangeAction
{
    apply, validate,

    /// <summary>
    /// Confirms and applies a changeset that a prior <c>apply</c> call rejected for exceeding
    /// the whole-file-rewrite size threshold (see <see cref="ToolErrorCode.ConfirmationRequired"/>).
    /// Pass only <c>confirmationCode</c> — the original changeset is cached server-side from the
    /// rejected call, so <c>changes</c>/<c>filepath</c>/<c>unifiedDiff</c> do not need to be resent.
    /// </summary>
    confirmationCode
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

// ── Refactoring ───────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AddRemoveAction
{
    add, remove
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
