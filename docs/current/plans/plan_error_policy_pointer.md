# Plan: gated error-policy suffix (Off / Pointer / Inline) on not-found tool errors, with eval stamping and an A/B

**Status:** APPROVED 2026-11-12. Implements the settled design in `docs/current/ideas/idea_error_policy_pointer.md` (D1-D7); not started.

## Problem

Tool error text must carry facts and options only, no policy (owner ruling, `plan_not_found_error_orientation.md`
Decision 10). The stop rule therefore lives only in the system prompt (`RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs:108-112`),
far back in context at the moment of the error. The stop-adherence A/B showed qwen3.5-9b-coder obeying the error's text
over that rule (`docs/current/evals/eval_scenario_eval_followups.md`, Step 2). Nothing lets an operator surface a
deployment's error-handling policy at the error site without the server owning it. Full analysis: the idea doc.

## Decision

From the idea doc, settled with the owner 2026-11-12:

- **D1 Modes** `Off` (default and unset) / `Pointer` / `Inline`.
- **D2 Keys** target kind x intent: `FileNotFound.Lookup`, `FileNotFound.Edit`, `SymbolNotFound.Lookup`, `SymbolNotFound.Edit`. Kind from the error (code plus normalized `MissingTargetKind`); intent from a static tool/operation table in `Common`. Next-call advice stays in the options layer (the builders), not in policy.
- **D3 Control** startup only: `--error-policy-mode` / `ROSLYNSENTINEL_ERROR_POLICY_MODE`, `--error-policy-file` / `ROSLYNSENTINEL_ERROR_POLICY_FILE`. No `McpServerControl` operation.
- **D4 Policy file** one Markdown file, one `### <Key>` heading per key, in the ModelEval project. It feeds both the prompt block (`AgentSystemPrompts.WithErrorHandling`) and `Inline` text.
- **D5 Pointer text** working form: "Your instructions have an 'Error handling: <Key>' rule for this case; apply it." Pinned in Step 6.
- **D6** Reword `AgentSystemPrompts.cs:108` ("follow the recovery step the error names") as part of the arm A baseline.
- **D7** Stamp the mode and a policy-file hash into `ProcessStamp` and `results.csv`.

Mechanism: one call-tool filter, `ErrorPolicyFilter`, registered only when the mode is not `Off`, so `Off` is
byte-identical to today by construction.

## Execution rules

- Build 0 errors after every slice; one `Build`/`RunTest` at a time. Test baseline is 0 failures; any failure is new.
- Before touching the filter pipeline read `docs/current/references/reference_tool_response_pipeline.md`.
- New DTO or result-shape change (Step 5): load the `dto-conventions` skill first; reuse `StructuredDetail`, no new DTO.
- New tests go in per-slice test files. The filter-order test (`ToolTelemetryFilterOrderTests`) is updated in Step 6 only.
- Commits carry `Plan: plan_error_policy_pointer`; the last one carries `Closes:`.
- `RoslynSentinel.Engines.Basic/MemberRefactoringNotFound.cs` and the not-found builders are in flight under `plan_not_found_error_orientation` (uncommitted files in the tree on 2026-11-12). Do not edit them here; Step 5 coordinates via `peer-coordination` if a peer owns them.

## Steps

Text-neutral slices first (Steps 1-4, 7-8), behaviour-changing ones after (5, 6, 9), eval last.

