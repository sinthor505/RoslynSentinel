# Plan: replay a saved transcript with a substituted error text through the real agent loop and a fake tool host, in a Common-only test project

**Status:** IN PROGRESS 2026-11-13. Steps 1-11 done (a99ed363, d1b30b9f, 51d6edb0, 6dc49057, 129da71e, cca1ac06, 1845498c, 2215c3dc, e1e8aac8, 8c773d97, 09ebacf1, c3efa20e, e5c85478, 069455d6). Operation-aware classification: 1367cda4, 379ffebe. Regenerate mode: 3246bedc. Step 12 ran: faithful for large effects, but loses arm F's live effect (`evals/eval_error_variant_replay_validation.md`). Steps 13-14 are on hold for an owner decision (coarse screen or stop).

## Problem

Each error-text arm in `plan_error_policy_pointer` (closed) cost a production edit in `Common`, a build, peer HOLDs and a full live run, and the builds between arms carried unrelated commits (proposal, "Motivation"). The first-miss decision is one model turn, so it can be probed from a saved transcript cut after the first error, with the error text replaced.

Source review (2026-11-13), beyond what the proposal states:

- **The runner and client live in `RoslynSentinel.Common`**, not in ModelEval. `ModelAgentRunner` uses `McpClient` at exactly two sites: `ListToolsAsync`, mapped to `AgentToolDefinition` (`Common/AgentLoop/ModelAgentRunner.cs:185-191`), and `CallToolAsync` (`:816`).
  - `ExecuteToolCallAsync` (`:797-845`) turns the `CallToolResult` into `ResultJson`, which is the first text block, and `TrailingText`, which is the remaining text blocks joined by a blank line.
  - `ModelFacingToolContent` (`:849-850`) shows the model `ResultJson + "\n\n" + TrailingText`.
  - A fake host that returns a `CallToolResult` with those two text blocks therefore reproduces the recorded bytes, with no change to the loop.
- **`RunAsync(seedTurns:, targetReached:)` already supports cutting and stopping** (`:174-181`):
  - `turnCap` (constructor, `:130`) must include the seeded turns.
  - `targetReached` is evaluated after each live turn and stops the run with `TargetReached`.
- **ModelEval references `Common`, `Server.Advanced` and `RoslynSentinel.Tests`** (`RoslynSentinel.Tests.ModelEval.csproj:38-40`). Any change anywhere in the solution therefore rebuilds Engines, Tools, the server and the 9,940-test project before a ModelEval test can run, and a peer's broken edit in any of them blocks it.
- **Envelope:**
  - The error text is `errorData.message`, beside `errorData.errorCode`. The code is `FileNotFound` for the ReadFile miss and `NotFound` for the Search symbol miss.
  - The policy block is the record's separate `TrailingText`, not part of `ResultJson`.
  - A `Search mode: text` miss is not an error: it returns `isError:false` with the wording in `statusMessage`.
- **`AgentToolCallRecord` is a sealed class with `required init` properties**, so an override builds a copy, as `RewriteTurns` does (`TranscriptResume.cs:279-291`).
- **Reasoning:** `BuildInputItems` treats seeded and live turns alike, and `ReasoningDroppedTurns` drops nothing below 20 assistant turns (`LmStudioAgentClient.cs:486-495`).
- **Not recorded anywhere: temperature, top-p and model key.**
  - Temperature and top-p default to `null`, so LM Studio's own default applies. They are set only when `roslynsentinel-modeleval.ps1 -Temperature/-TopP` is passed (`:429-448`).
  - The model key appears only in `agent.log`.
  - Mode and hash in the transcript header do not separate arms F and G; only `CommitHash` does.
- **No tool sets `ReadOnly`/`Destructive`** (`SentinelToolFactory.cs:185-202`), so the read/write split needs a hand list.
- **The identity data is extractable.** In all 30 E/F/G MissingFile transcripts, turn 1 is exactly one erroring `ReadFile`. The first calls at turn 2 match the proposal: E `ListSolutionItems` 7 / `Search` 3, F `Search` 10, G `Search` 7 / `ListSolutionItems` 3.

## Decision

