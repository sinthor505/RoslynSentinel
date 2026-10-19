# Semantic edit-impact diagnostics, a playbook and GetHelp: explain a rejected edit by what it changed

**Status:** PROPOSED 2026-10-19. Awaiting review; nothing built. Open questions were investigated 2026-10-19 (answers inline); a few remain.

## Motivation

When a mutating tool's compile gate rejects an edit, the model gets raw compiler errors plus a
per-error lookup hint. For a cross-file change that is the wrong abstraction: the model needs to know
which declaration the edit removed or changed, who still uses it, and what to do next.

Evidence (`Model_AppliesThreeChainedRefactors`, `qwen/qwen3.5-9b`, run folders
`20261019-163123-571` and `20261019-164409-795` under `_scratchbuild_112/.../model-eval/`):

- About 9-10 of 14 turns were spent resending single-file `ReplaceSnippet` edits that rename
  `CalcDisc`. Each was rejected with CS1061 at `OrderCheckout.cs:9`. The identical payload was resent
  two or three times in each run.
- The message never says "this edit removed `CalcDisc`; 1 caller outside the edit still uses it". It
  adds an "exists ... isn't accessible" clause (`CompilerErrorLookupHelper.cs:228`, inside
  `DescribeMissingMemberAsync` 171-232) that sends the model toward the wrong theory.
- The same weakness applies to every tool that uses the compile gate, not only `ReplaceSnippet`.
- Teaching the model how to sequence operations (extract before rename; a cross-file rename is one
  batch) currently has no home except tool descriptions, which cost prompt tokens on every turn even
  when never needed. The tool list already grew from 44 to 49 tools since the last passing build.

The plan `plan_replacesnippet_cross_file_edit_guidance.md` fixes the message text for this one case.
This proposal is the general mechanism.

## Proposal

Three tiers, all reading one deterministic playbook, so they cannot drift apart.

### Tier 1 - Inline diagnostic on every rejected edit (deterministic)

A pure engine in `RoslynSentinel.Engines.Basic`, beside `CompilerErrorLookupHelper`, with no MCP types:
`EditImpactAnalyzer.AnalyzeAsync(baseline, candidate, editedFiles, diagnostics, activeTools)` returns an
`EditImpactReport`; a formatter renders it into the error text. Names are provisional.

Stages:
1. **Symbol diff.** Collect declared members (type, name, parameter types, accessibility, return type)
   from the edited files before and after. Classify as removed, added, signature-changed,
   accessibility-changed, or renamed (a removed and an added member with matching signature and similar
   body; if unsure report "removed X, added Y" without claiming a rename). For a batch, diff the whole
   batch at once so a correct batch is not flagged for breaking its own callers.
2. **Impact.** For each removed or signature-changed member, run `SymbolFinder.FindReferencesAsync` on
   the baseline solution and drop references inside edited spans. The rest are callers the edit would
   leave broken, grouped by file with line numbers. Bounded: only removed/changed members, a timeout, a
   result cap.
3. **Attribution.** Link each compiler diagnostic (CS1061, CS0103, CS7036, CS0246 ...) to a diff entry by
   identifier and location. Unattributed diagnostics are listed raw underneath.
4. **Next step.** Pick an action from the findings, restricted to tools active in the current mode
   (check the active tool set, never suggest a gated-off tool).

Report shape (cap about 10 lines, action first):

```
Edit rejected. It renames CalcDisc -> CalculateDiscountedTotal (OrderPricingCalculator.cs:20),
but 1 caller still uses the old name:
  1. OrderCheckout.cs:9  new Calculator().CalcDisc(...)
Next step: resend as ReplaceSnippet(batchEdits: [...]) with an item per file, or use RenameSymbol.
Help: GetHelp(topic: "rename-across-files")
```

Additions with no callers are not reported (not an error; noise for small models) unless they cause an
error such as a duplicate definition. Repeat detection is part of this tier: hash
`(file, oldContent, newContent)` per session and, on an identical resend of a rejected payload, prepend
"identical to your rejected call at turn N; change the approach".

### Tier 2 - The playbook and a GetHelp tool (deterministic)

**Playbook.** One catalog keyed by `topic` and by `errorCode`. Each entry: 1-3 lines of short advice,
the full procedure, the tools it names (so text can be filtered to the active tool set), related entries.
Seed entries come from this eval: CS1061/CS0103 caused by a cross-file rename; the two `batchEdits`
argument errors; RenameSymbol "no longer resolves"; extract-then-rename sequencing.

**Inline use.** Every error carries the short advice and the pointer `GetHelp(errorCode: "...")`. This
costs nothing in tool descriptions, which is the point: detailed instruction is paid for only on error.

