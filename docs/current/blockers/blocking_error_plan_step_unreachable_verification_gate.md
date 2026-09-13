# Plan step names two unreachable verification methods but omits a reachable third — `04-phase2-repro-and-trivia-intent`

**Status:** AMENDED 2026-09-12 — downgraded from "unreachable gate" to "incomplete gate wording."
See "Amendment" section below. The underlying trivia-loss bug remains a confirmed, open defect;
a fix has been dispatched to a subagent separately from this writeup.

**Original status:** OPEN — halted, not converged. Worktree left in place, uncommitted.

**Run:** `20260912-223709-796`, plan `plan-eval-defect-remediation-v2-steps-runner`, host 113,
model `qwen/qwen3.6-35b-a3b`, assistance level L4/Directed.
**Step:** `04-phase2-repro-and-trivia-intent` (`docs/testing/plan-eval-defect-remediation-v2-steps-runner/04-phase2-repro-and-trivia-intent.md`)
**Worktree:** `C:\Users\Administrator\source\repos\RoslynSentinel-TestRuns\PlanStepRunner\20260912-223709-796\04-phase2-repro-and-trivia-intent\Worktree`
— halted, not cleaned up. Per `CLAUDE.md`, treat this only as a harness clone: do not run git
commands inside it and do not read its state as authoritative without cross-checking the run's
branch commits. The 3 touched files there (`RefactoringEngine.cs`, the `RemoveSummaryCommentAsync`
call site, `Scratch.cs`) are uncommitted since the step halted rather than converged.

## What happened

`stopReason=TurnCapExceeded` after all 40 turns. `converged=False`, `buildErrors=0`, `blocked=False`.
The step never reached its stated gate condition and was terminated by the turn cap mid-retest, not
by an error or an explicit block.

## Root cause: the plan step names two verification methods, neither reachable by this agent

`docs/testing/plan-eval-defect-remediation-v2-steps-runner/04-phase2-repro-and-trivia-intent.md`
(around lines 30-31) requires the model to reproduce a doc-comment-loss bug in
`ChangeAccessibilityAsync` and reach a "confirmed mechanism" *before* writing any fix, and names
exactly two acceptable ways to confirm it: "a debugger attached to the MCP server process, or a
small standalone unit test that calls `ReplaceNodeFormattedAsync` directly and inspects intermediate
trivia." Both are unreachable from inside this environment:

1. **No tool attaches a debugger.** Attaching a debugger to a running process is inherently a
   human/IDE action; there is no MCP tool surface for it at all, gated or otherwise.
2. **No path exists to unit-test `ReplaceNodeFormattedAsync` directly.** It is `private static` on
   `RefactoringEngine` (`RoslynSentinel.Basic/RefactoringEngine.cs:62`). A repo-wide search found
   zero `InternalsVisibleTo` attributes anywhere in source (the only hit is a mention in an obsolete
   plan doc, `docs/obsolete/plan-tool-disambiguation-remediation-v1.md`) — so no `Tests.*` project
   can see `RoslynSentinel.Basic`'s internals, let alone its `private` members. The `RunTest` tool
   (`RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs:1359-1398`) only runs `dotnet test`
   against tests already present in the loaded solution; it has no test-authoring affordance. To
   satisfy the step as literally worded, the model would first have to change the method's
   accessibility or add `InternalsVisibleTo` — neither mentioned or authorized by the plan step —
   and then invent a whole new test-fixture pattern from scratch, since no existing test in
   `RoslynSentinel.Tests.Basic` exercises `RefactoringEngine`'s private helpers directly.

`ChangeAccessibilityAsync` itself is at `RoslynSentinel.Basic/RefactoringEngine.cs:3109`, and is one
of dozens of call sites routing through the shared `ReplaceNodeFormattedAsync` helper — nothing
about this call site is special-cased in a way that would make the gate reachable some other way.

## Consequence, traced turn-by-turn

The model (correctly, given no alternative was reachable) fell back to source-only reasoning at
turn 22, in violation of its own plan-mandated gate, and proceeded to implement a plausible-but-wrong
fix: a `TriviaEditIntent` enum replacing the existing `Count > 0` heuristic at
`RefactoringEngine.cs:74`. It tested the fix live at turn 33 and found the doc comment was **still**
lost — meaning the heuristic was never the true root cause. Turns 35-39 were spent re-reading files
and re-theorizing with no new tool-mediated way to gather evidence, before the run hit the turn cap
at turn 40 mid-retest.

By turn 36 the model had begun suspecting the actual loss happens downstream, in
`Formatter.FormatAsync(document.WithSyntaxRoot(newRoot), annotation, ...)` at
`RefactoringEngine.cs:79`, possibly re-normalizing or dropping structured trivia during formatting.
**This is a hypothesis the model formed, not a confirmed root cause** — the environment gave it no
way to confirm or refute it, which is the entire point of this writeup. Do not treat it as settled;
whoever picks this back up should verify it independently (e.g. via a debugger, once one is
reachable, or a `ReadFile` diff of trivia immediately before/after the `Formatter.FormatAsync` call).

