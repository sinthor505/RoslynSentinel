# Plan: Render tool results as plain text or raw XML for the eval model (opt-in A/B setting)

**Status:** APPROVED 2026-11-26. Owner accepted all recommendations; the owner delegated readiness to the session (away overnight).

## Problem

Source: `proposals/proposal_plain_text_tool_results_for_eval_model.md`.

The agent loop sends each MCP tool result to the model as raw JSON text, so every `"` in quoted C#
reads as `\"`. The Qwen3.5 template makes the model write tool-call arguments raw, so it must
un-escape on every copy, and often does not (run 20261125-192447: `.\";` sent on six ReplaceSnippet
retries). Single-turn probes (Decision log, Q3 input): JSON leaks 16/100 without thinking and 25-47/100
with thinking; plain-text and raw-XML renderings leak 0.

## Decision

Summary: a renderer in `RoslynSentinel.AgentLoop` turns the final tool-result JSON into `plain` or
`xml` text at the one seam where the result becomes the model message
(`ModelAgentRunner.ModelFacingToolContent`). An env var selects the format, default `json`
(identity). Transcripts keep `ResultJson` unchanged. A 48-run interleaved A/B (3 formats x 2 thinking
modes) measures it. If a format meets the keep rule, a final step makes it the default (owner reviews
first).

Decision log:

- 2026-11-25 Q11 (escape-copy counter, investigation): there is no structured server signal. The
  escaped-quote diagnosis is hint text only (`SnippetErrorFormatter.cs:131`, `:590-640`), and the
  error code is the generic `NotFound`. Counter rule, validated on smoke run 20261125-192447
  (`...ThreadOptionalParameter_Step1\20261125-192735-136\transcript.json`, turns 8-13):
  - Inputs: the decoded string arguments `oldContent`, `newContent` and `content` of the edit and
    create tools, split per line.
  - Count a line when all of these hold: it contains `\"`; its form with `\"` replaced by `"`
    (trimmed) appears in the latest read of that `filePath`; the verbatim line does not appear there.
  - "Latest read" is reset after a successful write.
  - Report the count per call and per line.
  - A rule gated on `IsError` would miss turn 13 (a successful `CreateFile` containing `\"`).
  - Script model: `scripts/Get-EditToolFriction.ps1` (transcript walk). The arm comes from the job env,
    as in `scripts/Get-ThinkingArmTable.ps1:151-163`.
  - Probe counter: a bare `Contains('\"')` (`<scratch root>\template_probe\probe_plaintext.ps1:54`).
- 2026-11-25 Q3 input (format probe, `<scratch root>\template_probe\probe_xml.ps1`, results
  `probe_xml.csv`, sample renderings `xmlprobe_example_*.txt`): Qwen3.5-9B on .113, thinking off,
  temperature 0.7, N=15 per arm, one-turn ReadFile -> ReplaceSnippet copy. Arms: `json` (as sent),
  `plain` (probe_plaintext renderer), `xml` (one tag per field, text raw), `xmlesc` (well-formed XML,
  `& < >` escaped). Stray `\"` / entity leak / exact copy:
  - real file: json 15/0/0, plain 0/0/15, xml 0/0/15, xmlesc 0/2/13.
  - synthetic: json 5/0/10, plain 0/0/15, xml 0/0/15, xmlesc 0/0/15.
  - No stray `<source>`/`<item>` tags in any arm; no errors.
  - Reading: escaping is the cause, not the format; any rendering with unescaped C# text works.
    Well-formed XML reintroduces the defect via `&lt;` (`IReadOnlyCollection<string>`), so an XML
    renderer must keep text raw. The probe cannot separate `plain` from raw `xml`; that needs the
    multi-turn A/B (Q6).
  - N=100 rerun (`probe_xml_n100.csv`), plus a `complex` context (escape-heavy file: verbatim and raw
    strings, regex, a string holding a legit `\"` and `\\`, generics, `&&`, a `findings` array of
    objects). Leak classes: JSON leak (matches after JSON unescape), entity leak, other. Exact copy:
    small json 66 / plain 100 / xml 100 / xmlesc 100; real 0 / 100 / 100 / 90 (10 entity leaks);
    complex 98 / 100 / 100 / 100 (json: 1 JSON leak, 1 other). No stray tags, no errors.
  - JSON scoring 98/100 on the densest escaping is not evidence of safety: single-turn probes give a
    slip rate, not an upper bound (cf. the `.O` slip, which reproduced only with the full history).
