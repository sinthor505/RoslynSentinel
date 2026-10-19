# Plan: Teach ReplaceSnippet's description, schema and errors the atomic cross-file batch

**Status:** DRAFT 2026-10-19. Five environment fixes from the AppliesThreeChainedRefactors analysis; not started, awaiting approval.

## Problem

`Model_AppliesThreeChainedRefactors` with `qwen/qwen3.5-9b` stalled in two runs (logs:
`_scratchbuild_112/bin/RoslynSentinel.Tests.ModelEval/debug/model-eval/Model_AppliesThreeChainedRefactors/20261019-163123-571`
and `.../20261019-164409-795`; both cancelled before finishing). Both runs follow the same pattern:

- From turn 2 the model sent single-file `ReplaceSnippet` edits that rename `CalcDisc` in
  `OrderPricingCalculator.cs`. The compile gate rejected every one with CS1061 at
  `OrderCheckout.cs:9` (about 9-10 of 14 turns). The error never says the caller is in another file or
  that a `batchEdits` call would fix it, and it adds a misleading clause ("exists on ... but isn't
  accessible the way it was called").
- The model then tried `batchEdits` twice and both calls were malformed: a top-level `filePath` plus
  `batchEdits` (run 1, T13: "supply either filePath/oldContent/newContent or 'batchEdits', not both"),
  and batch items with no `filePath` (run 2, T8: "batchEdits[0]: filePath is required").
- The passing run `20260924-073055-415` and `20260924-072223-143` (under `_scratchbuild_113`) hit the
  same error text and got through by trial and error, so this is a weak spot, not a new regression.

Root-cause trace (subagent, 2026-10-19), with citations re-checked by a second subagent pass the same
day (corrections applied below; a few lines still unquoted, marked "verify"). Related, already recorded:
`docs/current/blockers/blocking_error_replacesnippet_schema_invites_anchor_only_edits.md`.

The larger fix (a semantic edit-impact diagnostic) is a separate proposal:
`docs/current/proposals/proposal_semantic_edit_impact_diagnostics.md`. Step 4 here is the interim
message fix that the proposal later supersedes.

## Decision

Make the batch form discoverable and its misuse self-correcting, in this order: settle which
`ReplaceSnippet` declaration the model sees, fix the description and parameter docs, make the schema
and the two batch errors carry the correct shape, reword the gate error, then tighten the eval prompt.

## Execution rules

- Sequential C# dispatches only (shared server process); one `Build`/`RunTest` at a time.
- Build 0 errors after each step. Compare tests with `docs/known-failing-tests.*.txt`; report only new
  failures.
- No tool names in guidance text unless the tool is active in the mode being described.
- After the last C# step: `Build(fullBuild)`, restart the server, then re-run the eval (the modeleval
  script needs `-Model qwen/qwen3.5-9b`; see `docs/current/reference_model_eval_procedure.md`).
- Messages must stay ASCII and must not leak paths beyond the workspace-relative form already used.

## Steps

### Step 1 - Settle the duplicate ReplaceSnippet declaration
- Files: `RoslynSentinel.Tools.Basic/WorkspaceTools.cs` (facade; `ReplaceSnippet` at :145, Description :153),
  `RoslynSentinel.Tools.Basic/WorkspaceFileEditTools.cs` (split class; attribute :55, Description :63),
  `RoslynSentinel.Server.Basic/ToolClassRegistry.cs`,
  `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs` (DI blocks :262-270 and :275-282).