Build a new test project, `RoslynSentinel.Tests.ErrorVariants`, that references only `RoslynSentinel.Common`. It runs the real `ModelAgentRunner` against a fake tool host built from a saved transcript. No MCP server, fixture or build of the code under test is needed, and an edit-build-test cycle compiles two projects.

Per sample:
1. Load `transcript.json`.
2. Cut it after the first error turn.
3. Splice the variant text into that call's `errorData.message`, and set its `TrailingText` per the variant.
4. Seed the runner with the cut turns.
5. Run it with `turnCap` = cut + N (N = 1 for one-step).
6. Classify the first new turn and write a `variants.csv` row.

Each sample also leaves a normal `transcript.json`, which the existing parse scripts read.

Settled here (owner may overturn):

1. **Tool-host seam in `Common`.**
   - Add `IAgentToolHost` with two members: `ListToolsAsync` returning `AgentToolDefinition`s, and `CallToolAsync` returning the SDK `CallToolResult`. No new DTO.
   - Add `McpClientToolHost`, an adapter over the MCP client.
   - Add a `ModelAgentRunner` constructor that takes the interface. The existing constructor wraps its `McpClient` in the adapter, so no caller changes.
   - The seam is text-neutral, replaces the earlier "extract the message builder" step, and keeps one loop for live and replay.
2. **Shared helpers move to `Common`; ModelEval keeps its members, delegating.**
   - The saved-transcript loader moves. Prefer deserializing `AgentTranscript` itself, the type the runner writes. Otherwise add a `Common` copy of `ResumeSource` and its `Load`/`KeepTurns`.
   - The `BLOCKED:` marker tests move (`ScenarioScorer.cs:335-338`).
   - `ContainsToolCallMarkup` becomes public (`ModelAgentRunner.cs:699-713`).
   - This is new-beside-old, not a type move, because cross-project MoveType has an open bug (engine reorg bug 2).
3. **Kept out of `Common`, the shipped library:**
   - The results archiver is copied into the new project with a deviation comment and a TODO to unify.
   - The error-policy hash normalization (about 5 lines) is copied the same way.
   - `AbsentTargetNames` is a run parameter; the project does not reference ModelEval scenarios.
4. **Variants target error envelopes only:** `isError:true`, path `errorData.message`, keyed by `errorCode`. Other envelope fields stay as recorded. `statusMessage` targets get a TODO.
5. **Byte fidelity:** the override splices the new JSON string value into the recorded `ResultJson` and leaves every other byte alone; a default encoder would turn `<` into `<`. A unit test asserts that an unchanged variant gives byte-identical `ResultJson`.
6. **Continuation is on-script only.**
   - After the cut, the fake host answers a call from the source transcript's later turns, matched by tool name plus canonical arguments, and only for read tools.
   - Any other call returns a fixed placeholder and sets an off-script flag. `targetReached` reads that flag and stops the run, and the row records `OffScript:<tool>`.
   - Sticky override is on: a served recorded result whose `errorCode` and normalized message equal the overridden call's gets the variant too. Otherwise the arm's own text would leak back in.
7. **`{SOLUTION}`** resolves to the fixture directory recorded in the source transcript, the `RoslynSentinelTests_<guid>` path, keeping the slash style of the surrounding text.
8. **Read/write split:** a hand list, with a unit test that every tool in one checked-in transcript header is listed. An unlisted tool becomes `Other:<tool>`.
9. **Sampling:** set through the env (`ROSLYNSENTINEL_LLM_TEMPERATURE` / `_TOP_P`) and written to every row. Live runs also start recording the model key, temperature and top-p (Step 10), so a later replay can refuse a mismatch.
10. **Sample size:** 10 source histories x 3 samples = 30 per variant cell, compared paired by source history.

### Owner rulings and pre-implementation review (2026-11-13)

- **Owner rulings.** The plan is approved. Steps 1-11 run now, with the owner away, so decisions are deferred with a recommendation. The run uses Batched mode: batch A is Steps 1-3, batch B is Steps 4-8, batch C is Steps 9-10. Step 11 is a harvest done outside the implementer lanes, alongside batch B.
- **Step 1.**
  - The project goes in `RoslynSentinel.slnx`. `scripts/Test-Parallel.ps1:24` discovers it by the `RoslynSentinel.Tests*.csproj` pattern.
  - It needs an explicit `NUnit` 5.0.0 reference, because ModelEval gets NUnit only through `RoslynSentinel.Tests`.
  - It copies ModelEval's `Configurations` line (`Debug;Release;DebugVisualStudio`).
  - Common's `InternalsVisibleTo` list does not include it, so it sees public types only, which is enough.
