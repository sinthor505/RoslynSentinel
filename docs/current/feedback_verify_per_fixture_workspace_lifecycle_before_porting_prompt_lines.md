---
name: verify_per_fixture_workspace_lifecycle_before_porting_prompt_lines
description: "Ported PIV's 'solution is already loaded' line to WholeFileRewriteAgentTests' Disambiguated prompt on code-similarity alone; got a correction that turned out to be based on a mistaken premise, then confirmed live that the original port was actually correct"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T07:08:34.993Z
---

**UPDATE 2026-09-05**: the "correction" below was itself based on a mistaken premise — the user
later clarified they weren't aware `LoadSolutionAsync` runs during `[SetUp]`/`RunPhaseAsync` for
either fixture and assumed Disambiguated matched *other, older* test scenarios that genuinely do
require the model to call `LoadSolution` itself. Re-added the orientation line and this time
verified live: turn 1 of the archived run at
`ModelTestingResults/113/.../MinimalGuidanceDisambiguated/20260905-065222-325/agent.log` goes
straight to `ReadFile` with zero `ListWorkspaceSolutions`/`LoadSolution` calls. So the original
code-level reasoning (both fixtures pre-load identically, the line is safe) was correct all
along; the revert was the actual mistake, not the port. **The bottom-line procedural lesson
still holds** — a direct correction from someone watching live runs is a strong signal and code
reading alone didn't settle the disagreement — but this time the fix was to get both sides
verified against a live transcript rather than to simply defer to the correction. Don't overcorrect
into always trusting a correction over a structural code read either; when the two disagree, that
itself is the signal to go get live evidence before either action.

Added PlanImplementVerifyAgentTests' "The solution is already loaded — do not call
ListWorkspaceSolutions or LoadSolution" line to
`WholeFileRewriteAgentTests.DisambiguatedMinimalGuidanceUserPromptTemplate` because both
fixtures' `[SetUp]`/`RunPhaseAsync` call `IWorkspaceManager.LoadSolutionAsync` in-process before
the model's first turn — mechanically they looked identical from reading the source. User
corrected: Disambiguated's model still needs to call `LoadSolution` itself; only PIV's version of
that pre-load makes the line safe to include.

**Why**: I confirmed the code-level mechanism (both call `LoadSolutionAsync` pre-turn, same
`IWorkspaceManager` instance the model's own MCP tools operate against) but that isn't sufficient
grounds to assume the *prompt claim* is safe to copy across fixtures — the two fixtures differ in
per-phase lifecycle (PIV: three isolated phases, each with its own fresh host that reloads the
solution every single time, guaranteeing the claim is always true; WholeFileRewriteAgentTests:
one `[SetUp]`, one model call) and the user flagged that difference matters even though I hadn't
located the exact mechanism that makes it matter. Trusted a structural code read over a direct
correction from someone who watches these runs live — should have deferred to the correction
first and treated the code-reading as supporting evidence, not the deciding vote, especially
since I never actually saw a live transcript proving the line was safe before adding it.

**How to apply**: before porting a prompt line between model-eval fixtures — especially one
making a factual claim about environment/tool state ("X is already loaded", "Y is unavailable")
— check that fixture's actual live transcript behavior (or ask), not just its setup code's
apparent structural similarity to another fixture where the claim was already verified. A claim
that's true for fixture A because of a specific per-phase reload guarantee is not automatically
true for fixture B just because both call the same underlying method during setup. See
[[project_planimplementverify_promptcontext_solution_preloaded]] for the original, correctly
verified case this was ported from.
