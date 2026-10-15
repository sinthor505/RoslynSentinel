# Plan: Replace Dictionary<FilePathWrapper, string> with a named DocumentChangeSet type

**Status:** DRAFT 2026-10-14. Introduce `DocumentChangeSet` in Common and migrate the write chokepoint first; status stays on the wrapping result types, not on the map.

## Problem

The codebase passes document path -> content maps as raw `Dictionary<FilePathWrapper, string>`.
Counts from a 2026-10-14 survey (`Search(mode: text)` line matches, so approximate):

- 254 production lines in 36 files (Common 27, Engines.Basic 65, Engines.Advanced 53, Tools.Basic 64,
  Tools.Advanced 45), plus 53 in tests. About 189 are `new Dictionary<FPW, ...>` expressions.
- Only about 6 `Dictionary<string, string>` sites are really path -> content
  (`ValidateAndApplyHelper.cs:170`, `TestCategoryApplyEngine.ApplyProjectAsync`,
  `WholeFileWriteTools.cs:307`, plus the `Dictionary<string, string?>` pre-image family).
- The type says nothing about meaning, and the key-type conversions repeat:
  `ValidateAndApplyHelper.cs:39` (`.Keys.Select(k => (string)k)` twice), `:172`
  (`dict[(string)key] = value`), `ValidationEngine.cs:99`, `TestCategoryApplyEngine.cs:159`.
- Mixed key types in one flow: input is `<FPW, string>`, but `ReadBeforeTextsAsync`
  (`ValidateAndApplyHelper.cs:190`) and `ApplyChangesResult.PreImages` are `<string, string?>`.
- Comparer mismatch (observed, effect untraced): `PersistentWorkspaceManager.cs:1557` and
  `ValidateAndApplyHelper.cs:195` build `new Dictionary<string, string?>()` with no comparer
  (case-sensitive), while `FilePathWrapper` equality is `OrdinalIgnoreCase` on `Absolute`
  (`FilePathWrapper.cs:25`).
- Many result types already carry the map next to their status fields: `ApplyChangesResult`
  (`FailedFiles` :22, `PreImages` :26), `AppliedChangeSummary.ChangedContent` (:17,47),
  `DocumentEditResult.Changes` (:25), `ChangeSignatureResult`, `RenameSymbolResult.PendingChanges`,
  `MoveMemberResult`, `SemanticReplaceOutcome`. No dedicated type exists.

## Decision

1. Add `sealed class DocumentChangeSet : Dictionary<FilePathWrapper, string>` in `RoslynSentinel.Common`.
   Deriving from `Dictionary` means every consumer that takes `Dictionary<FPW, string>` keeps
   compiling, so migration is incremental. Producers change to `new DocumentChangeSet()`.
2. **Do not implement `IResultStatus` on it.** A change set is data, not an outcome; `IsError` would
   be always false (as on `AppliedChangeSummary`). Status stays on the result types that wrap it
   (about 30 already implement `IResultStatus`).
3. The type owns the repeated helpers: `ToStringKeyed()`, `Paths`, `IsEmpty`, `Merge`.
4. Pre-images (`string?` values) get their own small type or a nullable-value sibling; do not force
   them into `DocumentChangeSet`. Its comparer is `OrdinalIgnoreCase`, matching `FilePathWrapper`.
5. Wire types stay `Dictionary<string, string>`. A `Dictionary<FilePathWrapper, ...>` or custom type
   in a tool parameter makes `JsonSchemaExporter` emit an unrepresentable `true` schema node that
   LM Studio rejects (`WholeFileWriteTools.cs:334-338`). `DocumentChangeSet` never appears in a
   `[McpServerTool]` parameter.
6. A status-bearing `ChangeSetResult : IResultStatus` (change set + failures + status) is deferred
   to a later step; see Risks.

## Execution rules