- **Step 2.**
  - **Split.** 2a covers the interface, the adapter and the runner change, accepted by the existing ModelEval runner tests. 2b is the new runner test in the new project.
  - **The old constructor stays.** `new ModelAgentRunner(` has 21 sites, including `Tools.Advanced/SubAgentModelRun.cs:104` and `Utilities.PlanStepRunner/Program.cs:220`.
  - **SDK risk closed.** `CallToolResult { Content = [new TextContentBlock { Text = ... }], IsError = ... }` is already built in `Server.Advanced/LoopLatchFilter.cs:80-83` (ModelContextProtocol 2.3.0).
  - **Stub handler.** The runner tests' stub is a private nested `ScriptedHandler : HttpMessageHandler`, one per fixture. It answers `GET` with `{"data":[]}`, because the client constructor starts a loaded-model check. The new project gets its own copy as a test double.
- **Step 3.**
  - Load by deserializing `AgentTranscript` with `PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate` (its `Tools` and `Turns` are get-only). This needs no new record.
  - **Split.** 3a is the loader and `TranscriptResume`. 3b is `StopMarker` and `ScenarioScorer`.
- **Step 5.** The copy carries `AgentTranscriptTurn.Assists` forward; `RewriteTurns` drops it today.
- **Step 9.** `AddLmStudioAgentClient` is in ModelEval, so the client is constructed directly: an `HttpClient` with `BaseAddress` `<host>/v1/`, preceded by `LlmOptions.Configure`.
- **Step 10.**
  - **Row type.** The results row is `ScenarioSample` (`Tests.ModelEval/ScenarioSample.cs:26`, 33 columns). `ScenarioSampleCsv.Parse` accepts column counts of 30, 31 and 33, so new columns need another legacy count.
  - **Pins.** `ScenarioSampleCsvTests.cs:102,162-166`, `ScenarioRescoringTests.cs:16,235`, `ScenarioSampleLoopTests.cs:50`.
  - **Construction sites:** 8.
  - **Tier.** Senior.

## Execution rules

- Build 0 errors per slice. Compare with the test baseline: 0 known failures (`feedback_known_baselines_stale_zero_failures`). Run one `Build`/`RunTest` at a time.
- C# changes go through the MCP tools. New tests go in per-slice files.
- **`Common` edits (Steps 2, 3, 10) rebuild everything once.** Live eval jobs build `Common` from the shared tree, so check `ListAgents` for a running eval before committing these steps.
- Live steps (12+) run through `modeleval-runner`, one job per host.
- New types: the NEW-DTO blocks below go into each creating call's `reason` (dto-conventions skill).
- Commits carry `Plan: plan_error_variant_replay`; the last carries `Closes:`.

## Steps

### Step 1 - Scaffold the test project
- Files: new `RoslynSentinel.Tests.ErrorVariants/RoslynSentinel.Tests.ErrorVariants.csproj` (NUnit 5, same test packages as ModelEval, `ProjectReference` to `RoslynSentinel.Common` only), the solution file, one placeholder test.
- Depends on: none.
- Effect: text-neutral.
- Change:
  - Add the project, following the solution-file and `scripts/` conventions in `reference_working_conventions.md`.
  - Check that `scripts/build.ps1` (Solution flavor) builds it and that its tests appear in the baseline run. If test discovery is by name pattern, confirm the pattern matches.
- Done when: `Build` 0 errors; `RunTest` on the new project passes; a full `scripts/build.ps1 -Flavor Solution` lists it.
- Done: a99ed363. A new project needed `Build(fullBuild)` plus `LoadSolution(forceReload: true)` before the server workspace accepted `CreateFile` in it.