- 2026-11-25 Q2/Q3 input (escaping note, `probe_note.ps1`, `summarize_note.ps1`; small context, JSON
  result, N=100 per arm split 67 on .113 / 33 on .112, host recorded per row). Note text:
  "Tool results are JSON. Inside string values, text is JSON-escaped: `\"` stands for `"`, `\\` for
  `\`, `\n` for a line break. Files contain the decoded text." (input half) + "Write tool-call argument
  values as raw text, so copy code in its decoded form." (output half).
  - Run 1, JSON leak: control 16; input half in system prompt 1; output half 1; both 0; both appended
    to the tool result 4 (all on .112); thinking 25.
  - Run 2, host reasoning budget off: control 16; thinking 28; thinking + both halves in system
    prompt 13.
  - Run-to-run variance is large (same setup on .113 alone: 34/100 earlier), so compare arms within
    one run only.
  - Run 3, thinking on, result format varied: JSON leak 47/100; plain 0/100 (100 exact); raw XML
    0/99 (99 exact, 1 no tool call). Rendering removes the thinking penalty.
  - Leak vs reasoning content (thinking + JSON, runs 2 and 3, n=199): reasoning that reproduces the
    code with `\"` leaked 41/47; reasoning that reproduces it decoded leaked 0/47; reasoning that does
    not reproduce the code leaked 34/105.
- 2026-11-25 Scenario facts: the four 113 scenarios are SweepConstructionSites,
  ThreadOptionalParameter, WideningRejectedByGate and WidenToReadOnlyDictionary. The Qwen baseline at
  c52d49f4 was 14 of 64 Pass, median wall 1.4-3.8 min per run (closed plan
  `plan_error_gated_thinking_and_client_reasoning_budget.md`, Decision Q21).
- 2026-11-26 owner accepted all of the following ("accept all"):
  - Q1 scope: agent loop only; every `ModelAgentRunner` (eval, PlanStepRunner, `SubAgentModelRun`)
    reads the setting because they share the seam. Server-side rendering for other MCP clients is a
    later proposal.
  - Q2 switch: `ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT = json | plain | xml`, default `json`,
    a `StartupSetting<T>` descriptor (`SettingScope.AgentLoop`), unknown value fails fast naming the
    variable, value and allowed values. Recorded in the transcript header as `ToolResultFormat`.
  - Q3 formats: build both `plain` and `xml`.
  - Q4 edge rules (shared): key order kept (so `largeResult` stays first); no escaping of any kind
    inside values (no JSON escapes, no XML entities); a body that does not parse as a JSON object or
    array passes through unchanged; numbers and booleans written as their JSON text.
    - Plain: `key: value` per scalar; objects nest by two-space indentation; array items are
      `- [i]` lines (Q19); `null` for null. A string is a block when it is multi-line, empty, or has
      leading or trailing whitespace: `key:` on its own line, the raw text from column 0, then a line
      `(end of <path>)` where `<path>` is the JSON path in `ResultPager` syntax (`successData.source`,
      `successData.hits[3].text`). A string that contains its own terminator line is written as a
      JSON-quoted single-line value instead.
    - XML: `<key>value</key>` with raw text; nested objects as nested elements; array items as
      `<item i="N">`; null as `<key/>`. A key that is not a valid XML name becomes
      `<entry key="...">` (attribute value with `"` and `&` written as `&quot;` / `&amp;`, keys only).
      A string containing its own closing tag is written as `<key encoding="json">"..."</key>`. A
      multi-line string starts on the line after the open tag and the close tag sits on its own line,
      so the copied lines carry no tag text; a single-line string stays inline.
    - Root: the top-level object's fields are written with no wrapper element / no indentation.
  - Q5 models: Qwen3.5-9B only (the model loaded on 112/113).
  - Q6 + Q16 measurement: one interleaved batch over hosts 112 and 113, 3 formats (`json`, `plain`,
    `xml`) x thinking (`off`, `triggered` with triggers `error`) x the four scenarios x 2 rounds =
    48 runs, pinned to one commit. Columns: Pass (`outcome=`), `stopReason=`, escape-copy lines,
    argument parse failures (Q20), median model-facing characters per tool result, median wall.
    Thresholds (agreed before any run; directional at n=16 per format, n=24 per thinking level):
    - keep: escape-copy lines down >= 80% vs `json` AND Pass no more than 10 pp below `json`;
    - drop: escape-copies down < 50%, OR Pass down >= 15 pp, OR argument parse failures up by >= 3
      per 16 runs;
    - between: park or add rounds (owner);
    - both formats keep: the one with fewer median model-facing characters wins;
    - no verdict: `json` arm total escape-copy lines < 5 (owner decides on more rounds).
  - Q10: `SnippetErrorFormatter.DescribeEscapedQuotes` TODO stays separate (TODO.md).
  - Q12: `roslynsentinel-interrogate.ps1` replays `ResultJson`; leave a `# TODO` there and an INBOX
    line, no change.
  - Q14: one plan.
  - Q17: no system-prompt escaping note in the eval; INBOX line as a candidate for other harnesses.
  - Q18: on a keep result, the last step flips the default to the winning format, after the owner has
    seen the table.
  - Q19: array items always show their index (`- [i]`, `<item i="i">`), matching `ResultPager` path
    syntax so `GetLargeResult(path: ...)` and `omitted[].path` stay formable.
  - Q20: report argument parse failures per arm (calls whose tool result is an argument
    deserialization / validation error, or whose `ArgumentsJson` fails to parse).
  - Q21: no DI container. `ToolResultFormat` enum, `IToolResultRenderer` interface, an abstract
    `JsonTreeRenderer` base holding the shared walk and Q4 shared rules, `PlainToolResultRenderer`,
    `XmlToolResultRenderer`, an identity renderer for `json`, one factory `ToolResultRenderers.For`.
    `ModelAgentRunner` gets an optional `ToolResultFormat?` ctor parameter defaulting to the env var
    (as `loopAssist`), so no construction site changes.
