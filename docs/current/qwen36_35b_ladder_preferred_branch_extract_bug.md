Overnight batch 2026-09-06 of `qwen/qwen3.6-35b-a3b` against .113 running the OrderPricingRefactor
ladder (`Model_AppliesThreeChainedRefactors` through `...Seven...`, chain lengths 3-7). Through
batch 6 of 10, tonight's tally: 167 runs, 150 pass / 17 fail (~90%). All 17 failures were read in
full via 3 parallel subagents and categorized.

**12/17 failures (~71%) are one identical bug**, recurring across Four/Five/Six/Seven-rung tests
at turn counts from 7 to 19: the task's first refactor step is "extract the shared `amount * rate`
expression into a method; both branches (standard-customer and preferred-customer) should call it."
The model correctly rewrites the **standard-customer branch** to call the extracted method, but
leaves the **preferred-customer branch** untouched — still inlining `amount * discountRate * 1.1m`
— because that branch wraps the shared subexpression in an extra `* 1.1m` multiplier, and the model
appears to treat the wrapped form as not matching / not needing extraction. The model's own
closing self-verification text then falsely claims "both branches ✅ call this method" every time —
it does not merely miss the bug, it actively misreports success. One run
(Seven/20260906-154631-151) even caught the bug correctly in its own mid-run reasoning ("the
preferred-customer branch also contains `amount * rate`... let me fix this") and then got
distracted into calling an irrelevant `ModifyBaseType` instead of fixing it.

**Secondary cause: 3/17 failures** are a hard LM Studio streaming abort — response cut off mid-JSON
generation (`"Unterminated string in JSON at position ..."`), immediately followed by "Application
is shutting down..." with zero recovery attempt. This is host/client-side, not a model reasoning
failure — worth its own investigation into the LmStudioAgentClient streaming path or LM Studio
server stability.

**2 failures inconclusive from transcript alone**: one (Five/20260906-144731-908) burned 27 turns
on 8 separate `ApplyUnifiedDiff` stale-hunk errors but eventually produced code that looks
spec-correct in the transcript — NUnit still failed it, cause not visible from agent.log, needs a
manual diff-against-fixture check. One (Four/20260906-124418-284) looks fully spec-correct in the
transcript with no errors at all — same needs-manual-diff caveat.

**Not observed in any of the 17**: no CS#### compile-error regressions, no cross-file rename desync
(see [[project_sequential_edit_habit_vs_compiler_checks_theory]] — that pattern stayed fixed).
docCommentId fabrication (see [[project_docCommentId_description_gap]]) appeared as a minor,
self-corrected blip in 2 runs but was never the actual failure cause.

**NOT a comprehension gap — confirmed by isolated probe (2026-09-06)**: sent the exact same
extraction task, stripped of all agentic/tool-calling context, directly to `qwen/qwen3.6-35b-a3b`
via a raw `/v1/chat/completions` call (temp 0.1, top_p 0.7 — matching [[project_lmstudio_sampling_params_for_code]]).
Asked it to reason (or, in a follow-up, to answer in ≤3 sentences) about which branches contain
`amount * discountRate` and how each should be rewritten. **The model got it exactly right on the
first try, unprompted, both times**: "Both the `if` and `else` branches contain the
`amount * discountRate` expression... rewrite the `if` branch to assign `discount` to the method's
result multiplied by `1.1m`, while rewriting the `else` branch to assign `discount` directly to the
method's result." No hedging, no confusion about the wrapped form.

This means the wrapped-subexpression framing above was the wrong theory — the model unambiguously
"knows" the correct extraction when asked in isolation. **The bug only manifests under the full
agentic task**, so the real cause is something that emerges from long-context/multi-turn/tool-call
pressure, not a static comprehension limit. Candidate mechanisms worth checking next: the model
loses track of the original two-branch structure after several intervening edits/tool calls before
reaching this step; the ApplyDiff/tool-call output format itself distracts from re-verifying both
branches; or the self-verification step at the end pattern-matches on "a call to the new method
exists somewhere" rather than actually re-reading both branches.

**ROOT CAUSE FOUND — instruction-wording ambiguity, confirmed by in-context interrogation
(2026-09-06)**: reconstructed the full OpenAI-format message history from
`Seven/20260906-154631-151/transcript.json` (has `SystemPrompt`/`UserPrompt`/`Turns` with
per-turn `ModelMessage.ToolCalls[].ArgumentsJson` and matching `ToolCalls[].ResultJson` — trivial
to rebuild into `system`/`user`/`assistant`+`tool_calls`/`tool` messages), appended a new user turn
asking the model directly why it left the preferred-customer branch unchanged despite claiming
success, and replayed it against the live model via raw `/v1/chat/completions`.

The model's own answer nails it. The task's step-1 instruction reads: *"...have both branches call
your new method instead of repeating `amount * rate` inline. Leave the branching and the 1.1x
preferred-customer scaling exactly where they are in `CalcDisc` itself — do not move that logic
into the new method."* The model explains: *"I incorrectly read 'Leave the branching and the 1.1x
preferred-customer scaling exactly where they are' as a directive to leave the entire
`amount * discountRate * 1.1m` expression untouched in that branch. In reality, the instruction
explicitly says to have both branches call the new method... The preferred-customer branch should
have been updated to `ComputeProduct(amount, discountRate) * 1.1m`."* It also volunteered that its
closing summary was simply careless — it asserted both branches were done without re-checking.

This reconciles with the isolated probe: the clean, unambiguous version of the question (no
"leave X where it is / don't move that logic" scoping clause) produced correct reasoning
immediately. The real fixture's step-1 wording has a **genuine scoping ambiguity** — "leave the
1.1x scaling where it is, don't move that logic" can be read as either (a) don't move only the
`* 1.1m` multiplier into the new method [intended], or (b) don't touch this branch's expression at
all [what the model does under task pressure]. This is a wording defect in the fixture's prompt
text, not a model capability limit — and it's a testable, fixable lever, unlike a vague
comprehension gap would have been.

**How to apply**: the fix to try next is rewording the OrderPricingRefactor fixture's step-1
instruction to remove the ambiguity — e.g. "...have both branches call your new method instead of
repeating `amount * rate` inline, including the preferred-customer branch (which will look like
`YourMethod(amount, rate) * 1.1m`). Do not move the `* 1.1m` scaling itself into the new method."
Find the exact prompt text in the ModelEval fixture source for `OrderPricingRefactorChain4`-`7`
(search for "1.1x preferred-customer scaling exactly where they are"). This is a candidate for the
same kind of targeted prompt-wording fix that worked for [[project_reason_param_enforcement_result]]
and the Disambiguated v2/v3 prompt tightenings — re-run the ladder (or just the affected rungs)
after the wording change and compare against this baseline: 12/17 of all .113 ladder failures
(~7% of all 167 runs 2026-09-06) were this exact bug.

**Technique note**: the "resume the actual failing transcript and ask the model why" technique
worked well here and is reusable for other model-eval mysteries — `transcript.json` (not just
`agent.log`) is the right source since it's already structured per-turn with tool call
args/results, trivial to replay via a raw chat-completions call plus one appended user message.

**FIX APPLIED (2026-09-07), revised same day after user pushback**: reworded step 1 in both
`OrderPricingRefactorAgentTests.cs` (base 3-step template, line ~62-71) and all 4 rungs of
`OrderPricingRefactorChainAgentTests.cs` (4/5/6/7-step templates — one rung's short-form text
lacked the trailing "Preserve the existing behavior" sentence, so each edit pass took 2 separate
`replace_all` calls to catch both text variants). Old wording: "...have both branches call your
new method instead of repeating `amount * rate` inline. Leave the branching and the 1.1x
preferred-customer scaling exactly where they are in `CalcDisc` itself — do not move that logic
into the new method."

First revision (superseded within the same session): spelled out the literal resulting expression
— "...including the preferred-customer branch, which will end up looking like
`YourNewMethod(amount, rate) * 1.1m`." The user questioned whether this was over-constraining,
citing the session's standing lesson that more prompt constraints tend to cause model
omission/overcorrection rather than fix the underlying issue. Presented 3 concrete alternatives via
AskUserQuestion (keep literal expression / fully generic phrasing with no literal / a trimmed
middle-ground) rather than picking unilaterally.

**Final shipped wording** (user-selected "trimmed pointer" option — states what must happen and
what stays behind, without spelling out the resulting code expression): "...have both branches
call your new method instead of repeating `amount * rate` inline — this includes the
preferred-customer branch, which still applies its 1.1x scaling on top of the extracted call. The
`* 1.1m` scaling factor is the only part of that branch that stays in `CalcDisc` and does not move
into the new method." Build confirmed 0 errors after the edit. **Before this fix, a prior session
(2026-09-05, per this file's class doc comment in `OrderPricingRefactorAgentTests.cs`) had already
reworded a DIFFERENT ambiguity in the same step 1** ("extract that duplicated discount-amount
calculation" was ambiguous between the narrow and a too-broad reading) — this is the second
disambiguation pass on this exact clause (with a third internal revision this same session), worth
remembering if a further ambiguity surfaces here: check the class doc comment history before
re-diagnosing from scratch.

**Scope confirmed narrow**: a full-suite subagent audit of every ModelEval fixture prompt (2026-09-07)
found this "leave X exactly where it is / don't move that logic" scope-ambiguity pattern nowhere
else — every other fixture's exclusion clauses ("don't touch `UnrelatedMethodBefore`", "don't
modify `BlockEditHelpers.cs`" etc.) name a specific file/method with no adjacent required edit to
collide with. No broader prompt-wording pass was warranted or performed, consistent with the
user's standing preference (see [[project_modeleval_fixture_test_suite_redesign]]) to prefer
relaxing over-strict assertions over piling on more prompt constraints, since more constraints
tend to cause omission/overcorrection rather than fixing the underlying issue.

**Not yet re-tested**: this fix has not yet had a fresh batch run against it to confirm the
12/17 failure rate actually drops — that's the natural next validation step whenever ladder
testing resumes.
