# Test error-message wording by substituting the tool result in a saved transcript, not by editing production code

**Status:** PROPOSED 2026-11-13. Owner idea (2026-11-13), awaiting review; nothing built. Step 11g of `plan_error_policy_pointer` names this as the follow-up once arms H and I finish.

## Motivation

Every error-text variant tried so far in `plan_error_policy_pointer` cost a production change, a build and a full live run, and each arm was only loosely a single-variable comparison.

- **Coupling to production code.** Arms F and G each changed the not-found text in `RoslynSentinel.Common` (`FileNotFoundMessage.cs`; arm G is commit `5e4694cb`, arm F is `fd279396`), and H/I will change `FileNotFoundMessage.cs` and `SymbolNotFoundDetail.cs`. The eval jobs rebuild from the shared working tree, so each arm also needed peer HOLD coordination.
- **The build is a confounder.** Between the E build (`c94ba651`) and the F build (`5923cff9`) six commits landed, of which one is the text under test (`eval_error_policy_pointer_ab.md`, "Arm F" Setup, "Commits between the two builds"). Arm G's section lists three more. The earlier A/B had the same problem: arm A straddled two schema commits ("Confound" bullet, "Rerun" section).
- **Cost per arm.** A 10-sample MissingFile cell takes about 6 minutes of host time (same doc, Recommendation item 1); with build, queue and hold the owner puts a full arm at 10-60 minutes. Step 11g asks for 30 samples per scenario per arm overnight.
- **The decision being tested is one model turn.** The Step 11d part 1 replay showed the first-miss decision can be probed from a saved transcript cut after the first error ("Explain-the-error replay"), and E, F and G all measured 0 stops at the first miss in 30 samples (Arm G, "The read"). The question "what does the model call next after this exact text" does not need a server, a fixture or a build.

Doctrine framing: the environment (error text) is the thing under test; the harness should let us vary it without touching the thing that ships.

## Proposal

Add a ModelEval-side **result override**: take the model-facing result of one recorded tool call, replace its error text (and optionally its trailing block) with a variant from a file, and sample the model's next turn(s).

### What exists to build on (verified in source)

- `TranscriptResume.Load` / `KeepTurns` / `RewriteTurns` (`TranscriptResume.cs:195-294`) cut a saved transcript and rewrite fixture paths; `ResumeSource` carries `SystemPrompt`, `UserPrompt`, `Tools`, `ToolDescriptions`, `ToolSchemas`, `CommitHash`, `Turns` (`TranscriptResume.cs:13-40`).
- `ModelAgentRunner.RunAsync(..., seedTurns:)` seeds those turns and builds each tool message from `ResultJson` plus `TrailingText` (`SeedConversation`, `ModelAgentRunner.cs:633-657`; `ModelFacingToolContent`, `:849-850`). So the override is a pure edit of two string fields on one `AgentToolCallRecord` before seeding.
- `ResumeSettings` already has the knobs `RESUME_TRANSCRIPT`, `RESUME_KEEP_TURNS`, `RESUME_SAMPLES`, `RESUME_CONTINUE_TURNS` (`TranscriptResume.cs:47-69`) and a CSV row type (`ResumeSampleRow`, `:101-136`), driven by `TranscriptResumeAgentTests`.
- Cold route: an authored-only seeded scenario (`ResumeKeepTurns` 0, `ResumeFaultSeeded` true; `RenameSymbolTypo`, `FaultScenarios.cs:64-65`) has the harness author the first call and seed its result (`BuildAuthoredFaultTurn`, `TranscriptResume.cs:301-356`; `ScenarioAgentTests.RunResumeSampleAsync`).
- The live loop talks to `/v1/responses` with `LlmOptions.Temperature` and `TopP` (`LmStudioAgentClient.cs:150-158`); `top_k` and `repeat_penalty` are not forwarded (`LlmOptions.cs:55-70`).

### Modes

| Mode | Source of history | What the override replaces | Continues against |
| --- | --- | --- | --- |
| **Replay one-step** (default) | saved `transcript.json` cut at the first-error turn | that call's error text and trailing block | nothing: one model call, classify the next turn |
| **Replay N-step** | same | same | real server on a fresh fixture, N more turns (the existing resume path, which also runs the schema and replay gates) |
| **Cold** | none: authored first call(s), prompts from the scenario | the authored call's real result | one-step or N-step as above |

Replay is preferred over cold because the saved history carries the model's own turn-1 reasoning; an authored turn carries none, so the model sees a call it never reasoned about. Cold is for when no suitable transcript exists. MissingFile's first error is the turn-1 `ReadFile`; MissingMethod's is the turn-2 `Search` after a successful `ReadFile` (Arm B/C "First miss and key"), so its cut is turn 2.

