---
name: reference_lmstudio_reasoning_budget_message
description: "LM Studio's per-model 'reasoning budget' + injected budget message setting forcibly breaks repetitive reasoning loops by splicing a stop-and-answer phrase into the token stream at a token cap."
metadata: 
  node_type: memory
  type: reference
  originSessionId: 7fd7aa1e-52d8-4d4b-8cba-0a83771ffb77
  modified: 2026-09-08T15:57:28.876Z
---

LM Studio has a load-time model setting, **reasoning budget**, that caps how many reasoning
tokens a model may generate before a configured **reasoning budget message** string (e.g.
`"... okay, now I have enough information to answer."`) is forcibly appended to the model's
in-progress reasoning stream. This isn't a prompt addition — it's spliced directly into the
token sequence mid-generation, so the model's next tokens are conditioned on having just
"said" that phrase, which reliably pulls it out of the reasoning phase and into producing an
actual `Content:` response/tool call instead of continuing to loop.

**Confirmed live** in
`ModelTestingResults/113/Model_AppliesSevenChainedRefactors/20260908-145334-491/agent.log` —
11+ separate injection points across one run, every one following the same shape: the model's
reasoning trails into a repetitive dead end (variations of "Let me try...", "Wait, maybe I can
use...", "Actually, I just realized...") chasing an unsupported operation (creating a new file
via `ApplyUnifiedDiff`, which only patches existing files), and the injected message cuts it off
mid-sentence and forces a `Content:` continuation. E.g. line 363: `"That... okay, now I have
enough information to answer. Content: I'll work through all seven steps systematically."`

**Relevance to the wider repetition-loop investigation** (a known `PlanImplementVerify` failure
mode where the model repeats the same wrong fix rather than converging): this is the LM
Studio-native version of the mitigation floated as "force an off-ramp after N tokens of unresolved
reasoning." It doesn't fix the
underlying reasoning-depth ceiling (the model in this log still never solves "create a new
file," per line 4043's "the task explicitly says... in its own new file... let me just report
the current state and note this limitation") but it reliably converts an open-ended stall into a
bounded, terminating turn — same value proposition as the harness-level repetition-detector idea
discussed for the `Model_FixesWholeFileRewriteBug_PlanImplementVerify` loop case, but available
today via LM Studio's inference settings UI (select "reasoning budget," set a token limit) rather
than requiring new RoslynSentinel harness code.

**Confirmed enabled and working on the `.113` host** — this is not a proposal to go check, it's
the explanation for why `.113` runs (like the one above) recover from stalls that a host without
this setting would not. When a model-eval run on `.113` shows the classic in-generation
repetition-collapse signature (long unresolved reasoning oscillating between near-duplicate
paragraphs) it may still self-resolve via this mechanism rather than needing a harness-level
repetition detector — don't assume every such run will run to the wall-clock cap on this host.
If a *different* LM Studio host (e.g. `.112`) shows an uninterrupted repetition loop, that's the
signal to check whether this setting is configured there too, since it clearly isn't universal
across hosts by default. This is a per-model LM Studio load setting, not a RoslynSentinel/harness
setting — it won't appear in this repo's code; inspect/set it in LM Studio itself per
[reference_lmstudio_loaded_models_endpoint.md](./reference_lmstudio_loaded_models_endpoint.md)-style host inspection.

**Cap size tuned down 2026-09-08**: the run above used a 2048-token reasoning budget on
`.113`. The user judged that too generous (it still lets a lot of dead-end reasoning accumulate
before the injected message fires) and reduced it to **1024 tokens**. Not yet re-verified with a
fresh log at the new cap — if investigating a stall on `.113` after this date, check which cap
was actually active for that run's timestamp rather than assuming 2048 or 1024 from this memory
alone.
