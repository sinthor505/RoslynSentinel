# Extract the agent-loop harness from RoslynSentinel.Common into RoslynSentinel.AgentLoop

**Status:** BUILT 2026-11-22 (commits 4b5db8de, 722be986, ae8937ab, 945170d9, 5f286df9, cab94f30, 1ec5ff24, b9232eb4, 7532633d, aa1dbdfd). Deviations: `ToolLoopLatch`/`LoopLatchOptions` stay in Common (Tools.Basic and Server.Advanced use them); `Utilities.PlanStepRunner` and `Tools.Advanced` reference AgentLoop (rule wording updated in reference_architecture_map.md); the `SubAgentEvalResult` tests also moved out of Tests.Basic (to Tests.SubAgent) so Tests.Basic drops its reference; the `LmStudioAgentClient`/`ReasoningReplayPolicy` cycle was broken first (policy owns the replay-window constants); files sit flat in `RoslynSentinel.AgentLoop/`. Earlier: APPROVED 2026-11-21, PROPOSED 2026-11-20.

## Motivation

`RoslynSentinel.Common` is a junk drawer: 324 classes, roughly 42.7k non-empty lines. Two consequences
matter here:

- **A half-finished edit in Common breaks every downstream build.** Today 23 projects depend on
  Common, so slices that edit Common cannot run in parallel with each other or with Engines/Tools
  slices.
- **The eval/agent harness lives in Common only by history.** `design/design_subagent_tool.md`
  (migration step 1, lines 322-343, and open item at line 405) left "Common vs. a new
  `RoslynSentinel.AgentLoop` project" explicitly open; the code ended up in Common without that
  question being closed. As a result, harness-only edits (for example the `StreamTruncated` stop
  reason, or `LmStudioAgentClient` log text) serialize with server-core work and recompile
  everything.

Splitting the server core further would not help: Engines and Tools depend on all of it. Extracting
only the harness would, because nothing server-side needs it (see Proposal, "Who references it").

Evidence below is from an explore-agent investigation with MCP tools on 2026-11-20, with spot checks
by the author of this doc (file existence, line counts, the two back-edges). Items not re-verified are
listed under Open questions.

### Size of the harness inside Common

| Group | Files | Lines |
| --- | --- | --- |
| `Common/AgentLoop/*.cs` (namespace `RoslynSentinel.Common.AgentLoop`) | 17 | ~3.4k (`ModelAgentRunner` 1100, `AgentTranscript` 611, `LoopAssist` 337, `RunSettings` 310, `AgentSystemPrompts` 223, `EvalHostTable` 206, ...) |
| `Common/LmStudioAgentClient.cs` (also holds `AgentChatMessage`, `AgentToolCall`, `AgentToolDefinition` and the stream exceptions) | 1 | 1292 |
| `Common/ReasoningReplayPolicy.cs` (only users: the client, the runner, `Tests/ReasoningReplayPolicyTests.cs`) | 1 | 181 |
| `Common/SubAgentEvalResult.cs` (optional; `Build(AgentRunResult ...)` at line 84-85 ties it to the harness) | 1 | 306 |

Total about 5.2k lines by `wc -l` (investigation estimate 4.7-5k; the difference is whether
`SubAgentEvalResult` and blank lines are counted), about 12% of Common.

## Proposal

Create a new project `RoslynSentinel.AgentLoop` (working name; see open question b) that references
`RoslynSentinel.Common` and nothing else in the solution, and move the harness into it. Layering stays
one-way: `Common` <- `AgentLoop`, with `Tools.Advanced` and `Utilities.PlanStepRunner` as consumers.

### What moves

- All of `Common/AgentLoop/*.cs` **except `ErrorPolicyDocument.cs`** (it is used by the server's
  `ErrorPolicyFilter` Inline mode and prompt composition, so it stays).
