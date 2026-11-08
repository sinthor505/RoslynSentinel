# Prompt: excavate ModelTestingResults for cross-run failure patterns

Copy everything below the line into a fresh Claude session.

---

You are analyzing archived transcripts from `RoslynSentinel`'s model-eval test suite —
automated tests where a small local LLM (qwen3-coder-9b, served by LM Studio) is given a
buggy C# repo and asked to fix it autonomously through an MCP tool server, with no human in
the loop. Each run's full transcript (every model turn, every tool call and its result) is
archived, and I want to find patterns across many runs that explain why the model sometimes
succeeds and sometimes fails badly, despite temperature=0.1 and near-identical prompts.

## Where the data is

`C:\Users\Administrator\source\repos\RoslynSentinel\ModelTestingResults\113\` — each
subdirectory under this is one test *name* (a distinct prompt/harness variant), and under
each test name are timestamped run directories (`yyyyMMdd-HHmmss-fff`). There's also a
`\112\` sibling directory (different LM Studio host) — lower priority, check `113` first
since it has far more runs archived.

Test names you'll find, in increasing order of scaffolding around the model:

- `Model_FixesWholeFileRewriteBug_MinimalGuidance` — single model call, vague/symptom-only
  prompt, full tool access, model must locate the bug, plan, and fix it all in one context.
- `Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated` — same shape, a prompt tweak
  that de-emphasized a "private" keyword that was biasing the model's diagnosis (see
  known findings below).
- `Model_PlansWholeFileRewriteFix_PrefersCallingHelper` — read-only tools, model only
  produces a prose plan, never executes it.
- `Model_FixesWholeFileRewriteBug_PlanThenExecute` — plan phase + execute phase as two
  model calls within the same test.
- `Model_FixesWholeFileRewriteBug_ScriptedPlan` — the model is handed a hand-written
  *correct* plan verbatim and only has to execute it (no bug-finding/planning of its own).
- `Model_FixesWholeFileRewriteBug_PlanImplementVerify` — three fully separate model calls,
  each with fresh context: plan (read-only) → implement (full tools, fed the plan-phase's
  own output) → verify (read-only, model judges its own fix and must emit `VERIFIED: PASS`
  or `VERIFIED: FAIL`). This one has `plan/`, `implement/`, `verify/` subdirectories inside
  each timestamped run dir instead of files directly in it.

Every run directory contains `agent.log` (human-readable turn-by-turn log, easiest to skim
first) and `transcript.json` (structured, easiest to grep/parse programmatically).

## transcript.json shape

```
{
  "SystemPrompt": "...",
  "UserPrompt": "...",
  "Turns": [
    {
      "ModelMessage": { "Content": "...", "ReasoningContent": "..." },
      "ToolCalls": [
        { "ToolName": "...", "ArgumentsJson": "...", "ResultJson": "...", "IsError": false }
      ]
    }
  ]
}
```

Important quirk: for this model, `Content` is frequently **empty** even on turns with
substantial reasoning — everything gets routed through `ReasoningContent` instead. Always
check both; don't assume an empty `Content` means the model said nothing.

## What's already known — don't rediscover these from scratch

- **ApplyDiff size guard gap (fixed, commit `579ead4`)**: the guard used to only measure
  line-count *shrinkage*; a whole-file comment-out (every line prefixed `//`) doesn't shrink
  line count so it slipped through undetected. Now fixed via a Roslyn-based active/non-comment
  line check. If you see a whole-file-comment-out run from *before* this fix, that specific
  failure mode is already understood and already patched — don't re-flag it as a new finding,
  but DO flag it if you see comment-out-style edits still slipping through in later runs.
- **"private" priming bug**: in `MinimalGuidance` runs, when the model's own reasoning
  mentioned the word "private" early on, it correlated strongly with failure (0/17 pass when
  "private" appeared vs 16/33 fail-rate otherwise) — the model was getting anchored on an
  accessibility-modifier theory of the bug that was wrong. `MinimalGuidanceDisambiguated`
  softened the prompt to reduce this priming; effect was real but partial (8/20 pass vs 34%
  baseline — not fully solved).
  - Related, separately fixed: a real `ChangeAccessibility`-on-a-helper-method failure
    signature existed across many runs (model changes a helper's accessibility instead of
    fixing the actual call site) — this was NOT just a red herring from the "private"-priming
    bug, it was a real recurring wrong-move pattern. Worth checking whether it still recurs in
    later PlanThenExecute/PlanImplementVerify runs, since those change the model's information
    diet in ways that might suppress or preserve it.
