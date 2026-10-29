# Finding: Engine layer has one dead anonymous-object return and a few `object`-typed result shapes

**Status:** OPEN 2026-10-27. Fixes ranked below; nothing decided or built.

## Context
Engine-scoped audit requested by the user on 2026-10-27, narrowing
[finding_anonymous_and_untyped_return_shapes.md](./finding_anonymous_and_untyped_return_shapes.md)
(2026-10-02, whole solution) to `RoslynSentinel.Engines.Basic` and `RoslynSentinel.Engines.Advanced`.
Method: `Search` text scans for anonymous creation (`new {`, multi-line `new`, `=> new {`), loosely
typed return signatures (`object`, `dynamic`, `*<object>`, `Dictionary<string, object>`), tuple return
signatures and `JsonSerializer.Serialize`; then `ReadFile` on each candidate and a `callers` lookup. The scans
are per-line regex, so every count is a lower bound (a `new` split from `{` across lines with a
comment between is missed). A Roslyn syntax pass was not run.

## What is broken
Result per candidate (method, current return shape, suggested DTO):

| # | Method | Current return shape | Suggested DTO |
| --- | --- | --- | --- |
| 1 | `RenameSymbolResult.ToToolResponse()` (`Engines.Basic/BasicRefactoringEngine.cs:59`) | `string` from `JsonSerializer.Serialize(new { success, oldName, newName, filesChanged, updatedHandle = new { ProjectName, DocCommentId }, note })`. Two anonymous objects on `:61`. 0 callers. | Delete. `RenameSymbolData` (`Tools.Basic/RefactoringStructuralTools.cs:24`) already names every field. A new engine-layer DTO would duplicate it. |
| 2 | `RefactoringToolHelpers.RequireUpdatedText` (`Engines.Basic/RefactoringToolHelpers.cs:66`) | `SentinelCallToolResult<object>?`. Both non-null success branches set `SuccessData = new AppliedChangeSummary(...)`. | Existing `AppliedChangeSummary`: return `SentinelCallToolResult<AppliedChangeSummary>?`. Makes `ToSummaryResult` (`:110`, which casts `SuccessData as AppliedChangeSummary`) unnecessary. |
| 3 | `GuardEngineResult` (`:143`), `GuardNothingToDo` (`:161`), `GuardEngineFailure` (`:182`) | `SentinelCallToolResult<object>`. Success data is a bare `string` message; failures carry `ErrorData` only. | No new DTO. `SentinelCallToolResult<string>` if every success is a message. Shared by `CodeTransformTools` (31 text matches), `RefactoringStructuralImpl` (8) and others, so migrate in slices. |
| 4 | `UnresolvedCallSitesException.StructuredDetail` (`Engines.Basic/ConstructorCallSiteFinder.cs:73`) | `IReadOnlyList<object>?` via `Sites.Cast<object>().ToList()`. Items are `UnresolvedConstructorCallSite`. | Existing `UnresolvedConstructorCallSite`. The `object` is inherited from `ToolException.StructuredDetail` (`Common`), so the fix is a typed or generic base property; `StructuredDetail` appears in 84 matches across 20 files. |
| 5 | `CodeGenerationEngine.GenerateDefaultConfigJsonAsync` (`Engines.Advanced/CodeGenerationEngine.cs:470`) | Local `Dictionary<string, object>` tree serialized into `UpdatedText`. | None. The document shape is driven by config key paths; `object` is legitimate. |

Related, typed but unnamed: 18 engine methods return unnamed tuples, mostly
`Task<(EditOutcome Outcome, string? Message, X Payload)>` (`GetConstructorParametersAsync`,
`GetMethodParametersAsync`, `GetContainerMembersAsync`, `GetMemberSourceAsync`), plus
`ResolveBoolMemberAsync` (2 overloads), `CollectSitesAsync`, `PlanInvertBooleanAsync`, `CommentFileAsync`,
`SeedContentHashesAsync`, `ParseDiagnostics`, `BuildContainerMissDetail`, `ResolveBySnippetWithWarning`,
`EvaluateOutputFreshness`, `AddInstanceMoveStructuralEditsAsync`, `MaterializeDocumentEditsWithAppliedEditsAsync`
and `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:121` (`Match`). Not analysed for DTO fit.

Agreement with the earlier finding: it counted 2 anonymous objects in `Engines.Basic` and listed
`RenameSymbolResult.ToToolResponse` as dead. Both match: the 2 are the nested pair on one line. Its second
dead method, `EngineError.ToToolResponse` (`Common/EngineResultWrapper.cs:126`), was not re-checked here
(a `callers` lookup by bare name returned Ambiguous).

No hits: `dynamic` as a return or parameter type (26 text matches in `PerformanceEngine` and
`SecurityEngine` are analyzer rules, comments and string literals) and `Engines.Advanced` anonymous
returns (11 `new {` matches, all string interpolation or comments).

## Root cause
Not traced beyond the pattern. Hypothesis for 2 and 3: the guards were written to serve tools with
different payload types, so they return the widest type and tools cast. Not verified against each caller.

## Why it matters
`SentinelCallToolResult<object>` hides the payload type from `FindReferences` and from the emitted schema,
and `ToSummaryResult` exists only to recover a type the producer already knew. Dead code (1) keeps a second,
drifting copy of the rename shape alive.

## Recommendation
1. Delete `RenameSymbolResult.ToToolResponse` (decided as safe by 0 callers; confirm with a build).
2. Type `RequireUpdatedText` as `AppliedChangeSummary` and remove `ToSummaryResult`. Open: check every caller
   that passes the result on as `SentinelCallToolResult<object>`.
3. Type the `Guard*` helpers in slices, new overload beside old, old `[Obsolete("Use X")]`, per CLAUDE.md.
   Open: whether `string` or a non-generic result type is the right success shape.
4. Typed `StructuredDetail` on `ToolException` is a `Common` change with a 20-file blast radius; plan it
   separately, not as part of this audit.
5. Decide whether the 18 tuples get an audit of their own (see the earlier finding's item 7 for the outcome-record idea).

## Out of scope
- `Tools.*`, `Server.*` and `Common` anonymous objects: covered by the 2026-10-02 finding.
- Item 5 (`Dictionary<string, object>` config tree): legitimate dynamic shape.
- Test projects.
