# Plan: Replace Dictionary<FilePathWrapper, string> with a named DocumentChangeSet type

**Status:** APPROVED 2026-11-21. Add `DocumentChangeSet` in Common, widen the write chokepoint to `IReadOnlyDictionary` (no caller edits), then retype result records and producers in slices.

## Problem

The codebase passes document path -> content maps as raw `Dictionary<FilePathWrapper, string>`.
Re-measured 2026-11-21 (`Search(mode: text)` line matches, so approximate):

- 300 production lines in 49 files (Common 43/9, Engines.Basic 84/20, Engines.Advanced 51/8,
  Tools.Basic 77/8, Tools.Advanced 45/4), plus 137 in tests. 195 are `new Dictionary<FPW, string>`
  expressions in 32 production files, plus 119 in tests.
- The type says nothing about meaning. No existing type plays this role (the nearest is the private
  `PendingChangeset` record, `PersistentWorkspaceManager.cs:119`).
- The `(string)k` key conversions repeat: `ValidateAndApplyHelper.cs:118,198,476`.
- Mixed key types in one flow: input is `<FPW, string>`, but `ReadBeforeTextsAsync`
  (`ValidateAndApplyHelper.cs:403`), `ApplyChangesResult.PreImages` (:35) and `PostImages` (:42) are
  `<string, string?>`.
- Comparer mismatch: `PersistentWorkspaceManager.cs:2438` (pre-images), `:2577` (post-images) and
  `ValidateAndApplyHelper.cs:408` build `new Dictionary<string, string?>()` with no comparer
  (case-sensitive), while `FilePathWrapper` equality is `OrdinalIgnoreCase` (`FilePathWrapper.cs:131`
  `Equals(object)`, `:158` `GetHashCode`). Traced 2026-11-21: no lookup relies on case-sensitivity
  (every lookup key is a `FilePathWrapper` converted with `(string)`/`ToString()`, the overlap check at
  `PersistentWorkspaceManager.cs:2338` uses `FilePathWrapper` equality). Test code was not traced.
- Result types carry the map next to their status fields: `ApplyChangesResult` (`FailedFiles` :31),
  `AppliedChangeSummary.ChangedContent` (:113), `DocumentEditResult.Changes` (:15),
  `ChangeSignatureResult`, `RenameSymbolResult.PendingChanges`, `MoveMemberResult`,
  `SemanticReplaceOutcome` and others (Step 6).
- `IWorkspaceMutator.ApplyProposedChangesAsync` has 197 call sites in 9 projects (47 production in
  10 files, 151 in tests; `InspectSymbol` blastRadius). Retyping its parameter to a new type would force
  all of them; the chokepoint never mutates the map, so widening to `IReadOnlyDictionary` forces none.
  Precedent: `EditImpactGate`, `ValidateAndApplyHelper.DetachChangedFiles` (:380) and
  `NearMissCorrection.SpansExplainCorrectedText` already take `IReadOnlyDictionary<FPW, string>`.

## Decision

1. Add `sealed class DocumentChangeSet : Dictionary<FilePathWrapper, string>` in `RoslynSentinel.Common`
   (owner, 2026-11-21: derived Dictionary over a wrapper or no type). Existing consumers keep
   compiling; producers change to `new DocumentChangeSet()`. No custom comparer argument: the key
   type's own equality is already `OrdinalIgnoreCase`.
2. Helpers on the type: `Paths` and `IsEmpty`, plus `Merge`. No `ToStringKeyed()`: its consumer
   (`StoreChangedContentAsync`) was removed in 7f00ecb9.
3. **Do not implement `IOperationResult` on it** (the interface formerly named `IResultStatus`). A change
   set is data, not an outcome. Status stays on the result types that wrap it.
4. Chokepoint parameters widen to `IReadOnlyDictionary<FilePathWrapper, string>` (owner, 2026-11-21:
   widen over retype). Zero caller edits; `Dictionary` and `DocumentChangeSet` both convert implicitly.
5. Pre-image and post-image maps keep `IReadOnlyDictionary<string, string?>` and gain
   `StringComparer.OrdinalIgnoreCase` at their three construction sites (owner, 2026-11-21: comparer only,
   no new type).
6. Wire types stay `Dictionary<string, string>`. A `Dictionary<FilePathWrapper, ...>` or custom type
   in a tool parameter makes `JsonSchemaExporter` emit an unrepresentable `true` schema node that
   LM Studio rejects (`WholeFileWriteTools.cs:450-452`, `BatchTypes.cs:229`). `DocumentChangeSet` never
   appears in a `[McpServerTool]` parameter.
