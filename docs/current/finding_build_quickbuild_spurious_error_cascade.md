# Finding: `Build(quickBuild)` intermittently reports a massive phantom error cascade on a clean worktree

**Status:** DECISION (2026-09-14, Phase 4 of manual-selfrun-20260914-remediation-v1, investigate
only — no `.cs` fix applied this phase, per plan). Root cause now traced to source with strong
circumstantial evidence (not re-reproduced on a 3rd worktree this run — see "What wasn't done"
below); the plan's original ordering hypothesis is refined, not confirmed as stated. See
"Decision" section below before reading the rest of this doc as the live analysis.

## Decision (2026-09-14, Phase 4)

Traced both build paths to source instead of taking the "dependency-ordering" hypothesis at face
value, per this phase's investigate-only instruction.

**`quickBuild`'s actual mechanism:** `RoslynSentinel.Basic/BuildEngine.cs`'s `RunQuickBuildAsync`
(solution-scope path) calls `DiagnosticEngine.GetSolutionDiagnosticsAsync`
(`RoslynSentinel.Basic/DiagnosticEngine.cs`), which loops `foreach (var project in
solution.Projects) { var compilation = await project.GetCompilationAsync(...); ... }` against the
**already-loaded, in-memory `MSBuildWorkspace`/`Solution`** held by `PersistentWorkspaceManager`.
There is no MSBuild-style sequential "build project A, then project B" pass here at all — Roslyn's
demand-driven `Compilation` model is supposed to resolve `ProjectReference`s transparently
regardless of `solution.Projects` iteration order, so the plan's literal framing ("quickBuild
picks up Advanced before Basic/Common are ready in the same pass") does not match how this code
works: there is no per-project sequential "readiness" for this loop to race.

**`fullBuild`'s actual mechanism:** `RunFullBuildAsync` in the same file does not touch the
in-memory workspace at all — it shells a brand-new `dotnet build <solutionPath>` **subprocess**,
which performs its own independent MSBuild restore/resolve/compile from scratch. This is why
`fullBuild` run immediately after a phantom `quickBuild` cascade reports 0 errors: the two paths
share no state.

