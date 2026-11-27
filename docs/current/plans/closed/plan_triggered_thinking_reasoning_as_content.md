# Plan: replay earlier reasoning as visible assistant content in triggered thinking mode

**Status:** IMPLEMENTED 2026-11-26. Steps 1-7 (9267ba2b 31bf8986 074c2e48 541f8b3c 3ead6848 190b89ec d44dffb7), Step 8 live (56 runs at 5006238e, verdict drop), Step 9 (83a8e80b): triggered runs default to the drop form.

## Problem

In triggered thinking mode (plan_error_gated_thinking_and_client_reasoning_budget, closed) a turn without
thinking that follows a thinking turn writes prose into Content ending in a literal `</think>` (smoke test at
8e02735a: 24 leaked turns in 3 of 4 triggered runs, 0 in always and baseline; INBOX 2026-11-25 13:11).

Evidence (INBOX 2026-11-25 15:35 and 2026-11-26 00:30; probes under `<scratch>/template_probe/`):
- Run 192447 rebuilt at turn 6 (first leak), Qwen3.5-9B on 113, 10 samples per arm: reasoning items replayed
  (as sent today) leaked 9/10, without reasoning items 0/10. At turn 15 (two leaks in history) only removing
  both the reasoning items and the leaked prose stopped it (0/10; each alone 8/10 and 10/10).