7. `AppliedChangeSummary.ChangedContent` is in ModifyModifier's published output schema
   (`ModifyModifierResultEnvelope`, `RefactoringStructuralTools.cs:61,210`). Before retyping it, a test
   pins its JSON shape and the output schema (Step 5).
8. A status-bearing `ChangeSetResult : IOperationResult` is deferred; see Risks.
9. Owner away for the run (2026-11-21): mid-run decisions are deferred to the tracker.

## Execution rules

- DTO hook: `DocumentChangeSet` fits no DTO role; its creating call carries
  `DTO-EXEMPT: collection type, no wire presence`.
- Dispatch per `dispatch-implementer`: `implementer` at D1 (3 files, 15 edits, 35 tool calls) or
  `implementer-sweep` at S0 (8 files, 25 call sites, 2 projects). Measure producers with
  `InspectSymbol(aspect: blastRadius)` before each brief.
- At most two implementing agents at once, on disjoint files (trial rule in `dispatch-implementer`).
- `Build` 0 errors after every slice. Filtered detached test runs per slice; one full run per batch
  (`reference_autonomous_run_conventions.md`). Baseline is 0 failures.
- Verify with `scripts/build.ps1 -Flavor Solution` at the end (Basic and Advanced are additive).
- Commit per slice, `Git` scope `listed`, explicit file list, `Plan: plan_document_changeset_type`
  trailer above `Co-Authored-By`.
- Re-check `Git(status)` before each dispatch; other sessions may touch these files.

## Steps

### Step 1 - Add DocumentChangeSet
- Files: `RoslynSentinel.Common/DocumentChangeSet.cs` (new), `RoslynSentinel.Tests.Basic/DocumentChangeSetTests.cs` (new)
- Depends on: none
- Effect: text-neutral (new type, no callers)
- Change: sealed class deriving `Dictionary<FilePathWrapper, string>`; constructors for empty,
  `IDictionary<FilePathWrapper, string>` copy and `IEnumerable<KeyValuePair<FilePathWrapper, string>>`;
  `IReadOnlyCollection<FilePathWrapper> Paths`, `bool IsEmpty`, `void Merge(IReadOnlyDictionary<FilePathWrapper, string> other)`
  (later entries win). Tests: each helper; two paths differing only in case are one key; System.Text.Json
  serialization of a `DocumentChangeSet` equals that of a plain `Dictionary<FPW, string>` with the same
  entries (`SharedJsonOptions.Compact`).
- Done when: Build 0 errors; the new tests pass; no production caller changed.

### Step 2 - Widen ValidationEngine and ValidateAndApplyHelper parameters
- Files: `RoslynSentinel.Common/ValidationEngine.cs`, `RoslynSentinel.Common/ValidateAndApplyHelper.cs`
- Depends on: none (pairs with Step 1: disjoint files)
- Effect: text-neutral
- Change: every `Dictionary<FilePathWrapper, string>` parameter in these two files becomes
  `IReadOnlyDictionary<FilePathWrapper, string>` (ValidationEngine overloads near :79, :89, :137, :150 and
  `ValidateChangesWithCandidateAsync`; the ValidateAndApplyHelper entry points). Update the
  `<see cref>`s that spell `Dictionary{FilePathWrapper, string}`. Where a body hands the map to a member
  that still requires `Dictionary`, leave that call and widen only if the callee is in these files;
  otherwise add `// TODO widen <callee>` and copy with `new Dictionary<...>(changes)`.
- Done when: Build 0 errors; no caller file edited.

### Step 3 - Widen the write chokepoint
- Files: `RoslynSentinel.Common/IWorkspaceMutator.cs`, `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Tests/Fakes/FakeWorkspaceManager.cs`
- Depends on: Step 2
- Effect: text-neutral
- Change: `ApplyProposedChangesAsync` (interface; PWM :2223 and core :2248; fake :158) takes
  `IReadOnlyDictionary<FilePathWrapper, string>`. Also widen `RevalidateAfterForeignWriteAsync` (:2965)
  and `CachePendingChangeset` (:127) if they only read the map. `_failedChangesCache` (:48) stays a
  `ConcurrentDictionary`. The `IWorkspaceMutator.cs:66` tuple and `TakePendingChangeset` (:146) keep their
  types in this step.
- Done when: Build 0 errors; no caller file edited.

### Step 4 - Case-insensitive pre-image and post-image maps
- Files: `RoslynSentinel.Common/PersistentWorkspaceManager.cs`, `RoslynSentinel.Common/ValidateAndApplyHelper.cs`, one test file in `RoslynSentinel.Tests.Basic`
- Depends on: Step 3 (shared files)
- Effect: behaviour-changing (minor): a lookup with a differently cased path now hits instead of missing
- Change: add `StringComparer.OrdinalIgnoreCase` at `PersistentWorkspaceManager.cs:2438` and `:2577`
  and `ValidateAndApplyHelper.cs:408`.
