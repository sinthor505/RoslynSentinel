# Eval: Edit-tool 3-arm comparison (ReplaceSnippet / ReplaceRange / EditSnippets) for a weak model

**Status:** DECISION 2026-11-23. All 90 jobs ended; owner ruling: keep EditSnippets, nothing is retired, the results inform the edit-tool surface only. Open follow-up: a ReplaceRange arm for `WholeFileRewriteAgentTests`.

## Context

Punchlist #21 (TODO.md line "Edit-tool 3-arm comparison for weak models (2026-11-21)", written up
there as the setup, session `todo-punchlist-G`; its purpose strings still say "#12"). It extends the
2026-11-09 two-arm A/B ([eval_edit_snippets_ab_2026_11_09.md](./eval_edit_snippets_ab_2026_11_09.md))
with a third edit tool, `ReplaceRange` ([proposal_replace_range_tool.md](../proposals/closed/proposal_replace_range_tool.md)),
and a new large-block fixture. The question: which single edit tool lets a weak model finish
multi-step refactors, and does a range-addressed edit avoid re-sending a large block?

Setup (as designed in the TODO line):

- Model `unsloth/Qwen3.5-4B-MTP-GGUF`, pinned checkout `9f8376f7`, host 85u, `evalqueue`, 90 jobs
  (ids `20261123-105231-001` .. `20261123-105402-001`), one run per job, 10 rounds x 3 tests x 3
  arms, interleaved.
- Arm chosen by env `ROSLYNSENTINEL_MODELEVAL_EDIT_ARM` (`ReplaceSnippet`, `ReplaceRange`,
  `EditSnippets`) through `EditArmToolSurface`; the model sees exactly one edit tool.
- Tests: `OrderPricingRefactor` (`Model_AppliesThreeChainedRefactors`),
  `OrderPricingRefactorChain4` (`Model_AppliesFourChainedRefactors`), and
  `ImplementationScenario -Scenario LargeBlockRewrite` (~6000-char block delete/replace; the new fixture).
- Table produced by `pwsh -NoProfile -File scripts\Get-EditArmTable.ps1`; the per-job CSV used here is
  `c:\tmp\arm-table-scope.csv` (90 rows, all `state=Finished`, all with a transcript). The means below
  were recomputed from that CSV.

## What is broken

Nothing is broken; this is a comparison. Results, passes out of 10 (per-cell means follow):

| Test | EditSnippets | ReplaceRange | ReplaceSnippet |
| --- | --- | --- | --- |
| LargeBlockRewrite (ImplementationScenario) | 2 | 10 | 7 |
| OrderPricingRefactor (3 chained refactors) | 9 | 5 | 7 |
| OrderPricingRefactorChain4 (4 chained refactors) | 10 | 10 | 8 |
| Total of 30 | 21 | 25 | 22 |

Means per cell (n=10 each; tokens are `Summary.InputTokens` summed over turns; `maxArg` is the longest
edit-call argument in characters):

| Test | Arm | Pass | Mean input tokens | Mean tool calls | Mean tool errors | Mean maxArg chars |
| --- | --- | --- | --- | --- | --- | --- |
| LargeBlockRewrite | EditSnippets | 2/10 | 554,612 | 20.2 | 11.4 | 7,097 |
| LargeBlockRewrite | ReplaceRange | 10/10 | 173,537 | 10.3 | 0.3 | 284 |
| LargeBlockRewrite | ReplaceSnippet | 7/10 | 502,078 | 20.3 | 5.6 | 4,026 |
| OrderPricingRefactor | EditSnippets | 9/10 | 409,766 | 13.4 | 2.0 | 1,963 |
| OrderPricingRefactor | ReplaceRange | 5/10 | 783,932 | 24.0 | 5.5 | 901 |
| OrderPricingRefactor | ReplaceSnippet | 7/10 | 453,659 | 14.5 | 1.7 | 1,760 |
| OrderPricingRefactorChain4 | EditSnippets | 10/10 | 243,292 | 14.9 | 1.3 | 1,089 |
| OrderPricingRefactorChain4 | ReplaceRange | 10/10 | 303,780 | 17.9 | 3.6 | 884 |
| OrderPricingRefactorChain4 | ReplaceSnippet | 8/10 | 258,322 | 15.6 | 1.7 | 1,076 |

Reading the numbers:

- LargeBlockRewrite is the one decisive cell. ReplaceRange passes 10/10 with a mean longest edit
  argument of 284 characters, far below the ~6000-char block: the block was not re-sent. The other two
  arms send the block (maxArg 4,026 / 7,097) and spend 2.9x-3.2x the input tokens; EditSnippets also
  averages 11.4 tool errors per run and passes 2/10.
- On the two refactor chains EditSnippets is best or tied (9/10 and 10/10). ReplaceRange is worst on
  OrderPricingRefactor (5/10, 24 calls, ~784k tokens, 5.5 errors) and ties for best on Chain4 at a
  higher token and error cost than EditSnippets.