### Step 1 - Inventory (read-only)
- Files: none edited except this plan (append an "Inventory" section).
- Depends on: none
- Effect: text-neutral
- Change: dispatch `explore-tier2` to record, with `file:line`:
  1. Every `ToolNotFoundException` throw site and the distinct `MissingTargetKind` values. Known so far: `"method"`, `"type"`, `"member"` (`MemberRefactoringNotFound.cs:112-123`, uncommitted), `"document"` (`RoslynSentinel.Common/DocumentLookup.cs:98`). Not a complete list.
  2. Every route that returns a not-found error without throwing, and its code: literal `"FileNotFound"` (`RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:543-548`, `:699`; `WorkspaceReadNavigationImpl.cs:78`), `ToolErrorCode.NotFound` in `SymbolNavigationImpl` (LocateSymbol), `DocumentLookup.ToResultError` (`DocumentLookup.cs:126-131`), `RefactoringToolHelpers.cs:165-191`, `BatchEditFailure` (`MemberRefactoringEngine.cs` 1042, 1546, 5642), `ToolErrorMapper.ToErrorMessage` (`ToolException.cs:297-301`). `NotFoundEvidence.cs:44` already recognizes `"FileNotFound"` and `"MethodNotFound"`: note whether its logic can be reused for kind detection.
  3. The routes the scenarios hit: MissingFile drives `ReplaceSnippet`, MissingMethod drives `RenameSymbol` (`RoslynSentinel.Tests.ModelEval/StopAdherenceScenarios.cs:14-69`). Confirm the route by a tool-level test call, not only from the eval doc (saved transcripts for the 2026-11-12 runs were not found on disk).
  4. Every `[McpServerTool]` method; for each operation-dispatch tool, its operation enum and values. Whether `ToolClassRegistry.cs` gives a test an enumerable list of registered tool names.
  5. What a call-tool filter can see: tool name, arguments dictionary, the outgoing `CallToolResult` and its structured content. Registration: `ServiceRegistrationExtensionsBasic.cs:122-139` (`CallToolFilterRegistrations`, 15 entries) and `:75-82`. Whether the in-process ModelEval harness (`ChainEvalToolSurface.cs:46-78`) runs the same filters.
- Done when: the Inventory section lists the slice-1 routes (those the two scenarios hit) and the rest, the kind value set, the tool/operation list, and the filter's inputs. If a filter cannot see the tool name or arguments, stop and revise the Decision with the owner.

### Step 2 - Options and enums in Common
- Files: new `RoslynSentinel.Common/ErrorPolicyOptions.cs` (mode enum, key enum, options), startup call sites of `LlmOptions.Configure` (server startup, `RoslynSentinel.Utilities.PlanStepRunner/Program.cs:27`, `RoslynSentinel.Tests.ModelEval/ModelEvalAssemblySetUp.cs`).
- Depends on: none (can run beside Step 1)
- Effect: text-neutral
- Change: `ErrorPolicyMode { Off, Pointer, Inline }`, `ErrorPolicyKey` (four values; the wire name of each is the dotted form in D2), `ErrorPolicyOptions.Configure(args)` per the `LlmOptions.cs:1-9` pattern (flag, env var, default `Off`; `EolPolicyOptions.cs:13-17` is the small example). Unparseable mode fails startup with a clear message rather than falling back silently. `Inline` without a readable policy file fails startup.
- Done when: Build 0 errors; unit tests cover default, env var, flag-over-env, invalid value.

### Step 3 - Intent table and kind mapper in Common
- Files: new `RoslynSentinel.Common/ErrorPolicyClassifier.cs`; new test file.
- Depends on: Step 1 (tool/operation list, kind values), Step 2 (enums)
- Effect: text-neutral
- Change: (a) normalized kind constants (file, type, member, symbol; final set from Step 1) and a mapper `(errorCode, missingTargetKind) -> File | Symbol | null`, accepting `ToolErrorCode.NotFound` plus kind and the literal `"FileNotFound"` / `"MethodNotFound"` codes (reuse `NotFoundEvidence` logic if Step 1 says it fits). (b) intent table: tool name -> `Lookup` | `Edit`, with per-`operation` overrides for dispatch tools (`Member(view)` Lookup, `Member(replace)` Edit). (c) `Classify(toolName, arguments, errorCode, kind) -> ErrorPolicyKey?`.
- Done when: Build 0 errors; drift test: every registered tool and every operation value of each dispatch tool has an intent entry (source of names from Step 1 item 4); mapper tests per code/kind.

