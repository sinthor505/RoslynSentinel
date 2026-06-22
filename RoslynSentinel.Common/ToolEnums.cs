using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

// ── Git ───────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitOperation { status, log, diff, stage, add, commit, revert }

// ── Workspace ─────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticScope { file, project, solution }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeaturesAction { list, get, update }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SolutionItemsKind { projects, files, dependencies }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProposedChangeAction { apply, validate }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangesetFormat { files, diff }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StagedChangeAction { apply, get, validate, discard }

// ── Symbols ───────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SymbolKindFilter { type, method, property, field, @event, any }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InspectSymbolAspect { info, blastRadius }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindUsagesSearchKind { implementorsOf, attributeUsages, objectCreations, extensionsFor, typesWithAttribute, methodsByReturnType }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindReferencesKind { callers, implementations }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypeInfoInclude { hierarchy, members, both }

// ── Refactoring ───────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AddRemoveAction { add, remove }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttributeModifyAction { add, replace, remove }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypedMemberKind { property, field }

// ── Documentation ─────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocAction { read, write, append, list }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocType { plan, handoff, completed_work, documentation, state }

// ── Asyncify ──────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AsyncMigrationPattern { AsyncBridgeCandidate, HandlerExtractCandidate, HandlerToAsyncCandidate, AsyncCallerUpliftCandidate }
