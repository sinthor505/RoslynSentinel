# Semantic edit-impact diagnostics, a playbook and GetHelp: explain a rejected edit by what it changed

**Status:** PROPOSED 2026-10-19. First slice (symbol diff, attribution, next-step line at the shared gate) approved 2026-11-09 and built, see "First slice". Second slice (rename lifting, edited-file attribution, wire verdict list) approved 2026-11-10, see "Second slice"; the rest is unreviewed. Open questions were investigated 2026-10-19 (answers inline); a few remain.

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

### First slice, approved 2026-11-09: rollout (b) + (c), minimal

Evidence: the 10-chain runs of 2026-11-09 (`evals/eval_edit_snippets_ab_2026_11_09.md`; runs
`20261109-170327-522` and `-173949-849`). qwen3.5-9b never called RenameSymbol and lost its turns pushing renames
through ReplaceSnippet. Haiku used RenameSymbol for both cross-file renames, finished in 18 calls and
never met the gate.

- **Scope:** a symbol diff plus attribution plus one next-step line. It is wired once in
  `ValidateAndApplyHelper`, so every gated tool gains it. Not in this slice: the playbook, GetHelp, repeat
  detection, reason hints and tier 3.
- **Symbol diff:** compare the members declared in each edited document between baseline and candidate,
  keyed by containing type, name, kind and parameter types.
  - Classify each change as removed, added, signature-changed or accessibility-changed.
  - Rename: exactly one removed and one added member in the same type, of the same kind and with equal
    parameter types. Otherwise report "removed X, added Y" and claim no rename.
  - Type renames follow the same rule at type level.
- **Attribution:** link each diagnostic in an unedited file to a diff entry when the diagnostic's
  identifier is the removed or changed member's old name. Unattributed diagnostics stay listed as today.
- **Next step,** naming only tools active in the current mode:

  | Attributed change | Diagnostics | Next step |
  | --- | --- | --- |
  | rename X -> Y | CS1061, CS0103, CS0117, CS0246 | "This edit renames X to Y and N file(s) still use X: use RenameSymbol on X instead." |
  | signature change | CS7036, CS1501, CS1503 | ChangeSignature or MethodSignature, which update callers |
  | accessibility narrowed | CS0122 | ChangeAccessibility |
  | anything else | - | one batch covering every affected file (the current text) |

- **Active-tool source:** nothing at the gate knows the active tool set today. `ToolAllowList` is
  registered only for claude-lean or `--hide-tools`. The slice adds one DI-registered source of the
  registered tool names, readable from the gate.
- **Gaps fixed in the same slice:**
  - The batch and CreateFile messages build their own text.
  - The f0830fc9 `DescribeUntouchedFileErrors` text is the fallback row of the table above.
- **Done when:** a fixture test (OrderPricingCalculator/OrderCheckout, no model) checks three cases:
  - a single-file rename of `CalcDisc` gets the RenameSymbol line when RenameSymbol is registered;
  - the same rename gets the batch line when RenameSymbol is not registered;
  - a correct two-file batch passes with no report.
- **Built:** `30f304ed`, `3f716737`, `4257167d`. Follow-ups from the 2026-11-09/10 runs, also built:
  `01b64868` (rename pairing by unique signature shape, tolerating accessibility changes and unrelated new
  members; CS0266/CS0737 hints) and `705ebc7f` (the RenameSymbol next step carries the real `projectName`
  and `docCommentId`; CS0107 hint).

### Follow-up idea (agreed 2026-11-10, not designed): report removed literals on a successful edit

- **Evidence:** run `20261110-000712-639` (10-chain, qwen3.5-9b-coder). At T2 the model rewrote the whole
  calculator file in one ReplaceSnippet call and dropped the `* 1.1m` preferred-customer scaling. The build
  stayed clean, so the compile gate had nothing to report. The edit was carried through T5, and only the
  harness's own post-run `ContosoOrders.Tests` run caught it (`GetFinalPrice_PreferredCustomer_AppliesScaledDiscount`:
  expected 89.0, actual 90.0).
