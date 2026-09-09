# Eval Defect Remediation v2 — Implementation Plan

## Context

The 2026-09-08 eval corpus (`docs/current/spec-eval-defect-remediation-v2.md`) surfaced three substrate-side defects, all in the same family: the server hands the model a verdict it cannot verify, and twice actively contradicted evidence the model had already noticed.

1. **`Build`'s `quickBuild` fabricates success.** `BuildEngine.RunQuickBuildAsync` hardcodes `ExitCode: -1` and derives `BuildSucceeded` from `errorCount == 0` with no check that anything was actually compiled — a build of zero projects reports as a clean pass. A strong model (`qwen3.6-35b-a3b`) caught the contradiction and second-guessed itself into trusting the lie anyway; a weaker model wouldn't even notice.
2. **The shared member-rewrite path (`ReplaceNodeFormattedAsync`) silently destroys XML doc comments and injects a stray CRLF into LF files.** This is a same-day regression: commit `208a1c4` (2026-09-07 00:21) made the helper unconditionally transplant old trivia onto rewritten nodes; commit `1d2d0bf86` (23 hours later) replaced that with a `Count > 0 ? new : old` heuristic to stop it from fighting `RemoveSummaryCommentAsync`'s intentional doc-comment stripping — and that heuristic is what the eval caught losing doc comments on `ChangeAccessibility`. Neither commit shipped a test.
3. **Two working server-side detectors (the orientation breaker, `DiffHunkAnalyzer`) fire correctly and are then logged and thrown away.** Nothing reaches the MCP wire. The model in the corpus recovered from the orientation-breaker case by guessing right; that's model competence covering an architecture gap, not evidence the gap is fine.

This plan corrects several of the spec's own implementation-detail guesses using live-codebase exploration done before planning (call-site counts, which type is actually on the wire, which method a tool truly calls) — those corrections are called out inline. It also resolves the ambiguities the spec explicitly left open, per decisions already made: `Findings` lands on `ToolResult<T>` (the real wire type) with `DiffEngine.ApplyDiff`'s signature changed as a narrow, explicit exception to the engine-signature freeze; `Directive` gets an **additive** `DirectiveKind` enum alongside the existing prose string (not a lossy replacement, since two of its three call sites carry real free-form guidance text); and spec test 2.9 is redirected to `ConstructorParameter`'s real code path with a new small test added for the call site the spec mis-attributed.

Per `feedback_dogfood_mcp_blocking_errors.md`, all reads/writes during implementation go through the RoslynSentinel MCP tools (`ReadFile`, `ApplyDiff`/`ApplyUnifiedDiff`/`WriteFile`, `Member`, `ModifyModifier`, etc.), not plain Read/Edit/Bash — any tool failure/gap during this work is itself a blocking finding to document and stop on, not something to route around.

