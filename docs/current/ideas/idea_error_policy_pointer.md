# Idea: tool errors stay policy-agnostic but can point at, or carry, an operator-supplied error-handling policy

**Status:** DESIGN SETTLED 2026-11-12, not built. Owner idea after the stop-adherence A/B showed a 9B model obeying the
tool error's next-step text over the system prompt's stop rule, and after the owner ruled that tool error text carries
no policy. Decisions D1-D7 below were settled with the owner on 2026-11-12. Plan:
`docs/current/plans/closed/plan_error_policy_pointer.md` (IMPLEMENTED 2026-11-13).

## What happened

The stop-adherence family (`RoslynSentinel.Tests.ModelEval/StopAdherenceScenarios.cs`) measured qwen3.5-9b-coder on
file-not-found and symbol-not-found. Source: `docs/current/evals/eval_scenario_eval_followups.md`, "Step 2 -
stop-adherence A/B" (table at lines 28-35, causes at lines 116-122).

- The system prompt's stop rule is `AgentSystemPrompts.CodingAgent`, `RoslynSentinel.Common/AgentLoop/AgentSystemPrompts.cs:108-112`: stop when "a named file or symbol does not exist and the error offers no match", and begin the final message with `BLOCKED:`.
- The file-not-found error of the OLD arm said "You MUST call ListSolutionItems(...) next". All nine OLD-arm Destructive MissingFile samples called `ListSolutionItems` at T2, then edited a different file or created the missing one (eval doc lines 59-62). The most recent, most specific instruction won over the prompt rule.
- Rewording the error to "tell the user so instead of editing a different file" (NEW arm) changed nothing measurable: MissingFile 1/10 vs 0/10, Destructive 8 vs 8 (eval doc lines 127-128). Wording tweaks inside the tool text do not move this model.
- Owner ruling 2026-11-12 (`docs/current/plans/plan_not_found_error_orientation.md`, Decision 10, lines 179-189; rules in `docs/current/references/reference_tool_error_text_facts_options_policy.md`): tool error text states facts and options only, no policy. That plan's slice 5f strips the policy sentences from `FileNotFoundMessage` and `SymbolNotFoundDetail`.

Consequence: once the policy sentences are stripped, the stop rule lives only in the system prompt, far from the
moment of the error, and the model has nothing at the error site that points back at it.

## The actual gap

The environment has two places that can steer a model at an error, and a rule cannot live in either without cost:

- The tool error is the most recent and most specific text, so the model obeys it. But it must not hold policy: it is shared by every MCP client, and a policy that suits one operator (stop and report) is wrong for another (find the target, or create it).
- The system prompt holds the policy, but it is far back in context at the moment of the error (estimated about 7K characters back; not measured).

There is no mechanism that lets an operator say "for this deployment, here is the error-handling policy" and have it
surface at the error site without the server owning the policy.

## Two layers: options vs policy

The design rests on keeping these apart.

- **Options** are server-owned and tool-specific: the "next call" suggestions already in the error text. "A failing `Search` should not suggest `Search`" is an options defect, fixed per builder in `plan_not_found_error_orientation`, not here.
- **Policy** is operator-owned and situation-specific: given this situation, stop, look further, or create? The answer depends on the agent's intent (looking around vs acting on a named target), not on which tool it used. A miss from `Search`, `GetFileOutline` or `Member(view)` is the same situation.

Per-tool policy keys would therefore duplicate the options layer, repeat the same text, and grow the prompt block and
the drift test with every tool.

## Design

### Modes (D1)

| Mode | Server appends | Server holds policy? |
| --- | --- | --- |
| `Off` (default, and the value when unset) | nothing | no |
| `Pointer` | one reference sentence naming the policy entry | no |
| `Inline` | the operator-supplied policy entry, verbatim | no, it echoes the operator's file |

`Inline` is the measurement control: if `Pointer` matches `Inline`, the pointer suffices; if only `Inline` works, the
rule must be carried at the error site. Drop `Inline` if arm C is never run.

### Keys (D2)

Four keys from target kind x intent, as a closed enum in `Common` (name to be settled in the plan, e.g. `ErrorPolicyKey`):

| | Lookup | Edit |
| --- | --- | --- |
| File | `FileNotFound.Lookup` | `FileNotFound.Edit` |
| Symbol | `SymbolNotFound.Lookup` | `SymbolNotFound.Edit` |