- ReplaceSnippet is middle on every test.

## Root cause

Not traced per run. The measured contrasts above are the evidence; why ReplaceRange does worse on
OrderPricingRefactor (1.7x ReplaceSnippet's tool calls, 3.2x its tool errors) and why EditSnippets
does worse on LargeBlockRewrite (long batch arguments, 11.4 errors) was not examined turn by turn.
Both are hypotheses: line-addressed edits cost extra read/locate calls on a many-small-edits task, and
a batch of exact-text snippets is the hardest shape to transcribe when a block is large.

## Why it matters

The surface question is no longer "snippet vs batch": a range-addressed tool removes the transcription
burden of a large block for a 4B model (10/10 at a third of the tokens), which none of the exact-text
tools achieve. At the same time the exact-text batch tool is the best of the three on the chained
refactor tests, so no single arm dominates and the data does not support a one-tool surface.

## Recommendation

**Decided (owner ruling, 2026-11-23):** keep EditSnippets; no retirement. The data does not justify
removing it: it wins OrderPricingRefactor 9/10 and Chain4 10/10 and loses only LargeBlockRewrite
(2/10), where ReplaceRange 10/10 shows range edits avoid re-sending a large block. Read the ruling as
"results inform the surface, nothing is removed". This supersedes the TODO line's "retire
EditSnippets unless it wins" clause (EditSnippets tool class, mode, registry rows,
`McpToolSchemaPatcher` / `ErrorPolicyIntentTable` names, four test call sites all stay).

**Open:**

- Give `WholeFileRewriteAgentTests` a ReplaceRange arm (see caveat 3); that comparison is not run.
- Whether to repeat at larger n or on a second model before using any arm ranking to change a default
  (caveat 1). Not scheduled.
- Whether ReplaceRange's weak showing on OrderPricingRefactor is an environment defect (error text,
  description) worth a per-turn trace. Not scheduled.

## Caveats

1. **Small n, one model, one host.** One model (Qwen3.5-4B-MTP), host 85u, 10 runs per cell, one run per
   job. A difference of 1-2 passes (10-20 points) is within noise; only the LargeBlockRewrite
   ReplaceRange-vs-EditSnippets gap (10 vs 2, plus the token and argument-size contrasts) is large
   enough to read as a real effect. Rankings among the refactor-chain cells are indicative only.
2. **ReplaceSnippet text visible in the ReplaceRange arm (checked on 4 transcripts, leak confirmed).**
   I searched four ReplaceRange-arm `transcript.json` files for "ReplaceSnippet" (jobs
   `20261123-105233-001`, `-105237-001`, `-105243-001`, `20261123-105239-002`, one per test plus a
   second OrderPricingRefactor run; the 85u run folders under
   `RoslynSentinel-ScratchDir\ModelTestingResults\http___192_168_10_85_8888_v1\`). Every one contains
   the name, so the arm is not a clean single-tool surface:
   - All four: the `ReplaceRange` tool description ends "You send only those two lines, not the block;
     use ReplaceSnippet for small exact text." and the `ReadFile` `showLineEndings` description says "never
     paste it into ReplaceSnippet". (I classified these as tool-description text by their wording, not
     by their JSON position.)
   - One (`20261123-105243-001`, OrderPricingRefactor): a tool-result error text reads "Declare
     'CalculateDiscount' in the same edit as the code that calls it (the same newContent for
     ReplaceSnippet, the same newMemberSource for Member) ...", naming a tool the model cannot call.
   This is an environment defect on the arm (descriptions and error text name a gated-off tool), and it
   could have depressed ReplaceRange's OrderPricingRefactor score; the effect was not measured. The
   same check was not run on the other 26 ReplaceRange transcripts, nor on EditSnippets-arm transcripts
   (which presumably also see ReplaceSnippet text from shared descriptions; not checked). Fix direction:
   make arm-conditional description and error text, or strip cross-tool names under the arm surface, then
   rerun the ReplaceRange cells.
3. **WholeFileRewriteAgentTests has no ReplaceRange arm.** `ConfigureToolSurface` in
   `RoslynSentinel.Tests.ModelEval/WholeFileRewriteAgentTests.cs` (~:343-357) recognises only
   `EditSnippets` and falls back to ReplaceSnippet, and carries a `// TODO ReplaceRange arm not wired
   here` marker. The MinimalGuidance whole-file-rewrite comparison therefore remains open; the dispatcher
   keeps it in TODO.md.
4. **Shared queue.** Host 85's queue was empty at submit time but jobs from other sessions interleaved
   behind these at the same priority; no effect on pass/fail is known, but wall times are not comparable.

## Out of scope

- Per-turn failure analysis of individual runs (see Root cause).
- A second model or host.
- Changing any tool description, default arm or registry entry; this doc records results only.
