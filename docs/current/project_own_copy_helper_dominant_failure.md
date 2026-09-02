---
name: project_own_copy_helper_dominant_failure
description: "Dominant MinimalGuidance/Disambiguated failure mode: model's reasoning claims it's reusing the shared helper, but its actual ApplyDiff payload pastes a full second copy of the helper's body into the caller instead. 45-55% of runs. The Disambiguated prompt's explicit 'call it directly, don't copy its body' instruction did not reduce this — it shifted failures toward a sibling bucket instead."
metadata: 
  node_type: memory
  type: project
  originSessionId: 18d9cda6-eed8-4198-86a2-eaa21d82eb19
  modified: 2026-09-02T08:34:38.406Z
---

Found during the 2026-09-02 model-eval excavation
(`docs/current/model_eval_pattern_analysis_2026_09_02.md` §2.1/§2.2). Two
related but distinct buckets:

1. **Own-copy-of-shared-helper** (MinimalGuidance 45%, Disambiguated 55%): model's
   `ReasoningContent`/`Content` describes reusing `BlockEditHelpers.ReplaceBlockFormatted`,
   but the actual `ApplyDiff` pastes a full second, still-`private` definition
   of that method into the caller file. `BlockEditHelpers.cs` is never
   touched. Build passes, model reports success confidently — functionally
   correct output, but exactly the anti-pattern the fixture is designed to
   catch.
2. **Ignores the helper entirely, inlines own fix** (MinimalGuidance 18%,
   Disambiguated 45%, PlanThenExecute 9%): model fixes the actual bug but
   never engages `BlockEditHelpers.cs` at all, writing a one-off inline
   replacement that drops the padding requirement.

**Why this matters**: MinimalGuidanceDisambiguated's prompt tweak explicitly
told the model to call the helper directly rather than copy its body — aimed
squarely at bucket 1. It didn't reduce bucket 1 (55% vs 45%, worse if
anything) and made bucket 2 much worse (45% vs 18%). Combined failure rate
from these two buckets stayed roughly flat across the prompt change — the
disambiguation shifted failures sideways between buckets rather than reducing
total failures. See [[project_model_eval_baseline_corrected_2026_09_02]] for
the corrected pass-rate context this sits inside.

Notably, **PlanThenExecute achieves ~80% mechanical correctness (36/45) on
this identical ambiguity**, using the same disambiguating text plus only a
"state your plan before editing" instruction — suggesting the fix isn't in
further wordsmithing the ambiguity-closing sentence, but in structurally
forcing a committed plan before the first tool call, which single-call
MinimalGuidance-style prompts don't require.

**How to apply**: don't invest further in wording tweaks to the
MinimalGuidanceDisambiguated prompt alone — re-test with an added "state a
plan before your first edit" instruction instead, since that's the variable
that actually correlates with the ambiguity being resolved correctly
elsewhere in the corpus.