- Change: determine which declaration is emitted for the eval's active modes (Refactor, Workspace,
  WorkspaceFileContent) and which for each other mode, and whether both can be active in one mode (the
  SDK's behaviour on a duplicate tool name is unverified; `WithSentinelTools` does not dedupe). Remove one
  copy, or make both delegate to one description/parameter set so they cannot drift. The facade's
  `filePath` already says "File to edit. Do not combine with batchEdits; each batch edit carries its own
  filePath."; the split class's `filePath` has no `[Description]`, and `McpToolSchemaPatcher` does not add
  one, so the emitted schema carries the note only when the facade is the registered class.
- Done when: exactly one description source for `ReplaceSnippet`; Build 0 errors; a tool-list test (or
  `McpServerStatus` listing) shows the tool still registered in every mode that had it.

### Step 2 - Rewrite the description and document filePath
- Files: `RoslynSentinel.Tools.Basic/WorkspaceFileEditTools.cs` (attribute :55, Description :63) or
  `WorkspaceTools.cs` :145/:153, whichever survives Step 1. The exact :55-78 span and the `filePath`
  parameter at :61 were not re-quoted; verify.
- Change: fix the garbled text ("for localized batchEdits" should read "edits"). State plainly: a
  rename or signature change that other files depend on is one atomic `batchEdits` call, each item
  carrying its own `filePath`; a single-file edit that breaks another file is rejected. Add a
  `[Description]` to `filePath`: "Single-edit form only. Do not combine with batchEdits; each batch item
  carries its own filePath." Mention the dedicated rename tool only if it is active in that mode.
- Done when: emitted schema (inspect the real `tools/list` output, not the C# signature) shows both
  sentences; Build 0 errors.

### Step 3 - Make the schema and the batch errors carry the shape
- Files: `RoslynSentinel.Common/McpToolSchemaPatcher.cs` (`ApplyReplaceSnippetLimits` :357; local function
  `PatchBatchEditItems` :393, called :384; it adds only `oldContent` to the item `required` array),
  `RoslynSentinel.Common/BatchTypes.cs` (`SnippetEdit` class :561; `FilePath` :564-565, default `""`, doc
  comment "Absolute path of the .cs file this edit applies to."),
  `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (:568 "not both" confirmed; :767 "filePath is
  required" not re-verified).
- Change: mark `filePath` required on batch items in the emitted schema (today only `oldContent` is);
  add a description to `SnippetEdit.FilePath`; append one valid example to each of the two errors, e.g.
  `batchEdits: [{"filePath":"A.cs","oldContent":"...","newContent":"..."},{"filePath":"B.cs",...}]`.
- Done when: unit tests assert the emitted schema `required` list and both error texts; Build 0 errors.
  Open: confirm requiring `filePath` does not break callers that use the single-file form inside batches.

### Step 4 - Reword the compile-gate error for cross-file breakage (interim)
- Files: `RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs` (`DescribeMissingMemberAsync` 171-232;
  the "exists ... isn't accessible" clause is at :228),
  `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs` (:700 gate message, `DescribeAsync` :702). Other
  tools' gate callbacks (batch :939-941, CreateFile :1044-1046) share the helper but are not changed here.
- Change: when a CS1061/CS0103-class error's caller is in a different file from the edit and the member
  was found only in the pre-edit workspace, drop the "exists ... isn't accessible" clause and say: "If
  this edit renamed or changed this member, other files still use the old form. Apply the change and all
  callers in one ReplaceSnippet(batchEdits) call, each item with its own filePath."
- Done when: a deterministic test using the eval fixture (OrderPricingCalculator / OrderCheckout) asserts
  the new text and the absence of the old clause; Build 0 errors. The semantic-impact proposal replaces
  this message once built; keep the change small.

### Step 5 - Separate the eval prompt steps
- Files: `RoslynSentinel.Tests.ModelEval/OrderPricingRefactorAgentTests.cs` (`UserPromptTemplate`).
- Change: the user template (:47) already numbers the three steps and says to scope the build to
  `ContosoOrders.Core`; add "finish and verify step 1 before step 2". The contradictory build wording
  seen in the bad runs ("cheapest build level" at system-prompt lines ~56-57 versus "Perform a full build"
  at ~86) sits in the agent's system prompt, not in this template: locate its source before editing and
  make it one rule. Name the rename tool only because it is active in this test's modes (Refactor,
  Workspace, WorkspaceFileContent). The template already says `Search` (the `SearchSolutionText` wording
  seen in the good runs came from an older build).
- Done when: the test compiles; eval re-run (below).

## Verification

Re-run `Model_AppliesThreeChainedRefactors` with `qwen/qwen3.5-9b` at least 3 times against the new
build. Judge by turns used and whether the first cross-file rename lands via one batch, not by a single
pass. Compare against the passing runs above (7 and 14 turns).

## Follow-on

The proposal's tiers build on this plan: the semantic diff and inline advice, a deterministic playbook
with a `GetHelp(topic?, errorCode?)` tool (detail returned only on error, not carried in tool
descriptions), and an optional server-side LLM advisor. Steps 2-4 here are the interim, hand-written
version of what the playbook later generates.

## Out of scope

- The semantic diff / impact report, `GetHelp`, the playbook, the LLM advisor and the reason-keyword
  hints (see the proposal).
- Two defects found in the review: a malformed `reason` surfaces as "failed unexpectedly (JsonException)"
  rather than a structured `InvalidArgument`, and `FindReferences` leaks an `InvalidOperationException`
  into its result (run `20260924-073055-415` T4). Track separately.
- Changing the compile gate's behaviour (it must keep rejecting non-compiling intermediate states).
- Tool-count reduction (44 to 49 tools since the passing build; hypothesis only).

## Risks and open decisions

- Which `ReplaceSnippet` declaration the model saw in the bad runs is unconfirmed (Step 1 settles it).
- The eval prompt was not diffed against the September version; Step 5 may not be the only prompt factor.
- The 113 baseline DLL was rebuilt on 2026-10-11, so it is not the September binary.
- The turn-12 reasoning loop in run 2 (about 9,000 lines, no tool call) is unexplained; these fixes may
  not prevent it.
