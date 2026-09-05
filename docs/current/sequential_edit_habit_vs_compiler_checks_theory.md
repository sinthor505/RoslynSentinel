# Sequential-edit habit vs compiler checks theory

Theory that cross-file rename desync failures come from the model's sequential-edit training
habit colliding with per-write compiler validation, not from confusion about the refactor.

Working theory (user's framing, from reviewing the 2026-09-05 post-fix ladder batch's
reasoning/tool-call/reason data): the model's training distribution strongly favors making
individual sequential edits (one file/hunk at a time, as in a chat turn or a single commit) and
either mentally bookkeeping the other edits it still owes, or hoping to reconcile everything at
the end. RoslynSentinel's per-write Roslyn pre-apply compiler check enforces cross-file
consistency synchronously, on every `WriteFile`/`ApplyDiff` call — which fights that instinct
directly, rather than confirming the model doesn't understand the refactor.

**Evidence** (see the 2026-09-02 model-eval pattern analysis doc, same session, ladder batch after
the CollapseWhitespace assertion fix and raised chain-ladder caps):
- Every sampled failing run's dominant error is the same shape: rename `CalcDisc` →
  `CalculateDiscountedTotal` in `OrderPricingCalculator.cs`, forget the call site in
  `OrderCheckout.cs` in the same turn → CS1061 → fix the call site → break the definition side
  again → ping-pong for 2-7 turns before converging. This is the compiler catching a two-file
  transaction the model applied as two independent sequential edits.
  `Model_AppliesFiveChainedRefactors/20260905-121822-775` shows this literally: turns 3-13 are 11
  turns of WriteFile/ApplyDiff/Member all failing on this exact desync, before turn 14+ converges
  cleanly via ReadFile+ApplyDiff pairs touching both files back-to-back.
- `RenameSymbol` — the one tool shaped specifically as an atomic, cross-file-safe rename — exists
  in the toolset and is barely used; the model defaults to whole-file `WriteFile` regeneration or
  piecemeal `ApplyDiff` instead, i.e. it reaches for the sequential-edit affordance over the
  transactional one even when the transactional one would sidestep the entire failure class. This
  matches a pre-existing finding (reason-param review: ChangeAccessibility under-used vs ApplyDiff
  for its own use case) — a second, independent instance of the same tool-choice bias.
- Passing runs are not error-free (Chain6 run1 and Chain7 run3 both hit 3 tool errors) — passing
  means recovering from the same desync pattern within the turn/wall-clock budget, not avoiding it.
  Chain7's pass took 37 turns (vs 17-20 for its 2 failures in the same batch), the most of any
  Chain7 run — directly consistent with the raised 60-turn/45-min chain-ladder cap (commit
  025351f) being load-bearing for that specific pass rather than incidental.

**Why it matters:** reframes the ladder's apparent "step-count difficulty" signal — it isn't
primarily measuring whether the model can reason about N chained refactors, it's measuring whether
the model recovers from one narrow, well-characterized failure mode (sequential-edit habit vs.
atomic compiler validation) before the budget runs out. That's a much more specific and more
fixable target than "make the model smarter about multi-file dependencies."

**Refined theory (combines two mechanisms, not either alone):** a second candidate explanation is
that the model judges a rename as "simple enough to track manually" based on visible call-site
count — "it's just 2 changes, I'll remember to fix both" — rather than a blanket sequential-edit
habit independent of scale. This fixture always has exactly 1 call site (`OrderCheckout.cs` →
`CalcDisc`), which is exactly the regime where "just do it manually" is a locally reasonable bet,
so low call-site count alone doesn't explain the failures — something else has to turn that
reasonable bet into a repeated trap. The synthesis: it's **both together** — a task shape that
naturally fits the model's sequential-editing training (few call sites, looks tractable to track
by hand) **combined with** a footgun on the tool side (WriteFile/ApplyDiff's pre-2026-09-05
descriptions never mentioned that a write leaving the solution in an inconsistent intermediate
state gets hard-rejected, not soft-warned). The model walks in with a reasonable-looking plan and
no signal that its normal workflow is about to be rejected outright, so it doesn't course-correct
until it's already mid-ping-pong. Neither mechanism alone needs to be wrong for the other to be
right — scale-judgment explains why the model doesn't reach for `RenameSymbol` unprompted on a
low-call-site rename, and the undocumented-validation footgun explains why that judgment, which
would often work fine in an environment with lazy/end-of-session validation, fails hard here
specifically.