- Same history on Qwen3.5-4B (host 85): reasoning items replayed 8/10 leak; old reasoning sent as plain
  assistant Content instead 0/10 leak, and the turn-5 decision held only in reasoning ("use ReplaceRange
  instead") was kept 10/10 (no reasoning at all: 5/10). A system-prompt line asking for a plan in the reply
  barely moved text out of reasoning (turn 4: 5/10 vs 2/10, about 60 chars; turn 5: 0/10).
- Mechanism, from the served template (`<scratch>/template_probe/template_113.jinja`): every assistant turn after
  the last user query renders as `<think>\n{reasoning}\n</think>\n\n{content}` (lines 103-104); a non-thinking
  request pre-fills `<think>\n\n</think>\n\n` (lines 152-153). The model sees reasoning-then-`</think>` in history
  but starts after an empty think block, and copies the pattern into its answer. Content containing `</think>` is
  re-split by the template into reasoning (lines 97-99), so a stored leak is replayed as a think block.
- Qwen3.5 model card (unsloth/Qwen3.5-9B-GGUF): history should hold only the final output, not thinking; the
  template implements that only for turns before the last user query, so inside one agent loop the reasoning
  is still rendered.

## Decision

- 2026-11-26 Q1: the setting offers `items` (today), `content` (earlier reasoning sent as plain assistant
  content) and `drop` (no earlier reasoning, model-card practice). Why: drop is one branch and becomes a
  measurable arm. Evidence: host 85 4B turn 6, content 0/10 leak and decision kept 10/10, drop 0/10 and 5/10.
- 2026-11-26 Q2: default stays `items`; the form is used only when a run sets it. The default flips to
  `content` for triggered mode only after the 9B live comparison and owner review. The form is fixed for the
  whole run (prompt-cache prefix stability). Why: one-turn 4B evidence is not enough to change every triggered run.
- 2026-11-26 Q6: the form is a property of ReasoningReplayPolicy (read from a new StartupSetting beside
  WindowSetting/PressureFractionSetting); ToInputItems gets it from BuildInputItems. CompleteAsync and
  AgentChatMessage do not change. Why: the policy already decides which turns' reasoning is replayed.
- 2026-11-26 Q4 probe (host 85, Qwen3.5-4B, 10 samples per arm, run 192447 history at turn 15 with leaks stored
  at T6 and T14, every turn's reasoning sent as content, script
  `<scratch>/plan_design_triggered_thinking_reasoning_as_content/probe_q4_leak_safeguard.ps1`): leaked content
  kept raw 0/10 leak, tags removed 0/10, prose dropped 0/10 (prose dropped: reply text 5/10, all ReadFile).
  Under the content form a stored leak no longer sustains the leak in this sample; the 4B items baseline at
  turn 15 was not measured (9B items: 9/10).
- 2026-11-26 Q4: in the content form, replayed assistant text has `<think>` and `</think>` tags removed (prose
  kept); replay-only, the transcript keeps the raw Content. Why: template lines 97-99 re-split Content holding
  `</think>` into a think block, the known trigger; removing the tags closes that for every model and keeps the plan.
- 2026-11-26 Q9: structured fields - transcript header `ReasoningReplayForm` (absent in older transcripts means
  items), summary `LeakedThinkTagTurnCount` (turns whose Content contains `</think>`), and `form` and
  `leakTurns` columns in scripts/Get-ThinkingArmTable.ps1.
- 2026-11-26 Q10: live comparison on Qwen3.5-9B, hosts 112 and 113, one pinned commit, same tool-result format in
  every arm: triggered/items (control), triggered/content, triggered/drop; both ImplementationScenario scenarios,
  4 rounds per scenario per arm (8 runs per arm, 24 runs), queued after the plain-text batch frees the hosts.
  Primary metric leaked turns per run; guard pass count and outcome classes. Thresholds: flip the triggered
  default to content if content has <= 1 leaked turn across its 8 runs, passes >= items passes - 1, and adds no
  new failure outcome class in more than 2 runs beyond items; prefer drop only if it meets the same bar and passes
  more runs than content; park with a finding if content keeps > 25% of the items arm's leaked turns.
- 2026-11-26 Q3 (implementer default): a turn with both reasoning and content replays as reasoning, a blank line,
  content; no label. Q5 (implementer default): the content form obeys the same ReasoningReplayPolicy drops.
  Q7: user-role nudges make the template drop earlier reasoning today; the content form shows it as text;
  accepted, not mimicked. Q8: the form setting is a StartupSetting beside WindowSetting/PressureFractionSetting.
- 2026-11-26 Q12: the tag removal applies to replayed assistant text in both the content and the drop form (not
  only when reasoning is replayed); items stays byte-identical. Why: a stored leak replayed raw is re-split by the
  template into a think block, which would contaminate the drop arm; drop then means "final output only".
- 2026-11-26 Q13: the transcript header `ReasoningReplayForm` is typed `ReasoningReplayForm?`, serialized as names
  through `[JsonConverter(typeof(JsonStringEnumConverter<ReasoningReplayForm>))]` like `ThinkingMode`
  (`ThinkingOptions.cs:7-9`, with the same "Intentional deviation" comment). Typing the transcript field
  `ToolResultFormat` (a lower-case string today, `ModelAgentRunner.cs:366`, though the `ToolResultFormat` enum
  exists) is a follow-up after plan_plain_text_tool_results_for_eval_model finishes (TODO.md line, note to peer dc);
  `ReasoningReplayMode` stays a string (a parameterised description; a typed record only when a reader needs it).
- 2026-11-26 Q14: Step 8 is valid only if the items control arm has at least 8 leaked turns across its 8 runs;
  otherwise the result is inconclusive and goes to the owner without a verdict. "Failure outcome class" in the
  thresholds means the arm table's `outcomeClass` counts; `outcome` and `stopReason` are reported, not scored.
- 2026-11-26 Readiness review 1 (explore-tier2 sonnet, 17 findings) folded in: StartupSetting.Enum instead of a
  hand-written Custom parse; `ParseFormOverride` helper on the `ParseWindowOverride` pattern (FormatException, no
  process-wide env change in tests); OpenTag constant and AgentTranscript.ThinkTags built from the shared
  constants; a named separator constant; test fixture, id normalisation and env save/restore specifics; slice-map,
  anchor and wording fixes; a TODO.md line; a stale form variable in a worker shell is made visible through the
  arm table's transcript `form` column instead of being cleared by roslynsentinel-modeleval.ps1 (clearing would
  also wipe the job's own `env` value).
- 2026-11-26 Readiness reviews 2 and 3 (explore-tier2 sonnet): 11 findings folded in (WithoutId hoist, T14
  JSON literal, T18 serialization test, exact regex constructions, live-run settings and plain tool results in
  Step 8, anchors, T13/T15 specifics, Step 7 edit count); review 3 found only a using-directive note, fixed.
- 2026-11-26 Readiness (plan-design): independent review clean at 15d5a27b.
- 2026-11-26 Design review (checklist 1-7): 1 pass (new enum ReasoningReplayForm, no tuples); 2 pass: ThinkTagText
  holds OpenTag/CloseTag for the replay strip, the leak count and AgentTranscript.ThinkTags, and
  LmStudioAgentClient holds the reasoning/content separator constant;
  3 pass (AgentTranscript.WithoutThinkTags rejected: private, and it deletes the text inside a tag pair, the
  opposite of Q4; its regex is rebuilt from the shared constants; LoopAssistTracker.CollapseForReplay unaffected
  because AgentChatMessage gains no field);
  4 pass (all in RoslynSentinel.AgentLoop, largest touched file LmStudioAgentClient.cs 1378 lines);
  5 pass (ToInputItems and BuildInputItems private, 1 and 2 callers; the policy gains an init property with
  default Items, so no constructor caller changes; a next form is one enum value plus one switch arm, since the
  setting's parse, expected text and the converter derive from the enum);
  6 pass (enum header field per Q13, int summary field); 7 simplest version = hard-code the content form in triggered
  mode with no setting or fields; the setting is needed for the A/B arms and the fields for the thresholds, so
  the chosen design adds only what the measurement uses.
- 2026-11-26 Run: owner present, Batched mode (Batch A = Steps 1-7, one orchestrator).
- 2026-11-26 Step 8 result (all jobs pinned at 5006238e, before the inline budget change 33f92df9; Qwen3.5-9B on 112/113,
  triggered/error, plain tool results). Main 24 runs: items 8 runs, 2 pass, 27 leaked turns; content 3 pass + 1 after
  cap, 0 leaks; drop 4 pass + 1 after cap, 0 leaks. Valid (items >= 8 leaks); both forms met the keep bar and the rule
  picked drop. Owner asked for more runs: +16 (content and drop, both scenarios, tie 3+1 each) and +16
  ThreadOptionalParameter-only. ThreadOptionalParameter totals: content 16 runs 9 pass + 5 after cap, median 25 turns;
  drop 16 runs 11 pass + 4 after cap, median 22 turns; 0 leaks in all 48 content/drop runs. SweepConstructionSites
  passed 0 in every arm. `outcomeClass` was SilentIncomplete on every run, passes included (final-message token
  classifier), so the class guard carried no signal. Jobs and rows: `c:\tmp\step8-replayform-jobs`,
  `c:\tmp\step8-replayform-runs-all2.csv`.
- 2026-11-26 Step 9 (owner: A): F = Drop. Why: drop led in every batch (4-3, 3-3, 4-3), fewest turns, smallest
  context, model-card practice and the pre-agreed rule; content stays available through the variable.

## Execution rules

- `Build` 0 errors before every commit; the dispatcher runs each step's named tests (implementers never run tests).
- Steps 1-7 keep every existing run's behaviour unchanged: the form defaults to `items`, so request bodies and
  existing transcript fields are identical unless a run sets `ROSLYNSENTINEL_REASONING_REPLAY_FORM`; new
  transcripts gain the two new fields (Step 5). Existing tests in LmStudioAgentClientInputItemsTests,
  ReasoningReplayPolicyTests, LoopAssistTrackerTests, ReasoningReplayRunnerTests, ThinkingRunnerTests and the
  AgentTranscript FinalMessage tests stay green unmodified.
- Step 8 runs only at a pinned commit containing Steps 1-7, never loads or unloads a model, and goes through
  `scripts/roslynsentinel-evalqueue.ps1`. Step 9 needs the owner's review of the Step 8 table first.
- Implementer defaults (settled, not open):
  - Invalid `ROSLYNSENTINEL_REASONING_REPLAY_FORM` value: `ParseFormOverride` throws `FormatException` with
    `StartupSettings.DescribeProblems`, exactly as `ParseWindowOverride` (`ReasoningReplayPolicy.cs:54-63`) does,
    so an eval arm never silently runs as items.
  - Tag removal is case-insensitive (like `AgentTranscript.ThinkTags`), removes every `<think>` and `</think>`,
    keeps the text between them, and trims the result.
  - `scripts/Backfill-TranscriptSummary.ps1` is not updated (it does not mirror the thinking counts either).
  - No `roslynsentinel-modeleval.ps1` parameter and no clearing of the variable there: jobs set it in their
    `env` block, like the tool-result format; the arm table's `form` column (from the transcript) exposes a run
    whose actual form differs from its job's arm.

## Steps

Slice map (two agents at most): [Step 1], then [Step 2 + Step 3], [Step 4 + Step 5], [Step 6 + Step 7]. Each pair
touches disjoint files (tests in Tests.ErrorVariants/Tests.Harness vs AgentLoop sources or scripts).
Steps 1-7 are text-neutral for every run that does not set the new variable. Step 8 is LIVE, Step 9 is
behaviour-changing and conditional.

### Step 1 - Form enum, tag helper and setting
- Files: `RoslynSentinel.AgentLoop/ReasoningReplayForm.cs` (new), `RoslynSentinel.AgentLoop/ThinkTagText.cs` (new),
  `RoslynSentinel.AgentLoop/ReasoningReplayPolicy.cs`; then `docs/generated/configuration.md` (regenerated)
- Depends on: none
- Effect: text-neutral (new members; the setting defaults to items)
- Change:
  - `ReasoningReplayForm.cs` (new, namespace `RoslynSentinel.Common`, `CreateFile`):
    `public enum ReasoningReplayForm { Items, Content, Drop }` with XML docs (Items: reasoning input items, today;
    Content: earlier reasoning sent as plain assistant message text; Drop: earlier reasoning not sent), annotated
    `[JsonConverter(typeof(JsonStringEnumConverter<ReasoningReplayForm>))]` with the comment
    `// Intentional deviation: serialized as names, see ThinkingMode.` (Decision Q13); the file needs
    `using System.Text.Json.Serialization;` (not an implicit using; the project enables only the default ones).
  - `ThinkTagText.cs` (new, namespace `RoslynSentinel.Common`, `CreateFile`, `using System.Text.RegularExpressions;`):
    `internal static class ThinkTagText`
    with `public const string OpenTag = "<think>";`, `public const string CloseTag = "</think>";`,
    `public static string RemoveTags(string text)` (per the execution-rule default: a private static readonly
    `Regex` built as `Regex.Escape(OpenTag) + "|" + Regex.Escape(CloseTag)` with `RegexOptions.IgnoreCase`,
    replaced by empty, then `Trim()`) and `public static bool HasCloseTag(string? text)` (false for null or
    empty; `text.Contains(CloseTag, StringComparison.OrdinalIgnoreCase)`).
  - `ReasoningReplayPolicy.cs` (one `batchEdits` call):
    `internal const string FormVariable = "ROSLYNSENTINEL_REASONING_REPLAY_FORM";` beside `WindowVariable`
    (line 49, same visibility);
    `public static readonly StartupSetting<ReasoningReplayForm> FormSetting = StartupSetting.Enum<ReasoningReplayForm>(null, FormVariable, ReasoningReplayForm.Items, "How earlier turns' reasoning is replayed: items (reasoning input items), content (plain assistant text) or drop (not replayed).", SettingScope.AgentLoop, SettingKind.Live);`
    (factory `StartupSetting.cs:236`, as `ThinkingOptions.ModeSetting`);
    `internal static ReasoningReplayForm ParseFormOverride(string? text)` on the `ParseWindowOverride` pattern
    (`FormSetting.Read(StartupArgs.Empty, _ => text)`, `FormatException(StartupSettings.DescribeProblems([read.Problem]))`
    on a problem, else `read.Value`);
    `public ReasoningReplayForm Form { get; init; } = ReasoningReplayForm.Items;`;
    `public static ReasoningReplayPolicy FromEnvironment(int contextLength)` (signature unchanged; today it returns
    `new ReasoningReplayPolicy(...)` directly, lines 144-147) gains an object initializer
    `{ Form = ParseFormOverride(StartupSettings.EnvironmentReader(FormVariable)) }` on that expression. The
    constructor `ReasoningReplayPolicy(int contextLength, int? fixedWindow, double pressureFraction = PressureFraction)`
    and `Mode` do not change.
  - Dispatcher, after the build: `$env:ROSLYNSENTINEL_UPDATE_GENERATED_DOCS=1; scripts/Generate-ArchitectureMap.ps1`
    to regenerate `docs/generated/configuration.md` (the freshness test pins it).
- Done when: `Build` 0 errors; `ConfigurationReferenceFreshnessTests` (Tests.ModelEval) passes after regeneration.
- Lane: `implementer` (3 files, about 7 edits). Effort: S
- Done: 9267ba2b

### Step 2 - Tests for Step 1
- Files: `RoslynSentinel.Tests.ErrorVariants/ThinkTagTextTests.cs` (new),
  `RoslynSentinel.Tests.ErrorVariants/ReasoningReplayPolicyTests.cs`
- Depends on: Step 1. Pairs with: Step 3
- Effect: tests only
- Change: tests T1-T5 of the test table.
- Done when: `Build` 0 errors; T1-T5 pass; existing `ReasoningReplayPolicyTests` pass.
- Lane: `test-writer`. Effort: S
- Done: 31bf8986

### Step 3 - Replay form in the input items
- Files: `RoslynSentinel.AgentLoop/LmStudioAgentClient.cs`
- Depends on: Step 1
- Effect: behaviour-changing only when a run sets the variable to content or drop; items is byte-identical to today.
- Change (one `batchEdits` call):
  - `private static List<object> ToInputItems(AgentChatMessage message, bool replayReasoning)` (1 caller,
    `BuildInputItems`) becomes `ToInputItems(AgentChatMessage message, bool replayReasoning, ReasoningReplayForm form)`.
  - `private static List<object> BuildInputItems(IReadOnlyList<AgentChatMessage> messages, ReasoningReplayPolicy? replayPolicy)`
    passes `replayPolicy?.Form ?? ReasoningReplayForm.Items` on both paths (policy and legacy window).
    `SerializeInputItems` keeps its signature.
  - New `internal const string ReasoningContentSeparator = "\n\n";` in `LmStudioAgentClient` (the only
    definition of the join; T7 asserts against it).
  - Assistant branch of `ToInputItems`: Items exactly as today. Drop: never adds a `ReasoningItem`; the message
    text is `ThinkTagText.RemoveTags(message.Content ?? "")`. Content: never adds a `ReasoningItem`; the message
    text is the reasoning (trimmed; only when `replayReasoning` is true and the reasoning is non-empty) joined
    with `ReasoningContentSeparator` to `message.Content` when both are non-empty, else whichever is non-empty,
    then `ThinkTagText.RemoveTags` (applied whether or not reasoning was included, Decision Q12). In both forms
    the existing condition for adding the message item applies to that text (non-empty, or no tool calls and not
    `CollapsedToolCalls`); function calls as today. Tool and user messages unchanged in every form. Comment the
    branch with the plan name and the template reason (Q4/Q12).
- Done when: `Build` 0 errors; existing `LmStudioAgentClientInputItemsTests`, `ReasoningReplayPolicyTests` and
  `LoopAssistTrackerTests` pass unmodified.
- Lane: `implementer` (1 file, about 5 edits). Effort: S
- Done: 074c2e48

### Step 4 - Tests for Step 3
- Files: `RoslynSentinel.Tests.ErrorVariants/LmStudioAgentClientInputItemsTests.cs`
- Depends on: Step 3. Pairs with: Step 5
- Effect: tests only
- Change: add a `Serialize(ReasoningReplayPolicy policy, params AgentChatMessage[] messages)` overload beside the
  existing `Serialize` helper (`LmStudioAgentClientInputItemsTests.cs:147-151`), like
  `ReasoningReplayPolicyTests.Serialize` (`:221-225`); then tests T6-T11 of the test table with a policy built as
  `new ReasoningReplayPolicy(contextLength, 0) { Form = ... }` (window 0 = replay all). Hoist the `WithoutId` local
  function (line 110, inside `ReasoningWindow_KeepsTheEarlierRequestAsAPrefix_BetweenBlockSteps`) to a private
  static helper of the class and use it there and in T11 (each reasoning item gets a fresh `rs_<guid>` id);
  `ReasoningReplayPolicyTests.cs:72` keeps its own copy (other assembly class, out of this step).
- Done when: `Build` 0 errors; T6-T11 pass.
- Lane: `test-writer`. Effort: S
- Done: 541f8b3c

### Step 5 - Transcript fields and runner stamp
- Files: `RoslynSentinel.AgentLoop/AgentTranscript.cs`, `RoslynSentinel.AgentLoop/ModelAgentRunner.cs`
- Depends on: Step 1
- Effect: text-neutral for runs (two new transcript fields; existing fields unchanged)
- Change:
  - `AgentTranscript.cs`: header property `public ReasoningReplayForm? ReasoningReplayForm` (style of
    `ReasoningReplayMode`, lines 163-167; doc: the form earlier reasoning was replayed in, null in transcripts that
    predate it, which replayed as items). `RunTotals`: `public int LeakedThinkTagTurnCount` beside
    `OverBudgetTurnCount` (line 456), computed in
    `public static RunTotals FromTranscript(AgentTranscript transcript, TimeSpan totalDuration)` next to lines
    547-548 as the number of turns whose `ModelMessage.Content` satisfies `ThinkTagText.HasCloseTag`. Rebuild the
    private `ThinkTags` regex (lines 231-233, today `<think>[\s\S]*?</think>|</?think>`) as
    `open + @"[\s\S]*?" + close + "|" + open + "|" + close` with `open = Regex.Escape(ThinkTagText.OpenTag)` and
    `close = Regex.Escape(ThinkTagText.CloseTag)`, same options, so the tag literal has one definition;
    text-neutral (the FinalMessage tests pin it).
  - `ModelAgentRunner.cs`: after line 364 (`transcript.ReasoningReplayMode = replayPolicy.Mode;`) add
    `transcript.ReasoningReplayForm = replayPolicy.Form;`. Reword the TODO at line 433 (434 after the insert) to: `// TODO Triggered mode: the reasoning replay form (ROSLYNSENTINEL_REASONING_REPLAY_FORM) stops the </think> leak when set to content; flip the triggered default after the live comparison (plan_triggered_thinking_reasoning_as_content Step 9)`.
- Done when: `Build` 0 errors; existing `ReasoningReplayRunnerTests`, `ThinkingRunnerTests` and
  `AgentTranscriptFinalMessageTests` pass unmodified.
- Lane: `implementer` (2 files, about 6 edits). Effort: S
- Done: 3ead6848

### Step 6 - Tests for Step 5
- Files: `RoslynSentinel.Tests.Harness/ReasoningReplayRunnerTests.cs`,
  `RoslynSentinel.Tests.Harness/RunTotalsLeakCountTests.cs` (new)
- Depends on: Step 5. Pairs with: Step 7
- Effect: tests only
- Change: extend `ReasoningReplayRunnerTests.SetUp`/`TearDown` (lines 42-68, which save, clear and restore
  `ROSLYNSENTINEL_REASONING_WINDOW` with `Environment.SetEnvironmentVariable`) to do the same for
  `ReasoningReplayPolicy.FormVariable`; then tests T12, T13, T15 and T18, and extend the existing
  `Transcript_WithoutReasoningReplayFields_Deserializes` (lines 156-169) with the T14 assertion instead of a new
  test, changing its JSON literal from `{"RunId":"r1"}` to `{"RunId":"r1","Summary":{}}` so `Summary` is not null.
  T15's file builds its `AgentTranscript` with its own small local builder (the `TranscriptWith` helper in
  `AgentTranscriptFinalMessageTests` is private to another assembly).
- Done when: `Build` 0 errors; T12-T15 and T18 pass.
- Lane: `test-writer`. Effort: S
- Done: 190b89ec

### Step 7 - Arm table columns and eval docs
- Files: `scripts/Get-ThinkingArmTable.ps1`, `docs/current/references/reference_model_eval_procedure.md`
- Depends on: Step 5 (field names)
- Effect: script output gains columns; existing arm labels unchanged
- Change:
  - `Get-ThinkingArmTable.ps1`: the arm key (`$parts` at lines 151-155, `$arm` at 156) appends `/<form>` only
    when the job's `env` sets `ROSLYNSENTINEL_REASONING_REPLAY_FORM`, so existing labels stay as they are.
    `Read-Run` (row at line 139) adds `form` (transcript `ReasoningReplayForm`, else `items`; compared
    case-insensitively) and `leakTurns` (`Summary.LeakedThinkTagTurnCount` when present, else the count of turns
    whose `ModelMessage.Content` contains `</think>`, for transcripts that predate the field). The arm row (line
    184) adds `form` (the distinct transcript forms of the arm's runs, comma-joined, so a run whose worker shell
    overrode the job's arm shows up) and `leakTurns` (total) and `leakTurns/run`. Add the new columns to the
    `-CsvPath` `Select-Object` list (lines 170-172). Update the comment-based help (arm key at lines 9-10, columns
    at 18-28). About 10 edits in the script plus 1 in the doc (implementer tier 1 allows 15).
  - `reference_model_eval_procedure.md`: one line for `ROSLYNSENTINEL_REASONING_REPLAY_FORM` beside the thinking
    variables (lines 78-81): values, default items, set per job in `env`, see this plan.
- Done when: `pwsh -NoProfile -File scripts/Get-ThinkingArmTable.ps1 -JobFolder c:\tmp\thinking-smoke-jobs` prints
  `leakTurns` 24 for the `triggered/error/-/-` arm and 0 for `baseline` and `always/-/-/-` (smoke at 8e02735a,
  INBOX 2026-11-25 13:11), with arm labels unchanged.
- Lane: `implementer` (non-C# files, Edit tool). Effort: S
- Done: d44dffb7

### Step 8 - LIVE comparison on the 9B
- Files: none (eval output under the scratch root)
- Depends on: Steps 1-7 committed; hosts 112 and 113 free (`scripts/roslynsentinel-evalqueue.ps1 status -ActiveOnly`
  empty and no direct eval running there; ask the owner if unsure)
- Effect: none on code; produces the decision table
- Lane: `modeleval-runner`, through `scripts/roslynsentinel-evalqueue.ps1` (`reference_model_eval_procedure.md`);
  model `unsloth/Qwen3.5-9B-MTP-GGUF` on 112 and 113; never load or unload models.
- Change: test `ImplementationScenario`, queued with `-Commit HEAD` (the pinned commit) and `-NoRunFolderMinutes 0`,
  `-EvalArgs "-Scenario <SweepConstructionSites|ThreadOptionalParameter> -StepWallClockMinutes 30"` on 113 and 45 on
  112 (the live-run settings of `reference_model_eval_procedure.md:478-479`), 4 rounds per scenario per arm, 24 jobs,
  each arm's rounds split 2/2 over the hosts per scenario. Every job sets
  `ROSLYNSENTINEL_MODELEVAL_THINKING=triggered`, `ROSLYNSENTINEL_MODELEVAL_THINKING_TRIGGERS=error` and
  `ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT=plain` (the default since e3995b05; stated explicitly so every arm
  matches; the 8e02735a smoke baseline used json, so only the Step 8 items arm is the control):
  | Arm | `ROSLYNSENTINEL_REASONING_REPLAY_FORM` |
  | --- | --- |
  | items (control) | `items` |
  | content | `content` |
  | drop | `drop` |
  Report: `Get-ThinkingArmTable.ps1` table (copy both machines' job files into one `-JobFolder`; the script reads
  one machine per call), per arm leaked turns (total and per run), `outcome=Pass` count, and the `outcome=`,
  `stopReason=` and `outcomeClass=` counts as the outcome classes.
  Validity (Decision Q14): the comparison counts only if the items arm has at least 8 leaked turns across its 8
  runs; otherwise report it as inconclusive, without a verdict.
  Thresholds (Decisions Q10 and Q14, agreed before the run): keep content (Step 9 flips to content) if the
  content arm has at most 1 leaked turn across its 8 runs, passes at least items passes minus 1, and no
  `outcomeClass` value counts more than 2 runs higher than in items; choose drop instead only if drop meets the
  same bar and passes more runs than content; park (no Step 9; write a finding per
  `docs/current/templates/README.md`) if content keeps more than 25% of the items arm's leaked turns. Anything
  else: report to the owner without a verdict. `outcome=` and `stopReason=` counts are reported, not scored.
  Every run's transcript `form` must match its arm; a mismatched run is excluded and resubmitted.
- Done when: 24 jobs finished, the table and verdict are in the run tracker, and the owner has reviewed them.
- Effort: M
- Done: 2026-11-26, 56 jobs at 5006238e, verdict drop (Decision, Step 8 result)

### Step 9 - Default flip (only on a keep result, after owner review)
- Files: `RoslynSentinel.AgentLoop/ReasoningReplayPolicy.cs`, `RoslynSentinel.AgentLoop/ModelAgentRunner.cs`,
  `RoslynSentinel.Tests.Harness/ReasoningReplayRunnerTests.cs`; then `docs/generated/configuration.md`
- Depends on: Step 8 verdict keep (content or drop) and owner OK
- Effect: behaviour-changing: triggered runs without the variable replay earlier reasoning in the chosen form
- Change: let F be the form Step 8 chose (Content or Drop). `FromEnvironment(int contextLength)` gains an optional
  `ReasoningReplayForm unsetDefault = ReasoningReplayForm.Items`, used when
  `FormSetting.Read(StartupArgs.Empty, StartupSettings.EnvironmentReader).Source == SettingSource.Default` (the
  way `ThinkingOptions.FromEnvironment` tells unset from explicit, `ThinkingOptions.cs:104`); an explicit
  variable still wins. Its 1 caller (`ModelAgentRunner.cs:358`) passes F when
  `_thinking.Mode == ThinkingMode.Triggered`, else Items. Update the `FormSetting` description ("unset: F in
  triggered mode, else items") and regenerate the configuration doc. Remove the TODO reworded in Step 5. Close
  INBOX lines 2026-11-25 13:11, 15:35 and 2026-11-26 00:30 per the INBOX rules (CLOSED.md). Tests T16-T17: extend
  `ReasoningReplayRunnerTests.NewRunner` (lines 179-198) with an optional `ThinkingOptions? thinking = null` passed
  to the `ModelAgentRunner` constructor's `thinking:` parameter (`ModelAgentRunner.cs:136` and `:181`).
- Done when: `Build` 0 errors; T16, T17, existing `ReasoningReplayRunnerTests` and
  `ConfigurationReferenceFreshnessTests` pass.
- Lane: `implementer` then `test-writer`. Effort: S
- Done: 83a8e80b (F = Drop)

## Tests

| # | Test (class.method) | Behaviour | Fixture | Asserts | Pass |
| --- | --- | --- | --- | --- | --- |
| T1 | ThinkTagTextTests.RemoveTags_LoneCloseTag_KeepsProse | Q4 strip keeps the leaked prose | none (static) | result equals the prose without the tag | equal |
| T2 | ThinkTagTextTests.RemoveTags_TagPairAnyCase_KeepsInnerText | strip removes `<think>`/`</THINK>`, keeps inner text | none | result equals the inner text | equal |
| T3 | ThinkTagTextTests.HasCloseTag_OnlyWhenCloseTagPresent | leak detection | none | true for `x</think>`, false for null, empty, `x` and `<think>x` | all four |
| T4 | ReasoningReplayPolicyTests.ParseFormOverride_ReadsForm | setting values (TestCase `items`, `Content`, `drop`, null, empty -> Items) | none: calls `ReasoningReplayPolicy.ParseFormOverride(text)` like the existing `ParsePressureFractionOverride` tests (no process-wide env change) | returned enum | matches case |
| T5 | ReasoningReplayPolicyTests.ParseFormOverride_Invalid_ThrowsNamingTheVariable | invalid value refused | same | `Assert.Throws<FormatException>`; message contains `ReasoningReplayPolicy.FormVariable` (as the pressure tests at `:185-201` check their variable) | both |
| T6 | LmStudioAgentClientInputItemsTests.ContentForm_ReasoningOnlyTurn_SendsReasoningAsMessageBeforeFunctionCall | Q1 content form | in-memory messages | parsed JSON: no item of type `reasoning`; a `role=assistant` item whose `content` equals the reasoning, before the `function_call` | all |
| T7 | LmStudioAgentClientInputItemsTests.ContentForm_ReasoningAndText_JoinsWithBlankLine | Q3 default | same | assistant `content` equals reasoning + `LmStudioAgentClient.ReasoningContentSeparator` + text | equal |
| T8 | LmStudioAgentClientInputItemsTests.ContentForm_LeakedCloseTagInContent_IsRemoved | Q4 replay strip | same | assistant `content` has no `</think>` (`ThinkTagText.HasCloseTag` false) and keeps the prose | both |
| T9 | LmStudioAgentClientInputItemsTests.ContentForm_PolicyDroppedTurn_SendsTextOnly | Q5 drops apply | `new ReasoningReplayPolicy(contextLength, 2) { Form = Content }` with enough turns to drop the first | dropped turn's assistant `content` equals its text only | equal |
| T10 | LmStudioAgentClientInputItemsTests.DropForm_NeverSendsReasoning_AndRemovesLeakedTags | Q1 drop form, Q12 | same, one turn with reasoning and a Content ending in `</think>` | no `reasoning` item, no item containing the reasoning text, and the assistant `content` has `ThinkTagText.HasCloseTag` false while keeping the prose | all |
| T11 | LmStudioAgentClientInputItemsTests.ItemsForm_IsIdenticalToPolicyWithoutForm | default unchanged | same | serialized JSON with explicit `Form = Items` equals the JSON from the same policy without `Form` set, and contains a `reasoning` item | both |
| T12 | ReasoningReplayRunnerTests.Transcript_StampsReasoningReplayForm | Q9/Q13 header (TestCase unset -> Items, `content` -> Content) | existing `ReplayScriptedHandler` runner fixture, variable set via `Environment.SetEnvironmentVariable` and restored in TearDown | `transcript.ReasoningReplayForm` enum | equal |
| T13 | ReasoningReplayRunnerTests.ContentForm_RequestBodiesCarryReasoningAsAssistantText | end-to-end form reaches the wire | same, variable `content`, scripted turns whose reasoning holds a unique marker | at least 2 captured `RequestBodies`; in the last one, parsed `input` has no item with `type` `reasoning` and an assistant message item whose `content` contains the marker | all |
| T18 | ReasoningReplayRunnerTests.Serialize_WritesReasoningReplayFormName | Q13 serialization | in-memory `AgentTranscript`, serialized with `ModelAgentRunner.TranscriptFileOptions` as `ThinkingRunnerTests.Serialize_WritesEnumNames` (`ThinkingRunnerTests.cs:341`) does | parsed JSON property `ReasoningReplayForm` is the string `Content`; deserialized back it equals `ReasoningReplayForm.Content`; with null it round-trips as null | all three |
| T14 | ReasoningReplayRunnerTests.Transcript_WithoutReasoningReplayFields_Deserializes (existing, extended) | backward compatibility | its existing JSON without the fields | also `ReasoningReplayForm` null and `Summary` deserializes with `LeakedThinkTagTurnCount` 0 | both |
| T15 | RunTotalsLeakCountTests.FromTranscript_CountsTurnsWithCloseTagInContent | Q9 summary count | in-memory `AgentTranscript` with 3 turns (one leaked, one reasoning-only with `</think>` in ReasoningContent, one clean) | `LeakedThinkTagTurnCount` == 1 | equal |
| T16 | ReasoningReplayRunnerTests.Triggered_FormUnset_UsesFlippedForm | Step 9 default (F = the Step 8 form, fixed when Step 9 is briefed) | runner fixture constructed with `thinking:` triggered options; a second case with thinking off | header form F for triggered, Items for off | both |
| T17 | ReasoningReplayRunnerTests.ExplicitForm_WinsOverTriggeredDefault | Step 9 override | same, triggered, variable `items` | header Items | equal |

All fixtures are in-process (no build, test or git subprocess). T1-T3 assert text because the text transformation
itself is under test; the tag comes from `ThinkTagText.CloseTag`. T12, T13, T16 and T17 live in the
`[NonParallelizable]` `ReasoningReplayRunnerTests`, whose SetUp/TearDown save, clear and restore the form variable
(Step 6).

Baseline: smoke at 8e02735a (`c:\tmp\thinking-smoke-runs.csv`): triggered arm 24 leaked turns in 4 runs
(192444: 8, 192447: 11, 192501: 5, 192457: 0; INBOX 2026-11-25 13:11), pass 1/4. Step 8's items arm re-measures the
control at the pinned commit; the thresholds compare against that arm, not the smoke numbers.

Size: `ToInputItems` 1 caller, `BuildInputItems` 2 (`CompleteOnceAsync`, `SerializeInputItems`),
`ReasoningReplayPolicy.FromEnvironment` 1 (`ModelAgentRunner.cs:358`); the new `Form` init property changes no
constructor call. Projects: RoslynSentinel.AgentLoop, Tests.ErrorVariants, Tests.Harness.

## Out of scope

- Plain-text tool results (proposal_plain_text_tool_results_for_eval_model.md).
- Enforced reasoning budget (deferred in the closed thinking plan).

## Risks and open decisions

Grounding 2026-11-26 (explore-tier2, sonnet): ToInputItems `LmStudioAgentClient.cs:853-899` (reasoning item
875-883), BuildInputItems 828-851, both static with no thinking info; ReasoningReplayPolicy is already passed to
CompleteAsync (only production caller `ModelAgentRunner.cs:434`) and to `SerializeInputItems` (3 test classes);
TODO at `ModelAgentRunner.cs:433`. LmStudioAgentClient.cs 1378 lines, ModelAgentRunner.cs 1208,
ReasoningReplayPolicy.cs 239. Registry plan closed (ThinkingOptions Step 15 done, 3fc5b101).

Open: none (phase 6/7).

Risks:
- Q7 User-role notes (malformed-call nudge `ModelAgentRunner.cs:614`, identical-success nudge
  :743-747, loop-assist injection :810 when enabled; role from LoopAssistOptions.InjectionRole, default user) move the
  template's last_query_index, so the template already drops reasoning before them; the content form would show
  that reasoning as text. Accepted as a risk (nudges are rare), not mimicked.
- Evidence for the content form is one decision point on the 4B; the 9B comparison (Q10) decides the default.
- Q11 plan_plain_text_tool_results_for_eval_model flipped the tool-result default to plain (e3995b05). Plain
  results may change the leak rate; Step 8 pins `plain` in every arm, re-measures the items control under it, and
  the Q14 validity gate catches a control that no longer leaks.
