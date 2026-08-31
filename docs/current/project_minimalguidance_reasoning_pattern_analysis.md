---
name: project_minimalguidance_reasoning_pattern_analysis
description: "Reasoning-level analysis of .113's 50-run MinimalGuidance batch: pass/fail is predicted by how the model interprets 'reuse that same approach' at the moment it finds the private helper — passing runs read it as 'copy the pattern', failing runs read it as 'call/expose the method'. 3 distinct failure modes identified, quoted verbatim."
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-08-31T19:53:29.955Z
---

Follow-up to [[project_overnight_50run_sweep_2026_08_31]] — extracted and compared the
model's `ReasoningContent` field (recorded live per-turn in `transcript.json`, see
[[project_model_eval_streaming_responses_api]]) across all 50 `.113` MinimalGuidance runs
(17 pass / 33 fail) to find what actually distinguishes success from failure, rather than
just tallying outcomes.

**The single strongest predictor found:** whether the word "private" appears anywhere in
the model's reasoning. It appears in **0/17 passing runs** and **16/33 (48%) failing
runs** — a cleaner signal than turn count, breaker-trip status, or anything else measured.
"public"/"accessib"/"ChangeAccessibility" show the identical 0% vs ~40% split, since
they're downstream of the same reasoning move.

**The fork point, quoted verbatim.** All 50 runs reach the same moment: `ListAll`/search
surfaces `BlockEditHelpers.ReplaceBlockFormatted`, a `private static` method in a
reference-only sibling file. What the model says NEXT determines the outcome almost
deterministically:

- **Passing runs** (17/17, no exceptions): *"I see `ReplaceBlockFormatted` in
  `BlockEditHelpers.cs`. Let me read that file to find the reusable pattern."* /
  *"Found it. ... that's the pattern to reuse."* — treats the method as source text to
  transcribe into `BlockConverter.cs`. Never mentions accessibility at all.
- **Failing runs** (16/33, near-verbatim across independent runs): *"The
  `ReplaceBlockFormatted` method is `private static` in `BlockEditHelpers`. I need to make
  it `public static` so `BlockConverter` can call it."* — immediately calls
  `ModifyModifier` or `ChangeAccessibility` on the reference-only file. Treats the method as
  something to invoke cross-class, and its `private` modifier as an obstacle to clear
  rather than a boundary to respect.

The prompt says *"reuse that same approach"* (never "call it" or "copy it" explicitly) —
this ambiguity is resolved differently turn-to-turn by the same model at the same
temperature/sampling config, and which resolution it lands on is the dominant outcome
variable.

**Three failure modes identified among the 33 fails** (see
[[project_repeat_penalty_ab_test]] for the `ChangeAccessibility`-on-helper signature this
extends):

1. **`ChangeAccessibility`-on-helper (16/33)** — described above. The largest single mode.
2. **Re-invents its own helper (8/33)** — never calls `ReplaceBlockFormatted` at all; writes
   a new, similarly-purposed method under a different name (`ReformatBlock`, etc.),
   "inspired by" rather than reusing the pattern. Fails
   `Does.Contain("ReplaceBlockFormatted")`. One sub-case in this bucket
   (`20260831-085110-511`) is more insidious: the final code's **doc comment** claims *"Uses
   BlockEditHelpers.ReplaceBlockFormatted to re-indent only the converted block"* while the
   method body actually just does `return rewritten;` — a confident, plausible-sounding lie
   that would pass casual review. Caught by regex-matching for a real call
   (`return\s+(\w+\.)?ReplaceBlockFormatted\(`) vs. a bare textual mention.
3. **Excessive thrashing (7/33)** — model eventually writes a genuinely correct call to
   `ReplaceBlockFormatted`, but racks up ≥2 failed tool calls (bad `ApplyDiff` attempts,
   etc.) getting there, tripping `AssertFixApplied`'s "at most 1 failed tool call" gate
   (`WholeFileRewriteAgentTests.cs:354`). One of these (`20260831-095717-944`) never
   converged at all within the 40-turn cap.

**Why this matters more than the sampling-param work:** [[project_repeat_penalty_ab_test]]
found Repeat Penalty 1.1 reliably kills this task (0/6), but even the BEST sampling config
found (RP disabled, Top P 0.7) only reaches 34% on MinimalGuidance specifically — see
[[project_overnight_50run_sweep_2026_08_31]]. Sampling params bound the *ceiling*; this
reasoning-fork analysis explains *why* the ceiling is where it is, and points at a fix on
the prompt-engineering side rather than the sampling side.

**How to apply:** implemented as a tightened prompt variant that removes the "reuse that
same approach" ambiguity — `DisambiguatedMinimalGuidanceUserPromptTemplate` /
`Model_FixesWholeFileRewriteBug_MinimalGuidanceDisambiguated` in
`WholeFileRewriteAgentTests.cs` (commit `5a7ee29`), runnable via
`roslynsentinel-modeleval.ps1 <host> MinimalGuidanceDisambiguated`. It adds one sentence:
"If the existing fix lives in a private method in another file, treat it as a pattern to
copy into your own fix, not a method to call directly — do not change any other file's
access modifiers." Not yet run. If it meaningfully raises the pass rate over the 34%
baseline on the same sampling config (RP disabled, Top P 0.7), that's a stronger and
cheaper lever than any further sampling-param tuning. Confirm with a fresh N=50 run before
concluding it worked, per the lesson of [[project_repeat_penalty_ab_test]] (3-4 run samples
proved too noisy to trust; this batch's 34% vs the earlier night's snapshot 75% is the
cautionary example).

**Methodology note:** reconstructing pass/fail and failure-mode classification from
`transcript.json` after the fact (rather than trusting `AssertFixApplied`'s live NUnit
result, which isn't persisted anywhere queryable) requires resolving the file's TRUE final
state — the last `ApplyDiff`'s `changes` payload if it's after the last `ReadFile` by tool-
call index, not just "the last ReadFile seen," and checking for a REAL method call
(regex against `return\s+X\(` / `=\s*X\(`) rather than a bare substring match, since a
model can mention a method name in a doc comment without calling it. Scripts used:
`C:\tmp\analyze_minimalguidance.ps1` (pass/fail reconstruction),
`C:\tmp\extract_reasoning.ps1` + `analyze_reasoning_patterns.ps1` (reasoning extraction and
keyword frequency by pass/fail group) — not committed to the repo, scratch analysis only.