- **Idea:** the same per-member baseline/candidate comparison as the symbol diff also compares the literal
  tokens (numeric and string) in each surviving member's body. A successful write that removes a literal
  from a member gets one advisory line, for example "removed literal `1.1m` from
  `CalculateDiscountedTotal`". It is not a rejection.
- **Why:** silent behaviour loss in a whole-member or whole-file rewrite is the failure the compile gate
  cannot see. A literal that disappears is a cheap, low-noise proxy for it.
- **Open:**
  - Noise when a literal moves into a new constant or helper (for example step 10's `LocalZone = 1`):
    suppress it when the same literal value appears in a member or constant added by the same edit.
  - Whether it lives in the success response (`findings`, advisory) or only on writes larger than N lines.
  - Cost on large files: only edited members are compared.

### Second slice, approved 2026-11-10: lift renames, attribute edited-file errors, verdict list on the wire

**Evidence.** Batch C of the 10-chain (`evals/eval_tenchain_reasoning_replay_2026_11_10.md`; logs under
`c:\tmp\rs_head113\ModelTestingResults\113\Model_AppliesTenChainedRefactors`, runs 301=`20261110-093510-834`,
303=`-095808-391`, 305=`-102443-078`, 309=`-105423-057`, contrast 302=`-095008-706`). The first-slice
redirect is correct and carries a copyable `RenameSymbol(projectName, docCommentId, newName)` call, yet the
model resends the rejected ReplaceSnippet. Its reasoning names RenameSymbol while the emitted call repeats
the previous tool name, so better text alone will not break the loop.

| Pattern | Runs | What happens | Fix |
| --- | --- | --- | --- |
| A | 305, 303 | Pure class rename via ReplaceSnippet; redirect correct; 305 sends the identical call 9 times (T11-T19), 0 RenameSymbol calls | Lift the rename |
| B | 301 | Rename plus added base interface, sent in parallel with an edit of the caller file; the caller edit gets bare CS0246/CS1061 with no next step because edited-file diagnostics are never attributed (`EditImpactAnalyzer.cs:206-209`) | Lift rename plus the rest; attribute edited-file errors |
| C | 309 | Extract plus rename in one edit is classified "removed CalcDisc, added CalculateBaseDiscount, CalculateDiscountedTotal" (two added members of the same shape defeat unique pairing), so the redirect asks for batchEdits | Better pairing |

As-built facts this slice relies on: the analyzer does no reference lookup and attributes by old-name token
in the diagnostic message; locals and local functions are not diffed; `EditImpactResult` is structured but
reaches the wire only as `ResultError.Message` text (`EditImpactGate.cs:63`). ReplaceSnippet calls
`EditImpactGate.DescribeAsync` itself (`WorkspaceFileEditImpl.cs:~955`, batch `:~1246`), not through
`ValidateAndApplyHelper`.

**Steps, in order, each compile-green:**

0. **Text fix, then re-baseline.** When a next step exists, drop the trailing "Fix the issue(s) below and
   retry:" (`WorkspaceFileEditImpl.cs:962-967`); it contradicts the redirect. Re-run the 10-chain with this
   and `31b206a6` (exact-repeat note, built after batch C) to get a fresh baseline before step 1.
1. **Rename lifting (experimental, server flag `--lift-renames`, off by default).** When the symbol diff
   finds exactly one unambiguous rename and the gate rejects the edit: run `Renamer.RenameSymbolAsync` on
   the baseline for the old symbol; take its output for every unedited document; keep the model's
   candidate text for the edited documents; validate the result through the same gate. Covers a pure rename
   (A) and a rename combined with other edits in the same call (B). On success the response states it
   plainly, e.g. "applied as RenameSymbol: N call sites in M files", with the files changed. If errors
   remain, reject with the remaining verdicts. Refuse to lift (fall back to the first-slice redirect) when
   pairing is ambiguous or the rename resolves to more than one symbol. A/B by restarting with and without
   the flag, never by prompt changes. **Built (uncommitted) 2026-11-10 as `--lift-renames`** (env
   `ROSLYNSENTINEL_LIFT_RENAMES`): `LiftRenamesOptions` (Common), `RenameLifter` (Engines.Basic), shared
   `TryLiftRenameAsync` in `WorkspaceFileEditImpl` used by both ReplaceSnippet paths; fixture tests in
   `ReplaceSnippetRenameLiftTests`. Edited files merge by span: the model's replacement spans (baseline coordinates,
   threaded from both ReplaceSnippet paths) win; renamer changes outside every span are applied on top, so an
   old-name use the snippet did not reach (own constructor, self-call) is renamed too. A file with no span
   information keeps the model's text verbatim.
   **Multi-rename (2026-11-11):** "exactly one rename" was too strict. H1 R5 T3/T4 (run `20261111-092200-526`) sent a whole-file edit
   renaming a class and its method; only the class had callers outside the file, yet the diff held two `Renamed`
   entries and the lift refused (`RenameLifter` guard `renames.Count != 1`). Now every `Renamed` entry is resolved
   to exactly one symbol (any entry that is not refuses the whole lift) and renamed independently on the baseline; the
   changes are unioned per document in baseline coordinates (overlaps dropped), the model's spans still win, and the gate
   validates. The summary lists each rename ("class A -> B, method C -> D") and keeps the rewritten-line quote.
   **H2 follow-ups (2026-11-11, uncommitted):** (a) a refused lift is no longer silent: with the flag on and at least one
   `Renamed` entry, the rejection opens with "Not auto-applied as RenameSymbol: <reason>." (`RenameLifter.PlanAsync`
   returns `(Plan, Refusal)`), and the first-slice redirect names every other attributed rename with one copyable
   `RenameSymbol` call each ("The same edit also renames ..."); (b) `RenameSymbol`'s success data carries `callSitesNote`
   (its success data is an anonymous object with no summary text, so it is a new field): the rewritten lines, up to 5 per
   file, then "These call sites are already updated; do not edit them again." (shared `RenameLifter.DescribeRewrittenLines`);
   (c) the lift's wire verdicts and findings are one per rename (`ToVerdicts`), each with its own rewritten-use count.