**How to apply:**
- When diagnosing future model-eval failures involving 2+ files, check first whether the error is
  this same cross-file desync shape (rename/signature-change on one side, stale reference on the
  other) before attributing it to task-level misunderstanding.
- Fixed 2026-09-05 (commit 3593771): `WriteFile`/`ApplyDiff`/`ApplyUnifiedDiff` descriptions now
  state the pre-apply compile-validation behavior explicitly and point to `RenameSymbol`/
  `ChangeSignature`/`validateOnApply=false` as the fix for a multi-file transaction — directly
  targets the "footgun" half of the combined theory. Not yet re-run against the ladder to confirm
  effect (smoke test in progress as of this writing).
- In-progress (commit f381bcd): `BlockWriteFile` toggle added to both
  `OrderPricingRefactorAgentTests`/`OrderPricingRefactorChainAgentTests`, default off — blocks only
  `WriteFile` (keeps `ApplyDiff`/`ApplyUnifiedDiff`/`RenameSymbol`/`ChangeSignature` exposed) to
  isolate whether removing the whole-file-rewrite escape hatch specifically reduces the
  rename-desync failure, or whether the same pattern resurfaces via `ApplyDiff` regardless (which
  would argue against "WriteFile is the problem" and toward "sequential-edit habit is the problem
  independent of which tool expresses it").
- Open question, NOT yet tested: this fixture can't distinguish the two mechanisms because it only
  ever has 1-2 call sites. A follow-up fixture/rung with a symbol having many (~10-20+) call sites
  would let the two theories make different predictions — if the model still ping-pongs manually
  at high call-site count even with WriteFile blocked and the description fix live, that argues for
  a scale-independent habit (the original framing); if it switches to `RenameSymbol` on its own
  once call-site count is visibly large but not on this low-count fixture, that confirms the
  scale-judgment half of the combined theory. Worth building once the current WriteFile experiment
  concludes.
- Unconfirmed: whether nudging tool choice actually reduces the ping-pong, or whether the model
  would just recreate the same desync pattern one level up (e.g. issuing two `RenameSymbol` calls
  non-atomically). Worth testing directly rather than assuming the fix works.

**Smoke test result (2026-09-05, commit 3593771 description-fix only, Chain5, n=3):** 2/3 passed,
vs. 0/3 pre-fix baseline. All 3 runs called `ApplyDiff` with `"validateOnApply":false` explicitly
— the exact escape hatch the new description text points to — confirming the model read and acted
on the new guidance rather than the improvement being incidental. The one failure (run 2) was a
**different, unrelated bug**: the model additionally mangled a string interpolation
(`$"Order {{id: {id}}}: {label}"`, spurious literal braces, CS8086-shape) while otherwise
correctly batching both files' changes with `validateOnApply=false` — i.e. the cross-file desync
pattern did not recur even in the failing run. This is a clean confirmation that the "footgun"
half of the combined theory was real and directly fixable by documenting existing behavior; it
does not yet test the "scale-judgment" half (this fixture is still 1-call-site) or the
`BlockWriteFile` experiment (not yet run). Note: this smoke test's build predates commit 3a4c521
(WriteFile's own rejection path was still returning the raw `Diagnostics.ToJson()` string, not the
`CompilerErrorLookupHelper`-routed guidance) — every `WriteFile` failure surfaced here shows the
old unhelpful raw-JSON text, and the model still recovered anyway purely from the upfront
description guidance. `CompilerErrorLookupHelper` routing for `WriteFile` (3a4c521) has not been
smoke-tested on its own yet.