**Separate, smaller, unrelated regression noticed along the way:** turn 30's edit introduced
`cancellationToken: default` as a named argument at the `RemoveSummaryCommentAsync` call site,
silently discarding whatever real token had previously been threaded through. Not caught by the
model or by any test; worth a follow-up fix independent of the trivia-loss investigation.

## What's confirmed vs. still open

- **Confirmed:** the plan step's two named verification methods are both unreachable by a
  non-interactive, tool-calling agent in this environment (traced to `RunTest`'s implementation and
  a repo-wide `InternalsVisibleTo` search, not assumed).
- **Confirmed:** the `TriviaEditIntent` fix at `RefactoringEngine.cs:74` does not fix the doc-comment
  loss — re-verified live at turn 33.
- **Not confirmed — hypothesis only:** that `Formatter.FormatAsync` at `RefactoringEngine.cs:79` is
  where the trivia is actually lost.
- **Not yet fixed:** the `cancellationToken: default` regression at the `RemoveSummaryCommentAsync`
  call site introduced at turn 30.

## What unblocks it

This is reported as options, not a decision — pick one (or combine):

1. **Plan-wording fix.** Reword Part A of
   `docs/testing/plan-eval-defect-remediation-v2-steps-runner/04-phase2-repro-and-trivia-intent.md`
   to name a verification method actually reachable with existing tools — e.g. `ReadFile`
   immediately after each edit stage to inspect trivia directly, or a temporary trace added via the
   `Member` tool and removed afterward — **or** explicitly authorize adding `InternalsVisibleTo` plus
   a throwaway test as a sanctioned first sub-step.
2. **Tooling gap (larger, cross-cutting).** There is currently no MCP-mediated way to author,
   register, and run a scratch unit test against internal/private engine internals at all. If
   "confirm via unit test" is meant to be a real option in future plan steps, this needs either a
   documented `InternalsVisibleTo` convention for `RoslynSentinel.Basic` → `Tests.Basic` plus a
   boilerplate fixture pattern the model can be pointed to, or a `RunTest`-adjacent tool for
   authoring and running a throwaway test in one guarded call.
3. **Plan-authoring convention.** Plan steps should instruct the model to halt and report explicitly
   when a named verification method turns out to be unreachable with available tools, rather than
   silently proceeding on an unconfirmed hypothesis. Applied here, this would have turned an
   invisible gate violation into a visible, actionable blocker around turn 22 instead of turn 40.

Once a direction is chosen: reword/extend the tooling as decided, then re-run step
`04-phase2-repro-and-trivia-intent` from a clean worktree (the current one should not be reused —
see the Worktree note above) to re-attempt the repro with a reachable gate.

## Amendment 2026-09-12: a reachable third verification path exists

A prior, unrelated, **completed** L4/Directed run of the same plan (`20260911-205633-213`, same
step file) was reviewed for comparison. Its step 4 hit the identical symptom — Part A's repro still
showed the doc comment lost after the Part B/C fix — but it did **not** stall on the two named
methods. It found and used a third path the plan step never names: write a scratch method with a
doc comment, run `ChangeAccessibility` on it, then `ReadFile` the result to check whether the
comment survived (concrete evidence at that run's turn 38: `ReadFile` showed the comment missing).
That run reasoned productively about `Formatter.FormatAsync`/`WithModifiers` through turns 47-54 and
converged naturally at turn 56 — gate still failed, but the run was never turn-capped, because it
reached concrete evidence quickly instead of stalling on "how do I verify this at all."

This changes the diagnosis:

- **Not confirmed as unreachable, revise to "incompletely specified."** The plan step's own text
  names two methods, both genuinely unreachable as detailed above — that part of the original
  analysis stands, file:line citations included. But it does not name the `ReadFile`-around-an-edit
  method, which is reachable with existing tools and was independently discovered by a different run
  of the same model. The gate is not a hard wall; it's an underspecified prompt that one run navigated
  around and another (today's, `20260912-223709-796`) got anchored on the two literal named options
  and never found the third.
- **Root cause reclassified:** primarily a **plan-wording gap** (missing the obvious reachable
  verification method from its own list of examples), secondarily a **model-robustness gap** (today's
  run fixated on the named methods rather than generalizing "verify via read-before/read-after,"
  which is a pattern the model is otherwise capable of, per the comparison run).
- **The trivia-loss bug itself is now doubly confirmed, not a hypothesis.** Two independent runs
  reached the same empirical result — doc comment lost after the Part B/C fix — using two different
  fix attempts (today's `TriviaEditIntent` enum change; the prior run's own fix attempt). The
  `Formatter.FormatAsync`/`WithModifiers` suspicion from both runs' turn-54-region reasoning should be
  treated as a real, reproducible defect worth fixing directly, independent of re-running this plan
  step. A fix has been dispatched separately.
- **Recommended option, given this:** option 1 above (plan-wording fix) still applies, but the
  concrete reachable method to add to the plan step's Part A is now known and should be named
  explicitly: *"apply the change under test, then `ReadFile` the affected span and confirm the doc
  comment/trivia is present — do this immediately before and after each edit stage."* Option 2
  (test-authoring tooling gap) is downgraded from "needed to unblock this step" to "still a real
  gap, but not the blocker for this specific step" since a `ReadFile`-based path was already
  sufficient in practice.