One-step needs no fixture or MCP server if the tool definitions come from the transcript header (`Tools`, `ToolSchemas`). Whether `ModelAgentRunner` can be driven without an MCP client is not verified; the likely route is to extract the message-building part of `SeedConversation` into a static helper and call `LmStudioAgentClient.CompleteAsync` directly. Open (below).

### Variants

- One small Markdown file per variant under a new `RoslynSentinel.Tests.ModelEval/ErrorVariants/` folder, id = file name, keyed like `error-policy.md` (by the not-found kind it targets, e.g. `FileNotFound`, `SymbolNotFound`).
- Sections: `## message` (replaces the human-readable error text; the envelope fields such as error code stay as recorded, since the envelope is not the text under test) and optional `## trailing` (absent = keep the recorded trailing block, empty = remove it). The trailing choice is part of the variant: source arms carried different carriers (Pointer, Inline, none), and an unstated carry-over would be a hidden confound.
- `{SOLUTION}` is substituted with the fixture directory, as `ResumeFaultArguments` does.
- A variant hash (first 12 hex of SHA-256 over the normalized file, like `errorPolicyHash`) goes into every output row.
- Variants can be **harvested**: arms E, F and G's exact texts are already in saved transcripts (runs `20261113-050525-257`, `20261113-061105-032`, `20261113-064107-618`), so the validation below needs no production code. Texts that embed fixture data (arm I's scoped inventory) are authored once by hand against the deterministic fixture.

### Sampling

- k samples per (variant x source transcript). Use several source transcripts (each live sample is one history), so the variance across histories is in the estimate; compare variants paired by source. A single source with many samples measures that history, not the text.
- Sampling settings must match the live arm being compared: `/v1/responses` through the harness client, with the temperature and top-p the live job used (read from the source job; the value is not verified here).
- Default k and source count: open; Step 11g's 30 per cell is the benchmark.

### Scoring (next-turn decision)

New classifier, `NextTurnDecision`, over the first assistant turn after the override:

- `StopLeading` (no tool call, message starts with `BLOCKED:`), `StopMarkerLater` (marker elsewhere), `StopProse` (states the target absent), `Widen:<tool>` (a read or lookup tool: `Search`, `ListSolutionItems`, `ListAll`, `ReadFile`, ...), `Write:<tool>` (`CreateFile`, `ReplaceSnippet`, `Member`, ...; the destructive proxy), `Malformed` (tool call written as text, empty reply).
- Reuse: the marker regex and marker-anywhere test are `private` in `ScenarioScorer` (`HasBlockedMarker`, `ContainsBlockedMarker`, `ScenarioScorer.cs:335-338`) and the absence check uses the scenario's `AbsentTargetNames`; make the first two `internal` and share them. The whole-run classes (`ProseStop`, `MarkerNotFirst`, `StoppedLate`, `ScenarioScorer.cs:23-38`) depend on the final message and turn count, so they apply only to N-step runs, where `ScenarioScorer.Score` is used unchanged.
- Valid/faulty pairing: MissingFile and MissingMethod (`StopAdherenceScenarios.cs:60-94`) are the faulty prompts where stop is correct. `StopAdherenceControl` (`:96-107`) is a valid rename with no authored error, so it cannot exercise an error text. The false-stop guard for a variant is an error where continuing is correct: `RenameSymbolTypo` (near match exists) and `RenameSymbolNoSuchMethod`, as Step 11g already plans. A variant that raises stops on the faulty prompts and also on the guard is not an improvement.

### Outputs

- A `variants.csv` beside the existing `results.csv`, one row per sample: run stamp, variant id and hash, source run and sample, cut turn, sample index, model key, temperature, top-p, `NextTurnDecision` class, next tool, `blockedAnywhere`, latency, tokens, harness error. A new record type, not extra columns on `ResumeSampleRow`. Archive via `ModelTestingResultsArchiver` like the other runs.
- Record `ResumeSource.CommitHash` as the source build: system prompt and tool schemas come from the transcript, so HEAD is irrelevant to the result, which is the point.

### What it cannot measure

- Multi-turn effects of real tool results. E's three turn-3 stops came from a real `ListSolutionItems` result plus the error sentence (Arm F, "The read"); a one-step replay sees only the first decision. N-step runs against the real server restore this, but later errors are then production text, not the variant (a repeated miss on the same path returns today's wording; "sticky" override is open).
- Whether the server can produce the variant, when it would (for example `WorkspaceCurrent` gating, `FileNotFoundMessage`), or what other facts it appends. A winning variant is a hypothesis that still needs the production change and a live confirmation arm.
- Drift in prompts or schemas after the source transcript was recorded.
- Interactions with later-arriving policy text (system-prompt changes) unless the source transcripts already contain them.

### Validation plan (before trusting it)