### Step 2 - Tool-host seam in Common
- Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs`, new `RoslynSentinel.Common/AgentLoop/IAgentToolHost.cs`, new `RoslynSentinel.Common/AgentLoop/McpClientToolHost.cs`, a test file in the new project.
- Depends on: Step 1, for the test location (the code change itself depends on nothing).
- Effect: text-neutral.
- Change:
  - Replace `_mcpClient` with `IAgentToolHost _toolHost`.
  - `ListToolsAsync` returns the mapped `AgentToolDefinition` list, so the mapping at `:186-191` moves into the adapter.
  - `CallToolAsync(name, arguments, ct)` returns `CallToolResult`. The block split and error checks stay in `ExecuteToolCallAsync`.
  - Add the constructor overload taking `IAgentToolHost`. The existing constructor wraps its `McpClient` in `McpClientToolHost`.
  - Make `ContainsToolCallMarkup` `public static`.
- Done when:
  - `Build` 0 errors, and the existing ModelEval runner tests pass (`LoopAssistRunnerTests`, `MalformedToolCallRunnerTests`, `ToolResultTrailingBlockRunnerTests`, `IdenticalSuccessRunnerTests`).
  - A new test drives `RunAsync` with an in-memory `IAgentToolHost` and a stub `HttpMessageHandler` behind `LmStudioAgentClient`, and asserts the tool message equals `first + "\n\n" + second` for a two-block result. If the runner tests already have a stub handler, reuse it.

### Step 3 - Shared transcript loader and stop-marker helpers in Common
- Files: new `RoslynSentinel.Common/AgentLoop/SavedTranscript.cs` (or a static `Load` on `AgentTranscript`), new `RoslynSentinel.Common/AgentLoop/StopMarker.cs`, `RoslynSentinel.Tests.ModelEval/TranscriptResume.cs`, `RoslynSentinel.Tests.ModelEval/ScenarioScorer.cs`, a test file in the new project.
- Depends on: none. Its files are disjoint from Step 2's, so it pairs with Step 2.
- Effect: text-neutral.
- Change:
  - Load a saved `transcript.json` into the `Common` transcript type, with `KeepTurns(n)`. Check first whether `AgentTranscript` deserializes as is: its get-only `Tools` list may need `JsonObjectCreationHandling.Populate`. If it does not, add a `Common` record mirroring `ResumeSource` (`TranscriptResume.cs:13-40`), add `ErrorPolicyMode`/`ErrorPolicyHash`, and justify it as a NEW-DTO.
  - `TranscriptResume.Load`/`KeepTurns` delegate to the new loader.
  - `StopMarker.IsLeading(string)` and `StopMarker.ContainsAnywhere(string)` carry the regexes from `ScenarioScorer.cs:335-338`, and the scorer's private methods delegate to them.
- Done when: `Build` 0 errors; `TranscriptResumeTests`, `ScenarioScorerTests` and `ProseStopScoringTests` pass; a round-trip test loads a checked-in trimmed transcript.
- Done: 51d6edb0 (`SavedTranscript`, Populate works; `ResumeSource` kept with a TODO to fold it), 6dc49057 (`StopMarker`). Step 2 done: d1b30b9f (seam), 129da71e (`RunnerToolHostTests`).

### Step 4 - Variant file loader
- Files: new `RoslynSentinel.Tests.ErrorVariants/ErrorVariant.cs`, `ErrorVariantLoader.cs`, a test file.
- Depends on: Step 1.
- Effect: text-neutral.
- Change:
  - Parse `ErrorVariants/<errorCode>/<id>.md`:
    - `## message` is required and holds the replacement text.
    - `## trailing` is optional. Absent keeps the recorded block; present and empty removes it.
  - The id is the file name. The hash is the first 12 hex of SHA-256 over the normalized file (CRLF to LF, trailing whitespace trimmed), using the copied `errorPolicyHash` normalization.
  - Reject a file with an unknown section or without `## message`.
  - NEW-DTO: `ErrorVariant` (Item, Tests.ErrorVariants). Evaluated: `ErrorPolicyFile` (ModelEval): a policy text with mode sections, not visible from this project. `ResumeSettings`: run knobs, not a text variant.
- Done when: unit tests cover absent, empty and present `## trailing`, that the hash is the same for CRLF and LF, and the rejections.

