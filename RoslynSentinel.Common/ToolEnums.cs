using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

// ── Git ───────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitOperation
{
    status, log, diff, stage, add, unstage, commit, revert
}

/// <summary>
/// Which files a stage/commit operation acts on. Replaces the former <c>stageAll</c> boolean, which
/// could silently override an explicit <c>files</c> list (a <c>files</c>+<c>stageAll:true</c> call
/// ran <c>git add -A</c> and staged unrelated untracked files). Scope and file list are now one
/// decision: <see cref="listed"/> is the only value that reads <c>files</c>, and combining
/// <c>files</c> with any other scope is rejected rather than silently resolved.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitStageScope
{
    /// <summary>Stage modifications/deletions of already-tracked files only (<c>git add -u</c>). New untracked files are NOT staged.</summary>
    tracked,

    /// <summary>Stage every change in the working tree, including untracked files (<c>git add -A</c>). Ignores <c>files</c> — passing both is an error.</summary>
    all,

    /// <summary>Stage exactly the paths named in <c>files</c>, untracked ones included (<c>git add -- &lt;paths&gt;</c>). Requires <c>files</c>.</summary>
    listed
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
public enum WriteFileOperation
{
    CreateFile, ReplaceFile
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MatchKind
{
    Literal, Regex
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NewTypeKind
{
    @class, record, @interface, @enum, @struct, staticClass
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

// Deliberately excludes accessibility keywords (public/private/internal/protected/...) so it is
// impossible to pass one to ModifyModifier — use ChangeAccessibility/AccessibilityLevel instead.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NonAccessibilityModifier
{
    @virtual, @abstract, @sealed, @static, @readonly, @override, @partial, @async, @new, @extern, @unsafe, @volatile
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExtractAsType
{
    @interface,
    @partialClass,
    @superclass
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntroduceAsType
{
    @localVariable, @field, parameter, @constant
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

// ── RunTest ───────────────────────────────────────────────────────────────────

public enum TestOutcome
{
    Passed, Failed, Skipped, NotExecuted
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestResultsFilter
{
    all, failed, skipped
}

// ── Content hashing ───────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentHashPurpose
{
    Comment
}
// Added by AddTopLevelType (expected - used for diagnostics)
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InsertionMemberKind
{
    field, constructor, destructor, property, @event, method, nestedtype
}
// Added by AddTopLevelType (expected - used for diagnostics)
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SyncInterfaceAction
{
    implement, sync, verify
}
// Added by AddTopLevelType (expected - used for diagnostics)
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InlineKind
{
    method, variable, field, parameter
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodemodKind
{
    add_benchmark_stub, generate_constructor, generate_decorator_class, generate_equality_overrides,
    generate_fluent_builder, generate_path_driven_tests, generate_repository_interface,
    generate_test_scaffold, generate_test_skeleton, generate_to_string_safe
}
