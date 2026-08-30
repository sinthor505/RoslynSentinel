---
name: system-prompt-v1-result-and-modifymodifier-gap
description: "First run of AgentSystemPrompts.CodingAgent against qwen3-coder-9b fixed the two targeted failure modes but exposed a new one — a ModifyModifier(remove, \"private\") usability gap and no-recovery-on-repeat-failure"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-30T08:56:21.065Z
---

After adding [[project_applydiff_fixes_unblocked_model_eval]]'s follow-up — a longer,
explicit `AgentSystemPrompts.CodingAgent` system prompt (role/rules/workflow/anti-hallucination) —
the first real rerun of `Model_FixesWholeFileRewriteBug_MinimalGuidance` against qwen3-coder-9b
on `.113` (2026-08-30) still **failed** (16 turns, 4m47s, well under the 10-min wall-clock cap;
`ModelFinished` because turn 16 made 0 tool calls with 0 content — silent abandonment, not a
timeout).

**What the new prompt fixed** (both are the two failure modes from the prior run that motivated
writing it): the model preserved `UnrelatedMethodBefore`/`UnrelatedMethodAfter`/`_unrelatedField`
byte-for-byte in every `ApplyDiff` attempt, and correctly referenced
`BlockEditHelpers.ReplaceBlockFormatted` by name rather than reinventing/renaming it.

**New failure mode this run exposed — a real `ModifyModifier` gap, not a model mistake per se**:
the fixture's `ReplaceBlockFormatted` is `private`; the model correctly diagnosed the `CS0122`
inaccessibility error and called `ModifyModifier(targetName: "ReplaceBlockFormatted", modifier:
"private", action: "remove")`. That call **reported success** but only stripped the literal word
`private` from the source — leaving the method as an implicit-private class member (C#'s default),
so it was never actually made callable cross-file. The very next `ApplyDiff` retry hit the
identical `CS0122` error. `ModifyModifier`'s `remove` action for an accessibility modifier does
not add the complementary `public`/`internal` — callers must explicitly `action: "add", modifier:
"public"` to actually widen access. Worth fixing or at minimum documenting in the tool description,
since a real (human or model) caller reasoning "remove private" → "now accessible" will hit the
same trap.

**Secondary issues observed, same run**: (1) one hallucinated `ApplyDiff(action:
"confirmationCode", confirmationCode: "76c80c7c")` — a one-digit-off guess at the real `changeId`
("66c80c7c") from the unrelated `ModifyModifier` response, invoking a flow (the whole-file-rewrite
size-guard escape hatch, see [[project_diffengine_trailing_blank_anchor_fix]]'s sibling commit)
that doesn't apply to this situation at all — despite the new prompt's explicit anti-hallucination
rule. (2) after the first `CS0122` failure, the model resubmitted the *identical* failing
`ApplyDiff` payload twice more (turns 11, 13) with no change to `BlockEditHelpers.cs` in between —
the "don't repeat the same failing call unchanged" rule didn't stick. (3) after a vacuous clean
`Build` (passing only because the actual fix was never applied to disk), the model didn't even
attempt a false-success report — it just stopped with no tool call and no content at turn 16.

**Why:** This is the first concrete evidence of the new system prompt's real effect — confirms it
closes the two gaps it targeted, but a longer prompt is not a general fix for compounding
tool-API-semantics confusion. The model can be fully correctly oriented (right file, right helper,
right approach) and still fail purely on one tool's non-obvious semantics.

**How to apply:** Before writing more prompt rules, consider fixing `ModifyModifier`'s
remove-without-complementary-add behavior directly (or renaming the parameter/documenting it more
sharply) — that's a tool-design fix, not a prompt problem, and likely has higher leverage than
further prompt iteration. If more model-eval runs hit the same `ModifyModifier` trap, that
confirms it's worth fixing before continuing to harden the prompt further.