Three phases, strictly sequenced with a build-clean gate between each (per the spec's own Sequencing section): **Phase 1 → gate → Phase 2 → gate → Phase 3 → gate**. Do not batch phases.

---

## Phase 0 — Baseline

Run the full test suite once before touching anything (`RunTest` MCP tool, or per-project) to record a clean pass count. Every later gate compares against this.

---

## Phase 1 — `Build` must not fabricate a verdict

**Files:** `RoslynSentinel.Common/BuildResult.cs`, `RoslynSentinel.Common/EngineResultWrapper.cs`, `RoslynSentinel.Basic/BuildEngine.cs`, `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (`Build` tool, ~line 1093), `RoslynSentinel.Tests.Battery/BatteryTwentyTests.cs` (~line 602).

### 1.1 — Reshape `BuildResult`

Replace the boolean verdict with a 3-state outcome and make `ExitCode` honest:

```csharp
public enum BuildOutcome { Succeeded, Failed, NotRun }

public record BuildResult(
    BuildOutcome Outcome,
    BuildVerifyLevel Level,
    List<string> ProjectsCompiled,
    bool DiagnosticsComplete,
    int ErrorCount,
    int WarningCount,
    List<DiagnosticInfo> Errors,
    List<DiagnosticInfo> Warnings,
    List<DiagnosticGroupSummary> ErrorSummary,
    List<DiagnosticGroupSummary> WarningSummary,
    string? StdoutTail,
    string? StderrTail,
    TimeSpan Duration,
    int? ExitCode = null,
    string? Detail = null);
```

`BuildSucceeded` is removed entirely (not redefined — a model that learned to read it must get a missing-field error, not stale semantics). `ExitCode` is `null` unless `Level`'s build actually spawned a process (i.e., only `RunFullBuildAsync`'s path populates it). `ProjectsCompiled` is the load-bearing new field — the substrate stating what it actually looked at.

The type name `BuildResult` stays the same, so the three pass-through consumers (`DiagnosticSummary.BuildVerification`, `DiagnosticsSummaryResult.BuildVerification`, `WorkspaceHealthReport.BuildVerification` — all `BuildResult?`, no field access) need no changes. Confirm this with a search sweep before calling Phase 1 done (1.5).

### 1.2 — `EngineResultWrapper<T>` / `EngineErrorCode` additions

Add `EngineErrorCode.BuildNotRun` to the existing 3-value enum. Add one small additive static helper — **no `.Failure(error, payload:)` factory exists today** (the spec's own code sketch calls a method that isn't real; only the 3-arg constructor `(EngineOutcome, T?, EngineError?)` exists):

```csharp
public static EngineResultWrapper<T> Failure(EngineOutcome outcome, EngineError error) => new(outcome, default, error);
```

Purely additive — no existing call site changes. Do not add `Findings` here yet; that's Phase 3.1's job, kept isolated so this shared type isn't touched twice across phases for unrelated reasons.

### 1.3 — The actual fix: `BuildEngine.RunQuickBuildAsync` (lines 18-77)

Gate on an empty compilation set, as the first check after resolving scope and before any error/warning tallying:

```csharp
if (projectsCompiled.Count == 0)
{
    return EngineResultWrapper<BuildResult>.Failure(
        EngineOutcome.InvalidInput,
        new EngineError(
            $"Quick build compiled zero projects. Scope '{scope}' with scopeName '{scopeName}' resolved to nothing — no compile verdict is available.",
            code: EngineErrorCode.BuildNotRun));
}
```

`EngineOutcome.InvalidInput` is used (not `Failure`/`InternalError`) because this is a caller-input problem — the requested scope doesn't resolve — not an engine crash. Otherwise derive `Outcome` from `ErrorCount` (`0` → `Succeeded`, `>0` → `Failed`); the `NotRun` case never reaches a constructed `BuildResult` — it short-circuits via the wrapper-level failure above instead.

`RunFullBuildAsync` (lines 83-202) is **not part of this defect** — it genuinely spawns `dotnet build` and reads a real `process.ExitCode`. Leave its logic alone; only adapt its result construction to the new field names (`Outcome` derived from `process.ExitCode == 0`, real non-null `ExitCode`, `DiagnosticsComplete: true`, populate `ProjectsCompiled` from whatever scope it already resolved).

### 1.4 — `BuildNotRun`'s hint

Hand-construct the hint directly in `Build`'s tool method rather than routing through `FailureRouter`. `FailureRouter` is keyed on a `FailureReason` enum scoped specifically to Asyncify transform failures, and its `ToolGraph` is only reflection-scanned against `SentinelAsyncifyTools` — `Build` lives in `SentinelWorkspaceTools`, entirely outside that scan. Forcing this through the Asyncify-specific router would mean expanding its scanned-type list for one well-understood failure mode; not worth it.

For Phase 1, fold the guidance into the existing error message text (mention `ListAll(kind: "all")` explicitly) rather than building a full `ToolHint` object — defer full hint-object wiring to Phase 3's `Findings` channel, which is the mechanism actually built to carry this kind of thing to the wire. Leave a one-line comment noting that.

### 1.5 — Call-site sweep

Before declaring Phase 1 done, search the whole solution for `BuildResult` construction and `.BuildSucceeded`/`.ExitCode` reads to confirm nothing outside `BuildEngine.cs` and the three known pass-through holders touches the old shape.

### 1.6 — Test updates (`BatteryTwentyTests.cs:602-650+`)

- `Build_QuickBuild_CleanSource_ReturnsSuccess` **currently asserts `ExitCode == -1` as documented-correct behavior** — this directly contradicts the fix and must be rewritten to assert `Outcome == BuildOutcome.Succeeded`, `ExitCode == null`, `ProjectsCompiled` non-empty.
- The other two tests in that block (`...SourceWithCompileError...`, `...RepeatedDiagnosticAcrossFiles...`) cast to `BuildResult` and read `.BuildSucceeded` — update to `.Outcome`.
- `GetDiagnostics_VerifyQuickBuild_AttachesBuildVerification` (line ~652) — read its body first; likely a mechanical field-name update since it only touches `BuildVerification` as pass-through.

### 1.7 — New tests

1. Zero-project scope → `ToolResult` non-success, error message references `ListAll`.
2. Clean solution, quick build → `Outcome == Succeeded`, `ProjectsCompiled` non-empty, `ExitCode == null`.
3. Solution with a genuine compile error → `Outcome == Failed`, `ErrorCount > 0`, `ProjectsCompiled` still non-empty (compiled, just had errors — must not collapse into `NotRun`).
4. Full build smoke test — confirm `ExitCode` still real, `Outcome` derives correctly (regression guard that `RunFullBuildAsync` wasn't disturbed).
5. New engine-level test (e.g. `RoslynSentinel.Tests.Battery/BuildEngineTests.cs`, matching `BatteryTwentyTests.cs` conventions) calling `RunQuickBuildAsync` directly with a manufactured zero-project scope, asserting `EngineOutcome.InvalidInput` / `EngineErrorCode.BuildNotRun` at the engine layer, below the MCP bridge.

### Phase 1 gate

`RoslynSentinel.Tests.Battery` + `RoslynSentinel.Tests.Basic` green, full solution build clean, zero regressions vs. Phase 0 baseline.

---

## Phase 2 — Trivia and EOL preservation in the shared member-rewrite path

**Files:** `RoslynSentinel.Basic/RefactoringEngine.cs` (`ReplaceNodeFormattedAsync` lines 62-81, `RemoveNodeFormattedAsync` lines 94-129, and callers), `RoslynSentinel.Common/DiffEngine.cs` (lines 85-93, dominant-EOL logic to extract/reuse), new `RoslynSentinel.Common/EolUtilities.cs`, new small type for trivia-edit intent, `RoslynSentinel.Tests.Basic/CodeEditingTests.cs`.

**Corrected vs. the spec:** `ReplaceNodeFormattedAsync` has **28 call sites**, not 23. `RemoveNodeFormattedAsync` has exactly 2 call sites: `RemoveMemberAsync` (line 1303, matches spec) and **`RemoveUsingDirectiveAsync` (line 2065)** — the spec wrongly attributes line 2065 to `ConstructorParameter` removal. `RemoveConstructorParameterAsync` never calls `RemoveNodeFormattedAsync` at all; it rebuilds the whole containing class and calls `ReplaceNodeFormattedAsync` (line 3960). Test 2.9 is corrected accordingly (see 2.6.4/2.6.5 below).

### 2.0 — Mandatory live repro before writing any fix

Reproduce the `ChangeAccessibility` doc-comment-loss bug live first. Static reading doesn't fully explain it: `ChangeAccessibilityAsync`/`AddModifierAsync`/`RemoveModifierAsync` only set `.WithTrailingTrivia(SyntaxFactory.Space)` on individual *new modifier tokens* — none of them explicitly touch the member's own leading trivia (where the doc comment lives), so it's not obvious from the source alone why the `Count > 0` branch ever fires away from `oldNode`. Confirm the actual mechanism (debugger or a targeted test) before finalizing 2.1/2.2 — this determines whether a safe default alone fixes all three modifier-editing methods, or whether one or more of them additionally needs an explicit "carry forward old leading trivia" step.

### 2.1 — Replace the `Count > 0` heuristic with an explicit intent signal

```csharp
public enum TriviaEditIntent
{
    PreserveOld,     // default: oldNode's leading trivia wins
    ReplaceLeading,  // newNode's leading trivia wins — the caller deliberately rewrote it
}
```

Trailing trivia always follows `oldNode` regardless of intent — none of the 28 call sites deliberately rewrites trailing trivia the way `RemoveSummaryCommentAsync` rewrites leading trivia, so it doesn't need its own axis.

### 2.2 — `ReplaceNodeFormattedAsync` signature change

Add `TriviaEditIntent triviaIntent = TriviaEditIntent.PreserveOld` as a new optional parameter. Because it defaults to `PreserveOld`, **none of the 27 other call sites need to change** — they get old-node trivia preservation unconditionally, which is what they actually want (and were only getting by accident under the old heuristic). **Only `RemoveSummaryCommentAsync` (line 3367) needs a one-line change**: pass `triviaIntent: TriviaEditIntent.ReplaceLeading` at its existing call, since it deliberately rewrites the node's leading trivia to strip the doc comment.

If 2.0's repro shows one of the three modifier methods needs more than the new default (e.g. it must explicitly re-attach `oldNode`'s leading trivia onto a freshly-synthesized `newNode` that never carried it), add that as a targeted one-line fix at that specific call site — do not change the other 26.

`RemoveNodeFormattedAsync` gets the same EOL-normalization treatment (2.3) at its single return point; it has no equivalent trivia heuristic to replace.

### 2.3 — EOL detection/normalization at the write point

Extract `DiffEngine.cs:85-93`'s dominant-EOL logic into a shared `RoslynSentinel.Common/EolUtilities.cs` (`DetectDominantEol(SourceText)`, `NormalizeEol(string content, string dominantEol)`), and have `DiffEngine.cs` call the extracted version instead of its inline block. Duplicating this logic across two engines is exactly the drift this spec is trying to eliminate.

Wire it into `ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync`: compute the dominant EOL from the **original document's pre-edit `SourceText`** (not the formatter's output), then normalize the final returned string against it before returning.

### 2.4 — Post-write invariant check (log-only for this phase)

Add a check after each write: (a) EOL homogeneity in the final text, (b) if `triviaIntent == PreserveOld` and `oldNode` had non-whitespace leading trivia, confirm the result still has it. Emit via `ILogger` only in this phase (matching how `DiffEngine.cs` already logs `DiffHunkAnalyzer` findings today without a wire channel) — full `Finding`-object wiring depends on Phase 3.1's type, which doesn't exist yet and shouldn't be pulled forward just for this. Leave a one-line comment pointing at Phase 3.1. Factor the check as a small pure function so Phase 3 can unit-test it directly once wired.

### 2.5 — Eval prompt and fixture

- Narrow the eval system prompt's normalization clause to scope it to trailing whitespace/indentation only, and state explicitly that comment/doc-comment/blank-line removal is a defect to report, not excuse. Locate the actual prompt text at implementation time (not pinned down during exploration).
- Convert one CRLF-only eval fixture to LF (the `PlanImplementVerify` fixture, per the spec) so 2.3 has fixture coverage; identify the exact fixture file at implementation time.

### 2.6 — Tests

Existing tests to verify still pass: `CodeEditingTests.cs:590` (`ChangeAccessibility_DoesNotReformatUnrelatedMembers`), `CodeEditingTests.cs:1009` (`SummaryComment_EnumMember_ViewAndRemoveRoundtrip` — the regression guard for the one call site whose behavior changes).

New tests:
1. `ChangeAccessibility` on a member with a doc comment → doc comment survives byte-identical. This is the single most important new test — it's the eval-caught bug, made permanent.
2. Same for `AddModifierAsync`/`RemoveModifierAsync` if 2.0 shows they're independently affected.
3. Blank-line/sibling preservation via a mainstream `ReplaceNodeFormattedAsync` call site (e.g. `Member` replace), confirming the new default doesn't regress ordinary formatting.
4. **Corrected 2.9**: `ConstructorParameter` removal's actual behavior via its real path (`RemoveConstructorParameterAsync` → `ReplaceNodeFormattedAsync`, full class-body rewrite) — same blank-line/sibling assertion style as test 3 above, not an assertion about `RemoveNodeFormattedAsync`.
5. **New**: small regression test for `RemoveUsingDirectiveAsync` (line 2065) — the true second `RemoveNodeFormattedAsync` call site, currently untested — assert clean removal with correct surrounding trivia/EOL.
6. EOL homogeneity: one CRLF-dominant fixture through a representative edit → zero stray LF in output; one LF-dominant fixture → zero stray CRLF.

### Phase 2 gate

`RoslynSentinel.Tests.Basic` + `RoslynSentinel.Tests.Battery` green, full solution build clean, zero regressions vs. Phase 1's gate.

---

## Phase 3 — Route suppressed findings to the model

**Files:** new `RoslynSentinel.Common/Finding.cs`, `RoslynSentinel.Common/EngineResultWrapper.cs`, `RoslynSentinel.Common/ToolResult.cs`, `RoslynSentinel.Common/DataTag.cs`, `RoslynSentinel.Server.Basic/WorkspaceReadNavigationImpl.cs` (~line 510), `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (orientation breaker, ~line 1972), `RoslynSentinel.Common/DiffEngine.cs` (`ApplyDiff`, lines 53-70), `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` (`ApplyUnifiedDiff`, lines 542-660), `RoslynSentinel.Common/OperationSummary.cs`, `BatchTypes.cs`, `BreakerStatusReport.cs`, `RoslynSentinel.Server.Advanced/SentinelAsyncifyTools.cs`.

**Corrected vs. the spec:** `EngineResultWrapper<T>` is **not** the MCP wire type — `ToolResult<T>` is. Every tool method manually bridges the two by hand today (see `GetDiagnostics` as the representative pattern); there is no automatic adapter. `ApplyUnifiedDiff` never touches `EngineResultWrapper<T>` at all — `DiffHunkAnalyzer`'s report is fully swallowed inside `DiffEngine.ApplyDiff` (returns bare `SourceText`), so surfacing it requires changing that method's signature. Per prior decision, this is an explicit, narrow exception to the Do-Not list's engine-signature freeze, scoped only to adding the diff report to the return value.

### 3.0 — `DirectiveKind`: additive, not a replacement

`Directive` is a bare `string` in three places, but they aren't equivalent: `OperationSummary.Directive` is a short single sentence; `BatchResultSummary.Directive` and `BreakerStatusReport.Directive` carry long free-form prose authored at ~15 call sites in `SentinelAsyncifyTools.cs`. Per decision: add a **new** `DirectiveKind` enum property alongside the existing string on all three types — don't replace the string, don't drop the prose.

```csharp
public enum DirectiveKind { Proceed, ReviewRequired }
```

- `OperationSummary`: add `DirectiveKind`, derive it in `FromCounts` from `Outcome` (`CompletedFully`/`CompletedWithNoOps` → `Proceed`; `PartialProgress`/`NoProgress`/`NothingToDo` → `ReviewRequired`) rather than taking it as a new parameter.
- `BatchResultSummary` (`BatchTypes.cs`): add `DirectiveKind` (default `Proceed`); classify each of the ~15 authoring sites in `SentinelAsyncifyTools.cs` individually by reading its message (precondition-failure/empty-target messages → `ReviewRequired`, genuine success → `Proceed`).
- `BreakerStatusReport.cs`: add `DirectiveKind` as a new record parameter; update its (expected small number of) construction sites positionally.

This mirrors the existing pattern already used for `BatchResultSummary.Severity` (a keyed field living next to human-readable prose, per its own doc comment) — not a new convention.

### 3.1 — `Finding` type, `ToolResult<T>`/`EngineResultWrapper<T>` additions

```csharp
public sealed record Finding(string Source, string Message, FindingSeverity Severity = FindingSeverity.Info);
public enum FindingSeverity { Info, Caution, Warning }
```

(`Finding`/`Findings`, not `OperationFinding` — the orientation breaker and `DiffHunkAnalyzer` findings aren't coupled to `OperationSummary`'s Asyncify-specific outcome machinery, so borrowing that name would imply a relationship that doesn't exist.)

Add `IReadOnlyList<Finding> Findings { get; init; } = []` to both `EngineResultWrapper<T>` and `ToolResult<T>` — purely additive, no existing constructor call site breaks.

**Bridge-site sweep**: grep for every manual `EngineResultWrapper<T>` → `ToolResult<T>` bridge (pattern: `TryGetData` followed by `new ToolResult<`) and copy `Findings` across at each one. Do this sweep first, before 3.3/3.4, so the two concrete producers below have somewhere to land.

Also add `DirectiveKind DirectiveKind { get; init; } = DirectiveKind.Proceed` to `ToolResult<T>` itself, parallel to `Findings` — `ApplyUnifiedDiff` (3.4) needs a place to put it and `ToolResult<T>` doesn't carry any directive-like field today.

### 3.2 — `DataTag.SearchPattern`

Add it to the enum. If, once 3.3 is implemented, nothing actually consumes it, drop it rather than leaving dead enum surface — don't add it speculatively beyond what 3.3 needs.

### 3.3 — Wire the orientation breaker into the triggering call itself

The breaker's trip counter (`PersistentWorkspaceManager.RecordSearchOutcome`) is driven by an MCP request filter that parses `SearchSolutionText`'s *already-serialized* JSON response for a `totalRecords` field — it isn't called from inside `SearchSolutionText`/`WorkspaceReadNavigationImpl` at all today. That's why the corpus showed byte-identical `NoMatches` prose on the exact call that tripped the breaker: the trip message only appears as a pre-check short-circuit on the *next* call, after the damage (three burned turns) is already done.

Fix: call the breaker's trip-check from inside `WorkspaceReadNavigationImpl`'s search implementation itself, immediately after the match count is known, so the **triggering call's own result** — including its `NoSearchMatchesException` catch path (line ~510-536) — carries a `Finding` (`Source: "OrientationBreaker"`) with the `ListAll`-mentioning hint text, not just a bare `NoMatches` message. Consider having `RecordSearchOutcome` return a "just tripped" signal so the filter and this new in-line check consume one shared source of truth instead of two independently-derived trip detections (avoiding exactly the kind of divergence this whole spec is about).

Also add `ListAll` to the base `NoSearchMatchesException` hint text unconditionally (today it mentions `LocateSymbol`/`GetFileOutline`/`ProjectDoc` but not `ListAll`, even though `ListAll` is what actually worked in the corpus) — independent of whether the breaker has tripped.

Extend the existing `OrientationBreakerFilterTests.cs` (already covers filter-level end-to-end behavior — don't duplicate it) with a test driving 3 consecutive zero-match `SearchSolutionText` calls and asserting the **3rd call's own result** carries the finding, not a 4th call.

### 3.4 — Wire `DiffHunkAnalyzer` out of `DiffEngine.ApplyDiff`

Change `DiffEngine.ApplyDiff`'s return type from bare `SourceText` to a small wrapping record:

```csharp
public sealed record DiffApplyResult(SourceText Text, DiffReport Report);
```

The method's internals are otherwise unchanged — it already computes `DiffHunkAnalyzer.Analyze(unifiedDiff)` and logs when `HasFindings`; now it also returns the report instead of discarding it. Update `ApplyDiff`'s callers (grep for all of them; `ApplyUnifiedDiff` at `SentinelWorkspaceTools.cs:588` is the Phase-3-relevant one) to unwrap `.Text` where they currently use the return value directly.

In `ApplyUnifiedDiff`, when constructing the final `ToolResult<object>` (line ~612), populate `Findings` from the report when `HasFindings` (`Source: "DiffHunkAnalyzer"`), and set `DirectiveKind = ReviewRequired` — the diff still applies (`Success = true`), but the model is told to re-read rather than trust, matching the spec's own recommendation.

New tests in a new `RoslynSentinel.Tests.Basic/DiffEngineTests.cs` (keep separate from `DiffHunkAnalyzerTests.cs`, which tests the analyzer in isolation — this file tests the plumbing that surfaces its output): clean diff → `Findings` empty, `DirectiveKind == Proceed`; malformed-header diff (reuse the existing analyzer test fixture) run through the full `ApplyUnifiedDiff` path → `Findings` non-empty, `Success == true`, `DirectiveKind == ReviewRequired`.

### Phase 3 gate

`RoslynSentinel.Tests.Basic` + `RoslynSentinel.Tests.Battery` + `RoslynSentinel.Tests.Asyncify` (the `DirectiveKind` migration touches `OperationSummary`, which `OperationOutcomeRoutingTests.cs` builds against) green, full solution build clean, zero regressions vs. Phase 2's gate.

---

## Verification

- After each phase's file changes: `Build` (fullBuild, via the MCP tool itself — dogfooding the very tool Phase 1 fixes) must report zero errors.
- Run each phase's named test projects via the `RunTest` MCP tool and confirm the phase's new/updated tests pass and nothing else regresses.
- Phase 1: manually invoke `Build` via MCP with a deliberately-bad `scopeName` and confirm the response no longer claims success.
- Phase 2: manually invoke `ChangeAccessibility` via MCP on a real member with a doc comment (in a scratch file) and diff the before/after text to confirm the doc comment survives.
- Phase 3: manually drive 3 consecutive zero-match `SearchSolutionText` calls via MCP and confirm the 3rd response itself carries the finding.
- Per `feedback_dogfood_mcp_blocking_errors.md`: if any MCP tool call during this work fails, returns wrong data, or is unreachable, stop, write `docs/current/blockers/blocking_error_<slug>.md`, and end the turn rather than falling back to non-MCP edits or routing around it.
