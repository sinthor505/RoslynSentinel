---
name: project_oldblock_not_found_double_replace_bug
description: "CONFIRMED root cause: on PlanImplementVerify this is a PLAN-PHASE bug, not an implement-phase one — the plan model itself writes the buggy double-replace code into its 'Content for BlockConverter.cs' block roughly half the time, and the implement phase faithfully transcribes it. 10/20 (50%) on a clean, confound-free .113 PlanImplementVerify batch 2026-09-02; also seen in MinimalGuidanceDisambiguated (3/20). An explicit prompt warning against this exact mistake (commit 5f6d193) had ZERO measurable effect on a same-night re-run: still 10/20 (50%) with the warning text confirmed present and correctly wired into the model's actual prompt."
metadata:
  type: project
---

First isolated during [[project_planimplementverify_0of20_root_causes_2026_09_02]] (5/20 runs,
confounded with the verify-phase wall-clock bug) then confirmed recurring in the same night's
`.113` `MinimalGuidanceDisambiguated` re-run (3/20) — same `FunctionalFixVerifier:
ConvertAbstractClassToInterface threw at runtime: oldBlock not found in fileText` signature in
two independently-run test variants with different prompts.

**Confirmed on a clean re-run**: once the verify-phase build-level fix (commit `7e77a31`)
removed the confounding `WallClockCapExceeded` failures, a fresh 20-run `.113`
`PlanImplementVerify` batch came back 0/20 with **10/20 (50%) attributable to this exact bug**
— now the single dominant failure mode, well clear of any other cause.

**Root cause located precisely**: for `PlanImplementVerify`, this is a **plan-phase** bug, not
an implement-phase one. Read the `plan/transcript.json` for a failing run
(`20260902-131021-094`): the plan model's own "Content for `BlockConverter.cs`" code block
already contains the buggy pattern verbatim —
```csharp
var rewritten = fileText.Replace(oldHeader, newHeader, StringComparison.Ordinal);
return BlockEditHelpers.ReplaceBlockFormatted(rewritten, oldHeader, newHeader);
```
— and the implement prompt explicitly instructs the model to "Apply it as described," so the
implement phase faithfully transcribes the plan's own bug via `ApplyDiff` rather than
independently re-deriving a correct fix. Checked a second failing run
(`20260902-134429-839`): its plan phase produced the *correct* single-call pattern
(`ReplaceBlockFormatted(fileText, oldBlock, newBlock)` on the original text) — that run's
failure was a different, unrelated implement-phase mismatch — confirming this specific bug is
not deterministic but a roughly-50%-of-the-time plan-generation quality issue, not something
baked into the prompt itself.

**Mechanism**: the reference/expected fix pattern is:
```csharp
var newHeader = $"public interface I{className}";
return BlockEditHelpers.ReplaceBlockFormatted(fileText, oldHeader, newHeader);
```
i.e. call the helper once, on the *original* `fileText`, with both the old and new header —
the helper does the find-and-replace-with-reindent itself. The buggy pattern several runs
produce instead:
```csharp
var rewritten = fileText.Replace(oldHeader, newHeader, StringComparison.Ordinal);
return BlockEditHelpers.ReplaceBlockFormatted(rewritten, oldHeader, newHeader);
```
The model does the header swap itself first via `string.Replace`, *then* separately calls
`ReplaceBlockFormatted` (presumably believing it still needs to for the re-indent step) —
but by that point `oldHeader` no longer exists in `rewritten` for `ReplaceBlockFormatted` to
find, so it throws `InvalidOperationException("oldBlock not found in fileText.")` at runtime.
`FunctionalFixVerifier` (added per [[project_functional_fix_verifier_added]]) catches this by
actually invoking the compiled method via reflection, not just checking that it compiles — the
build succeeds cleanly in every observed instance of this bug, since the C# itself is valid.

**Why this happens (likely)**: the task/plan text describes the fix in two conceptual steps
("rewrite the header" + "use the helper so only the block gets reindented"), and the model
implements both steps literally and sequentially instead of recognizing they're the same
operation — `ReplaceBlockFormatted` already performs the replace, the model doesn't need (and
must not do) its own `.Replace()` call first.

**Prompt-warning fix tested — NEGATIVE RESULT**: added an explicit warning to
`PlanUserPromptTemplate` in `PlanImplementVerifyAgentTests.cs` (the plan phase's own prompt,
commit `5f6d193`) naming this exact mistake — "do not call `string.Replace` yourself first and
then pass the ALREADY-replaced text into the helper along with the old block content." Same
warning also added to `DisambiguatedMinimalGuidanceUserPromptTemplate`. Re-ran a fresh,
clean-rebuilt 20-run `.113` `PlanImplementVerify` batch afterward: **still 10/20 (50%)** —
byte-identical rate to the pre-fix batch. Directly confirmed the warning text reached the
model correctly (read a v3 run's `plan/transcript.json.UserPrompt` and found the exact warning
paragraph present verbatim). This rules out "the model doesn't know this is wrong" — it's
being told explicitly, in the same prompt, immediately before generating the plan, and still
produces the buggy pattern at the same rate. Spot-checked one run whose plan happened to be
correct despite matching a loose grep for the buggy substring, confirming the failure isn't a
counting artifact either.

**How to apply**: this is confirmed to be a genuine model reasoning-depth limitation, not a
knowledge gap fixable by prompt wording — the 9B model cannot reliably reconcile "the helper
already does the replace" with "I want to also change the header text" as the same operation,
even when told directly not to conflate them. Don't attempt further prompt-wording iterations
on this specific bug; that lever is now empirically closed.

**"Harden `ReplaceBlockFormatted`" is NOT a valid fix and should not be attempted**:
`ReplaceBlockFormatted` isn't a real RoslynSentinel tool — it's synthesized fixture content
(`RoslynSentinel.Tests.ModelEval/Fixtures/WholeFileRewriteReproducer.cs:31`) that the test
hands the model as a pre-existing helper representing a realistic reusable pattern already in
the target codebase. Making it silently idempotent (tolerate being called on already-mutated
text) would mean the test stops actually checking whether the model calls it correctly — it
would quietly bail out a genuinely wrong call instead of that call correctly throwing, changing
what the test measures rather than fixing anything real.

**Conclusion: accept this as a real ~50% ceiling** on this specific fixture/pattern
combination for this model size, and treat it as expected baseline noise rather than something
to chase further — consistent with [[project_scriptedplan_5run_result]]'s finding that this
model's bottleneck is planning/reasoning depth, not tool execution or prompt ambiguity. This
bug is exactly that kind of failure: a reasoning-depth gap in code synthesis that persists even
under a maximally explicit warning, not a tool-use or ambiguity problem this harness can fix.