**`GetHelp(topic?, errorCode?)`.** `topic` is a small enum (kept short because it ships in the schema),
candidates: `rename-across-files`, `extract-then-rename`, `change-signature`, `move-member`,
`change-accessibility`, `batch-edits`. `errorCode` is a free string matched against both server codes
(`ToolErrorCode`) and compiler ids (CS####). It returns the full entry. It lives in the always-on toolset
and its description stays terse. A weak model may never call it unprompted, so the error pointer and one
line in the eval/system prompts carry the discovery.

A completeness test enumerates the `ToolErrorCode` consts by reflection and fails when a code has no
playbook entry. Each future eval failure analysis adds or sharpens an entry (closes the failure-analysis
loop).

### Tier 3 - Optional LLM advisor (off by default)

Fires only when no deterministic rule matches, or on the second rejection of the same tool. Input: the
tool call, the error, the semantic diff, the matching playbook entries (the playbook is its prompt).
Output is post-filtered to tools in the active set, length-capped, returned in a separate field marked
advisory, and logged verbatim with its input. It may point at a separate LLM from the eval model.
Evaluate with an A/B eval before enabling anywhere. Rationale for deferring rather than rejecting: the
server-side LLM pattern already exists (see Q1 below), latency and contention are acceptable to the owner,
and a separate endpoint removes the contention risk.

### Reason-keyword hints (advisory, lower confidence)

When an edit is rejected, use the call's `reason` as a secondary signal ("rename Foo to Bar" plus a text
edit that broke the build suggests the rename tool; "extract" suggests keeping the old signature in the
first edit). The semantic diff is the primary signal, since a weak model's reason can be vague. Fire only
on rejection. Small fixed intent table (rename, extract, move, change signature, change accessibility,
delete), filtered by the active tool set. Prerequisite: the reason must be available where the error is
built (see Q2).

### Rollout

Migration-first, each slice compile-green: (a) playbook catalog + completeness test + inline short advice
(no analyzer yet); (b) symbol diff + unit tests; (c) impact + attribution, wired once at the shared gate
(Q3) with the fixture regression test (OrderPricingCalculator/OrderCheckout, no model); (d) repeat
detection; (e) `GetHelp`; (f) reason capture + hints; (g) tier 3 advisor. The old per-error hint stays as
a fallback for diagnostics the analyzer cannot attribute.

## Findings from the open-question review (2026-10-19)

Subagent trace; citations are paths under the repo root. Not all lines were re-quoted by hand.

- **Q1 ILlmClient.** `RoslynSentinel.Common/ILlmClient.cs` has one method:
  `Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct = default)`
  (single turn, text only). Implementation `LmStudioClient.cs` (non-streaming `chat/completions`, throws
  on HTTP failure or empty completion). Registered **only in the Advanced flavor**, unconditionally
  (`Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` ~69-78); `Server.Basic` never registers it.
  The only consumer is `Engines.Advanced/CommentingEngine.cs` (:39, :408). Endpoint/model are one global
  `LlmOptions` (static; `--llm-*` args or `ROSLYNSENTINEL_LLM_*` env; default `http://localhost:1234/v1`,
  timeout 30 s); there is no per-feature override. Consequences: a separate advisor LLM needs new
  advisor-specific options and a second (named/keyed) registration; Basic needs a registration or a
  null-object advisor. `LmStudioAgentClient` is the model-eval client, not an `ILlmClient`.
- **Q2 reason.** `ToolCallReason` (`Common/ToolCallReason.cs:13-50`) is required, min 10 chars with a space,
  validated at JSON bind time. It is **not stored or logged**; the only handling is `ToolCallEcho`
  (`Server.Basic/ToolCallEcho.cs`, filter at `ServiceRegistrationExtensionsBasic.cs:1027-1057`), which echoes
  `{toolCallId, name, arguments}` back to the caller without persisting. So the rejection path cannot read
  the reason today; reason hints need it captured in the request-filter pipeline (see memory:
  central MCP request filter pipeline) and made available where the error is built. Not resolved: the exact
  list of tools without `reason` (confirmed present on Git, McpServerStatus, GetLargeResult, LoadSolution,
  ReplaceSnippet; `ExternalFileDrift` and `McpServerControl` unchecked).
- **Q3 chokepoint.** There is one shared gate, not one method: `ValidationEngine`
  (`Common/ValidationEngine.cs`; static `ValidateChangesWithCandidateAsync(Solution baseline, ...)` ~149-263
  returns `ValidationCandidateResult(Report, Candidate)`, so **both the baseline and candidate solutions
  exist when the message is built**), driven by `ValidateAndApplyHelper.cs` (~121), which takes a
  per-tool `describeValidationFailure` callback. Per-tool messages: `WorkspaceFileEditImpl.cs:700` (via
  `DescribeAsync` :702), batch :939-941, CreateFile :1044-1046. Wiring decision: plug the analyzer in at
  `ValidateAndApplyHelper` / the report level once, so all tools gain it; per-tool callbacks keep only
  tool-specific wording.
