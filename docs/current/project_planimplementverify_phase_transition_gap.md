---
name: project_planimplementverify_phase_transition_gap
description: "PlanImplementVerify-specific costs of the fresh-context-per-phase design: 9/39 runs (23%) never produce an implement/verify phase at all after a normal-looking plan phase (1 confirmed fixture-not-on-disk race, 8 unexplained); separately, 5/39 (13%) implement-phase runs stumble on a wrong workspace path and then reliably timeout on the tighter 25-turn/5-min budget rather than recovering."
metadata: 
  node_type: memory
  type: project
  originSessionId: 18d9cda6-eed8-4198-86a2-eaa21d82eb19
  modified: 2026-09-02T08:34:54.774Z
---

Found during the 2026-09-02 model-eval excavation
(`docs/current/model_eval_pattern_analysis_2026_09_02.md` §0, §2.6). Two
distinct gaps, both specific to PlanImplementVerify's three-separate-model-calls
design:

1. **Stuck-in-plan-phase (9/39 runs, 23%)**: run directory has a `plan/`
   subfolder but no `implement/`/`verify/`. One
   (`20260901-035746-273`) traced to the fixture file genuinely not existing
   on disk yet when the plan phase's workspace loaded — a test-harness
   `SetUp` timing race. The other 8 show the plan phase converging normally
   (readable, correct plan text, e.g. `20260901-202752-704`) with no
   explanation found for why the implement phase was never created — likely
   an unhandled exception/cancellation between `RunPhaseAsync` calls, not
   reproduced further. Currently scored as neither pass nor fail — pure lost
   signal.
2. **Implement-phase wrong-workspace stumble → reliable timeout (5/39, 13%)**:
   first `ReadFile`/`LoadSolution` targets a wrong/guessed path, gets a real
   `FileNotFoundException`. All 5 eventually self-correct the path — but all
   5 still end in `TurnCapExceeded`/`WallClockCapExceeded` rather than
   `ModelFinished`, because the implement phase's budget (25 turns / 5 min)
   is much tighter than the single-call variants' (40 turns / 30 min) and
   leaves no slack to absorb the stumble. One of the five instead crashed on
   a raw LM Studio serving error mid-generation (infra fault, not model
   reasoning).

**Why this matters**: this is a cost *introduced by* the
fresh-context-per-phase design itself — each phase re-discovers the workspace
from scratch with no memory of the plan phase's own successful navigation,
and the implement phase's tighter budget (chosen on the theory that a
narrower-scoped call needs less time) leaves little room for the same kind of
early stumble the single-call variants absorb comfortably. This is one
concrete answer to "why hasn't splitting into plan/implement/verify reliably
improved outcomes" — see [[project_modifymodifier_accessibility_footgun]] for
the other (bigger) one.

**How to apply**: if PlanImplementVerify is retained, either budget its
implement phase more generously or give it some form of workspace-path
continuity carried over from the plan phase (e.g. pass the plan phase's
resolved solution path into the implement phase's initial context) rather
than requiring rediscovery from zero. The 8 unexplained stuck-in-plan-phase
runs need a repro before they're actionable — start by adding
exception/cancellation logging around the plan→implement transition.