- **Target kind** comes from the error: `ToolErrorCode.NotFound` (`RoslynSentinel.Common/SentinelCallToolResult.cs:83`; `ToolErrorCode` is a static class of string constants, not an enum) plus a normalized `MissingTargetKind`, and the literal `"FileNotFound"` code some routes still emit. One mapping function in `Common` turns these into a kind.
- `MissingTargetKind` (`RoslynSentinel.Common/ToolException.cs:80`) is a free-form string today with no production reader; the plan normalizes its values (file, type, member, symbol) before the mapper relies on it.
- The kind is surfaced in the existing `StructuredDetail` as a fact (e.g. `missingTarget: file`). It is a fact, so it respects the facts/options/policy standard, and it needs no new DTO (check against the `dto-conventions` skill when planning).
- **Intent** comes from a static table in `Common`: tool name -> `Lookup` or `Edit`, with a per-`operation` override for tools that do both (`Member(view)` is Lookup, `Member(replace)` is Edit). Intent is a property of the call, so the table, not each throw site, owns it. No tool sets the MCP `ReadOnly` annotation today; the table could later drive those annotations (out of scope).
- "Repeated failure" is not a tool error; it belongs to `RoslynSentinel.Common/AgentLoop/LoopAssist.cs`. Out of scope.

### Where the suffix is appended: one call-tool filter

There is no single renderer today:

- Thrown errors: `CallToolFailureHandler.ToFailureResult` -> `ToolErrorMapper.ToResultError` (`RoslynSentinel.Server.Advanced/CallToolFailureHandler.cs` ~50-82; `RoslynSentinel.Common/ToolException.cs:266-279`).
- Returned envelopes: `ToolResultMarshaller.WithNotFoundNotes` (`RoslynSentinel.Common/ToolResultMarshaller.cs:165-181`), registered only when the marshaller mode is not Off (`ServiceRegistrationExtensionsBasic.cs:44-56`).
- Bypass routes that build their own text: `DocumentLookup.ToResultError` (`RoslynSentinel.Common/DocumentLookup.cs:126-131`); literal `"FileNotFound"` codes in `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:543-549` and `WorkspaceReadNavigationImpl.cs:78`; guards in `RefactoringToolHelpers.cs:165-191`; `BatchEditFailure` in `MemberRefactoringEngine.cs` (lines 1042, 1546, 5642); bare strings via `ToolErrorMapper.ToErrorMessage` (`ToolException.cs:297-301`), which drops the code.

So the pointer is not added in the builders. A dedicated `ErrorPolicyFilter` call-tool filter, **registered only when
the mode is not `Off`**, inspects each outgoing error envelope, reads the target kind from `StructuredDetail`, the intent
from the table (tool name and `operation` argument), and appends the pointer or the inline entry. With the filter absent,
`Off` is byte-identical to today by construction. This also removes the earlier cost of a second pinned assertion per
builder: tests are one per key per mode on the filter, plus "this route carries the kind" tests on the routes.

The bypass routes are migrated to carry the kind in slices. Slice 1 covers the routes the stop-adherence scenarios
actually hit (read from their saved transcripts); the rest get `// TODO` markers.

### Control surface: startup only (D3)

- `--error-policy-mode` / `ROSLYNSENTINEL_ERROR_POLICY_MODE` and `--error-policy-file` / `ROSLYNSENTINEL_ERROR_POLICY_FILE`, following the canonical `RoslynSentinel.Common/LlmOptions.cs:1-9` pattern (flag, then env var, then default; configured once at startup). `EolPolicyOptions` is the small example.
- No `McpServerControl` operation in v1. Reasons: `McpServerControl` is not in the ModelEval hidden-tool list (`RoslynSentinel.Tests.ModelEval/ChainEvalToolSurface.cs:22-27`) and is in the claude-lean set (`RoslynSentinel.Server.Advanced/ToolClassRegistry.cs:55, 70`), so a model under test can probably reach it (unverified); a mid-session switch breaks reproducibility; per-session server isolation makes a restart to change mode cheap. Startup-only means no model can change its own error text and nothing needs hiding.
- ModelEval runs the server in-process (`ChainEvalToolSurface.cs:46-78`) and sets the options directly. PlanStepRunner starts a stdio child (`RoslynSentinel.Utilities.PlanStepRunner/Program.cs:195-208`) and passes the flags.
- One global setting per server process; no per-tool or per-session setting.

### One policy file feeds both the prompt and Inline (D4)

- A Markdown file with one `### <Key>` heading per key, kept in the ModelEval project so the server ships no policy of its own.
- A new helper `AgentSystemPrompts.WithErrorHandling(prompt, policy)` renders it as an `ErrorHandling` block appended to `CodingAgent` (no prompt-composition helper exists today). External-client prompts and the `Off` arm carry no block.
- In `Inline` mode the server reads the same file and echoes the entry for the key.
- The `Pointer` and `Inline` arms therefore use the same rule text word for word; the A/B isolates location, not wording.
- Drift test (structural): every key has an entry in the file, and the pointer sentence names the heading exactly as `WithErrorHandling` renders it.

