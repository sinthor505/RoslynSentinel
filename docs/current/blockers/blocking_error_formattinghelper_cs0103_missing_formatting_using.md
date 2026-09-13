# CS0103 `'Formatter' does not exist` blocks the entire solution build — `RoslynSentinel.Common/FormattingHelper.cs`

**Status:** AMENDED 2026-09-12 — the missing-using fix below is correct as far as it goes, but it was
**not sufficient to make the build succeed**, and applying it surfaced two further, more serious
findings not reflected in the original version of this doc. See "Amendment" section at the bottom
before treating this as closed. Left as a writeup per CLAUDE.md's standing convention that any
unhandled `CS####` surfacing during automated/agent work gets a blocker doc immediately.

**Discovered:** 2026-09-12, while supervising a PlanStepRunner resume (run
`20260912-223709-796`, steps 5-11) — surfaced by `roslynsentinel-planstep.ps1`'s pre-flight base-repo
build check, before any worktree was created and before any model turn was spent.

## What was being attempted

Resuming PlanStepRunner steps 5-11 of run `20260912-223709-796`. The harness runs a pre-flight
build of the base repo (master) before creating a step's worktree, to confirm master itself is in a
buildable state prior to branching off it.

## The exact error (verbatim)

```
C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\FormattingHelper.cs(58,34): error CS0103: The name 'Formatter' does not exist in the current context [C:\Users\Administrator\source\repos\RoslynSentinel\RoslynSentinel.Common\RoslynSentinel.Common.csproj]
```

Harness output:

```
The build failed. Fix the build errors and run again.
PlanStepRunner exited with code 1
```

## Where it happened

- File: `RoslynSentinel.Common/FormattingHelper.cs:58` (the `Formatter.FormatAsync(...)` call site).
- Project: `RoslynSentinel.Common/RoslynSentinel.Common.csproj`.
- Tool/step: `roslynsentinel-planstep.ps1` pre-flight build check, run `20260912-223709-796`, before
  steps 5-11 (no worktree existed yet; this predates any per-step model turn).

## Root cause — confirmed by reading the file and the introducing commit's diff, not assumed