### Step 5 - Result override on the seed
- Files: new `RoslynSentinel.Tests.ErrorVariants/ResultOverride.cs`, a test file, test data with one real ReadFile-miss `ResultJson` and one real Search symbol-miss `ResultJson`.
- Depends on: Steps 3 and 4.
- Effect: text-neutral.
- Change:
  - `ResultOverride.Apply(transcript, cutTurn, variant)` returns `(IReadOnlyList<AgentTranscriptTurn> Turns, string RecordedMessageHash)`, a tuple, so no new DTO.
  - Keep turns 1..cutTurn. `cutTurn` defaults to the first turn that contains an error call.
  - In the cut turn, find the single call whose `ResultJson` has `isError:true`. Fail if there are zero or several such calls, or if its `errorData.errorCode` differs from the variant's folder.
  - Locate the `errorData.message` token with `Utf8JsonReader`, encode the new text with the same escaping as the recorded value, and replace that byte span.
  - Set `TrailingText` per the variant, substitute `{SOLUTION}`, and copy every other field into a new `AgentToolCallRecord`.
  - `// TODO` for `statusMessage` targets.
- Done when: unit tests show:
  - an unchanged-text variant gives byte-identical `ResultJson` on both real samples;
  - the trailing semantics hold;
  - a recorded `<` and backslashes survive;
  - the errorCode-mismatch and multi-error refusals fire.

### Step 6 - Transcript tool host (the fake)
- Files: new `RoslynSentinel.Tests.ErrorVariants/TranscriptToolHost.cs`, `ToolEffectList.cs`, a test file.
- Depends on: Steps 2, 3 and 5. The sticky rule reuses the override's splice.
- Effect: text-neutral.
- Change:
  - `ListToolsAsync` builds `AgentToolDefinition`s from the header's `Tools`, `ToolDescriptions` and `ToolSchemas`, in `Tools` order. Refuse a transcript whose `ToolDescriptions` is null or whose `ToolSchemas` lacks a listed tool.
  - `CallToolAsync`:
    - A read tool (`ToolEffectList`) whose name and canonical arguments match a recorded call after the cut gets that recorded `ResultJson`/`TrailingText` as two text blocks, with `IsError` as recorded and the sticky override applied (Decision 6).
    - Anything else gets a fixed placeholder result and sets `OffScript` (the tool name, the turn).
  - It exposes `bool OffScript` for the `targetReached` predicate.
  - `ToolEffectList` is the hand list of read and write tools. DTO-EXEMPT for both classes: services/holders.
- Done when: unit tests show that a matched call returns the recorded bytes, an unmatched or write call sets `OffScript`, the sticky rule applies, and the coverage test passes over the checked-in header's `Tools`.

### Step 7 - Next-turn classifier
- Files: new `RoslynSentinel.Tests.ErrorVariants/NextTurnDecision.cs`, a test file.
- Depends on: Steps 2 and 3 (`ContainsToolCallMarkup`, `StopMarker`) and Step 6 (`ToolEffectList`).
- Effect: text-neutral.
- Change: classify the first assistant turn after the cut into exactly one class.
  - `StopLeading`: no tool call; the message starts with `BLOCKED:`.
  - `StopMarkerLater`: no tool call; the marker appears elsewhere.
  - `StopProse`: no tool call, no marker; the message names an absent target (a run parameter) as absent.
  - `Widen:<tool>`, `Write:<tool>`, `Other:<tool>`: the first call decides; the call count is recorded.
  - `Malformed`: tool-call markup in the text, or an empty reply.
  - `NoCallOther`: no tool call and none of the above.
  - `blockedAnywhere` is recorded separately. For N > 1, also record the turn where the run stopped and why (`TurnCap`, `OffScript:<tool>`, `Stop*`).
- Done when: unit tests per class.