- `Common/LmStudioAgentClient.cs`
- `Common/ReasoningReplayPolicy.cs`
- `Common/SubAgentEvalResult.cs` (needed to break back-edge 2 below)

The namespace stays `RoslynSentinel.Common.AgentLoop` for now. That keeps every existing `using`
working and keeps the log categories that `Parse-AgentLog.ps1` matches on unchanged. (Decided for the
first cut; whether to rename later is open question b.)

### What stays in Common

- `LmStudioClient` and `ILlmClient`: the single-turn BulkComment client, used by `CommentingEngine`
  (`Engines.Advanced/CommentingEngine.cs:20,39`) and registered in
  `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs:29-34`. Not part of the agent loop.
- `PromptTokenEstimator`, `LlmOptions` (used by `ServerStdio.cs:43`, `ServerHttp.cs:31`,
  `CommentingEngine.cs:37`), and `GitWorktree/` (used by `PlanStepRunner` and the SubAgent tool).
- Server-side loop and error-policy types: `ToolLoopLatch`, `LoopLatchOptions` (which also hosts the
  shared flag helpers `GetFlagValue`/`ParseEnabled` used by `LiftRenamesOptions`, `NearMissOptions` and
  `NotFoundInventoryOptions`), `LoopLatchFilter` (in `Server.Advanced`), `NotFoundInventoryOptions`,
  `ErrorPolicyOptions`/`Classifier`/`IntentTable`, and `ErrorPolicyDocument`.
- `ErrorPolicyWiring` is harness-only (`Utilities.PlanStepRunner/Program.cs:239`) and moves.

### Who references the harness today

- `Engines.Basic`, `Engines.Advanced`, `Tools.Basic`, `Tools.Experimental`: no harness types.
- `Tools.Advanced` (SubAgent tool): `SubAgentModelRun.cs:101-111` builds `FlushingFileLoggerProvider`,
  `LmStudioAgentClient` and `ModelAgentRunner`; `SubAgentImpl.cs:118` uses `AgentTranscript`;
  `SubAgentEvalImpl.cs:49` uses `AgentSystemPrompts.CodingAgent`. There is no DI registration for the
  agent client.
- `Server.Advanced`: only `ErrorPolicyDocument` (`ErrorPolicyFilter.cs:25,31,45,93`;
  `ServiceRegistrationExtensionsBasic.cs:226`) - stays in Common, so no new reference needed.
- `Utilities.PlanStepRunner/Program.cs:95,97,223,239`: `FlushingFileLoggerProvider`,
  `LmStudioAgentClient`, `FilteringToolHost`, `McpClientToolHost`, `AgentToolAllowLists`,
  `ErrorPolicyWiring`, `AgentSystemPrompts`, `ModelAgentRunner`.
- Test files importing the namespace: `Tests.ModelEval` 58, `Tests.ErrorVariants` 20, `Tests` 4,
  `Tests.Basic` 2, `Tests.PlanStepRunner` 2, `Tests.Server` 2, `Tests.SubAgent` 2.

### What the harness needs from the core (all public, no cycle)

`LlmOptions`, `PromptTokenEstimator`, `ErrorPolicyOptions`/`Mode`/`Keys`, `ToolLoopLatch.RefusalMessage`,
`LoopLatchOptions.IgnoredRefusalLimit` (`ModelAgentRunner.cs:660-671`), and `NotFoundInventoryOptions.StampMode`
(tests only). It needs no `IWorkspaceManager`, `ToolCallContext`, `ActiveToolSurface` or engine, so the
dependency points one way.

### Back-edges to break first (verified 2026-11-20)

1. `Common/ToolLoopLatch.cs:126` (`RecordOutcome`) calls the **internal**
   `LoopAssistTracker.ExtractNextStep` (`AgentLoop/LoopAssist.cs:231`; also used at `LoopAssist.cs:121`;
   5 more uses in `RoslynSentinel.Tests`). Fix: move `ExtractNextStep` and the `NextStepMarker` constant
   to a small Common helper; `LoopAssistTracker` keeps a forwarding member so its own callers and tests
   do not change.