- Dispatch each step to `implementer` (3 or fewer .cs files per slice) or `implementer-senior`
  over the limit; measure `Call sites:` with `InspectSymbol(aspect: blastRadius)` before briefing.
- C# edits are sequential, never parallel (shared server process, `SessionHalted` risk).
- `Build` 0 errors after every step. Run `RunTest` over Tests.Basic and Tests.Advanced after
  steps 2, 3 and 5 and compare against `reference_known_failing_tests`; report only new failures.
- Verify with `build.ps1 -Flavor Solution` at the end of each phase (Basic and Advanced are additive).
- Commit per step, `Git` scope `listed`, explicit file list.
- Check `git log` and `docs/current/CLOSED.md` before starting; another session may have touched
  these files (see memory `project_concurrent_sessions`).

## Steps

### Step 1 - Add DocumentChangeSet
- Files: `RoslynSentinel.Common/DocumentChangeSet.cs` (new)
- Change: sealed class deriving `Dictionary<FilePathWrapper, string>`; constructors for empty, copy
  and `IEnumerable<KeyValuePair<...>>`; `ToStringKeyed()` (case-insensitive result), `Paths`,
  `IsEmpty`, `Merge`. Add unit tests for each helper and for case-insensitive key equality.
- Done when: Build 0 errors; new tests pass; no production caller changed yet.

### Step 2 - Migrate the write chokepoint
- Files: `IWorkspaceMutator.cs`, `PersistentWorkspaceManager.cs` (`ApplyProposedChangesAsync` :1439,
  `PendingChangeset` :102, `CachePendingChangeset` :110, `TakePendingChangeset` :129,
  `_failedChangesCache` :45), `ValidateAndApplyHelper.cs`
- Change: parameter and field types become `DocumentChangeSet`. Replace the `(string)k` conversions
  (`ValidateAndApplyHelper.cs:39,172`) with `ToStringKeyed()`. `ValidateAndApplyHelper.cs:160`
  (`IReadOnlyDictionary`) and `StoreChangedContentAsync` follow.
- Done when: Build 0 errors; Tests.Basic and Tests.Advanced show no new failures. Callers that still
  pass a plain `Dictionary` fail to compile, so list them in the brief (47 direct
  `ApplyProposedChangesAsync` call sites in 9 files: AsyncifyTools 18, AsyncBatchEngine 11,
  WholeFileWriteTools 5, WorkspaceFileEditImpl 4, WorkspaceTools 3, MsToolAugmentEngine 2,
  CommentingTools 2, ValidateAndApplyHelper 1, WorkspaceProjectManagementImpl 1). Split into
  sub-slices if over three files.

### Step 3 - Migrate ValidationEngine and the 8 ValidateAndApplyAsync callers
- Files: `ValidationEngine.cs` (overloads at :79, :89, :126), then callers:
  `RefactoringStructuralImpl.cs:64`, `RefactoringSignatureImpl.cs:56`,
  `AdvancedRefactoringTools.cs:95`, `GenerationTools.cs:70`, `RefactoringExtractionDocsImpl.cs:52`,
  `SemanticFindReplaceTools.cs:96`, `TestCategoryApplyEngine.cs:147`, `NamedArgumentsTools.cs:135`
- Change: parameter types and local constructions become `DocumentChangeSet`; `ValidationEngine.cs:99`
  uses `ToStringKeyed()`. One slice per three files.
- Done when: Build 0 errors; tests as in Execution rules.

### Step 4 - Pre-image type and comparer fix
- Files: `ApplyChangesResult.cs:26`, `PersistentWorkspaceManager.cs:1557`,
  `ValidateAndApplyHelper.cs:35,190,195,280`
- Change: introduce the pre-image sibling type (case-insensitive comparer) and replace the
  `Dictionary<string, string?>` sites. First trace whether any lookup currently relies on
  case-sensitive keys (see Risks); if one does, record it and keep that site as is.