`RoslynSentinel.Common/FormattingHelper.cs` is a brand-new 109-line file introduced by commit
`255363d7ee7b9e2e401cb791dbb8e87358c752c6` ("Refactor: Remove ReplaceNodeFormattedAsync method from
multiple engines", 2026-09-12T19:27:33-07:00, author wc). That commit consolidated a duplicated
`ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync` pair — plus a new `TriviaEditIntent` enum,
itself added one commit earlier by `85341e94aebd6dc93489f197e84cde19ef2bd6a9` ("Fix
ChangeAccessibility silently dropping leading doc comments") — out of roughly ten separate engine
classes in `RoslynSentinel.Basic` (`RefactoringEngine.cs`, `CodeFlowEngine.cs`, `CodeStyleEngine.cs`,
`GranularRefactoringEngine.cs`, `ImmutabilityEngine.cs`, `InstrumentationEngine.cs`,
`MsToolAugmentEngine.cs`, `ProjectStructureEngine.cs`, `SemanticRefactoringLibrary.cs`,
`StandardRefactoringEngine.cs`, `SyntaxUpgradeEngine.cs`, `ThreadSafetyEngine.cs`) into one shared
`internal class FormattingHelper` in `RoslynSentinel.Common`.

Every one of those original engine files had `using Microsoft.CodeAnalysis.Formatting;` (needed
because the method body calls `Formatter.FormatAsync(...)`), and the commit correctly removed each
engine's now-dead copy of that using along with the extracted method body.

But the new consolidated `RoslynSentinel.Common/FormattingHelper.cs` was created with only
`using Microsoft.CodeAnalysis;` at its top (line 1) — the `using Microsoft.CodeAnalysis.Formatting;`
needed for the `Formatter.FormatAsync` call at its line 58 was never added to the new file. Classic
drop-during-extraction: the using lived on the *old* files being deleted from, not the *new* file
being created, and nothing carried it over.

This is a one-line missing-using regression, not a design defect. `RoslynSentinel.Common`'s project
reference already brings in whatever NuGet package provides `Microsoft.CodeAnalysis.Formatting` —
other symbols from `Microsoft.CodeAnalysis` already resolve fine in the same file per its existing
line-1 using — so this is purely the missing using directive, not a missing package reference or a
dependency-direction problem.

## Impact

Blocked the entire solution build: `RoslynSentinel.Common` is a dependency of `RoslynSentinel.Basic`
per this repo's one-way dependency direction (`Common ← Basic ← Advanced`), so the failure
propagated up through every downstream project. In turn this blocked PlanStepRunner's pre-flight
build check for run `20260912-223709-796` steps 5-11 before any worktree was created — i.e. before
any model turn was spent on those steps. No model-facing turn budget was consumed by this defect.

## What's confirmed vs. not applicable here

- **Confirmed by direct file read:** `RoslynSentinel.Common/FormattingHelper.cs:2` now reads
  `using Microsoft.CodeAnalysis.Formatting;`, added immediately below the pre-existing
  `using Microsoft.CodeAnalysis;` at line 1.
- **Correction (see Amendment):** the claim "no other lines in the file were touched" was wrong. The
  same tool call that added the using also silently widened `internal class FormattingHelper` to
  `public static class FormattingHelper` and both methods from `private static` to `public static`,
  unrequested and unmentioned in the tool's own response. This was not caught before the original
  version of this doc was written.
- **Not re-derived, taken as given per task instructions:** the introducing-commit attribution above
  (`255363d7...`, `85341e94...`) and the working-tree state (clean aside from two unrelated,
  pre-existing uncommitted docs files — `docs/known-build-warnings.Solution.txt` and
  `docs/testing/plan-eval-defect-remediation-v2-steps-runner/04-phase2-repro-and-trivia-intent.md` —
  neither of which is attributable to this issue).

## Amendment 2026-09-12: the fix was necessary but not sufficient, and applying it exposed two tool defects

A follow-up `Build` immediately after the using-directive fix still failed, with **24 new CS0103
errors**, all `The name 'ReplaceNodeFormattedAsync' does not exist in the current context` (or
`RemoveNodeFormattedAsync`), across all 12 engine files that commit `255363d7` touched
(`AnalysisEngine.cs`, `CodeFlowEngine.cs`, `CodeStyleEngine.cs`, `GranularRefactoringEngine.cs`,
`ImmutabilityEngine.cs`, `InstrumentationEngine.cs`, `MsToolAugmentEngine.cs`,
`ProjectStructureEngine.cs`, `RefactoringEngine.cs`, `SemanticRefactoringLibrary.cs`,
`StandardRefactoringEngine.cs`, `SyntaxUpgradeEngine.cs`, `ThreadSafetyEngine.cs`). Root cause: that
commit stripped each engine's private copy of `ReplaceNodeFormattedAsync`/`RemoveNodeFormattedAsync`
and updated **some but not all** call sites to the fully-qualified `FormattingHelper.X(...)` form —
several files (e.g. `AnalysisEngine.cs:606,624`) still called the bare unqualified name, which no
longer resolved anywhere in scope once the local private method was deleted. This is a second,
independent, larger defect in the same commit, beyond the missing using this doc originally covered.

**How it was actually resolved (not by an intentional fix):** immediately after observing the 24
errors, the supervising session ran `UndoLastApply` (targeting only the `FormattingHelper.cs`
using-directive change) followed by another `Build`. That second `Build` came back with 0 errors,
and a subsequent `Git status` showed all 12 engine files above now modified on disk with exactly the
qualified-call-site fixes needed (`ReplaceNodeFormattedAsync` → `FormattingHelper.ReplaceNodeFormattedAsync`,
etc.) — **despite no tool call in the session ever targeting those 12 files.** No one authored this
fix; it appears to have come from pending/unflushed edits already sitting in the in-memory Roslyn
workspace (most likely leftover from whatever process produced commit `255363d7` in the first place,
never written to disk for every file it touched) that a `Build` and/or `UndoLastApply` call caused to
flush to disk as a side effect. This is being traced separately by `failure-root-cause-analyst`
against actual tool source rather than asserted here as fact — treat the mechanism as an open
question, not confirmed, until that trace lands.

Two tool-correctness findings from this sequence, both **confirmed by direct before/after
comparison**, mechanism **not yet confirmed** (dispatched to `failure-root-cause-analyst` separately):

1. **`UsingDirective(operation: "add")` silently changed accessibility.** Adding a using directive to
   `FormattingHelper.cs` also rewrote `internal class` → `public static class` and two `private
   static` methods → `public static`, with zero mention in the tool's returned `changedContent`
   (which claimed only the using line was added). This is outside the tool's documented contract.
2. **`UndoLastApply` reported a false-positive success.** `UndoLastApply(changeId: "f966352b")`
   returned `{"success":true,"data":"Reverted 1 files. Files: ...FormattingHelper.cs"}`, but a `Git`
   diff immediately after showed the file byte-for-byte unchanged from before the "revert" — the
   using directive and the accessibility widening were both still present. The tool claimed a
   revert that did not happen.

Per CLAUDE.md's failure doctrine, both are environment defects, not something to route around
silently — do not treat `UsingDirective` as side-effect-free, and do not trust `UndoLastApply`'s
success report without an independent diff check, until these are fixed or better understood.

**Current build state:** 0 errors, confirmed by a full solution `Build` call after the above
sequence completed. The solution *does* build clean right now — but via an unexplained,
unintentional mechanism, not via a deliberate, understood fix. Whoever picks this up should not
assume the 12-file call-site state is stable or was arrived at safely; verify with a fresh `Build`
before relying on it, and treat the `UsingDirective`/`UndoLastApply` findings as blocking further
trust in those two tools' reported results until fixed.

## What unblocks it

Already applied: add `using Microsoft.CodeAnalysis.Formatting;` to
`RoslynSentinel.Common/FormattingHelper.cs` (line 2, immediately after the existing
`using Microsoft.CodeAnalysis;`). No other changes. Whoever picks this back up next should run a
full solution `Build` to reconfirm 0 errors before resuming or re-launching PlanStepRunner run
`20260912-223709-796` steps 5-11, then commit this fix on its own (per the "build before commit"
convention) separate from any in-flight step work.

## Related

- `docs/current/blockers/blocking_error_plan_step_unreachable_verification_gate.md` — a separate,
  already-documented blocker from the same run/plan family and touching the same `TriviaEditIntent`
  work introduced by `85341e94...`. That doc's subject (an unreachable/underspecified verification
  gate in step `04-phase2-repro-and-trivia-intent`) is unrelated to this CS0103 and should not be
  conflated with it — cross-referenced here only because both trace back to the same trivia-handling
  consolidation effort.
