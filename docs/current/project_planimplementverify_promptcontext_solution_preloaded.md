---
name: planimplementverify_promptcontext_solution_preloaded
description: "PlanImplementVerify prompts never told model the solution is pre-loaded, causing wasted ListWorkspaceSolutions(\"/\") guess every phase; fix drafted but not yet verified"
metadata: 
  node_type: memory
  type: project
  originSessionId: baae58f2-ea41-48a8-b6da-6d65bc32d78d
  modified: 2026-09-05T04:42:16.518Z
---

User-identified finding (not self-discovered): in `PlanImplementVerifyAgentTests.cs`, each phase
(plan/implement/verify) gets a fresh MCP host with its own `IWorkspaceManager`, into which
`_fixture.SolutionPath` is always pre-loaded via `workspaceManager.LoadSolutionAsync(...)` *before*
the model's first turn (`RunPhaseAsync` ~line 365) — but none of the three prompt templates
(`PlanUserPromptTemplate`/`ImplementUserPromptTemplate`/`VerifyUserPromptTemplate`) ever told the
model this. Every phase's first turn wastes a call on `ListWorkspaceSolutions`/orientation (usually
guessing `workspacePath:"/"`) that was always unnecessary — see
[[project_listworkspacesolutions_driveroot_hang_fixed]] for the related tool-level bug this
guess used to trigger (now just a fast `InvalidArgument` + self-correct via `ListAll()`).

**Fix drafted**: added one line after the `# Task:` header in all three prompt templates:
"The solution is already loaded — do not call ListWorkspaceSolutions or LoadSolution, go straight
to ReadFile/SearchSolutionText/ListAll on the path below."

**Status as of 2026-09-04: NOT YET VERIFIED.** The edit was made but a subsequent granite-4.2-8b
run (`20260905-040447-961`) still showed the model calling `ListWorkspaceSolutions("/")` as turn 1
in the implement phase — meaning either the build wasn't picked up before that run started, or the
prompt fix doesn't fully suppress the behavior. Re-verify with a fresh build + fresh run before
trusting this fix is live.

**Why**: distinguishing this from the tool-level bug matters — the tool fix prevents the *hang*,
this prompt fix is meant to prevent the *wasted turn* from happening at all. They're complementary,
not the same fix.

**How to apply**: before crediting this fix, confirm (a) `RoslynSentinel.Tests.ModelEval` was
rebuilt after the prompt edit, (b) a fresh run's transcript no longer shows a
`ListWorkspaceSolutions` call as any phase's turn 1.
