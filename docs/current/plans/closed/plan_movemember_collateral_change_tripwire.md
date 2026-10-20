# Plan: collateral-change tripwire for MoveMember (item B)

**Status:** CLOSED 2026-10-19 (implemented 2026-10-15; risk outcomes recorded below; only dry-run edits remain, tracked in the issue). Design decided with the user (tripwire, MoveMember only); B1 c27896cc (CollateralChangeGuard, CollateralChangeRefused), B2 74895c51 (expectedEdits in ValidateAndApplyAsync), B3 7e2d04fd (MoveMemberResult.AppliedEdits forwarded by the tool), B1t 83df2fdf (guard-fires battery test). Facts below were traced by exploration agents earlier in this session and not all were re-read when this was written; re-anchor with `Search` before dispatching.

## Problem

The write chokepoint refuses only one kind of collateral damage: a changed line-ending style on an existing `.cs` file
(`EolChangeGuard`, called from `ValidateAndApplyHelper.ValidateAndApplyAsync`, ~`RoslynSentinel.Common/ValidateAndApplyHelper.cs:47`,
with a backstop in `PersistentWorkspaceManager.ApplyProposedChangesAsync`). Nothing refuses a file that is reformatted
while keeping its EOL style. The compile gate does not help (an unresolved cref is only warning CS1574). `LineChanges`
makes the damage visible but refuses nothing.

Origin: the MoveMember bug that produced a 1178-line diff for a one-method move
(`docs/current/blockers/resolved/blocking_error_movemember_reformats_entire_source_and_caller_files.md`). It was fixed by
building minimal `TextChange`s against the original `SourceText` (`RoslynSentinel.Engines.Basic/MoveMemberTextEdits.cs`),
but nothing stops a later change from re-adding a whole-file normalizer after those edits are applied.

## Decision

A **tripwire**, MoveMember only. The engine reports the `TextChange`s it applied; the write chokepoint recomputes
"pre-image plus those edits" and refuses when the text it is about to write differs.

- `MoveMemberResult` gains the `TextChange`s each route applied (per file).
- `ValidateAndApplyAsync` gets an optional expected-edits parameter. Right after the pre-image read (next to the EOL guard)
  it applies the edits to the pre-image and compares the result with the proposed text. A mismatch returns a refusal with
  a new `ToolErrorCode.CollateralChangeRefused`, naming the file and the first differing line.
- No allowance formula (no "N lines more than declared" threshold): exact equality, or refusal.
- Dry runs may return the edits instead of the full text. Real applies already drop `ChangedContent`
  (`AppliedChangeSummary`, `ChangedContentResultId`).

**What it does not do (stated so nobody over-trusts it):** it catches post-processing applied after the engine's
`ApplyChanges` (for example a re-added whole-document `Formatter` or `NormalizeWhitespace` pass), not a wrong edit set, because the text and the
edits come from the same source. It is partly circular by design.

## Execution rules

- Dispatch sequentially, never in parallel. One `Build`/`RunTest` at a time.
- Measure call sites with `InspectSymbol(aspect: blastRadius)` before each brief; read tier limits from
  `.claude/implementer-tiers.json` at dispatch time.
- New method parameters go before `CancellationToken`. Note: the current `ValidateAndApplyAsync` signature reportedly also
  has `deletePaths` and `describeValidationFailure` after `cancellationToken` (agent report, not re-read); keep the new
  parameter optional and place it per the CLAUDE.md rule, and check the call-site count first.
- Never leak exception text into `ResultError`; the guard returns a structured error.
- B3 touches `MemberRefactoringEngine.cs`, as do the D2 slices and Q3 of the cref plan. Land B3 first.

## Steps

### Step 1 - B1: CollateralChangeGuard and error code (implementer-senior)
- Files: `RoslynSentinel.Common/CollateralChangeGuard.cs` (new), the file declaring `ToolErrorCode` (locate with
  `Search`), a new test file in the Common test project.
- Change: a pure function `Check(preImage, proposedText, edits)` returning ok or a failure carrying the file path and the
  first differing line number and text (cap the quoted text; no source dumps). Add `ToolErrorCode.CollateralChangeRefused`
  with its tool-boundary mapping and message conventions (see `docs/current/proposals/proposal_tool_error_code_taxonomy*`
  if present; check `ToolErrorCode` consumers for exhaustive switches).