### Step 8 - Row type and CSV
- Files: new `RoslynSentinel.Tests.ErrorVariants/VariantSampleRow.cs`, a test file.
- Depends on: Step 7.
- Effect: text-neutral.
- Change: one row per sample, written beside the run's transcripts as `variants.csv`. Columns:
  - run: run stamp, model key, temperature, top-p, max tokens, continue turns;
  - variant: variant id, variant hash, errorCode;
  - source: source run folder, source sample, source `CommitHash`, cut turn, recorded-message hash;
  - outcome: sample index, decision class, first tool, call count, `blockedAnywhere`, stop turn, stop reason;
  - cost and faults: latency, input and output tokens, harness error.
  - NEW-DTO: `VariantSampleRow` (Item, Tests.ErrorVariants). Evaluated: `ResumeSampleRow` (ModelEval): resume-gate columns, not visible from this project. `ScenarioSample` (ModelEval): whole-run scoring, not visible.
- Done when: a header and round-trip test passes.

### Step 9 - Replay test and front door
- Files: new `RoslynSentinel.Tests.ErrorVariants/ErrorVariantReplayTests.cs`, the copied archiver, new `scripts/roslynsentinel-errorvariants.ps1`.
- Depends on: Steps 4-8.
- Effect: harness only. It adds a new `[Explicit]` test and script; nothing ships.
- Change:
  - Env: `VARIANT_SOURCES` (run folders or transcript paths), `VARIANT_FILES` (ids, or `*` for an errorCode folder), `VARIANT_SAMPLES` (default 3), `VARIANT_CONTINUE_TURNS` (default 1), optional `VARIANT_CUT_TURN`, `VARIANT_ABSENT_TARGETS`.
  - For each source x variant x sample:
    - `ResultOverride.Apply`, then a new `TranscriptToolHost`.
    - `new ModelAgentRunner(llm, host, repeatedFailureLimit: 10, maxTokensPerTurn: 16384, turnCap: cut + N)`, the live values.
    - `RunAsync(source.SystemPrompt, source.UserPrompt, sampleDir, seedTurns:, targetReached: _ => host.OffScript)`.
    - Classify, then append the row.
  - Construct `LmStudioAgentClient` directly from `LlmOptions`, or reuse its registration helper if that is in `Common`.
  - Check the loaded model once per run via `/api/v1/models` and record the key.
  - The script maps its parameters to the env, requires `-Model` (`feedback_modeleval_script_model_param_required`), builds only this project, and archives.
- Done when:
  - `Build` 0 errors.
  - A unit test runs the whole pipeline on a checked-in transcript with a stub `HttpMessageHandler` (no model) and writes one classified row plus a transcript.
  - With no model reachable, a harness-error row is written, not a crash.
  - A one-sample smoke run on one E transcript against the real host (via `modeleval-runner`) writes one row.

### Step 10 - Record sampling settings and model key in live runs
- Files: `RoslynSentinel.Common/AgentLoop/AgentTranscript.cs` (header), the header-filling code in `ModelAgentRunner.RunAsync` (`:201-208`), `RoslynSentinel.Tests.ModelEval/ScenarioSample.cs` (the results row and its CSV), the construction sites and pins listed under "Pre-implementation review".
- Depends on: Step 2, which touches the same runner file, so run Step 10 after it.
- Effect: harness output only. It adds new transcript header fields and `results.csv` columns.
- Change:
  - Record the model key, temperature and top-p in the transcript header and in `results.csv`; `null` means the server default.
  - Step 9 refuses a source whose recorded settings differ from the run's, and records `unknown` for older sources.
- Done when: `Build` 0 errors; a header round-trip test passes; the pins are updated in this slice.
- Done: c3efa20e. `AgentTranscript.ModelKey`/`Temperature`/`TopP` come from the new `LmStudioAgentClient.Model` and `LlmOptions`. `results.csv` gains `modelKey`, `temperature` and `topP` (36 columns; 33 is accepted as legacy).
- Step 9 done: e5c85478, test fix 069455d6.
  - **Extra column.** `variants.csv` gained a 26th column, `SourceSettings` (`matched`/`mismatch`/`unknown`).
  - **Harness errors.** A settings mismatch or a failed model check is written as a harness-error row, not thrown.
  - **Loop assist** is off in replay.
  - **Smoke run** (2026-11-13, host 112, `qwen3.5-2b`; no `qwen3.5-9b` was loaded): one E sample with armE classified `Widen:ListSolutionItems`, stopped at `TurnCap`, no harness error, 27 s. Run `ModelTestingResults\112\ReplayVariants_SamplesAndClassifies\20261113-195930-964`.