- Done when: Build 0 errors; a test proves two paths differing only in case resolve to one entry.

### Step 5 - Migrate result types that carry the map
- Files (one or two per slice): `ApplyChangesResult.cs`, `AppliedChangeSummary.cs`,
  `DocumentEditResult.cs`, `BasicRefactoringEngine.cs` (`ChangeSignatureResult`, `RenameSymbolResult`),
  `ConstructorCallSiteFinder.cs:61`, `NamedArgumentsEngine.cs:19`, `SemanticReplaceTypes.cs:64`,
  `MemberRefactoringEngine.cs:13` (`MoveMemberResult`), `StaticConversionEdits.cs:17`,
  `OutParamRefactoringEngine.cs:17`, the `AsyncOptimizationEngine.cs` records (:1657, :2303, :3599)
- Change: property types become `DocumentChangeSet`. Replace the tuples carrying the map
  (`SemanticReplaceEngine.cs:1232`, `CommentingEngine.cs:68`, `MemberRefactoringEngine.cs:3844`,
  `IWorkspaceMutator.cs:38`) with named records where practical.
- Done when: Build 0 errors; JSON output of `AppliedChangeSummary` is byte-identical before and
  after (snapshot test, see Risks).

### Step 6 - Opportunistic engine migration
- Files: the remaining roughly 150 `new Dictionary<FPW, string>` expressions in Engines.* and Tools.*
- Change: mechanical replacement as each file is next touched, or in sequential batches of three
  files. Not required for the chokepoint to benefit.
- Done when: `Search(mode: text, query: "new Dictionary<FilePathWrapper, string>")` returns zero in
  production code, or the remainder is listed with a reason.

## Out of scope

- Making `DocumentChangeSet` or `FilePathWrapper`-keyed maps a tool parameter or wire type.
- Implementing `IResultStatus` on the change set itself.
- The call-site fixup maps (`CallSiteFixupMap`, `MoveMember` and `ConstructorParameter` parameters),
  path -> metadata maps (`SymbolRelationshipImpl.cs:179`) and unrelated `string -> string` maps
  (`ToolArgumentValidator`, `FailureRouter`, MSBuild properties).
- Test files, except where a signature change forces an edit.

## Risks and open decisions

- **Inheritance vs wrapper (open):** deriving from `Dictionary` is pragmatic but exposes the full
  mutable API. A non-inheriting wrapper is cleaner but forces a big-bang migration. Decision needed
  before Step 1; this plan assumes inheritance.
- **`ChangeSetResult : IResultStatus` (deferred):** whether to add it depends on how many of the
  results in Step 5 repeat the same `Changes` + `Error` pair. Decide after Step 5, with evidence.
- **Comparer change is a behaviour change (unverified):** switching pre-image maps to
  `OrdinalIgnoreCase` could alter behaviour if a lookup depends on case-sensitive keys. Not traced;
  Step 4 begins with that trace.
- **JSON serialization (unverified):** `AppliedChangeSummary.ChangedContent` serializes with FPW keys
  via `FilePathJsonConverter` (`ReadAsPropertyName` / `WriteAsPropertyName`), while
  `StoreChangedContentAsync` copies to string keys (`ValidateAndApplyHelper.cs:170-175`) for an
  undocumented reason. Confirm a derived `Dictionary` serializes identically, and check the
  `OperationBlobWriter` and large-result paths, which the survey did not cover.
- **Reflection and schema:** if any result type that holds the map is emitted in a tool output
  schema, confirm the schema is unchanged after Step 5.
- **Caller count unreliable:** `Search(references, callers)` on `ApplyProposedChangesAsync` returned
  784 callers across 127 files, inconsistent with the 47 text-search sites. Re-measure with
  `InspectSymbol(aspect: blastRadius)` before each brief.
- **Concurrency:** about 36 files are touched. Other sessions may edit them; re-check `git status`
  before each dispatch.
