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

**How to apply:**
- When diagnosing future model-eval failures involving 2+ files, check first whether the error is
  this same cross-file desync shape (rename/signature-change on one side, stale reference on the
  other) before attributing it to task-level misunderstanding.
- Consider a targeted single-purpose test that isolates just a cross-file rename (no other ladder
  steps) to measure recovery-turn-count distribution directly, independent of step count — would
  confirm or falsify this theory more cleanly than reading it off the ladder's mixed signal.
- Two candidate levers, not yet tried: (a) prompt/system-message nudge toward `RenameSymbol` for
  rename-shaped edits specifically, so atomicity is handled by tool choice rather than turn-by-turn
  model bookkeeping; (b) treat multi-file-touching refactor steps as inherently needing extra
  turn/wall-clock budget for compiler-driven recovery, independent of raw step count — the ladder's
  cap raise already supports this and visibly helped (Chain7 pass).
- Unconfirmed: whether nudging tool choice actually reduces the ping-pong, or whether the model
  would just recreate the same desync pattern one level up (e.g. issuing two `RenameSymbol` calls
  non-atomically). Worth testing directly rather than assuming the fix works.