- 2026-11-26 implementer defaults (owner delegated): Q7 `</think>` leak not measured; Q8 render at
  both call sites of `ModelFacingToolContent` (live `:676`, resume `:888`), `TrailingText` appended
  unchanged after the rendered first block; Q9 no prompt change (field names kept verbatim); Q13
  header field `ToolResultFormat` beside `Thinking` (`AgentTranscript.cs:171`); Q15
  `EstimateMessageTokens` follows the rendered text. New: `AgentToolCallRecord.ModelFacingChars`
  (int?, length of the text sent for that call) so the arm table can report size without a renderer.
- 2026-11-26 design review (checklist 1-7): 1 pass (enum, interface, named classes; no tuples);
  2 pass (env var in one descriptor; terminator and index syntax defined once in the base / each
  renderer); 3 pass (no renderer exists; index syntax reuses `ResultPager` path syntax; option pattern
  reuses `LoopAssistOptions`); 4 pass (new files in `AgentLoop`, refs `Common` only; no `Common`
  change; `ModelAgentRunner.cs` 1196 lines + ~20); 5 pass (`ModelFacingToolContent` has 2 call sites,
  optional ctor param reaches 0 of 33 construction sites; a format change is one class; hot path:
  one `JsonDocument` parse per tool call, negligible next to model latency); 6 pass (format and size
  are typed transcript fields); 7 simplest version is one plain renderer with no switch; the switch is
  needed for the A/B arms and the second renderer costs one subclass on the shared base.