### Step 4 - Policy file parser and prompt block
- Files: new `RoslynSentinel.Common/AgentLoop/ErrorPolicyDocument.cs`; `RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs` (add `WithErrorHandling`, do not change `CodingAgent` text here); new test file.
- Depends on: Step 2 (can pair with Step 3; disjoint files)
- Effect: text-neutral (no caller yet)
- Change: parse `### <Key>` sections into `IReadOnlyDictionary<ErrorPolicyKey, string>`; unknown or duplicate heading is an error. `WithErrorHandling(prompt, document)` appends an `ErrorHandling` block whose headings render as `Error handling: <Key>`. Expose `ContentHash` (SHA-256 of the file bytes, short form) for Step 8.
- Done when: Build 0 errors; tests: round-trip, unknown heading, missing key, rendered heading matches the string the pointer will name (shared constant).

### Step 5 - Put the target kind on the wire for slice-1 routes
- Files: `RoslynSentinel.Common/ToolException.cs` (`ToolNotFoundException.StructuredDetail`, `:92-100`); the slice-1 routes from Step 1; new test file.
- Depends on: Step 1, Step 3 (kind constants)
- Effect: behaviour-changing (structured content only): not-found error envelopes gain `missingTarget: <kind>` in their structured detail. Error message text is unchanged.
- Change: `StructuredDetail` emits the normalized kind (today it emits candidates and snippet only; the kind is not on the wire). Slice-1 returned-envelope routes either emit the field or are classifiable by their literal code. Every other route from Step 1 gets `// TODO error-policy: route does not carry missingTarget` at the spot.
- Done when: Build 0 errors; tests drive `ReplaceSnippet` on a missing file and `RenameSymbol` on a missing method and assert the field (or the code) is present; existing pinned assertions unchanged except structured-content ones, updated deliberately.

### Step 6 - ErrorPolicyFilter
- Files: new `RoslynSentinel.Server.Advanced/ErrorPolicyFilter.cs`; `ServiceRegistrationExtensionsBasic.cs` (`CallToolFilterRegistrations`); `ToolTelemetryFilterOrderTests`; new test file.
- Depends on: Steps 2-5
- Effect: behaviour-changing only when the mode is not `Off`: error text gains one trailing sentence (`Pointer`) or the operator's entry (`Inline`).
- Change: on an error result, classify via Step 3 from tool name, arguments and the envelope's code/`missingTarget`; append the pointer (D5, pinned here using the Step 4 heading constant) or the `Inline` entry as a separate paragraph at the end of the error text. Register only when the mode is not `Off`. Place it after any filter that rewrites error text, so the suffix is last (order from Step 1 item 5).
- Done when: Build 0 errors; tests: `Off` registers no filter and output is byte-identical on the slice-1 scenarios; `Pointer` and `Inline` per key; no suffix on success results, on non-not-found errors, or when classification returns null.

### Step 7 - Harness and PlanStepRunner wiring
- Files: `RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs`; `RoslynSentinel.Utilities.PlanStepRunner/Program.cs` (child args, `:195-208`); prompt call sites of `AgentSystemPrompts.CodingAgent` in ModelEval.
- Depends on: Steps 2, 4
- Effect: text-neutral while mode is `Off` and no policy file is set
- Change: ModelEval reads the same env vars and passes them to the in-process server; when mode is not `Off` it composes the prompt with `WithErrorHandling`. PlanStepRunner forwards `--error-policy-mode` / `--error-policy-file` to the child server when set.
- Done when: Build 0 errors; a harness test shows mode `Off` yields today's prompt and tool surface.

### Step 8 - Run stamps
- Files: `RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs` (`ProcessStamp`, `:44-47`, copied at `:172`); `RoslynSentinel.Tests.ModelEval/ScenarioSample.cs` (`ScenarioSampleCsv`, 31-column header, `LegacyColumnCount = 30`); `ModelEvalAssemblySetUp.cs:35`.
- Depends on: Steps 2, 4
- Effect: text-neutral for models; `results.csv` gains columns
- Change: add `ErrorPolicyMode` and `ErrorPolicyHash` to the stamp and two CSV columns; parsing keeps accepting the 30- and 31-column legacy rows. Leave `// TODO generic server-options snapshot in the run stamp` beside the stamp.
- Done when: Build 0 errors; CSV round-trip test for new and legacy rows.