### Step 11 - Harvest E/F/G variants and recover their sampling settings
- Files: `RoslynSentinel.Tests.ErrorVariants/ErrorVariants/FileNotFound/armE.md`, `armF.md`, `armG.md`; the eval doc of Step 12.
- Depends on: Step 4.
- Effect: text-neutral.
- Change:
  - Copy `errorData.message` and `TrailingText` verbatim from runs `20261113-050525-257` (E), `20261113-061105-032` (F) and `20261113-064107-618` (G), with the fixture path replaced by `{SOLUTION}`.
  - Confirm the texts are identical across each run's 10 transcripts.
  - Recover each job's temperature, top-p and model key from the eval-queue records or `agent.log`. If they are not found, say so and validate with the env unset (the server default), which is what a job launched without `-Temperature`/`-TopP` used.
- Done when: the three files load; Step 5's identity test passes on their own transcripts; the settings are written in the eval doc.
- Done: 09ebacf1 (harvest), cca1ac06 (the files load), 1845498c (identity: each arm file applied to its own run gives byte-identical `ResultJson`). Steps 4-8 done: cca1ac06, 1845498c, 2215c3dc (`ToolEffectList` classes 18 tools Read and 37 Write; tools that pick read or write by `operation` count as Write, an open decision below), e1e8aac8, 8c773d97.
  - **Texts.** In each run, the message and the trailing text are byte-identical across all 10 samples. The trailing text is the same 422 characters in E, F and G.
  - **`{SOLUTION}` not needed.** No fixture path appears in `errorData.message`, which carries only the relative path.
  - **Sampling settings.** The queue job records (`%LOCALAPPDATA%\RoslynSentinel\evalqueue\113\jobs\20261113-050504-001`, `-061048-001`, `-064033-001`) show `evalArgs` `-Scenario StopAdherenceMissingFile -Samples 10`, with no `-Temperature`/`-TopP`. The server default applied, so validation runs with the env unset.
  - **Model and policy.** All three ran `qwen3.5-9b-mtp` on host 113 with policy mode Inline. E's job set `ROSLYNSENTINEL_ERROR_POLICY_FILE=error-policy-v2.md`.

### Step 12 - LIVE validation: identity and known contrast (one-step)
- Files: new `docs/current/evals/eval_error_variant_replay_validation.md`.
- Depends on: Steps 9 and 11.
- Effect: none (measurement).
- Change: run through `modeleval-runner` on the host and model the source jobs used (9b), with N = 1.
  - (a) **Identity:** each arm's own text on its own 10 histories, 3 samples each, 90 rows in all.
  - (b) **Contrast:** the E, F and G texts each on the F histories and on the E histories, paired by source.
  - Report the per-cell counts of each class against the live turn-2 tallies.
- Outcome classes per cell:
  - `Faithful`: all bars met.
  - `TooPermissive`: first-miss stops above the bar.
  - `Drifted`: the tool-mix bar missed.
  - `HarnessFault`: any harness error, or a `Malformed` share above 0.1.
- Pass bars, fixed before the run (proposal step 2):
  - F-text `Search` share >= 0.8;
  - E-text `ListSolutionItems` share >= 0.5;
  - `Stop*` <= 2 of 30 for every text.
- Done when: the eval doc records every cell, the settings and the model key, with a verdict per cell.

### Step 13 - LIVE validation: cross-history, and an on-script continuation look
- Files: the same eval doc.
- Depends on: Step 12 passing.
- Effect: none.
- Change:
  - Repeat 12(b) on histories from a different arm: A', D or G, whichever has 10 clean turn-1 ReadFile-miss transcripts.
  - Then run E's text on the E histories with N = 5, and report the off-script rate and turn of exit. E's live turn-3 stops followed a `ListSolutionItems` result, so this shows whether on-script continuation reaches that point.
- Done when: verdicts are recorded. If 12 or 13 fails, stop, record the gap as a finding, and do not screen with the tool.