- **Planning, not execution, is the bottleneck**: `ScriptedPlan` (model executes a known-good
  plan verbatim) scored 5/5 with zero failed tool calls, vs ~20-40% when the model must find
  the bug and plan itself. This strongly suggests tool-execution reliability is NOT the
  limiting factor — bug localization and planning is.
- **Splitting plan/implement/verify into separate model calls did not obviously fix things**
  (per the human reviewing this data, prior to this analysis request) — some runs of even the
  most-scaffolded variant still fail or get "hopelessly lost," despite each phase getting a
  narrower, simpler job than the single-call `MinimalGuidance` variant. This is the central
  open puzzle: why would decomposing the task into simpler, isolated sub-problems not reliably
  raise the pass rate, if planning-under-load was really the bottleneck?
- **A specific failure signature worth hunting for elsewhere**: in one `PlanImplementVerify`
  run (`20260902-062730-159`), the *implement* phase's turn 2 `ReasoningContent` stated a
  clean, correct-sounding 2-step plan — but the 2 actual tool calls that followed were (1) a
  `ModifyModifier` call that failed/errored, then (2) an `ApplyDiff` call that commented out
  the *entire* target file, something never mentioned anywhere in the stated plan. Tentatively
  named **"reasoning-vs-tool-call divergence"** or **"silent action substitution"**: the
  model's own articulated reasoning and its actual tool-call payload diverge, seemingly
  triggered by the preceding tool error rather than by any reasoning step that led there. This
  is distinct from "wrong plan" (the plan was fine) and "lost in context" (only 2 tool calls
  deep, not deep in a long transcript). **Primary goal of this analysis: determine whether
  this pattern recurs across other failing runs** (in any test variant, not just
  PlanImplementVerify), and if so, whether it's specifically preceded by a tool-call error, or
  can also occur unprompted.
- Temperature=0.1 and a fixed LM Studio load-time seed do NOT make runs deterministic —
  small early sampling divergences compound across a long sequence of agentic decisions, and
  GPU floating-point reduction isn't perfectly reproducible run-to-run even at low temp. Don't
  treat "but the prompt was identical" as a reason to expect identical outcomes; focus on what
  *diverges* between a pass and a fail run of the same test name, not on why they differ at all.

## What to actually do

1. For each test name under `113\`, read enough `agent.log`/`transcript.json` files (sample
   broadly, not just the first few — check both early and late runs since later runs may
   reflect prompt/guard fixes made partway through) to determine pass/fail per run. A run's
   final phase transcript (or the only transcript, for single-phase tests) will usually make
   the outcome clear from the last turn's tool calls/results and any final model message; if a
   `results.csv` exists at a higher level, prefer it as ground truth over re-deriving pass/fail
   yourself.
2. For **failing** runs specifically, classify each into a failure signature — reuse the
   named ones above where they fit, and name new distinct ones where they don't. For each
   signature, note: which test variant(s) it appears in, whether it's preceded by a tool-call
   error, how deep into the transcript it occurs (early/mid/late), and roughly how often it
   recurs.
3. Specifically check whether "reasoning-vs-tool-call divergence" (see above) shows up outside
   the one already-known run — this is the single most-wanted answer from this analysis.
4. Compare pass rates *and* failure-signature mix across test variants — especially
   `PlanThenExecute` and `PlanImplementVerify` against `MinimalGuidanceDisambiguated` — to help
   explain why added scaffolding hasn't reliably improved outcomes. Consider whether new
   failure modes are being *introduced* by the scaffolding itself (e.g., a plan phase producing
   a plan that's technically fine but ambiguous enough that the implement phase interprets it
   differently than intended) rather than just failing to fix the old ones.
5. Report back: a ranked list of failure signatures by frequency, which are new vs already
   known, concrete run-directory paths as evidence for each, and any actionable next step
   (e.g., "add a guard for X," "the verify phase's own prompt seems to cause Y," "no pattern
   found, looks like genuine sampling variance"). If you find nothing conclusive for a
   question above, say so plainly rather than forcing a pattern onto noisy data.