- Done when: Build 0 errors; unit tests cover identical, differing in the middle, differing at EOF (final newline),
  CRLF versus LF pre-image, overlapping and adjacent edits, empty edit list.

### Step 2 - B2: wire the guard into ValidateAndApplyAsync (implementer, or senior if call sites exceed the tier)
- Files: `RoslynSentinel.Common/ValidateAndApplyHelper.cs`, plus its callers only if the new parameter is not optional.
- Change: optional `IReadOnlyDictionary<FilePathWrapper, IReadOnlyList<TextChange>>? expectedEdits` (name to taste). When
  non-null, after the pre-image read and before the compile gate, run the guard per file that has an entry. Files with no
  entry are unchecked. Refusal path mirrors the EOL guard's.
- Done when: Build 0 errors; the existing helper tests unchanged; a test shows a mismatching edit set is refused with
  `CollateralChangeRefused` and nothing is written, and a matching set passes.

### Step 3 - B3: MoveMemberResult and route capture, tool forwarding (implementer-senior)
- Files: the file declaring `MoveMemberResult`, `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`,
  `RoslynSentinel.Tools.Advanced/AdvancedRefactoringTools.cs` (the MoveMember tool, ~line 543-552).
- Change: capture the applied `TextChange`s at each place the engine calls `ApplyChanges`
  (`MoveMemberTextEdits.cs:200-204`) for the four routes: base-type, existing-class, new-class, and instance (which goes
  through `MaterializeDocumentEditsAsync`, `MemberRefactoringEngine.cs:3978-3994`, keyed by `DocumentId`, so map to file
  paths). Return them on `MoveMemberResult`; the tool passes them to `ValidateAndApplyAsync`.
- Done when: Build 0 errors; an integration test per route (static to existing class, static to new class, base type,
  instance) shows the real apply passes the guard; `MoveMemberPreservesUntouchedTextTests` still green.

### Step 4 - B1t: tripwire test (test-writer)
- Files: a test in `RoslynSentinel.Tests.Battery.Basic`.
- Change: prove the guard fires through the real MoveMember path. Needs a seam that simulates a post-edit normalizer
  (for example a test-only wrapper that alters the proposed text before the helper runs). Decide the seam in the brief; do
  not add a production flag for it.
- Done when: the test fails if the guard is removed and passes with it.

## Out of scope

- Other tools (Member, MethodSignature, and so on). Extend only after the MoveMember tripwire has run for a while.
- An allowance formula or line-count threshold.
- Adopting `LineChanges` at the remaining `AppliedChangeSummary` sites (separate TODO item).
- Catching a wrong edit set. That needs the byte-exact tests in `MoveMemberPreservesUntouchedTextTests`.

## Risks and open decisions - outcomes (verified against source 2026-10-19)

- **New-class route:** decided to skip. The new file is in `result.Changes` with its full text but not in `AppliedEdits`
  (`MemberRefactoringEngine.cs` ~4248-4257, record doc); the helper only checks files that have an entry and a pre-image.
  Covered by `StaticMethodToNewClass_RealApplyPassesTheCollateralGuardAndLeavesTheNewFileUncheckedAsync`.
- **Edit positions:** resolved. `MoveMemberTextEdits.ApplyChanges` delegates to `CollateralChangeGuard.ApplyEdits`, so the
  engine and the guard share one implementation.
- **Caller-file rewrites:** resolved. Every route records edits per touched document (callers included), keyed by file path;
  the instance route maps `DocumentId` to path in `MaterializeDocumentEditsWithAppliedEditsAsync`.
- **Signature:** resolved (optional `expectedEdits` on `ValidateAndApplyAsync`).
- **Error code taxonomy:** resolved. `ToolErrorCode` is a class of string constants (no exhaustive switches); the
  `FromErrorCode` default maps to `Failed`, and `IsRefusalThatMustNotBeMasked` includes the code.
- **Dry run edits:** NOT done and not planned here. The guard runs on dry runs, but the result still returns full text
  (`ChangedContent`). Tracked in `issues/issue_movemember_open_followups.md` (collateral-change item).
- **Extending to other tools:** still out of scope; revisit once the MoveMember tripwire has run for a while.

Line references in the Steps above are historical (the tool call is now near `AdvancedRefactoringTools.cs:489`).