### Step 14 - First screening use and close
- Files: the eval doc, the proposal and this plan (Status lines), `docs/current/TODO.md`.
- Depends on: Step 13 passing, and the guard decision below.
- Effect: none.
- Change:
  - Screen the arm H and I `FileNotFound` and `NotFound` texts, harvested from their runs, on MissingFile and MissingMethod histories. MissingMethod is cut at turn 2. Arm I's folder list is fixture data, so it comes from the transcript.
  - Run the guard check.
  - State that a winning text needs a live confirmation arm before any production change.
  - Add a TODO.md entry for phase 2: a decorator over `McpClientToolHost` that swaps matching error results in live runs (the runner-boundary override, now small).
  - Close the plan and move the proposal to `proposals/closed/` via `ProjectDoc close`.
- Done when: the screening table is recorded and the docs are closed.

**Lanes.**
- Step 1 comes first. Steps 2 and 3 then pair (disjoint files), and Step 4 can join either lane.
- Step 5 follows Steps 3 and 4. Step 6 follows Steps 2 and 5. Steps 7, 8 and 9 follow in order.
- Step 10 runs any time after Step 2. Step 11 runs any time after Step 4.
- At most two implementers run at once.

## Out of scope

- Continuation against a real server. If it is built, the override must apply after the replay gate, or `resultDrift` is nonzero by construction (`ResumeScenarioGate.cs:51-54`).
- Cold mode.
- The runner-boundary override in live runs (phase 2, TODO above).
- A full stub MCP server.
- `statusMessage` targets.
- Replacing the source system prompt with the current one.
- Forwarding `top_k`/`repeat_penalty`, which `/v1/responses` ignores (`LlmOptions.cs`).
- Moving ModelEval's runner tests onto the fake host. It is possible after Step 2, but not this plan.

## Risks and open decisions

- **Guard scenarios (owner decision before Step 14).** The false-stop guard needs an error where carrying on is correct.
  - `RenameSymbolTypo` is seeded only through the cold route, which this plan does not build.
  - `RenameSymbolNoSuchMethod` (`FaultScenarios.cs:95`) is cold and unseeded, with expected outcome `Recover`.
  - Arms H and I ran Typo and NoSuchMethod on 4b, while the faulty cells ran on 9b.
  - Options:
    - (a) screen the guard on 4b from those transcripts, noting the model mismatch;
    - (b) record 10 NoSuchMethod/Typo histories on 9b first, which is one live job;
    - (c) build cold mode.
  - Recommended: (b).
- **Operation-dispatched tools: DECIDED 2026-11-13 (owner), operation-aware.** These tools count as a read only for these values:
  - `operation` `view`: `Member`, `MethodSignature`, `ConstructorParameter`, `UsingDirective`, `SummaryComment`;
  - `action` `verify`: `SyncInterface`;
  - `action` `read`, `list`, `trailers` or `validate`: `ProjectDoc`.
  Any other value, or a missing value, counts as Write. The values come from the checked-in transcript header's enums. Done: 1367cda4 (`ToolEffectList.ClassifyCall`/`IsReadCall`, which the fake host uses), 379ffebe (`NextTurnDecision`).
- **Guard scenarios: DECIDED 2026-11-13 (owner), option (b).** Record 10 NoSuchMethod/Typo histories on 9b in one live job before Step 14.
- **Batch C additions: DECIDED 2026-11-13 (owner), keep.** These are the `SourceSettings` column, the harness-error row on a settings mismatch or failed model check, and loop assist off in replay.
- **Replay fidelity is unproven** until Steps 12-13. No wording ships on replay alone.
- **On-script continuation is a narrow window.** It covers only calls the source run made with the same arguments; Step 13 measures how far it gets. Write calls always go off-script.
- **`Populate` deserialization of `AgentTranscript` is untested.** If it fails, Step 3 falls back to a `Common` record mirroring `ResumeSource`.
- **The source jobs' sampling settings may be unrecoverable** (Step 11). If so, validating with the server default is an assumption, and the eval doc says so.
- **Histories carry their arm's trailing text and reasoning** (proposal risk 4). A variant result is conditional on the source arm, hence the paired contrast and Step 13.
- **A model swap mid-run invalidates a cell.** Mitigated by the per-run loaded-model check and the model key on every row.
- **`Common` edits (Steps 2, 3, 10) trigger one full rebuild each** and affect live eval jobs building from the shared tree. Coordinate with peers.
