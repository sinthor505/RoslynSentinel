# Step 2.3 — Eval prompt/fixture updates + Phase 2 tests

**This step implements only this file** (see "Files this step touches" below). Do not read,
open, or act on any other plan step file (e.g. via `ProjectDoc`) — another process runs each step
as its own isolated task.

## Prior state

Steps 2.1-2.2 already landed: the `TriviaEditIntent`-based fix, the shared `EolUtilities`, and the
log-only post-write invariant check. The solution builds clean and manual repro confirms both the
doc-comment-preservation and EOL-normalization fixes work. No Phase 2 tests have been added yet.

## Context

**Files this step touches:**
- The eval system prompt (locate at implementation time — not pinned down in advance)
- The `PlanImplementVerify` eval fixture (locate at implementation time)
- `RoslynSentinel.Tests.Basic/CodeEditingTests.cs`

**Corrected vs. an earlier draft of this work:** test coverage for `ConstructorParameter`
removal must go through its **real** code path: `RemoveConstructorParameterAsync` →
`ReplaceNodeFormattedAsync` (full class-body rewrite) — **not** `RemoveNodeFormattedAsync`, which
`RemoveConstructorParameterAsync` never calls. The true second `RemoveNodeFormattedAsync` call
site is `RemoveUsingDirectiveAsync` (~line 2065), which needs its own new small test.

## Task

### A. Eval prompt and fixture

1. Narrow the eval system prompt's normalization clause to scope it to trailing
   whitespace/indentation only, and state explicitly that comment/doc-comment/blank-line removal
   is a defect to report, not excuse. Find the actual prompt text first (search for terms like
   "normalize" or "incidental whitespace" in the eval test project) before editing it.
2. Convert one CRLF-only eval fixture to LF — the `PlanImplementVerify` fixture. Identify the
   exact fixture file (search the `RoslynSentinel.Tests.ModelEval` fixtures directory) before
   editing it.

### B. Confirm existing tests still pass

Read and re-run (don't just assume) these two existing tests, which should be unaffected or
positively confirmed by this phase's changes:
- `CodeEditingTests.cs` — `ChangeAccessibility_DoesNotReformatUnrelatedMembers` (~line 590)
- `CodeEditingTests.cs` — `SummaryComment_EnumMember_ViewAndRemoveRoundtrip` (~line 1009) — this
  is the regression guard for `RemoveSummaryCommentAsync`, the one call site whose trivia
  behavior deliberately changed (`ReplaceLeading` intent).

### C. New tests

Add to `CodeEditingTests.cs` (or a suitable new file if that one is already very large):

1. `ChangeAccessibility` on a member with a doc comment → doc comment survives byte-identical.
   **This is the single most important new test — it's the eval-caught bug, made permanent.**
2. Same for `AddModifierAsync`/`RemoveModifierAsync` if step 2.1's live repro showed they're
   independently affected by the same bug (check its "Prior state"/reported findings, or
   re-derive by reading `RefactoringEngine.cs` if that's not available to you).
3. Blank-line/sibling preservation via a mainstream `ReplaceNodeFormattedAsync` call site (e.g.
   `Member` replace), confirming the new default doesn't regress ordinary formatting.
4. `ConstructorParameter` removal's actual behavior via its real path
   (`RemoveConstructorParameterAsync` → `ReplaceNodeFormattedAsync`, full class-body rewrite) —
   same blank-line/sibling assertion style as test 3 above. This replaces an earlier, incorrect
   plan to test this against `RemoveNodeFormattedAsync` directly.
5. Small regression test for `RemoveUsingDirectiveAsync` (~line 2065) — the true second
   `RemoveNodeFormattedAsync` call site, currently untested. Assert clean removal with correct
   surrounding trivia/EOL.
6. EOL homogeneity: one CRLF-dominant fixture through a representative edit → zero stray LF in
   output; one LF-dominant fixture → zero stray CRLF.

## Gate — Phase 2 gate

Run `RoslynSentinel.Tests.Basic` and `RoslynSentinel.Tests.Battery` via the `RunTest` MCP tool.
Both must be green. Full solution build clean. Zero regressions versus Phase 1's gate.

Report the gate results above and stop — do not proceed to any other step.