**Refined hypothesis (source-grounded, not yet re-reproduced to fully confirm):**
`LoadSolutionAsync` (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:433`) calls
`_workspace.OpenSolutionAsync(solutionPath, null, cancellationToken)` directly — no `dotnet
restore` call, and no wait for one, anywhere in that method. `MSBuildWorkspace.OpenSolutionAsync`
does not itself restore; it depends on `obj/project.assets.json` and related restore artifacts
already being current on disk. A freshly created `git worktree add` checkout has source files but
not a prior worktree-specific restore — if `LoadSolution` is called against such a worktree before
`dotnet restore` has completed there, `OpenSolutionAsync` will "succeed" but the resulting
`Compilation`'s metadata references for NuGet-sourced assemblies (e.g. `Microsoft.CodeAnalysis`,
exactly the namespace named in the reported `CS0234`) will be missing/stale — producing a large,
uniform cascade across every `.cs` file in the affected project that references those APIs
(`RoslynSentinel.Advanced`, which depends heavily on `Microsoft.CodeAnalysis.*`). This is a
restore/workspace-load-timing race specific to fresh worktrees, not a project-compile-ordering
race within `quickBuild` itself.

**What wasn't done:** did not reproduce on a 3rd independent worktree this run to directly observe
the restore-timing race in progress (e.g. checking `obj/project.assets.json`'s mtime/existence at
the moment `quickBuild` was called vs. when `LoadSolution` ran) — the trace above is a precise,
source-grounded explanation consistent with every detail of both reproductions (fresh worktree
only, `CS0234`/`CS0246` specifically on `Microsoft.CodeAnalysis`-dependent `RoslynSentinel.Advanced`,
and `fullBuild`'s independent restore succeeding immediately after), but is not a live-observed
confirmation. A future session reproducing this should check restore-artifact timestamps at the
moment of the `LoadSolution` call that preceded the failing `quickBuild`, rather than guessing
further.

**No `.cs` fix implemented this phase**, per the plan's investigate-only instruction for Phase 4.
If the restore-timing hypothesis is confirmed, the natural fix is for whatever drives
`LoadSolution` against a freshly created worktree (PlanStepRunner's worktree setup, most likely)
to run/await `dotnet restore` before calling `LoadSolution` — not a change inside `BuildEngine`
itself, since `quickBuild`'s only defect (if this hypothesis holds) is trusting a workspace that
was loaded too early, not anything wrong in how it reads that workspace.

**Prompt-text sub-task:** searched this repo (excluding `C:\RoslynSentinel-TestRuns\` run
artifacts) for a standing, reusable PlanStepRunner/model-eval prompt template instructing a model
to treat `quickBuild`'s error list as authoritative — found none. The "authoritative" instruction
the original finding cites was specific to one run's generated plan-step text under
`C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\`, not a persistent template asset in
this repo, so there was no standing `.md`/prompt file to correct here. The one standing,
reusable, model-facing text that could carry this caution is `Build`'s own tool `[Description]`
in `RoslynSentinel.Server.Basic/SentinelWorkspaceTools.cs` — but that is a `.cs` attribute string,
and editing it would be a code change, which this phase's own instruction excludes ("investigate
only — no fix"). Flagging as a recommended follow-up instead of doing it here.

## Recommendation (updated)

- Before implementing any fix, confirm the restore-timing hypothesis by checking
  `obj/project.assets.json` state at the moment of the `LoadSolution` call that preceded a failing
  `quickBuild`, ideally via a debugger or added diagnostic logging on a fresh worktree reproduction.
- If confirmed, the fix belongs in whatever calls `LoadSolution` against a newly created worktree
  (most likely PlanStepRunner's setup path) — ensure `dotnet restore` runs and completes first —
  rather than in `BuildEngine`/`DiagnosticEngine`.
- Separately (independent of the above): add a caution to `Build`'s `[Description]` for
  `quickBuild` noting that a sudden very-high error count, especially `CS0234`/`CS0246` on the
  solution's own base namespaces, can indicate an unrestored/stale workspace rather than real
  source errors, and suggest cross-checking with `fullBuild` before acting on it. Not implemented
  this run (out of Phase 4's investigate-only scope).

---

## Original finding (below, refined by the Decision above)

**Status:** confirmed recurring defect, not yet fixed, root cause not traced. Found during
self-run `manual-selfrun-20260914-004605` — first seen step 08 (dismissed as a possible one-off),
2nd reproduction step 10 (escalated to a recurring defect worth flagging).

## Context

`Build(quickBuild)` against a fresh, independent worktree returned a ~26,700-error cascade of
`CS0234`/`CS0246` errors across `RoslynSentinel.Advanced` (e.g. "The type or namespace name
'CodeAnalysis' does not exist in the namespace 'Microsoft'"). `Build(fullBuild)` run immediately
after, against the same unchanged worktree, reported 0 errors — confirming the cascade was never
real.

Same failure shape occurred on two separate, independent worktrees (steps 08 and 10), which
upgrades it from "non-reproducible curiosity" to a recurring defect.

## Why this matters

Step 10's own plan text explicitly instructed relying on `quickBuild`'s error list as "the
authoritative list" of broken callers when widening a return type. Trusting that instruction
here would have meant chasing ~26,700 phantom errors instead of the 2 real caller fixes that
`fullBuild` correctly identified. This silently defeats the one scenario (fast-path error
triage) `quickBuild` exists for.

## Root cause (not traced this run — candidate hypothesis only)

Candidate: `quickBuild`'s scoped/incremental compile picks up `RoslynSentinel.Advanced` before
its `RoslynSentinel.Basic`/`.Common` dependencies are ready in the same pass, so it briefly sees
an incomplete/stale reference graph and reports cascading missing-namespace errors that a full,
correctly-ordered build never hits.

## Recommendation

- Investigate `quickBuild`'s project build ordering — confirm whether it always waits for
  dependency projects to finish before compiling a dependent project, or whether it can start a
  dependent's compile against a partially-built dependency.
- Until root-caused, treat a `quickBuild` error list with sudden high volume (thousands of
  errors, especially `CS0234`/`CS0246` on the tool's own base namespaces) as a signal to
  cross-check with `fullBuild` before acting on it, rather than trusting it as authoritative.
- Consider updating any plan/prompt text that tells a model to treat `quickBuild`'s error list
  as authoritative, since this run showed that advice would have been actively unsafe.

## Reference

- Session log: `C:\RoslynSentinel-TestRuns\manual-selfrun-20260914-004605\findings-log.md` —
  step 08 (first occurrence), step 10 (~line 1177, 2nd reproduction).