- Done when: Build 0 errors; a test proves two paths differing only in case resolve to one pre-image entry.

### Step 5 - Pin the AppliedChangeSummary wire shape
- Files: `RoslynSentinel.Tests.Basic/AppliedChangeSummaryTests.cs` (or a new sibling file), and the
  ModifyModifier output-schema guard next to `RoslynSentinel.Tests.Server/StructuredContentDataTagTests.cs:175`
- Depends on: none (test-writer; pairs with Step 2 or 3)
- Effect: text-neutral (tests only)
- Change: a test that serializes a populated `AppliedChangeSummary` and compares the JSON to a pinned
  string; a test that pins the ModifyModifier output schema node for `ChangedContent`.
- Done when: both tests pass on the current code.

### Step 6 - Retype result records and their producers
- Slices (each: property/record element type becomes `DocumentChangeSet`, and every producer that builds
  the value in a plain `Dictionary` changes to `new DocumentChangeSet()`; measure producers first and
  split if over D1):
  - 6a Engines.Basic: `BasicRefactoringEngine.cs` (`ChangeSignatureResult`, `RenameSymbolResult`),
    `MemberRefactoringEngine.cs` (`MoveMemberResult` :21; tuples :4255, :4331), `NamedArgumentsEngine.cs:19`
  - 6b Engines.Basic: `SemanticReplaceTypes.cs:70` (+ tuple `SemanticReplaceEngine.cs:1237`),
    `StaticConversionEdits.cs:17`, `ConstructorCallSiteFinder.cs:81`
  - 6c Engines.Advanced: `OutParamRefactoringEngine.cs:18`, `AsyncOptimizationEngine.cs` (:1746, :2404,
    :3682), `CommentingEngine.cs:68` tuple
  - 6d Common: `AppliedChangeSummary.cs` (:113), `DocumentEditResult.cs` (:15),
    `ApplyChangesResult.cs` (`FailedFiles` :31 only if it is path -> content); then `IWorkspaceMutator.cs:66`
    tuple and `TakePendingChangeset`
- Depends on: Step 1; 6d also on Steps 3-5. 6a/6c pair (different projects); 6b/6d pair.
- Effect: text-neutral (CLR type only; wire JSON unchanged)
- Change: as above. Tuples keep their shape (only the element type changes).
- Done when: Build 0 errors per slice; the Step 5 tests still pass after 6d.

### Step 7 - Remaining construction sites
- Files: the remaining production `new Dictionary<FilePathWrapper, string>` expressions (about 195 before
  Step 6), grouped by project
- Depends on: Step 1
- Effect: text-neutral
- Change: `implementer-sweep` slices at S0 (8 files, 25 sites), one repeated `ReplaceSnippet` batch pattern
  `new Dictionary<FilePathWrapper, string>(` -> `new DocumentChangeSet(` plus the declared local type where
  it is spelled out. Re-measure the inventory first and derive the slice map from it.
- Done when: `Search(mode: text, query: "new Dictionary<FilePathWrapper, string>")` returns zero in
  production code, or the remainder is listed here with a reason.

## Out of scope

- Making `DocumentChangeSet` or `FilePathWrapper`-keyed maps a tool parameter or wire type.
- Implementing `IOperationResult` on the change set itself.
- Retyping the 151 test call sites or test-local dictionaries, except where a signature change forces it.
- Replacing map-carrying tuples with named records (separate DTO decision).
- The call-site fixup maps (`CallSiteFixupMap`, `MoveMember` and `ConstructorParameter` parameters),
  path -> metadata maps and unrelated `string -> string` maps (`ToolArgumentValidator`, `FailureRouter`,
  MSBuild properties).

## Risks and open decisions

- **`ChangeSetResult : IOperationResult` (deferred):** decide after Step 6 whether enough results repeat
  the same `Changes` + error pair; record the evidence here.
- **Derived-type serialization (unverified):** System.Text.Json should treat a `Dictionary` subclass as a
  dictionary, and no `JsonSerializerContext` exists in production; Step 1's serialization test checks it.
  `DetachedPayloads.IsMapLike` (`is IDictionary`) still matches the derived type.
- **Output schema:** `JsonSchemaExporter` output for `ChangedContent` after 6d is unverified; Step 5 pins it.
- **Step 7 tooling:** no semantic "retype locals" tool; a `ReplaceSnippet` batch pattern is the named
  operation. If it cannot express a site, report `TOOL-GAP:` rather than hand-editing.
- **Concurrency:** about 50 files are touched over the run. Re-check `Git(status)` before each dispatch.
