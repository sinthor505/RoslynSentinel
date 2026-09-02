---
name: project_oldblock_not_found_double_replace_bug
description: "CONFIRMED root cause: on PlanImplementVerify this is a PLAN-PHASE bug, not an implement-phase one — the plan model itself writes the buggy double-replace code into its 'Content for BlockConverter.cs' block roughly half the time, and the implement phase faithfully transcribes it. 10/20 (50%) on a clean, confound-free .113 PlanImplementVerify batch 2026-09-02; also seen in MinimalGuidanceDisambiguated (3/20)."
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

**How to apply**: this is a model capability gap, not a harness or prompt-structure defect —
the existing prompts already show the correct single-call pattern implicitly via the
`ReplaceBlockFormatted` doc comment the model reads via `ReadFile` before editing, and the
model still gets it wrong about half the time when generating a plan from scratch. Confirmed
at 50% on a clean, confound-free batch — high enough to act on now. **Fix applied**: added an
explicit warning to `PlanOnlyUserPromptTemplate` in `PlanImplementVerifyAgentTests.cs`
(the plan phase's own prompt) calling out this exact mistake by name, since that's where the
bug is actually introduced — not the implement phase's prompt, which was the wrong target for
a fix (implement is just faithfully transcribing what plan already got wrong). Also considered:
hardening `ReplaceBlockFormatted` itself to be idempotent (if `oldBlock` isn't found but
`newBlock` already is, treat as already-applied) — deferred, since it risks masking genuinely
wrong fixes elsewhere and the prompt-level fix targets the actual point of failure more
precisely. Re-run `.113` `PlanImplementVerify` (20 runs) to check whether the plan-phase
warning meaningfully reduces this specific bucket.