2. **Edited-file attribution and locals.** Pass the edited spans to the analyzer. A diagnostic in an
   edited file but outside the edited span is attributed like one in an unedited file. Diff locals and
   local functions inside edited members, keyed by (member, name), so a renamed local whose later uses sit
   outside the snippet gets "renamed local x -> y; N uses outside your edit" instead of a raw CS0103.
   Fixes the bare second call in pattern B and the local-variable case. **Built (uncommitted) 2026-11-11:**
   `EditImpactAnalyzer.AnalyzeAsync(editedSpans:)` converts the baseline-coordinate spans to candidate regions (verified against the candidate
   text, else the whole file counts as edited); `IsOutsideEdit` attributes an error outside every region like one in an unedited file; local and
   local-function renames/removals come from `EditImpactLocalDiff` (kept out of `DiffSymbols` so `RenameLifter` still counts only member renames).
   Pattern B as built: the caller edit's error is INSIDE its own span (it uses a name only the rejected sibling edit declares), so outside-span
   attribution alone does not reach it; `SymbolChangeKind.Unapplied` attributes a missing name that is declared nowhere in the solution but appears in
   a rejected edit of another file (`RejectedEditMemory`) or one still in flight (`InFlightEditMemory`, registered while a ReplaceSnippet call runs), so it
   is order-independent for parallel calls; paths are filtered to the analyzed solution's documents (the memories are process-wide). Parameters, pattern/out/deconstruction,
   catch and foreach variables are diffed too (`EditImpactLocalDiff`, members keyed by parameter types).
   Tests: `EditImpactSpanAttributionTests`.