2. `SubAgentEvalResult.Build(AgentRunResult, ...)` (`SubAgentEvalResult.cs:84-85`) depends on a harness
   type. Fix: move `SubAgentEvalResult` into the new project. `Tools.Advanced` and three test projects
   that use it gain the new reference they get anyway.

### Migration-first slices

Per CLAUDE.md, new member beside old, finish in slices; CS0618 warnings in the middle are expected.

1. New `RoslynSentinel.AgentLoop` project referencing `Common`, with package references, the
   `InternalsVisibleTo` entries and a solution entry. Builds empty.
2. Back-edge 1: the `ExtractNextStep` helper in Common, `LoopAssistTracker` forwards to it.
3. Move the self-contained files; add `ProjectReference` from `Tools.Advanced`, `Utilities.PlanStepRunner`,
   `Tests.ModelEval` (explicit) and `Tests.ErrorVariants`.
4. Move `SubAgentEvalResult`; relocate stranded tests (below); repoint
   `Tests.Basic/BareFileWriteRatchetTests.cs:23-24`, which hard-codes the path
   `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`.
5. Update the architecture rule wording (below) and regenerate `docs/generated/architecture_projects.md`.
   Verify with `scripts/build.ps1 -Flavor Solution` and the baselines.

### InternalsVisibleTo and test placement

`Common.csproj:26-37` grants internals to `Tests`, `Tests.Tools.Basic`, `Tests.Server`, `Engines.Basic`,
`Tests.Basic`, `Tools.Basic`, `Tests.ModelEval`. Internals that move are at `LoopAssist.cs:209-306`,
`ModelAgentRunner.cs:45-71`, `LmStudioAgentClient.cs:743-863,1246,1255` and `ReasoningReplayPolicy.cs:93`.
So the new project needs `InternalsVisibleTo` for `Tests` and `Tests.ModelEval`; Common keeps
`Tests.ModelEval` (`ModelEvalAssemblySetUp` uses the internal `GitReaders.Default`).

Tests that would otherwise force `Tests`/`Tests.Basic` to reference the new project:
`Tests`: `LoopAssistTrackerTests`, `ReasoningReplayPolicyTests`, `AgentTranscriptFinalMessageTests`,
`LmStudioAgentClientInputItemsTests`. `Tests.Basic`: `ErrorPolicyWiringTests`,
`ErrorPolicyDocumentTests` (lines 212 and 227 only), and `ResultStatusTests` if `SubAgentEvalResult`
moves. **Recommendation (open, not decided): relocate these to `Tests.ModelEval` or
`Tests.ErrorVariants`** so `Tests` and `Tests.Basic` stop rebuilding on harness edits.

### Architecture rule wording

`Utilities.PlanStepRunner` references only Common (`...PlanStepRunner.csproj:34`), and that is written
as a rule in CLAUDE.md "Architecture" and `docs/current/references/reference_architecture_map.md:22`.
Referencing `AgentLoop` breaks the literal wording but keeps the intent (nothing server-side is
reachable; `AgentLoop` depends only on `Common`). Update the wording to "references only Common and
AgentLoop" and regenerate `docs/generated/architecture_projects.md` (line 32 is generated).

### Expected benefit and how to measure it

Today 23 projects depend on Common. After the split, a public-API edit to the harness stops
rebuilding `Engines.Basic`, `Engines.Advanced`, `Tools.Basic`, `Tools.Experimental`,
`Tests.Support.Basic`, `Tests.Tools.Basic` and `Battery.Basic` (7 of 23), plus `Tests` and `Tests.Basic`
if their harness tests move (9 of 23). `Tools.Advanced`, `Server.Advanced` and about seven test projects
still rebuild until the SubAgent tool gets its own project (open question c).

The parallelism benefit is separate and larger in practice: harness slices stop counting as Common
edits, so they can be paired with Engines/Tools slices.

