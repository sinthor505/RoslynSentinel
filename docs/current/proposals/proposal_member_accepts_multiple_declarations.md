# Let `Member` accept several member declarations in one call

**Status:** PROPOSED 2026-11-14. Awaiting owner decision on which option (1-4) to build; nothing is implemented.

## Motivation

`Member(addMember)` and `Member(replace)` take exactly one declaration in `newMemberSource`. A source
holding two or more is rejected by `MemberRefactoringEngine.DetectMultipleMemberDeclarations`
(`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:2283`), with this text (replace variant first,
add variant second):

- "newMemberSource contains N member declarations (...), but replace takes exactly one member - the
  single declaration that replaces '<name>'. Recovery: call Member(operation: replace) once per existing
  member ... and call Member(operation: addMember, containerName, insertAfterMemberName) once for each
  member that is new."
- "... but addMember takes exactly one member per call. Recovery: call Member once per member ... make
  each call after the first as Member(operation: addMember, insertAfterMemberName: <previous member's name>)."

The rejection is clean and correct as a guard (see History). The problem is that the dominant observed
shape is not an error in the model's intent: it is "an existing method plus the helper it now calls", or
"two fields that belong together", which is a natural unit of work. The tool surface makes the model split
it into N calls, and in the worst observed run the model did not converge on the split for several turns.

### Evidence (measured 2026-11-14 over `ModelTestingResults/**/agent.log`, 6492 logs)

A count of the phrase `takes exactly one member` gave 21 matching lines in 10 log files. Two caveats found
while verifying (the brief for this doc said "21 rejections / 7 distinct runs"):

- **21 is matching lines, not rejections.** Each rejection is logged twice in the newer logs: once as the
  server's `[Error] ... ToolInvalidArgumentException` line and once as the agent runner's tool result
  (ShippingKind step 3 run `20261114-061601-418`, agent.log lines 105 and 111 are one rejection).
  The three archived copies of the three-chain run `20261019-193544-997` log it once each (older format).
  Counting one rejection per run-turn gives about **8 distinct rejections** (addMember 5, replace 3).
- **6 distinct run ids, not 7.** Several logs are copies of the same run under different host folders:
  `20261019-193544-997` (3 copies), `20261109-170327-522` (2: `113/` and `113/20261110-122941/`),
  `20261113-003916-771` (2: `85/` and `http___192_168_10_85_1234_v1/`). The remaining runs are
  `20261113-002744-378`, `20261112-192512-927` and the ShippingKind run. 7 of the 10 log files are from
  2026-11-09 or later (of the 2027 logs scanned); that part of the brief holds.

Per-run detail (declarations named in the rejection text):

| Run | Operation | Declarations in the rejected call |
| --- | --- | --- |
| `Model_AppliesTenChainedRefactors/20261112-192512-927` (host 113) | addMember, 3 rejections (turns 8, 9, 12; all three rejection texts name the same two methods) | Method `MultiplyAmountAndRate`, Method `DescribeOrder` |
| `Model_AppliesTenChainedRefactors/20261113-003916-771` (host 85) | addMember | Field `LocalZone`, Field `RegionalZone` |
| `Model_AppliesTenChainedRefactors/20261109-170327-522` (host 113) | replace | Method `CalculateDiscountedTotal`, Method `MultiplyAmountAndRate` |
| `Model_AppliesTenChainedRefactors/20261113-002744-378` (host 112) | replace | Method `CalculateDiscountedTotal`, Method `GetDiscountedAmount` |
| `Model_AppliesThreeChainedRefactors/20261019-193544-997` (host 112) | replace | not extracted |
| `Model_CompletesShippingKindStep(3)/20261114-061601-418` (host 113) | addMember with `insertAfterMemberName` (stack shows `InsertMemberAfterAsync`) | Method `IsKnown`, `IsExpedited`, `Normalize` |

Observations:

- Every extracted rejection is 2 or 3 declarations; the replace cases pair a method with a new helper whose
  name is not the target (hypothesis: the first name is the replace target, from the declared names; the
  target's `memberName` argument was not extracted).
- Run `20261112-192512-927` rejected the same two-method payload on turns 8, 9 and 12 (the rejection
  lines are 479, 509, 690 of its agent.log; each is followed by `Turn N: Member FAILED`). The recovery text
  was in the tool result each time. Why the model repeated it was not traced (hypothesis: the recovery
  text asks for a different call shape and the model re-tried the form it had already composed). The
  cost was roughly 5 turns (brief's figure; turns 13 and 14 in that log are also `Member FAILED` but
  their cause was not checked here).
- ShippingKind step 3 (`20261114-061601-418`): turn 2 missing `containerName`, turn 3 three-member
  rejection, turn 4 succeeded: one turn lost.

Re-measure before and after with a count of `Turn \d+: Member FAILED` lines whose neighbouring tool result
contains the phrase, not the raw phrase count (it double counts).

### A second benefit: the compile gate

A multi-declaration call is also the only way to land "caller plus helper" without tripping the compile
gate. Added one at a time, the caller first is rejected with `CS0103` for the helper not yet present
(`docs/current/plans/closed/plan_mutating_and_test_tool_result_noise.md:119`), so the recovery text's
"once per member" has an ordering constraint it does not mention. Whether the models in the runs above
hit this on the second call was not checked.

## History: why the one-member rule exists

Method used: the engine source, the docs it points at, and the commit itself. Confirmed 2026-11-14 with
`Git(log, pickaxe: "DetectMultipleMemberDeclarations")` and `Git(show, b4e6dd4c)`: the guard was added in
`b4e6dd4c` (2026-09-28, "Report multi-member newMemberSource with count, names, and per-call recovery") to
replace a generic "not a valid member declaration" error. Its message says only that the parser
enumerated one declaration; it gives no design reason against several. Later pickaxe hits (`7ac61ade`,
`046deb59`, `00822e3d`) only reworded hints and advice. What the docs and code say:

- The rule is a **parser limit, not a design choice.** Both add and replace parse the input with
  `SyntaxFactory.ParseMemberDeclaration`, which parses only the first declaration. Its source comment
  (`MemberRefactoringEngine.cs:964-975`, `DetectTrailingUnparsedDeclaration`) says the rest is "silently
  discarded" and the only signal is `ContainsDiagnostics` (CS1073).
- Before the guard, this produced **silent partial writes and false success**:
  `docs/current/blockers/closed/blocking_error_member_addmember_silent_partial_write.md` (success reported
  while 3 of 4 fields were dropped) and
  `.../blocking_error_member_addmember_silently_drops_second_declaration.md` (second method dropped, then a
  misleading CS0103 on the retained one). Both carry a stale `OPEN` status line although they sit in
  `closed/`; not touched here.
- `DetectMultipleMemberDeclarations` itself was added to fix a **misleading error**: replace reported
  "not a valid member declaration" for a valid multi-member block, which "sent agents off rewriting a
  perfectly valid member instead of splitting the call" (the method's doc comment, `:2261-2272`). The
  finding doc `docs/current/findings/finding_member_replace_drops_leading_blank_line_and_verify_gap.md:106-115`
  records it as commit `b4e6dd4c3612140e2a1f3faf088db98d6fd9aa92`, wired into replace, add, insert-after and
  insert-before, tests in `MemberSingleDeclarationTests`. Taken from that doc, not independently
  verified against `git log`.
- Two earlier notes treated multi-member as out of scope or acceptable:
  `plan_claude_lean_core_write_tools.md:111` ("Out of scope: A multi-member form of `Member`") and
  `plan_mutating_and_test_tool_result_noise.md:120` ("the rejection is clean; batching is a feature").
  Neither records a reason for keeping one-member-per-call beyond scoping; no doc found argues that
  batching is harmful.

So the constraint exists because the engine could not previously handle more than one declaration safely.
`DetectMultipleMemberDeclarations` already parses all declarations (it wraps the source in a probe class
and counts `probeClass.Members`), so the building block for accepting N declarations exists.

## Current behaviour, verified in source

- Callers of `DetectMultipleMemberDeclarations` (4, all in `MemberRefactoringEngine.cs`):
  `InsertMemberAfterAsync` (:2086), `InsertMemberBeforeAsync` (:2146), `ReplaceMemberAsync` (:2473),
  `AddMemberAsync` (:2612). The three add-side callers pass `"addMember"`; replace passes `"replace"`
  plus the target name.
- `AddMemberAsync` is also called by `InsertMemberAfterAsync`/`InsertMemberBeforeAsync` (enum/non-type
  fallback), `AddFieldAsync` (:2318, :2340), `AddPropertyAsync` (:2396) and the tool layer
  (`RefactoringStructuralImpl.MemberAddToContainerAsync`, `RefactoringStructuralImpl.cs:950`). The typed
  callers build a single declaration, so a multi-member add path must be a new method beside
  `AddMemberAsync`, not a changed contract.
- `MemberReplaceAsync` is the only tool-layer caller of `ReplaceMemberAsync`
  (`RefactoringStructuralImpl.cs:707`).
- Insertion and the binding check are single-member: `RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync`
  (`RoslynSentinel.Common/RoslynFormattingHelper.cs:279`) takes one `newMember` and an index, and
  `AddMemberCallBindingCheck.RequireKeepsBindingsAsync` (`AddMemberCallBindingCheck.cs:73`) checks one
  member (method, property or instance constructor) against one `insertIndex`, building one trial.
- The write is one `ValidateAndApplyAsync` per tool call (`ApplyAddedMemberAsync`,
  `RefactoringStructuralImpl.cs:980`), so a multi-member add that produces one `updatedText` is already one
  compile gate and one ledger entry; no ledger change is needed for options 1 and 2.
- `MemberEditEffectFacts.DescribeAdded` (`MemberEditEffectFacts.cs:87`) already returns "one row per
  declared name" parsed from the source; whether it parses several top-level members was not verified
  (hypothesis: `AddedNames` may parse only the first).
- `Member`'s `newMemberSource` description is `"Exactly one member declaration (signature and body; ...)"`
  (`RefactoringStructuralTools.cs:130`); the per-operation description string on `operation` lists
  required params only.
- `WriteToolAdviceHelper.AdviseForOversizedEdit` (`RoslynSentinel.Common/WriteToolAdviceHelper.cs:166`)
  has a `RejectedContentShape.MultipleMembers` case (:181) that tells the caller `Member(replace)` "takes
  exactly one per call. Make one Member call per member". `RejectedContentClassifier.Classify`
  (`Engines.Basic/RejectedContentClassifier.cs`) supplies the shape. This text becomes wrong if Member
  accepts several, and is the route a `ReplaceSnippet` oversize rejection sends models to.
- Existing tests: `RoslynSentinel.Tests.Tools.Basic/MemberSingleDeclarationTests.cs` has one multi-member
  test for replace (3 members) and one for addMember (2 members, no anchor), plus single-malformed and
  stray-brace tests. The outline shows **no test of the multi-member rejection through
  `insertAfterMemberName` / `insertBeforeMemberName`**, the path the ShippingKind step 3 run actually hit.

## Proposal

Four options. None is decided; the owner picks.

### Option 1 (recommended first): addMember accepts N declarations

- `newMemberSource` may hold N complete member declarations (N >= 1). They are inserted together, in source
  order, at the requested position: end of the container by default, or after/before the anchor
  (`insertAfterMemberName` / `insertBeforeMemberName`) with the whole block kept contiguous and in order.
- One `updatedText`, one `ValidateAndApplyAsync` (one delta compile check, one `changeId`), so
  `UndoLastApply` reverts the whole batch. **Atomic:** a parse or binding failure for any declaration
  writes nothing.
- Result reports every added member (see "Result shape").
- `DetectMultipleMemberDeclarations` stops being an error for `addMember`; it becomes the splitter that
  returns the member list. The guard against silent drops stays: if the probe class parse reports an
  `IncompleteMemberSyntax` or the per-declaration parse has diagnostics, reject as today.
- Engine work stays in `Engines.Basic` with no MCP types: a new `AddMembersAsync` (or equivalent) beside
  `AddMemberAsync`, returning `DocumentEditResult`; the old method is kept for the typed callers.
  The tool layer (`Tools.Basic`) chooses it when the source holds more than one declaration (or always).
- The binding check (`AddMemberCallBindingCheck`) is per member today. Open question 3 covers how to run
  it for N members.

### Option 2 (recommended second, after measuring option 1): replace accepts N

- `replace` accepts N declarations only when **exactly one** declaration matches the target by name **and**
  parameter types. That one replaces the target; the other N-1 are added after it, in source order, as
  in option 1. Anything else (no match, two matches, an overload set, a renamed target) is rejected with
  the existing message, so no ambiguous guess is made.
- Covers the observed `CalculateDiscountedTotal` + new helper replace cases.
- Matching rule per kind: method = name + parameter type list; constructor = type name + parameter
  types; property/field/event = name; indexer = parameter types. How "parameter types" are compared
  (syntactic text vs semantic `ITypeSymbol`) is open (question 4).
- Sequenced after option 1 because replace has more ambiguity (overloads, `contextSnippet` disambiguation,
  `PreferNonInterfaceMember`, enum members, `MergeLeadingTriviaForReplace`) and option 1 gives a measurement
  of how models use the multi-member form first.

### Option 3: keep the rule, improve only the recovery text

Keep both rejections. Rewrite the Recovery text, for example by naming the exact calls the model should
make for this payload (the first declaration's name, the second's anchor) instead of the generic
`<ContainerName>` / `<MemberName>` placeholders, and mention the caller-before-helper compile order.
Cheapest (one engine file plus the two existing tests); does not remove the extra turns, only the
repeat-without-converging case. Note `ToolCallHint.Call` already renders the call shape.

### Option 4: a separate batch operation

A new `MemberAction` value or a separate tool taking `members: [{...}]`. See "Alternatives".

### Cross-cutting design

- **Size cap and oversize advice.** `Member` has no size cap that this work found (the advice text says
  "it has no size limit"; the cap lives in `ReplaceSnippet`, `WorkspaceFileEditImpl.cs:1269`). A
  multi-member add widens the escape route from an oversized `ReplaceSnippet`: the `MultipleMembers` case in
  `WriteToolAdviceHelper.AdviseForOversizedEdit` (:181-183) and the `FileLevel` case (:184-186) must be
  reworded in the same change (one `addMember` call with all the new members, or the per-member `replace`
  advice for existing ones), otherwise a model is told to do N calls that one call could do. Their tests in
  `Tests.Tools.Basic` / `Tests.Server` assert that text and move with it. Whether there should be a total
  length cap on a multi-member `newMemberSource` is open (question 6).
- **Operation ledger / UndoLastApply.** No change for option 1 and 2: the batch is one document write, so one
  `changeId`. A `dryRun` returns the same envelope. If a later design applies N sequential single-member
  writes internally, that would produce N changeIds and break "one undo"; reject that implementation shape.
- **DocumentEditResult.** The record has `Message` (one string), `Warnings` and `AppliedEditIndexes`
  (`DocumentEditResult.cs`), and `Outcome` is `Modified` or `AlreadyInTargetState`. For a batch,
  `Message` lists each added declaration via `DescribeParsedMember` ("// Added Method 'A', Method 'B'.");
  `AppliedEditIndexes` is documented as "batch engines only" and could carry the indexes of declarations
  that changed text, but since the operation is atomic every declaration is applied or none is, so it adds
  nothing; leave null. Partial success is not a possible outcome.
- **Tool result text.** `MemberAddToContainerAsync` builds the description ("Added new member to '<c>' in
  <file>."). It needs a plural/listing form for N members; the statusMessage should name each added member.
  `RecordAddedEffect(MemberEditEffectFacts.DescribeAdded(newMemberSource, ...))` should record all N Added
  effects (check question in "Current behaviour").
- **Schema / description text and token cost.** Only the `newMemberSource` `[Description]` changes, from
  "Exactly one member declaration (...)" to a form that allows several (for example "One or more member
  declarations (signature and body each); for replace, see below"). Net token change is a handful of tokens
  on a description that is already sent on the lean surface (`Member` is in `AgentToolAllowLists.Lean`); no
  new parameter, so no new schema property. The `operation` description string is unchanged. Option 2
  needs one extra clause on `replace`, a second handful of tokens. Option 4 adds a parameter or an
  enum value and costs more. A description widening carries a risk of its own: weak models may then
  paste N declarations where one would do. The observed pairs are 2-3, so that risk is small
  but unmeasured.
- **Tests needed (planner defines, reviewer confirms they cover the changed path).**
  1. addMember with N=2 and N=3, no anchor: all added in order, one `changeId`, one result listing.
  2. addMember N=2 with `insertAfterMemberName` and with `insertBeforeMemberName`: contiguous and in order
     at the anchor (this is the path ShippingKind step 3 hit; today untested for the rejection).
  3. Atomicity: N=2 where the second declaration fails (own parse error; compile error; a binding capture)
     writes nothing; `UndoLastApply` after a success reverts both.
  4. Caller-before-helper in one call passes the compile gate (the case the one-per-call form fails).
  5. The binding check still refuses a batch member that re-points an existing call.
  6. Existing "silent drop" regression coverage kept: a malformed second declaration, a stray `}`, a body
     fragment (`IncompleteMemberSyntax`) are still rejected, not partially written.
  7. Option 2: exactly-one-match replaces and the rest are added after it; zero matches, two matches
     (overloads), renamed target are rejected with the existing message; `contextSnippet` disambiguation
     still selects the target.
  8. The reworded `AdviseForOversizedEdit` text, and a check that the advice never names a gated tool.
  Passing tests of the existing `MemberSingleDeclarationTests` prove only the rejection path and the
  single-member parse; they say nothing about N-member insertion order, atomicity or the binding check,
  so a green run of that class is not evidence for this change.
- **Measurement plan.** Before: count `Turn N: Member FAILED` whose tool result carries the multi-member
  phrase, and the turns between that failure and the next successful `Member` call, per run, for
  `Model_AppliesTenChainedRefactors` and `Model_CompletesShippingKindStep(3)` (baseline above: about 8
  rejections over 6 runs; worst case `20261112-192512-927`, three in a row). After: run the same
  fixtures on the same hosts with the live-run queue (not a direct modeleval call), re-count; success
  criterion is zero multi-member rejections and no regression in pass rate or turn count. Also record
  how often models send N >= 2 declarations after the change, to see whether the widened description
  induces batching where it is not needed.

## Alternatives considered, not pursued

- **Option 3 (recovery text only).** Not pursued as the first step: the cost of the current behaviour is
  turns, not wrong writes, and clearer text still leaves the N-call split. Kept as the fallback if options
  1 and 2 are rejected; also a cheap complement while they are built.
- **Option 4 (separate batch operation or tool).** Loses on surface cost: a new operation or tool adds
  schema and description tokens to the lean surface and a second way to add a member for models to choose
  between. Also needs a `[McpServerToolType]`/registry/DI entry and a fresh session if a new class. The
  existing `ReplaceSnippetBatch` and `proposal_batch_replacesnippet.md` precedent batches *edits*; here the
  unit is a *declaration list in one text*, which `newMemberSource` can carry with no new parameter. A
  batch object form would also let a caller pass a different anchor per member, which is more power than
  the observed cases need.
- **Silently accept N, apply only the first (status quo before the guard).** This is the bug the guard
  fixed (silent partial write). Rejected permanently.
- **Applying N sequential single-member calls inside one tool call.** Simpler to build, but N compile
  gates, N changeIds, and the caller-before-helper ordering problem returns. Rejected.

## Open questions

1. Option 1 or 1+2 together, or option 3 only? Owner decides.
2. Atomic insert and the anchor semantics for `insertAfterMemberName` with N members: block inserted after
   the anchor, in source order (assumed here); should an anchor be allowed to name one of the members
   being added? Assumed no.
3. Binding check for N members: run `RequireKeepsBindingsAsync` per member against one combined trial, or
   extend `CallBindingCheck`'s `AddedMemberIds` (already an array, `AddMemberCallBindingCheck.cs`, `CheckAsync`)
   to all N ids in one trial? The array shape suggests the latter, but `LocateNewMemberAsync` locates a
   single member by insert index; not verified.
4. Option 2 matching: syntactic parameter-type text or semantic comparison? A model may write
   `decimal` vs `System.Decimal`, or `IReadOnlyList<Order>` vs the existing alias.
5. Does `MemberEditEffectFacts.DescribeAdded` / `AddedNames` handle several top-level members? Not
   verified here.
6. Should the multi-member form have a count or length cap (for example N <= 5) to keep failure
   modes and the result text bounded?
7. Do the two closed blockers' stale `OPEN` status lines need correcting? Out of scope for this proposal.
8. History: settled 2026-11-14 (see History). `b4e6dd4c` introduced the guard as an error-message fix;
   no design reason against several declarations was found.

## Cost / risk

- **Touches:** `Engines.Basic/MemberRefactoringEngine.cs` (new multi-member add method; guard changes at
  four call sites; replace match rule for option 2), `RoslynFormattingHelper` (insert N members; today
  single), `AddMemberCallBindingCheck` (N members), `Tools.Basic/RefactoringStructuralImpl.cs`
  (`MemberAddToContainerAsync` description and effect recording, `MemberReplaceAsync` for option 2),
  `Tools.Basic/RefactoringStructuralTools.cs` (one description), `Common/WriteToolAdviceHelper.cs` plus its
  tests, and `MemberSingleDeclarationTests` (the two multi-member rejection tests become acceptance
  tests; the message texts they assert on change).
- **Could break:** the formatting of the inserted block (blank lines between adjacent members; see
  `finding_member_replace_drops_leading_blank_line_and_verify_gap.md`, whose insert-path fix landed in
  `InsertMemberFormattedAsync`), binding-check cost on a larger trial, any caller or eval prompt that relies
  on the exact rejection text (`RefactoringToolHelpers.RequireUpdatedText` has a 1000-character guidance cap
  that was raised so this Recovery text is not cut; `plan_error_codes_follow_cause.md:226`).
- **Makes harder:** the "exactly one declaration" invariant that `DetectTrailingUnparsedDeclaration` and
  the single-member `ParseMemberDeclaration` calls rely on; the silent-drop guard must be kept per
  declaration. A wider schema description lets weak models over-batch.
- **Not a server-registration change:** no new tool class, so no fresh chat session is needed; a server
  restart is, since the engine and tool source change.
- **Layering:** engine logic in `Engines.Basic` with no MCP types; `Common` holds only the advice text;
  `Tools.Basic` holds the tool-boundary description and error mapping.

## Related

- `docs/current/findings/finding_member_replace_drops_leading_blank_line_and_verify_gap.md` (origin of the guard).
- `docs/current/blockers/closed/blocking_error_member_addmember_silent_partial_write.md`,
  `docs/current/blockers/closed/blocking_error_member_addmember_silently_drops_second_declaration.md`
  (why silent acceptance was unsafe).
- `docs/current/plans/closed/plan_claude_lean_core_write_tools.md:111` (multi-member form listed out of scope).
- `docs/current/plans/closed/plan_mutating_and_test_tool_result_noise.md:119-120` (compile-gate ordering; "batching is a feature").
- `docs/current/plans/plan_error_codes_follow_cause.md:226` (Recovery text length cap).
- `docs/current/proposals/closed/proposal_batch_replacesnippet.md` (batching precedent for `ReplaceSnippet`).
- `RoslynSentinel.Tests.Tools.Basic/MemberSingleDeclarationTests.cs` (current tests of the rule).
