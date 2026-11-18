# Server prevents near-miss member names and, as an eval-only option, auto-corrects unambiguous ones

**Status:** PROPOSED 2026-11-18. Phase B scope settled with the owner (eval feature, no fix ids); plan in
[plan_server_slip_recovery.md](../plans/plan_server_slip_recovery.md).

## Motivation

Weak models sometimes write a near-miss member name, a token-path slip such as `ShippingKinds.Oovernight`
for `ShippingKinds.Overnight` (evidence in
[finding_overnight_token_path_slips.md](../findings/finding_overnight_token_path_slips.md)). Once a model
has slipped it usually cannot retype the name correctly, even when it knows the spelling. Tiel received
the correct name 14 times, said it had made a typo, and still sent `Oovernight` in 16 later edit calls
(`ModelTestingResults/113/Model_RunsShippingKindStepPromptFinal(4)/20261117-214637-592`). Every recovery
path the server offers today ends with the model retyping the name, which is the operation that fails.

What exists:

- **The server already knows the fix.** `CorrectedLineHint.FindClosest` and `RewriteLine`
  (`RoslynSentinel.Engines.Basic/CorrectedLineHint.cs:25`, `:51`) compute the closest name and the
  corrected line for CS0103 (`CompilerErrorLookupHelper.DescribeClosestNameInScopeAsync`,
  `RoslynSentinel.Engines.Basic/CompilerErrorLookupHelper.cs:391-420`) and CS0117
  (`DescribeMissingMemberAsync`, `:514-650`). CS1061 returns early before the closest-member clause (`:528`).
- **The fix is text only.** The hint is part of the compile-rejection message and nothing applies it or
  exposes it as data. The CS0117 text repeats the wrong name and its letter count (`:604`).
- **The rejected edit is not written.** `ValidateAndApplyHelper` validates before writing
  (`RoslynSentinel.Common/ValidateAndApplyHelper.cs:174-186`, `ToolErrorCode.ValidationFailed`).
- **Prevention works where it applies.** A tool result showing the qualified names took tiel from 24/400
  slips to 0/400 (2026-11-18). Today no product tool emits qualified enum members: the outline renders them
  bare (`WorkspaceReadNavigationImpl.ExtractOutlineEntries`, `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs:574`).

### Scope: where assistance stops (owner decision 2026-11-18)

Server-side corrections so far have targeted benign mismatches (whitespace, indentation). A substituted
member name is different: the code can compile and be wrong, and the model then works from a belief about
the file that differs from the file. So:

- **Production answer:** the correct sampler preset makes slips rare (the finding's sampler section), and
  a run that gets stuck on one is cheaper to revert and re-run than to keep adding crutches.
- **Phase A** (prevention and structured data) carries no substitution and is on for everyone.
- **Phase B** (auto-correction and the hint display toggle) is an **evaluation feature**, off by default.
  Its purpose is measurement: runs with it on and off separate the cost of the slip from the cost of the
  task, and the display toggle measures whether repeating the wrong name matters.

The harness side, for clients we own, is
[proposal_harness_slip_rollback_and_resample.md](proposal_harness_slip_rollback_and_resample.md).

## Proposal

All parts are semantic (Roslyn binding of the receiver), so nothing is specific to `ShippingKinds`.

### Phase A, part 1: qualified enum members in read output

Outline results list enum members in qualified form (`ShippingKinds.Overnight`), so a model that reads the
type copies the canonical tokens instead of composing `.O` + rest. Enum members first, where the evidence
is; static members of other types wait for a measured slip.

### Phase A, part 2: structured near-miss detail on the rejection

When a rejection contains CS0117 or CS1061 (later CS0103) and a closest name within the `FindClosest`
limit exists, the rejection's structured detail carries one near-miss item per such diagnostic:

- the diagnostic id, file, line and column;
- the receiver type's display name (none for CS0103);
- the replacement text as the receiver is written plus the closest name (`ShippingKinds.Overnight`) and the
  ranked candidates;
- the corrected line (`CorrectedLineHint.RewriteLine`);
- whether the wrong name is declared as a member anywhere in the solution (then it is more likely a wrong
  receiver than a slip).

The wrong name itself is not in the item: position plus replacement is enough.

Where it goes: every compile-gate site already builds its `structuredDetail[0]` through one method,
`EditImpactGate.BuildDetail` (`RoslynSentinel.Engines.Basic/EditImpactGate.cs:43-55`), which constructs
the single `EditImpactDetail` (`RoslynSentinel.Engines.Basic/EditImpactResult.cs:113`). Its three async
callers (`EditImpactGate.Describer` for the `ValidateAndApplyHelper` tools, `EditImpactGate.RejectionAsync`
for WriteFile, ApplyDiff, ApplyUnifiedDiff and CreateFile, `WorkspaceFileEditImpl.ComposeCompileRejectionAsync`
for ReplaceSnippet) all hold the workspace manager, the proposed changes and the diagnostic report, so the
near-miss list is one new field on `EditImpactDetail`, computed in those three places. Unlike today's
text hint (`DescribeMissingMemberAsync` resolves the type by its simple name as a string), the detector
binds the receiver in the proposed document, so it sees the real type, accessibility and static or
instance context.

`ErrorCode` stays `ValidationFailed`. The item is the signal the harness proposal consumes.

### Phase B, part 3: eval-only auto-correction (server option, off by default)

A server startup argument with an env-var fallback (the `LlmOptions` pattern,
`RoslynSentinel.Common/LlmOptions.cs:37`, `:150`). When on, a rejected edit is corrected and applied instead
only when **all** of these hold:

- every compile error the edit introduces is a near-miss item (an edit with one slip and one real error is
  rejected with hints for both, never half-fixed);
- each item has exactly one candidate within the `FindClosest` limit (`Overnight` and `Overnite` both in
  range is ambiguous);
- the wrong name is declared as a member nowhere in the solution;
- the whole corrected edit validates clean.

Otherwise the edit is rejected with the hint (part 4). Candidates are shown qualified as written in the
code (`ShippingKinds.Overnight`), with the full namespace only when two types share the simple name; the
0/400 result came from the as-written form.

The success result states the corrected line plainly, so the model's next read or snippet has the real text
to copy (the typo stays in its own call arguments; a later `ReplaceSnippet` whose old text contains it is
the expected failure and is counted in the live run). There is no silent mode.

Precedent: the lifted rename already rewrites an edit server-side and reports it as a finding on success
(`WorkspaceFileEditImpl.WithLiftFinding`, `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs:1208`).

Accepted risk, documented: a model that writes a use before declaring the member (it means to add
`Overnite` later) gets a silent change of meaning that compiles. That is the main reason this is eval-only.

### Phase B, part 4: hint display toggle (server option)

One option, `show` or `hide`, governs every place the server repeats the wrong name, so a single switch is
the A/B:

| Place | show | hide |
| --- | --- | --- |
| Rejection hint sentence | names the wrong name, then the qualified candidates and corrected line | position, qualified candidates and corrected line only |
| Raw compiler message (`[COMPILER ERROR]` block and `structuredDetail` diagnostics) | as the compiler wrote it | wrong name redacted |
| Source line quoted under the diagnostic | the line as written | replaced by the corrected line |
| Auto-correction report (part 3) | explicit substitution (`Oovernight` -> `Overnight`) | neutral fact: "line 42 is now `case ShippingKinds.Overnight:`" |

The model's own call arguments always contain the wrong name, so "hide" means the server never repeats it.
The default (`show`) keeps today's behaviour.

### Phase B, part 5: substitution record

- **Server log:** one structured line per substitution (tool, file, line, from, to, diagnostic id,
  candidate count, display mode), and one per ambiguous case that got a hint instead.
- **Result `_meta`:** the same records go in the MCP result's `_meta`, which clients normally do not show to
  the model, so the record is complete in hide mode. The harness copies them into the transcript (harness
  proposal). Clients we do not own ignore them.

## Alternatives considered, not pursued

- **Retry by reference (fix ids).** Dropped by the owner 2026-11-18. The last code-by-reference failed:
  `ApplyDiff(action: confirmationCode)` was disabled on 2026-08-30 (9e998518) because models made up codes
  (41f5ca04, 3f259960); `ToolEnums.cs:118-134` keeps the commented-out member. It would also need a store
  with pre-image checks (`CachePendingChangeset`, `PersistentWorkspaceManager.cs:127`, has none) and a new
  tool.
- **Auto-correction on by default, or as a product feature.** Compiles-but-wrong risk and a model working
  from a false belief about the file; production relies on the sampler preset and revert-and-rerun.
- **Silent auto-correction.** The file then differs from the model's belief with no clue at all.
- **Show all valid values before the first write.** Only works when the model reads the type first; part 1
  covers that case.
- **Logit ban or constrained decoding.** Inference-server specific, needs the intended name in advance,
  and `/v1/responses` ignores `logit_bias` (finding open item 6).
- **A new error code per diagnostic.** One rejection can hold several diagnostics; per-diagnostic
  structured items carry the same information.

## Open questions

- **`_meta` passthrough:** whether the MCP C# SDK and `McpClientToolHost` carry a result's `_meta` to the
  harness. Fallback: a result field the harness strips before the model sees it.
- **Options home:** `LlmOptions` or a new server options class for the two Phase B options.
- **CS1061** goes through an extension-method path first (`:528`); the detector should offer a near-miss
  instance member when no extension candidate exists.

## Cost / risk

- Part 1 changes outline output; it adds a few tokens per enum member.
- Part 2 adds a structured field to rejections; message text is unchanged.
- Parts 3-4 change behaviour only when their options are set. Part 3 must pass through ReplaceSnippet's
  collateral-change check, which validates the model's own edits (`ValidateAndApplyHelper.cs` `expectedEdits`):
  the corrected text needs its own pass, or a near-miss correction counts as an expected edit.
- Runs with substitutions must be reported as assisted, never as plain passes.

## Related

- [proposal_harness_slip_rollback_and_resample.md](proposal_harness_slip_rollback_and_resample.md)
- [finding_overnight_token_path_slips.md](../findings/finding_overnight_token_path_slips.md) - open item 4.
- [plan_error_codes_follow_cause.md](../plans/plan_error_codes_follow_cause.md) - error code work in progress.
- TODO.md "Token-path slip recovery design".
- Memory: project_token_path_slips_eval_rules.