1. **Identity check.** Override with the recorded text unchanged, on the 10 histories of one arm. The next-call distribution must match what those live samples did after the miss. This measures replay fidelity with no variant confound.
2. **Reproduce a known contrast.** Using E, F and G texts (harvested) on the same 10 histories, compare with the live first-call distributions: E `ListSolutionItems` 7 / `Search` 3, F `Search` 10, G `Search` 7 / `ListSolutionItems` 3 (Arm F and Arm G tables), and 0 of 30 stops at the first miss. Proposed pass bar, to be fixed before the run: F-text `Search` share at least 0.8, E-text `ListSolutionItems` share at least 0.5, and first-miss stops for any of the three at most 2 of 30. A large first-miss stop rate for a text that live produced 0 stops means replay is too permissive.
3. **Cross-history.** Repeat 2 with histories from a different arm (A', D or G), since E/F/G system prompts are identical but histories differ.
4. Only then use it to screen H and I wording, and confirm the best one live (30 samples) before any production decision.

## Alternatives considered, not pursued

- **Keep the current method** (production edit, build, live arm). Ground truth, so it stays as the confirmation step; too slow and too coupled as the screening step.
- **Extend `roslynsentinel-interrogate.ps1` with a result-override file.** Cheapest for ad hoc looks, but it replays through `/v1/chat/completions` with reasoning folded into `<think>` and default sampling 0.1/0.7 (`reference_interrogate_script.md`), one answer per call, no classifier, no CSV, and in the Step 11d replay the model wrote its call as raw `<tool_call>` text in 10 of 12 answers. Keep it for insight, not measurement.
- **A temporary environment switch in the production error builders** that reads the variant text from a file (the arm D / policy-mode pattern). Avoids rebuilds but puts a test hook in `Common`, still needs a server restart per variant and still pays a full live run per cell.
- **A stub MCP server returning canned results** for the live loop. Faithful loop, but needs the 55 tool schemas and a recorded-call lookup; considered as the later "continue against a stub" option, not the first build.
- **Override at the runner boundary in live runs** (rewrite the first not-found result in `ExecuteToolCallAsync`, `ModelAgentRunner.cs:797-845`). Gives full cold multi-turn runs with no production error-builder change, but each cell still costs a full run. A natural phase 2: it shares the substitution function with this proposal.

## Open questions

- Variant file format: replace the message field only (proposed) or the whole `ResultJson`? The envelope shape for each error family needs checking against saved transcripts; not verified here.
- One-step without a server: can the runner's message building be reused without an MCP client, or is a small extraction needed?
- Sticky override: should a later result with the same normalized error text also be replaced in N-step runs?
- Source of the read-only versus write tool split for `Widen`/`Write` (a hand list, or tool metadata if it exists; not checked).
- Temperature and top-p to use, and whether to refuse a run whose settings differ from the source job's.
- Default k, number of source transcripts, and the pass bar for validation step 2 (proposed above, not agreed).
- Whether to allow replacing the source system prompt with the current one (`-UseCurrentPrompts`) to detect prompt drift.
- Does `ReasoningDroppedTurns` (`LmStudioAgentClient.cs`, used by `BuildInputItems`) drop seeded reasoning in a way that differs from live at turn 1-2? Not read.

## Cost / risk

- **Touches:** `RoslynSentinel.Tests.ModelEval` only (a variant loader, an override helper, a classifier, a CSV record, an env var on the resume front door); `ScenarioScorer` helper visibility; no server, `Common` or prompt change. Size is a guess, not measured: a few hundred lines plus unit tests, and the unit-testable parts need no model.
- **Risks:** (1) replay fidelity unproven until validation steps 1-2; (2) few source histories overfit to those histories; (3) a screening result read as a live result, so the doc rule is that no wording ships on replay alone; (4) transcripts embed arm-specific trailing text and system prompts, so a variant tested on them is conditional on that context; (5) a model unloaded or swapped mid-run (it happened on 2026-11-12, `eval_error_policy_pointer_ab.md` Context) invalidates a cell, so record the model key per row.
- **Makes harder:** nothing in production. It adds a second measurement path whose agreement with live must stay checked, and a variants folder to curate.
- **Remains the shared resource:** the model host; one job at a time per host still applies (peer HOLD on the working tree no longer does, because nothing rebuilds).

## Related

- `docs/current/evals/eval_error_policy_pointer_ab.md` (arms A-G; "Explain-the-error replay").
- `docs/current/plans/closed/plan_error_policy_pointer.md` (Step 11g, arms H and I).
- `docs/current/references/reference_interrogate_script.md`.
- `docs/current/plans/plan_not_found_error_orientation.md` (the not-found error rewrite whose measuring stick is the stop-adherence family).
- Memory: `project_modeleval_testhost_gotchas`, `reference_model_eval_procedure`.