**Acceptance/measure:**
- Before and after, touch one public `AgentTranscript` member and count recompiled projects (Build
  output, or the edit tools' affected-project count).
- `scripts/build.ps1 -Flavor Solution` builds with 0 errors.
- No new test failures vs `docs/known-failing-tests.*.txt` (baseline is zero failures).

### Timing recommendation

Do this after the current punchlist (2026-11-20 TODO review). The punchlist's critical path (#16, #25,
#28) is server-core Common work, which this extraction does not shorten; running the extraction first
would only add churn to the files those items edit.

## Alternatives considered, not pursued

- **Leave the harness in Common (status quo).** What happened by default;
  `design_subagent_tool.md` left the choice open and the code went to Common. Rejected as the
  steady state because of the build fan-out and serialization described in Motivation.
- **Split the server core of Common further (for example Common.Results, Common.Workspace).** Would not
  help: Engines and Tools depend on all of the core, so any core edit still rebuilds them, and the
  split adds project-reference churn across 23 consumers. Extracting the harness is the only cut where
  almost no server code is downstream.
- **Move the harness back under `Tests.ModelEval`** (its original home per the design doc). Rejected in
  that design doc: the live server (`Tools.Advanced` SubAgent) must not reference a test project.
- **Move the harness into `Utilities.PlanStepRunner`.** Hypothesis, not investigated in depth:
  `Tools.Advanced` and the test projects would then reference an executable-style utility project;
  a library project is the cleaner reference target.
- **Also move `LmStudioClient`/`ILlmClient`.** Rejected: single-turn client with server consumers
  (`CommentingEngine`), so moving it would add an `Engines.Advanced` -> `AgentLoop` edge for no gain.
- **Do the SubAgent project split in the same change.** Not rejected, deferred as open question c: it is
  the natural home for the remaining fan-out (`Tools.Advanced`, `Server.Advanced`), and
  `proposal_usage_based_core_split.md`'s "split Advanced into vertical cluster projects" (TODO.md) would
  carry it, but it is a larger, separate move.

## Open questions

a. **Does MSBuild reference-assembly behaviour already skip downstream compiles for body-only edits?**
   Hypothesis, unmeasured. If true, the build-time benefit is smaller than 7-9 of 23 for private-body
   edits; it bounds the build-time benefit only, not the parallelism one (a broken half-edit still breaks
   consumers). Measure first, with a body-only and a public-API edit.
b. **Project name and namespace.** `RoslynSentinel.AgentLoop` vs `RoslynSentinel.Utilities.*` vs a
   `Harness` name; and whether the namespace should eventually change from `RoslynSentinel.Common.AgentLoop`
   (kept for now to preserve usings and `Parse-AgentLog.ps1` log categories).
c. **Combine with the SubAgent project split?** Removes the remaining `Tools.Advanced` / `Server.Advanced`
   / test-project fan-out; larger scope.
d. **Reflection-based tests that enumerate Common types** (for example `ToolSignatureCatalogReflection`,
   `IOperationResult` implementers) have not been checked for assumptions about where these types live.
e. **Frozen-eval tree copies (`c:\tmp\rs_frozen`) and scripts** may hard-code paths beyond
   `Common/AgentLoop` (for example `LmStudioAgentClient.cs` at the Common root); not checked.
f. **`PackageReference` flow for `Microsoft.Extensions.Logging`** (and any other package the moved files
   use): which packages the new project must declare itself versus inherit transitively through
   `Common`. Not checked.
g. **Test relocation.** Whether to move the stranded tests to `Tests.ModelEval`/`Tests.ErrorVariants`
   (recommended above) or give `Tests`/`Tests.Basic` a reference.

## Cost / risk

- **Touches:** a new csproj and solution entry; about 5.2k lines moved (history is preserved with
  `git mv`-style moves only if the tooling does them; otherwise `git log --follow` is needed);
  `ProjectReference` additions in `Tools.Advanced`, `Utilities.PlanStepRunner`, `Tests.ModelEval`,
  `Tests.ErrorVariants` (and maybe `Tests`, `Tests.Basic`); `InternalsVisibleTo` edits in two csproj
  files; CLAUDE.md and `reference_architecture_map.md` wording; regenerated architecture doc; one
  path pin in `BareFileWriteRatchetTests`.
- **Risk:** internals reachability (missed `InternalsVisibleTo` shows up as CS0122 in tests, which is
  loud, not silent); a missed back-edge shows up as a circular-reference error (also loud); reflection
  or path-based tests and scripts (open questions d, e) could fail quietly or only on full runs.
- **Concurrency:** the move itself is a Common edit and must not run alongside other Common slices or
  other sessions touching these files (`Search` for `TODO` in each file before moving, per CLAUDE.md).
- **Makes harder:** one more project to maintain and one more reference for new harness consumers.
  The kept namespace means the namespace no longer matches the project name until a later rename.
- **Does not fix:** the junk-drawer problem for the remaining ~88% of Common, nor the `Tools.Advanced`
  / `Server.Advanced` fan-out (see open question c).

## Related

- `docs/current/design/design_subagent_tool.md` (lines 322-343 and 405: the original open "Common vs.
  new `RoslynSentinel.AgentLoop` project" decision; this proposal closes it in favor of the new project,
  pending owner approval).
- `docs/current/proposals/proposal_usage_based_core_split.md` (the Basic/Advanced tool split, not a
  Common split; its vertical-cluster idea in TODO.md is the home for a SubAgent project).
- `docs/current/references/reference_architecture_map.md` (rule wording to update).
- `docs/current/references/reference_model_eval_procedure.md` (eval hosts and filters; relevant to open
  question e).
- `docs/current/references/reference_parse_agent_log_script.md` (log categories that depend on the
  kept namespace).

## Decisions (todo review 2026-11-21)

Approved by the owner; unblocked because the 2026-11-20 punchlist is done. Review notes: `Common/AgentLoop`
now holds 18 files (`RunDirectoryArchive.cs`, `LlamaServerModels.cs` added since the inventory); a third,
cosmetic back-edge exists (`LlmOptions.cs:19,65` crefs to `LmStudioAgentClient`); the ratchet has 4 path
pins (`BareFileWriteRatchetTests.cs:23-27`), not 2.

- **a. Measure MSBuild reference-assembly skipping first?** Choice: no pre-measure; the measure is the acceptance check. Why: the parallel-dispatch goal does not depend on it.
- **b. Name and namespace.** Choice: project `RoslynSentinel.AgentLoop`, namespace kept. Why: keeps ~90 test usings and the `Parse-AgentLog.ps1` categories.
- **c. Combine with a SubAgent project split?** Choice: no. Why: SubAgent's place is settled by the tool-surface review (stays in main) and the Advanced cluster split.
- **d. Reflection tests over Common types.** Choice: none found; a full test run in the move slice is enough.
- **e. Scripts and frozen trees.** Choice: fix the path comments in `scripts/Backfill-TranscriptSummary.ps1` only.
- **f. Package flow.** Choice: declare nothing; add a package reference only on a build error.
- **g. Stranded tests** (`LoopAssistTrackerTests`, `ReasoningReplayPolicyTests`, `AgentTranscriptFinalMessageTests`, `LmStudioAgentClientInputItemsTests`, `ErrorPolicyWiringTests`, `ErrorPolicyDocumentTests`). Choice: move to Tests.ModelEval / Tests.ErrorVariants. Why: Tests and Tests.Basic stop rebuilding on harness edits.
- **Acceptance:** `scripts/build.ps1 -Flavor Solution` 0 errors; no new test failures against the baselines; touching one private and one public `AgentTranscript` member recompiles fewer projects than before the move.