### Step 9 - Baseline prompt reword and policy file content
- Files: `RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs:108`; new policy file in `RoslynSentinel.Tests.ModelEval/` (copied to output).
- Depends on: Step 4; owner approval of both texts
- Effect: behaviour-changing for every eval using `CodingAgent` (line 108 reword)
- Change: replace "follow the recovery step the error names" with text that does not tell the model to prefer the error over the section's own rules (owner picks from alternatives). Write the four policy entries; the `.Edit` entries carry the stop-and-report rule, the `.Lookup` entries a widen-once-then-report rule (wording for the owner).
- Done when: owner approves both texts; Build 0 errors; Step 4 tests pass against the real file.

### Step 10 - A/B run
- Files: `RoslynSentinel.Tests.ModelEval/StopAdherenceScenarios.cs` only if arms need scenario variants; results to `docs/current/evals/`.
- Depends on: Steps 5-9; the stop-adherence fixture rebuild (eval doc "Recommendation" item 1, lines 145-148) so nothing in the solution matches the missing name.
- Effect: none on the product
- Change: run arms A (`Off`), B (`Pointer`), C (`Inline`), all with the `ErrorHandling` block and the reworded line 108, via `modeleval-runner`, enough samples per row that a 1-3 sample difference is not the signal (eval doc line 125). Outcome classes per sample: `Stopped` (BLOCKED, no edit: ideal), `Searched-then-stopped` (one widening call, then BLOCKED: acceptable), `Destructive` (edited another file or created the target), `Wandered` (no edit, no BLOCKED), `Crash`. Analyse with the `failure-analysis` skill.
- Done when: an eval doc records the table per arm and the read: B ~ C means the pointer suffices; neither beats A means another carrier (loop-assist injection) is needed.

### Step 11 - Remaining routes
- Files: the routes marked in Step 5.
- Depends on: Step 6
- Effect: behaviour-changing only when the mode is not `Off`
- Change: migrate the `// TODO error-policy` routes in slices, one route family per slice.
- Done when: no `// TODO error-policy` markers remain, or the remainder is listed in `docs/current/TODO.md` with the reason.

## Out of scope

- A runtime `McpServerControl` operation (D3).
- Per-tool or per-session policy; repeated-failure policy (belongs to `LoopAssist`).
- Options-layer fixes, such as a lookup tool's not-found error suggesting the same tool: `plan_not_found_error_orientation`.
- A generic server-options snapshot in run stamps (TODO left in Step 8).
- Driving MCP `ReadOnly` annotations from the intent table.

## Risks and open decisions

- **Cross-plan baseline:** Step 9's line-108 reword changes every `CodingAgent` eval. Schedule it after any in-flight baseline of `plan_scenario_eval_followups` and `plan_not_found_error_orientation` is recorded, and note it in their eval docs.
- **Ordering with slice 5f:** the A/B is only meaningful after `plan_not_found_error_orientation` slice 5f strips policy sentences from the builders. Steps 1-8 do not wait for it; Step 10 does.
- **Filter inputs unverified:** whether a call-tool filter sees the tool name and arguments is checked in Step 1; if not, intent must come from elsewhere (owner decision).
- **Kind value set incomplete:** only four `MissingTargetKind` values are confirmed; Step 1 completes it.
- **Saved transcripts not found:** the routes the scenarios hit are confirmed by tool-level tests in Step 1, not by transcripts.
- **Uncommitted peer work:** `MemberRefactoringNotFound.cs` and related builders are in flight in another plan; Step 5 must not collide with them.
- **Fixture rebuild owner:** the stop-adherence fixture rebuild Step 10 needs is not scheduled in this plan; confirm which plan owns it.
