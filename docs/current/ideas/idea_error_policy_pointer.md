# Idea: tool errors stay policy-agnostic but can point at, or carry, an operator-supplied error-handling policy

**Status:** NOT DESIGNED 2026-11-12. Owner idea after the stop-adherence A/B showed a 9B model obeying the tool error's next-step text over the system prompt's stop rule, and after the owner ruled that tool error text carries no policy.

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

A tension to resolve in the same pass (verified, not part of this idea's mechanism): the same prompt section says
"When a tool returns an error, follow the recovery step the error names" (`AgentSystemPrompts.cs:108`) directly
before the stop rule. With policy-free errors that sentence has less to follow, but it still tells the model to
prefer the error's text over the section's own stop rule whenever the two disagree.

## The actual gap

The environment has two places that can steer a model at an error, and a rule cannot live in either without cost:

- The tool error is the most recent and most specific text, so the model obeys it. But the owner ruled it must not hold policy: it is shared by every MCP client, and a policy that suits one operator (stop and report) is wrong for another (find the target, or create it).
- The system prompt holds the policy, but it is far back in context at the moment of the error. The brief estimates the stop rule sits about 7K characters back in the system prompt; that figure is not measured here.

There is no mechanism that lets an operator say "for this deployment, here is the error-handling policy" and have it
surface at the error site without the server owning the policy.

## Possible fixes (not decided)

The owner's idea has two parts; the assessment below is the main session's, for the owner to settle.

### 1. A neutral pointer, backed by a prompt section

Prompts carry an `ErrorHandling` section with one subitem per error kind (for example `ErrorHandling -> FileNotFound`).
A tool error then appends: "Refer to the ErrorHandling -> FileNotFound policy for guidance." The tool nudges without
holding policy, and the pointer brings the prompt rule back into recency at the moment of the error.

### 2. An operator-only control that gates the pointer

The policy mode is set at runtime by an operator-only control. Tool responses are gated on it and default to agnostic
(no pointer) when nothing is set. The control is never visible to models under test or to subagents.

### Assessment

**Dangling pointer.** An external MCP client has no `ErrorHandling` section in its prompt. A pointer to a section the
client does not have is noise, or worse an instruction to look for something missing. The pointer must therefore
appear only when an operator has declared that the prompt carries the section. Default Off gives exactly today's
agnostic text.

**Modes.**

| Mode | Server appends | Server holds policy? |
| --- | --- | --- |
| Off (default) | nothing | no |
| Pointer | the reference sentence only | no |
| Inline | operator-supplied policy text, verbatim, keyed per error kind | no, it echoes what the operator set |

Inline is the control arm. It tests whether a pointer is enough for a 9B model when the referenced rule sits far back
in the system prompt: if Pointer matches Inline, the pointer suffices; if only Inline works, the rule must be
carried at the error site.

**Keys.** Key the prompt subsections and the policy entries on the existing error identities, so the set of valid
references is enumerable rather than free text. Candidate sources:

- `ToolErrorCode` (`RoslynSentinel.Common/SentinelCallToolResult.cs:75`) and `DocumentEditResult.ErrorCode`; typed throws per `docs/current/plans/plan_error_codes_follow_cause.md` and `docs/current/proposals/proposal_tool_error_code_taxonomy.md` (the numeric scheme there is unbuilt).
- Caveat found while verifying (open): `ToolErrorCode` has one `NotFound` code (line 83), but the owner's example `FileNotFound` is a finer kind than that, and symbol-not-found is another. The key cannot be `ToolErrorCode` alone. Candidates: a new finer code, or `ToolNotFoundException.MissingTargetKind` (carried through `ComposeFailure`, commit 580e2a55). Settle the key before building anything.

A unit test asserts that every key a tool can point at has a matching `ErrorHandling` subsection in the prompt
(`AgentSystemPrompts.CodingAgent` or the composable block, see open question 3), so the two cannot drift.

**Control surface.** Prefer a new operation on the existing operator tool over a new tool class.

- `McpServerControl` is `McpServerControlTool` in `RoslynSentinel.Tools.Basic/AdminTools.cs:476-477`, described as "Operator control", with operations selected by the `McpServerControlOperation` enum (`StopServer`, `LaunchTestInstance`, `StopTestInstance`, `ListTestInstances`). Add `SetErrorPolicy` / `GetErrorPolicy` operations there.
- This avoids a new `McpServerErrorHandlingControl` class, and with it the three-part registration (`[McpServerToolType]` class, `Server.Advanced/ToolClassRegistry.cs` entry, DI block in `ServiceRegistrationExtensions*.cs`) and the fresh-session requirement a new tool class brings.
- The tool's current parameter list has `mode` and `includeTools` strings for test instances; reusing or adding parameters for the policy mode and inline text needs a look at how those are validated, so a policy call cannot be confused with a test-instance call. Open design detail.
- Verify, do not assume, that `McpServerControl` is absent from the eval and subagent tool surfaces: check with `McpServerStatus(toolListing: inactive)` and the eval harness's allow-list (`LlmOptions.MinimalToolSchema` is one such narrowing). I did not verify this; the brief says it is operator-only.
- Also a startup option, following the canonical `LlmOptions.cs` pattern (`RoslynSentinel.Common/LlmOptions.cs:1-9`: `--flag` argument, then `ROSLYNSENTINEL_*` environment variable, then default; configured once at startup). The in-process ModelEval harness and PlanStepRunner then set the mode without an MCP call. `EolPolicyOptions` and `IdleRestartOptions` are existing examples of the same shape.

**Reproducibility.** The active mode, and a hash of any inline text, must be recorded in every eval run stamp and
results row, the way the eval queue already records `ROSLYNSENTINEL_*` variables. Without it the A/B numbers are
unattributable, and a run made with the control set by an operator call mid-session cannot be reproduced.

**Scope.** One global setting per server process is enough: per-session server isolation already gives one process
per window. No per-tool or per-session setting.

**Measurement plan.** Run the stop-adherence family after its fixtures are rebuilt so that nothing in the solution
matches the missing name or description (eval doc "Recommendation" item 1, lines 145-148). Without that the family
cannot separate "ignored the stop rule" from "found the target the prompt misnamed" (eval doc lines 107-115), and any
A/B on it is uninterpretable.

| Arm | Tool error text | Prompt |
| --- | --- | --- |
| A | agnostic (Off) | with the `ErrorHandling` section |
| B | agnostic plus Pointer | with the section |
| C | agnostic plus Inline | with the section |

If B is close to C, the pointer suffices. If neither beats A, the rule needs another carrier (a loop-assist
injection, see `RoslynSentinel.Common/AgentLoop/LoopAssist.cs`, or the prompt tension at `AgentSystemPrompts.cs:108`).
Ten samples per row make differences of 1-3 samples noise (eval doc line 125); size the run accordingly.

**Dependencies and coordination.** Lands after `plan_not_found_error_orientation`'s slice 5f (the policy strip). The
pointer is a separate gated sentence appended at the same spot in the builders, so it does not block that plan and
that plan does not need to know about it. One cost: every builder that gains the sentence gets a second pinned
assertion (Off has none, Pointer has it), so the test count grows with the builder count.

### Alternatives considered (not in the brief; for the owner to weigh)

- **Keep policy in the tool text, tuned until it works.** Already tried in effect: OLD "You MUST call ListSolutionItems next" vs NEW "tell the user so instead of editing a different file" gave no measurable difference, and the owner ruled against policy in tool text. Lost on both counts.
- **Prompt only, no pointer.** This is arm A. It is the baseline the pointer has to beat; it is the status quo the eval already scored as failing for this model.
- **Always-on pointer.** Simpler, no control surface, but dangles for every external client and cannot be A/B tested. Lost on the dangling-pointer point.
- **Per-session policy via the request.** More flexible but needs a protocol field and a way to hide it from models; one global setting per process already fits the isolation model.

## Open questions

Each with a recommendation; none decided.

1. **Mode names.** Recommend `Off` / `Pointer` / `Inline`, and `Off` as the default and the value when unset.
2. **Does Inline belong at all?** It makes the server echo operator text, which edges toward the server carrying policy. Recommend yes, as the measurement control, behind the same gate and documented as an operator-supplied echo; drop it if arm C is never run.
3. **Where does the prompt section live?** Recommend a separate composable block (appended to `AgentSystemPrompts.CodingAgent` by the harness when the mode is Pointer or Inline), so a prompt for external clients and the Off arm carry no section at all and the drift test targets one block. The alternative, a fixed section inside `CodingAgent`, makes the Off arm carry unreferenced policy.
4. **The key** (see Keys above): `ToolErrorCode` alone cannot name `FileNotFound`. No recommendation yet; needs a look at how many error kinds a policy actually needs (likely two or three: file not found, symbol not found, repeated failure).
5. **The `AgentSystemPrompts.cs:108` sentence.** Whether "follow the recovery step the error names" should be reworded once errors carry no steps. Recommend deciding it with arm A, since it affects the baseline.

## Cost and risk

- Touches `Common` (options class, mode enum, the builders' pointer sentence), `Tools.Basic` (`McpServerControl` operations), the eval harness (stamp/results row, prompt composition) and a drift test. No new tool class.
- Risk: a gate that is on by accident leaks an agnostic-breaking sentence to external clients; Off must be the default and tested as byte-identical to today's text.
- Risk: the operator control reachable from a model session would let a model change its own error text; the gating check above is mandatory, not optional.
- Makes harder: every error builder gains a gated suffix, and the A/B record gains a dimension that every future run must stamp.

## Related

- `docs/current/evals/eval_scenario_eval_followups.md` - Step 2 evidence and recommendations 1-2.
- `docs/current/plans/plan_not_found_error_orientation.md` - Decision 10 and slice 5f (the policy strip this builds on).
- `docs/current/references/reference_tool_error_text_facts_options_policy.md` - the facts/options/policy standard the pointer must respect (it is a reference to a rule, not the rule).
- `docs/current/plans/plan_error_codes_follow_cause.md` and `docs/current/proposals/proposal_tool_error_code_taxonomy.md` - the error identities used as keys.
- Memory: Tool error code taxonomy (partial), Startup arg wiring (canonical `LlmOptions.cs`), Model-eval procedure.