- 2026-11-26 Readiness answers (independent `explore-tier2` pass raised 9 questions; settled by the
  session under the owner's delegation):
  - Namespaces: new `AgentLoop` files use `RoslynSentinel.Common.AgentLoop` (sibling precedent); new
    test files in `RoslynSentinel.Tests.Harness/` use namespace `RoslynSentinel.Tests.ModelEval`
    (as `ToolResultTrailingBlockRunnerTests.cs:27`). `ConfigurationReferenceFreshnessTests` lives in
    `RoslynSentinel.Tests.ModelEval/`.
  - Descriptor: `SettingKind.Live`, `SettingScope.AgentLoop`, as `LoopAssistOptions` (`LoopAssist.cs:37-83`).
    `ToolResultFormatOptions.FromEnvironment(Func<string, string?>? read = null)` (test seam as
    `LoopAssist.cs:76`) throws `InvalidOperationException` naming the variable, the bad value and the
    allowed values when the read reports a problem.
  - `ModelFacingToolContent` stays `private static` and gains an `IToolResultRenderer renderer`
    parameter; the runner holds the renderer in a readonly field resolved in the `IAgentToolHost` ctor.
  - `ModelFacingChars`: the live path computes the model-facing text before it builds the
    `AgentToolCallRecord` (`:640-656`) and sets the property in the initializer. Seeded records keep
    their recorded value (no recompute).
  - Q4 additions: empty object / array: plain `key: {}` / `key: []`, XML `<key></key>`. A scalar array
    item is `- [i] value`; an object or array item is `- [i]` followed by its content indented two
    more spaces. Root array: items at column 0 (plain) / `<item i="N">` elements at root (XML).
    Multi-line = contains `\n`; block text is written exactly as decoded (CRLF kept).
  - Q11/Q20 inputs: the script implementer reads one real transcript to list the edit and create
    tools and their string arguments (at least `oldContent`, `newContent`, `content`, also inside
    `batchEdits` items) and the read results holding file text (`successData.source` of `ReadFile`,
    `GetMethodSource`); argument-failure error codes are picked from `ToolErrorCode`
    (`RoslynSentinel.Common/SentinelCallToolResult.cs:75`) and listed in the script header.
  - Hot path: `plain`/`xml` add one `JsonDocument` parse per tool call beside `BodyReportsFailure`;
    accepted (microseconds to milliseconds against seconds of model latency). `json` does no parse.
- 2026-11-26 Readiness (plan-design): owner delegated; independent review returned the 9 questions
  above, all settled; anchors corrected.

## Execution rules

- Build 0 errors before every commit; test baseline 0 failures; run one `Build`/`RunTest` at a time.
- All C# edits through the RoslynSentinel MCP tools (CLAUDE.md, Dog-fooding).
- `json` must stay byte-identical to today: `ToolResultTrailingBlockRunnerTests` (`Tests.Harness`)
  stays green unchanged.
- Commit each step with `Plan: plan_plain_text_tool_results_for_eval_model` and `Co-Authored-By`.

## Steps

### Step 1 - Contract skeleton and setting
- Files: new `RoslynSentinel.AgentLoop/ToolResultRendering.cs` (enum `ToolResultFormat`,
  `IToolResultRenderer`, `ToolResultRenderers.For`, identity renderer, abstract `JsonTreeRenderer`),
  new `RoslynSentinel.AgentLoop/PlainToolResultRenderer.cs`, new
  `RoslynSentinel.AgentLoop/XmlToolResultRenderer.cs`, new `RoslynSentinel.AgentLoop/ToolResultFormatOptions.cs`
  (descriptor + `FromEnvironment()`, modelled on `LoopAssistOptions` in `LoopAssist.cs:37-83`),
  `docs/generated/configuration.md` (regenerated by `scripts/Generate-ArchitectureMap.ps1`).
- Depends on: none
- Effect: text-neutral (new types, renderer bodies `throw new NotImplementedException(); // TODO implement (Step 2|3)`; identity renderer and options implemented).
- Change: final signatures: `public enum ToolResultFormat { Json, Plain, Xml }`;
  `public interface IToolResultRenderer { string Render(string resultJson); }`;
  `public static class ToolResultRenderers { public static IToolResultRenderer For(ToolResultFormat format); }`.
  Env var `ROSLYNSENTINEL_MODELEVAL_TOOL_RESULT_FORMAT`, values `json|plain|xml` (case-insensitive),
  default `json`, under a `[StartupSettingGroup("Tool result format (ModelEval)")]`.
- Tests: new `RoslynSentinel.Tests.Harness/ToolResultFormatOptionsTests.cs`: unset -> Json; each value
  parses; unknown value throws naming the variable. `ConfigurationReferenceFreshnessTests` green.
- Done when: Build 0 errors; the new tests and `ConfigurationReferenceFreshnessTests` pass.
- Effort: S

### Step 2 - Shared walk and plain renderer
- Files: `ToolResultRendering.cs` (`JsonTreeRenderer` body), `PlainToolResultRenderer.cs`, new
  `RoslynSentinel.Tests.Harness/PlainToolResultRendererTests.cs`.
- Depends on: Step 1
- Effect: text-neutral (no caller yet).
- Change: implement Q4 shared + plain rules.
- Tests (fixture: inline JSON strings shaped like real results: a ReadFile success with a C# source
  holding `"`, `\`, `<`, `&`, tabs and leading indentation; an error envelope with `structuredDetail`;
  a `largeResult` page with `omitted[]`). The rendering is the wording under test, so substring
  asserts on leaf values are allowed (checklist 11 exception):
  `Render_EveryLeafValueAppearsVerbatim` (walk the input with `JsonDocument`; each string leaf's
  lines, each key and each number appears in the output);
  `Render_MultiLineSource_IsBlockWithPathTerminator`; `Render_StringContainingTerminator_FallsBackToJsonQuoted`;
  `Render_ArrayItems_ShowIndex`; `Render_KeyOrderPreserved_LargeResultFirst`;
  `Render_NonJsonBody_ReturnedUnchanged`; `Render_LeadingWhitespaceString_IsBlock`.
- Done when: Build 0 errors; these tests pass.
- Effort: M
- Pairs with: Step 3 (disjoint files except none; Step 3 touches only its own files)

### Step 3 - XML renderer
- Files: `XmlToolResultRenderer.cs`, new `RoslynSentinel.Tests.Harness/XmlToolResultRendererTests.cs`.
- Depends on: Step 1 (uses the base from Step 2 if it lands first; otherwise overrides the abstract
  hooks declared in Step 1)
- Effect: text-neutral.
- Change: Q4 XML rules.
- Tests (same fixtures, copied into the test file): `Render_EveryLeafValueAppearsVerbatim`,
  `Render_NoEntitiesInValues` (`<`, `&` stay raw), `Render_InvalidXmlNameKey_UsesEntryKey`,
  `Render_ValueContainingOwnClosingTag_FallsBackToJsonEncoding`, `Render_ArrayItems_HaveIndexAttribute`,
  `Render_MultiLineValue_TagsOnOwnLines`, `Render_NonJsonBody_ReturnedUnchanged`.
- Done when: Build 0 errors; these tests pass; no `NotImplementedException` stub left.
- Effort: M

### Step 4 - Wire into the runner and transcript
- Files: `RoslynSentinel.AgentLoop/ModelAgentRunner.cs`, `RoslynSentinel.AgentLoop/AgentTranscript.cs`,
  new `RoslynSentinel.Tests.Harness/ToolResultFormatRunnerTests.cs`.
- Depends on: Steps 2 and 3
- Effect: behaviour-changing only when the env var or ctor param selects `plain`/`xml`; `json` is
  byte-identical.
- Change: optional ctor parameter `ToolResultFormat? toolResultFormat = null` on the
  `IAgentToolHost` constructor (`:169`) and passed through from the `McpClient` constructor;
  resolved `toolResultFormat ?? ToolResultFormatOptions.FromEnvironment()`; `ModelFacingToolContent`
  renders the first block through `ToolResultRenderers.For(format)` before appending `TrailingText`;
  both call sites (`:676`, `:888`). `AgentTranscript.ToolResultFormat` (string, header, beside
  `Thinking`); `AgentToolCallRecord.ModelFacingChars` (int?).
- Tests (model on `ToolResultTrailingBlockRunnerTests`: scripted SSE handler, `ToolOutput`):
  `Plain_ToolOutputIsRendered_ResultJsonUnchanged`; `Xml_ToolOutputIsRendered`;
  `Default_ToolOutputIsResultJsonExactly`; `Transcript_RecordsFormatAndModelFacingChars`;
  `SeededRecord_RendersOnResume`. Existing `ToolResultTrailingBlockRunnerTests` unchanged and green.
- Done when: Build 0 errors; full `Tests.Harness` run 0 failures.
- Effort: M

### Step 5 - Arm table script and deferred notes
- Files: new `scripts/Get-ToolResultFormatArmTable.ps1`; `scripts/roslynsentinel-interrogate.ps1`
  (one `# TODO` line at the `ResultJson` replay, `:239`); `docs/current/INBOX.md` (two lines: Q12,
  Q17).
- Depends on: Step 4 (reads `ToolResultFormat`, `ModelFacingChars`)
- Effect: none on code paths.
- Change: modelled on `scripts/Get-ThinkingArmTable.ps1` (job -> run folder mapping, `outcome=` /
  `stopReason=` counts). Arm = (`ToolResultFormat` from the transcript header, falling back to the
  job env, then `json`) x (`Thinking.Mode`). Columns: runs, Pass, stopReason counts, escape-copy
  lines and calls (Q11 rule), argument parse failures (Q20), median `ModelFacingChars`, median wall.
  `-Detail` emits per-call escape-copy rows. Validate the counter on the smoke run named in Q11
  (expect 10 lines over turns 8-13).
- Done when: the script runs on the `thinking-smoke-8e02735a` jobs and reports 10 escape-copy lines
  for run 20261125-192735-136.
- Effort: S

### Step 6 - LIVE A/B batch
- Files: none (output under the scratch root).
- Depends on: Steps 1-5 committed; pinned checkout of that commit.
- Lane: `modeleval-runner` via `scripts/roslynsentinel-evalqueue.ps1`; hosts 112 and 113
  (Qwen3.5-9B-MTP-GGUF already loaded; never load or unload); standing contract only.
- Change: 48 jobs, interleaved (round-robin over arms so each host sees every arm):
  | Arm | Variables |
  | --- | --- |
  | json-off | none |
  | plain-off | `TOOL_RESULT_FORMAT=plain` |
  | xml-off | `TOOL_RESULT_FORMAT=xml` |
  | json-trig | `THINKING=triggered`, `THINKING_TRIGGERS=error` |
  | plain-trig | `TOOL_RESULT_FORMAT=plain`, `THINKING=triggered`, `THINKING_TRIGGERS=error` |
  | xml-trig | `TOOL_RESULT_FORMAT=xml`, `THINKING=triggered`, `THINKING_TRIGGERS=error` |
  Each arm: the four scenarios x 2 rounds. Tag `toolresultformat-ab-<sha8>`.
- Outcome classes: `outcome=` (Pass, PassedAfterCap, Fail classes), `stopReason=`, plus the
  escape-copy and parse-failure counts; verdict per format against the Decision Q6 thresholds,
  pooled over thinking and per thinking level.
- Done when: 48 runs finished (resubmit crashed jobs once), the arm table and the verdict are in this
  plan under the step.
- Effort: L (wall clock)

### Step 7 - Default flip (only on a keep result, after owner review)
- Files: `ToolResultFormatOptions.cs` (default), `docs/generated/configuration.md` (regenerate).
- Depends on: Step 6 verdict and owner review.
- Effect: behaviour-changing (default model-facing format).
- Done when: Build 0 errors; `ConfigurationReferenceFreshnessTests` and Step 1 tests (default
  assert updated) pass.
- Effort: S

## Out of scope

- Server-side rendering of tool results for other MCP clients (later proposal, Q1 option C).
- `SnippetErrorFormatter.DescribeEscapedQuotes` wording (TODO.md, Q10).
- A system-prompt escaping note (Q17), the `</think>` leak (Q7), model families other than Qwen3.5-9B.
- Changing `roslynsentinel-interrogate.ps1` replay (TODO only).

## Grounding (2026-11-25, explore-tier2)

- Single seam: `ModelAgentRunner.ModelFacingToolContent` (`RoslynSentinel.AgentLoop/ModelAgentRunner.cs:1077`),
  called from the live loop (`:676`) and resume seeding (`SeedConversation`, `:888`).
  `LmStudioAgentClient.ToInputItems` (`:855-863`) copies `Content` into `function_call_output`
  unchanged. Only `/v1/responses`; no chat-completions path.
- Server JSON: `SentinelToolFactory.WireSerializerOptions` (`RoslynSentinel.Common/SentinelToolFactory.cs:74-83`),
  compact, camelCase, nulls omitted, `UnsafeRelaxedJsonEscaping` (`"` -> `\"`, newline -> `\n`).
- Runners: production `SubAgentModelRun.cs:210`, `PlanStepRunner/Program.cs:233`; 31 test sites.
- Option pattern: `LoopAssistOptions`. A new descriptor trips `ConfigurationReferenceFreshnessTests`
  until `scripts/Generate-ArchitectureMap.ps1` regenerates `docs/generated/configuration.md`.
- No JSON-to-text renderer exists in the solution. Readers of `ResultJson` stay valid (transcript
  keeps JSON).

## Risks and open decisions

- No open owner questions. Step 7 waits for owner review of Step 6.
- Risk: single-turn probes cannot show multi-turn side effects (paging, nested-JSON arguments,
  token growth); Step 6 measures them.
- Risk: run-to-run variance is large; arms are interleaved in one batch on the same hosts.