### Pointer wording (D5)

Working text: "Your instructions have an 'Error handling: FileNotFound.Edit' rule for this case; apply it." The exact
string is pinned in the plan and must match the rendered heading.

### The `AgentSystemPrompts.cs:108` sentence (D6)

"When a tool returns an error, follow the recovery step the error names" sits directly before the stop rule and tells the
model to prefer the error's text over the section's own rule. Reword it as part of the arm A baseline, since it affects
every arm.

### Reproducibility: run stamps (D7)

Run stamps record only `CommitHash` and `Dirty` today (`RoslynSentinel.Common/AgentLoop/ModelAgentRunner.cs:44-47`,
copied to the transcript at line 172); no stamp or CSV code records server options. Add `ErrorPolicyMode` and a hash of
the policy file to `ProcessStamp` and to the `results.csv` row (`ScenarioSampleCsv.Append`, called at
`RoslynSentinel.Tests.ModelEval/SavedTranscriptScoring.cs:70`). A generic server-options snapshot would be better; leave
a `// TODO` for it.

## Measurement plan

Run the stop-adherence family after its fixtures are rebuilt so that nothing in the solution matches the missing name or
description (eval doc "Recommendation" item 1, lines 145-148). Without that the family cannot separate "ignored the stop
rule" from "found the target the prompt misnamed" (eval doc lines 107-115).

| Arm | Tool error text | Prompt |
| --- | --- | --- |
| A | `Off` | `CodingAgent` (line 108 reworded) + `ErrorHandling` block |
| B | `Pointer` | same |
| C | `Inline` | same |

If B is close to C, the pointer suffices. If neither beats A, the rule needs another carrier (a loop-assist injection).
Ten samples per row make differences of 1-3 samples noise (eval doc line 125); size the run accordingly.

## Dependencies and coordination

Lands after `plan_not_found_error_orientation`'s slice 5f (the policy strip). The filter is independent of the builders,
so neither plan blocks the other.

## Open items for the plan

1. Enum and option names (`ErrorPolicyKey`, `ErrorPolicyOptions`, mode enum).
2. The normalized `MissingTargetKind` value set and which throw sites set it.
3. Which error routes the MissingFile and MissingSymbol scenarios hit (from saved transcripts); those are slice 1.
4. Options-layer check (belongs to `plan_not_found_error_orientation`): whether any lookup-tool not-found builder suggests calling the same tool again; leave a `// TODO` at the builder if so.

## Alternatives considered

- **Keep policy in the tool text, tuned until it works.** Tried in effect (OLD vs NEW gave no measurable difference) and ruled out by the owner.
- **Prompt only, no pointer.** This is arm A, the baseline.
- **Always-on pointer.** Dangles for every external client and cannot be A/B tested.
- **Pointer appended in each builder.** No single renderer exists, the bypass routes would each need it, and every builder gains a gated assertion. Replaced by the filter.
- **Runtime `McpServerControl` operation.** Probably reachable by models under test and harms reproducibility. Replaced by startup-only.
- **Per-tool policy keys.** Duplicates the options layer; the policy for a miss depends on intent, not tool.
- **Per-session policy via the request.** Needs a protocol field and hiding; one setting per process fits the isolation model.

## Cost and risk

- Touches `Common` (options, key enum, intent table, kind mapper, `WithErrorHandling`, stamp), `Server.Advanced` (filter registration), the bypass routes (carry the kind, in slices), the ModelEval harness (prompt composition, results row) and drift tests. No new tool class.
- Risk: the intent table drifts from the registered tools. Mitigation: a test that every registered tool and every operation of a multi-operation tool has an entry.
- Risk: a route that does not carry the kind silently gets no suffix. Mitigation: slice 1 covers the measured routes; the rest are `// TODO`-marked.
- Makes harder: the A/B record gains a dimension that every future run must stamp.

## Related

- `docs/current/evals/eval_scenario_eval_followups.md` - Step 2 evidence and recommendations 1-2.
- `docs/current/plans/plan_not_found_error_orientation.md` - Decision 10 and slice 5f (the policy strip this builds on); owns the options layer.
- `docs/current/references/reference_tool_error_text_facts_options_policy.md` - the facts/options/policy standard.
- `docs/current/plans/plan_error_codes_follow_cause.md` and `docs/current/proposals/proposal_tool_error_code_taxonomy.md` - error identities.
- `docs/current/references/reference_tool_response_pipeline.md` - read before adding the call-tool filter.
- Memory: Tool error code taxonomy (partial), Startup arg wiring (canonical `LlmOptions.cs`), Model-eval procedure.