- **Q4 error codes.** `ToolErrorCode` is a static class of 22 string consts (`Common/SentinelCallToolResult.cs:42`;
  the memory note citing `ToolResult.cs:26` and about 11 codes is stale). `ResultError`
  (`SentinelCallToolResult.cs:335`) = `(ErrorCode, Message, Detail, StructuredDetail, StructuredDetailLargeResult)`
  with **no hint field**: add an additive optional `Hint` (short advice + `GetHelp` pointer) rather than
  overloading `Message`. `ToolErrorMapper` (`Common/ToolException.cs:173`) maps everything unrecognised to
  `Exception` ("failed unexpectedly (TypeName)"), which is the same collapse the taxonomy proposal
  (`docs/current/proposal_tool_error_code_taxonomy.md`, not built) wants to stop; the playbook is keyed by
  these codes, so that proposal and this one share a prerequisite. `CompilerErrorLookupHelper` is keyed by
  raw diagnostic-id strings with no registry (CS0103 :102-169, missing member :171-232, CS0122 :234-283,
  duplicate definition :295-347).
- **Q5 embedded data.** No precedent: no `EmbeddedResource` or `CopyToOutputDirectory` in any project, no
  `GetManifestResourceStream`, no MCP prompts/resources. The playbook would be the first embedded data.
  Open decision between an embedded JSON/Markdown resource (prose-friendly, editable without C# escaping)
  and a static C# catalog (compile-checked, no loader); lean: embedded JSON in `Common` with the
  completeness test, because entries are prose-heavy.
- **Defects found along the way (separate from this proposal):** a malformed `reason` surfaces as
  "Tool call failed unexpectedly (JsonException)" instead of a structured `InvalidArgument`;
  `FindReferences` leaked "failed unexpectedly (InvalidOperationException). Data: FindImplementations ..."
  in the passing run `20260924-073055-415` T4. Both are candidate playbook entries and candidate fixes.

## Alternatives considered, not pursued

- **Message-text fix per tool only** (the sibling plan). Cheap and worth doing first, but it special-cases
  one failure and duplicates per tool. Kept as the interim step.
- **Auto-fix callers inside the gate.** Rejected for now: it changes what the model asked for and hides
  the mistake; reconsider once the diagnostic proves useful.
- **Always push models to `RenameSymbol`.** Not enough alone: the model must learn why the text edit
  failed, and non-rename cross-file changes (signature changes) have no single tool.
- **LLM-first troubleshooting.** Deferred to tier 3, not rejected. The deterministic playbook is needed
  regardless (it is the advisor's prompt), context isolation does not distinguish an LLM from a rules
  table (only the matched entry reaches the caller either way), and an LLM tier is nondeterministic and
  harder to trace to a `file:line`.
- **MCP sampling for the advisor.** Not pursued: client support is uneven and the existing direct
  LM Studio pattern (`ILlmClient`) already works.

## Open questions

- Exact per-mode registration of the two `ReplaceSnippet` declarations and whether the SDK shadows or
  throws on a duplicate name (belongs to Step 1 of the plan).
- Which mutating tools lack `reason` (list not resolved); how to carry the reason from the request filter
  to the error builder (ambient context vs explicit parameter).
- Rename-detection similarity threshold; whether "removed X, added Y" is enough without it.
- Reference-lookup timeout and cap values on large solutions (needs measuring).
- Playbook format: embedded JSON vs static C# (lean stated above); versioning.
- Whether the report should also be structured data in the result so a harness can assert on it.
- `ResultError` JSON serialization options (null omission for the new `Hint` field) not checked.
- Which advisor model and endpoint to use for tier 3; budget for the A/B eval.

## Cost / risk

- Touches the shared gate path of every mutating tool; a wrong attribution could mislead more than it
  helps, so keep unattributed errors visible and the old hint as a fallback.
- Reference lookups add latency only on rejection, not on success.
- Rename detection, the reason-keyword table and the tier 3 advisor are the weakest parts; all are
  advisory text, never a behaviour change, and tier 3 is off by default.
- Output length: weak models degrade on long diagnostics, hence the 10-line cap and the move of detail
  into `GetHelp`.
- `GetHelp` adds one always-loaded tool description; keep the topic enum small.
- Playbook text that names tools must be filtered by the active tool set or it contradicts gating.

## Related

- `docs/current/plans/plan_replacesnippet_cross_file_edit_guidance.md` (interim fixes, prerequisite)
- `docs/current/blockers/blocking_error_replacesnippet_schema_invites_anchor_only_edits.md`
- `docs/current/proposal_tool_error_code_taxonomy.md` and `docs/current/ideas/server_telemetry_idea.md`
  (shared prerequisite: stable error codes; telemetry counts them)
- Memory: compiler-error lookup hints, caller-fixup tool audit idea, central MCP request filter pipeline.
