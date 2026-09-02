---
name: project_oldblock_not_found_double_replace_bug
description: "Recurring model logic bug on the WholeFileRewriteAgentTests fixture: model calls fileText.Replace(oldHeader, newHeader, ...) to build the return value, then passes that ALREADY-replaced text plus the pre-replacement oldHeader string into BlockEditHelpers.ReplaceBlockFormatted, which throws because oldHeader no longer exists in the text. Seen in both PlanImplementVerify and MinimalGuidanceDisambiguated batches on 2026-09-02."
metadata:
  type: project
---

First isolated during [[project_planimplementverify_0of20_root_causes_2026_09_02]] (5/20 runs)
then confirmed recurring in the same night's `.113` `MinimalGuidanceDisambiguated` re-run
(1/3 runs so far) — same `FunctionalFixVerifier: ConvertAbstractClassToInterface threw at
runtime: oldBlock not found in fileText` signature in two independently-run test variants with
different prompts, so this is a real, repeatable model reasoning pattern, not a one-off.

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
both prompts already show the correct single-call pattern implicitly via the existing
`ReplaceBlockFormatted` doc comment the model reads via `ReadFile` before editing. Two possible
mitigations if this keeps recurring at a meaningful rate once other confounds (verify build
level, error budget) are cleared:
1. Add an explicit warning in the task prompt: "call `ReplaceBlockFormatted` directly on the
   original file text — do not pre-replace the header yourself first."
2. Harden `ReplaceBlockFormatted` itself to be idempotent/more forgiving (e.g. if `oldBlock`
   isn't found but `newBlock` already is, treat as already-applied and return unchanged) — but
   this risks masking genuinely wrong fixes elsewhere, so prompt guidance is the safer first
   lever to try.
Don't act on this yet — first confirm its rate once the verify-phase build-level fix
(commit `7e77a31`) and the `MinimalGuidanceDisambiguated` plan-before-edit fix (commit
`7bfba2d`) have both had a clean batch to run against, so this bug's true rate isn't
confounded with the harness-level failures that were suppressing signal tonight.