3. **Verdict list on the wire.** Serialize the changes into the rejection's structured detail: per entry
   kind, old/new name, location, verdict (valid / breaks N uses / lifted), broken uses (capped) and the
   suggested call. Advisory entries on a successful write (the removed-literal idea above) go to `findings`.
   Lets the eval harness assert on the verdict rather than on message text. **Built (uncommitted) 2026-11-11:** reuses the existing
   `ResultError.StructuredDetail` channel (`errorData.structuredDetail`); new item `ImpactVerdict` (Engines.Basic: kind, declarationKind, container,
   oldName, newName, location, verdict `valid`/`breaksUses`/`lifted`/`liftRejected`, brokenUseCount, brokenUses (DiagnosticInfo, capped 5),
   suggestedTool, nextStep; max 20, added declarations left out). Both ReplaceSnippet rejections carry it as one typed item `EditImpactDetail {verdicts, diagnostics}` (the only element of
   `structuredDetail`; verdicts empty when nothing was attributable; the lift attempt comes first). Revised 2026-11-11: this replaced the first-built mixed list. A lifted write adds an `EditImpact` finding `edit-impact verdict=lifted ...`.
   `EditImpactGate.AnalyzeAsync` returns the structured result; `DescribeAsync` delegates. **Wired everywhere 2026-11-11 (uncommitted):** the
   `ValidateAndApplyHelper` tools (`describeEditImpact` callback now returns `(Text, StructuredDetail)`; `EditImpactGate.Describer` fills it) and
   WriteFile/ApplyDiff/ApplyUnifiedDiff/CreateFile (`EditImpactGate.RejectionAsync`, replaces `PrependAsync`) all carry `structuredDetail[0] = EditImpactDetail`,
   built by the shared `EditImpactGate.BuildDetail`; message text unchanged. The detail is present even when no verdict is attributable (empty verdicts,
   diagnostics only), so for the helper tools `structuredDetail` is now an `EditImpactDetail`, not the bare diagnostic list, whenever the edit-impact callback is wired. Tests: `EditImpactGateWiringTests`.
   A created file only adds declarations, so CreateFile never yields a `breaksUses` verdict from the symbol diff.
4. **Pairing for pattern C.** When a removed member ties with two added members of the same shape, pair it
   with the added member that calls the other one (the renamed method calls the extracted helper). Verify
   against run 309 before building; hypothesis.

**Parked: reference impact and rebinding detection.** For removed, renamed or signature-changed members,
look up references on the baseline and check whether each still binds to the same symbol in the candidate
(an edit can compile while calls silently bind to another overload, extension or hidden member). Capped,
changed members only. Sibling of the removed-literal idea. Parked until an eval failure needs it.

**Still rejected: fixes driven by compiler diagnostics.** Roslyn's code fix for a missing name is to
generate the member, which restores the old name the model meant to remove; diagnostics cascade and most
have no unique fix. Intent lifting from the symbol diff is the mechanism instead.

**Done when:** fixture tests (OrderPricingCalculator/OrderCheckout, no model) cover: a pure rename lifted
with the flag on and redirected with it off; a rename plus base-list change lifted; an ambiguous pairing
not lifted; a renamed local with a use outside the snippet attributed; the verdict list present in the
structured detail. Then the 10-chain re-run with qwen3.5-9b, flag on vs off.

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
  (`docs/current/proposals/proposal_tool_error_code_taxonomy.md`, not built) wants to stop; the playbook is keyed by
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
- `docs/current/proposals/proposal_tool_error_code_taxonomy.md` and `docs/current/ideas/closed/idea_server_telemetry.md`
  (shared prerequisite: stable error codes; telemetry counts them)
- Memory: compiler-error lookup hints, caller-fixup tool audit idea, central MCP request filter pipeline.
