# Plan: Member(addMember) accepts several member declarations in one call (option 1)

**Status:** IN PROGRESS 2026-11-14. Steps 1-10 done (72dabecd, 50facb61, 78116e06, 888f2f3a, 55355155, f5077193, 46152134, 3e28b49e, 7c16f4c3; Step 10 is the baseline, no commit); Step 11 next. Option 1 only (addMember, with or without an insert anchor, takes N declarations atomically); option 2 (replace takes N) is a follow-up after measurement. Batched mode, owner away. Nothing is built.

## Problem

`Member(addMember)` and `Member(replace)` take exactly one declaration in `newMemberSource`; a source with two or more
is rejected by `MemberRefactoringEngine.DetectMultipleMemberDeclarations`
(`RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs:2276`). The observed shape is a natural unit of work
("a method plus the helper it now calls", "two fields that belong together"), not a malformed call. Evidence, motivation,
history of the one-member guard (a parser limit, not a design choice) and the rejected alternatives are in
[proposal_member_accepts_multiple_declarations.md](../proposals/proposal_member_accepts_multiple_declarations.md):
about 8 distinct rejections over 6 distinct runs (addMember 5, replace 3); worst case
`Model_AppliesTenChainedRefactors/20261112-192512-927` rejected the same two-method payload on turns 8, 9 and 12.
Fixing it is an environment change: the tool accepts what the model naturally composes instead of making the model
split it and, as a side effect, discover the caller-before-helper compile order (CS0103) by trial.

Source claims re-verified for this plan (2026-11-14), including two corrections to the proposal:

- Correction 1: the proposal says the multi-member rejection through `insertAfterMemberName` / `insertBeforeMemberName`
  is untested. `MemberSingleDeclarationTests.cs:148` is a `[TestCase]` (test method at `:151`) over `(null, null)`,
  `("Alpha", null)` and `(null, "Beta")`, so the anchor variants of the 2-declaration rejection are covered today. What is
  untested is the 3-declaration anchor shape of ShippingKind step 3 and every N-member success path.
- Correction 2 (proposal open question 5, was a hypothesis): `MemberEditEffectFacts.AddedNames`
  (`MemberEditEffectFacts.cs:109`) parses with `SyntaxFactory.ParseMemberDeclaration(source)`, which sees only the first
  declaration, so `DescribeAdded` (`:87`) would record one Added effect for N members. Needs a fix (Step 3).
- `DetectTrailingUnparsedDeclaration` (`MemberRefactoringEngine.cs:975`) is the silent-drop guard: it fires on
  `ContainsDiagnostics` of the single-member parse. For a valid N-member source the first parse carries CS1073, so
  the new add path must split first and keep this guard only for the non-split (single-member) case.
- Four sites call `DetectMultipleMemberDeclarations`: `InsertMemberAfterAsync` (:2086), `InsertMemberBeforeAsync` (:2146),
  `ReplaceMemberAsync` (:2473), `AddMemberAsync` (:2612). `AddMemberAsync` (:2566) has 32 callers across 5 files
  (engine fallbacks :2077, :2137, typed `AddFieldAsync` (:2308; calls at :2318, :2340), `AddPropertyAsync` (:2392; call
  at :2396), the tool at `RefactoringStructuralImpl.cs:950`, and tests). Mostly tests: 13 in `AddMemberCallBindingTests`,
  12 in `MemberRefactoringTests`.
- `RoslynFormattingHelper.InsertMemberDeclarationAndFormatInsertionAsync` (`RoslynFormattingHelper.cs:279`) has 3 engine
  callers and 3 direct byte-exact characterization tests (`InsertMemberFormatCharacterizationTests`). It takes one member.
- `AddMemberCallBindingCheck.RequireKeepsBindingsAsync` has 3 callers (all in `MemberRefactoringEngine`);
  `AddMemberCallBindingCheck.CheckAsync` has one test caller (`AddMemberCallBindingTests.cs:221`).
- `MemberAddToContainerAsync` (`RefactoringStructuralImpl.cs:931`) chooses `AddMemberAsync` / `InsertMemberAfterAsync` /
  `InsertMemberBeforeAsync`, then one `ApplyAddedMemberAsync` (`:980`) = one `ValidateAndApplyAsync` = one compile
  gate and one changeId. No ledger change is needed.
- Anchors are resolved by `ResolveInsertAnchorIndex` (`MemberRefactoringEngine.cs:2204`) against the container's
  EXISTING direct members, before any insertion; its not-found text is built by `BuildInsertAnchorNotFoundMessage`
  (method at `:2173`, call at `:2219`).

## Decision

Decided by the owner on 2026-11-14 (the owner is away for the run; nothing is pending except the items left under
"Open decisions"):

1. **Option 1 only**, as in the proposal's ACCEPTED status: addMember accepts N >= 1 complete member declarations in
   `newMemberSource`, appended at the end of the container, or (with `insertAfterMemberName` / `insertBeforeMemberName`)
   inserted as one contiguous block, in source order, after / before the anchor. One `updatedText`, one
   `ValidateAndApplyAsync`, one changeId; a parse, binding or compile failure for any declaration writes nothing (atomic).
   `replace` keeps rejecting a multi-declaration source exactly as today (option 2 is out of scope).
2. **Sequencing of the switch (red window).** Step 8 only adds the `AddMembersAsync` body; `AddMemberAsync` keeps its old body, so
   Steps 1-8 are text-neutral and `AddMembersAsync` is tested directly (Step 9) because no tool reaches it yet. Step 11 switches
   `AddMemberAsync` to delegate to `AddMembersAsync` AND removes the insert-after / insert-before / add rejection together. The
   red window of `MemberSingleDeclarationTests.AddMember_WithTwoMembers_*` is Step 11 to Step 19.
3. **Typed callers keep a single-declaration guard.** `AddFieldAsync` and `AddPropertyAsync` reject a multi-declaration source
   with the same error text as today (the add branch of `DetectMultipleMemberDeclarations` stays for them). The guard lands in
   Step 8, before the delegate switch, so it is compile-green and the typed callers never accept N at any point; its test is in
   Step 9.
4. **Same-commit baseline.** A new step (Step 10) queues the baseline model-eval jobs on the pre-change behaviour, and Step 11 (the
   first behaviour-changing step) is held until the last baseline job has started. Steps 11 and 20 name Step 10 in Depends on.
   Historical run folders from the proposal are context, not the baseline.
5. **Proposal open question 2 (an anchor may not name an added member): reject, and add a one-clause hint** to the anchor
   not-found message (Step 12). Source fact: `ResolveInsertAnchorIndex` resolves against the existing members, so such an anchor is
   already "not found" and needs no special case for correctness; the hint saves the model a turn.
6. **Proposal open question 6 (count cap): no cap.** Failure modes are bounded (a bad declaration rejects the whole batch, the
   status text grows linearly with N, observed N is 2 and 3). Revisit with the N distribution Step 20 records. A total-length cap
   is also left out: `Member` has no size cap today (the cap lives in `ReplaceSnippet`).
7. **Proposal open question 3 (binding check): one combined trial for all N members** (reasoning in Step 5).
8. **AddMemberAsync becomes a delegate to AddMembersAsync** (Step 11), so the 32 existing callers (13 binding tests, the typed
   callers) exercise the shared N = 1 body and there is no 60-line duplicate. Single-member behaviour stays byte-identical,
   including the two existing null-parse texts ("Failed to parse new member." for the add path and "Cannot edit: invalid member
   source." for the insert methods; `ParseNewMemberDeclarations` takes the text as a parameter).
9. New engine and tool logic stays layered: `Engines.Basic` has no MCP types, `Common` holds formatting and advice text,
   `Tools.Basic` holds the description and result text.

## Execution rules

- Before EACH dispatch re-run `Git(operation: status)`. Other sessions had uncommitted edits while this plan was written
  (`BatteryTwentyFiveTests.cs`, `scripts/roslynsentinel-modeleval.ps1`, and files of the reasoning-replay work); none is in this
  plan's areas, but a peer may touch `MemberRefactoringEngine.cs`, `RefactoringStructural*.cs` or `WriteToolAdviceHelper.cs`
  at any time, and the line numbers in this plan (verified 2026-11-14) will move. Re-anchor by member name, not by line.
- All C# edits go through the RoslynSentinel edit tools; Markdown through Edit/Write. One `Build` or `RunTest` at a
  time (shared `bin/obj`). Two implementing agents at most, only on the pairs marked parallel-ready; the dispatch-implementer
  rule prefers different projects for concurrent dispatches, so same-project pairs are marked weak.
- Limits used for sizing (`.claude/implementer-tiers.json`, current depth tier 1): `implementer` = at most 3 files, 15 edits,
  1 acceptance, 35 tool calls. The implementer's one acceptance per step is `Build` 0 errors. Tests are run by the
  dispatcher, never by an implementer; the "Dispatcher runs" line on a step is the check that covers it.
- Expected red window: from Step 11 until Step 19,
  `MemberSingleDeclarationTests.AddMember_WithTwoMembers_ReportsCountNamesAndOneCallPerMemberAsync` fails, because it asserts the
  rejection that Step 11 removes. Do not run `RoslynSentinel.Tests.Tools.Basic` before Step 19 and do not count that failure as
  new. Likewise `WriteToolAdviceHelperTests` is red only inside Step 14 and is repaired by the same step.
- Text-neutral steps come first (1-9); Step 10 is a hold-and-measure step; behaviour-changing steps are 11, 12, 13, 14 and the tests
  that move with them.
- A server restart is needed before any live check (Step 20): `Build(level: fullBuild)`, then
  `McpServerControl(StopServer)` and `LoadSolution`. No new `[McpServerToolType]` class, so no fresh chat session.
- Stub searches (Steps 1 and 21) are scoped by `fileGlob` to the three skeleton files: another plan in the tree uses the same
  `TODO implement` stub marker in other files.
- Commit trailers: `Plan: plan_member_accepts_multiple_declarations` on each slice; the last slice carries
  `Closes: plan_member_accepts_multiple_declarations`. That does not close the proposal (option 2 is still open).
- Leave `// TODO` markers as named in the steps; do not put plan or doc IDs in code comments.

## Steps

### Step 1 - Contract skeleton (one compile-green write per file)
- Done: 72dabecd
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Common/RoslynFormattingHelper.cs`,
  `RoslynSentinel.Engines.Basic/AddMemberCallBindingCheck.cs`
- Depends on: none
- Effect: text-neutral (new members, no callers)
- Lane: `implementer` (3 files, about 7 edits). Tool: one `batchEdits` call per file (see
  `docs/current/references/reference_edit_tool_gotchas.md`), definitions in the order below.
- Change: add these members, each body `throw new NotImplementedException(); // TODO implement (Step n)`:
  - `MemberRefactoringEngine` (new, Step 8): `public Task<DocumentEditResult> AddMembersAsync(FilePathWrapper filePath, string containerName, string newMemberSource, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)` (same parameter order as `AddMemberAsync`).
  - `MemberRefactoringEngine` (new, Step 2): `internal static IReadOnlyList<MemberDeclarationSyntax>? TrySplitMemberDeclarations(string source)` (non-throwing; null unless the source is a clean list of 2+ declarations).
  - `MemberRefactoringEngine` (new, Step 2): `internal static IReadOnlyList<MemberDeclarationSyntax> ParseNewMemberDeclarations(string newMemberSource, string unparsableMessage = "Failed to parse new member.")` (1+ members or throws `ToolInvalidArgumentException`; the parameter is the text thrown when the single-member parse returns null, so each caller keeps its existing text).
  - `MemberRefactoringEngine` (new, Step 2): `internal static string DescribeAddedMembers(IReadOnlyList<MemberDeclarationSyntax> members)`.
  - `RoslynFormattingHelper` (new, Step 4): `public static Task<string> InsertMemberDeclarationsAndFormatInsertionAsync(Document document, SyntaxNode root, TypeDeclarationSyntax container, int insertIndex, IReadOnlyList<MemberDeclarationSyntax> newMembers, string eol, CancellationToken cancellationToken = default)`.
  - `AddMemberCallBindingCheck` (new, Step 5): `public static Task RequireKeepsBindingsAsync(Solution solution, Document document, TypeDeclarationSyntax container, IReadOnlyList<MemberDeclarationSyntax> newMembers, int insertIndex, string updatedText, CancellationToken cancellationToken = default)` and `public static Task<CallBindingCheckResult> CheckAsync(Solution original, DocumentId documentId, TypeDeclarationSyntax container, IReadOnlyList<MemberDeclarationSyntax> newMembers, int insertIndex, string updatedText, CancellationToken cancellationToken = default)`. Overloads of the existing single-member methods; a `MemberDeclarationSyntax` argument never binds to the list overload.
  Existing signatures are unchanged. Do not touch any caller.
- Done when: `Build` 0 errors for `RoslynSentinel.Common` and `RoslynSentinel.Engines.Basic`, and `Search(mode: text, query: "TODO implement", fileGlob: "**/{MemberRefactoringEngine,RoslynFormattingHelper,AddMemberCallBindingCheck}.cs")` lists exactly these 7 stubs (the three files held none before).

### Step 2 - Splitter, parse and describe bodies
- Done: 50facb61 (adds a fallback not named below: probe has diagnostics but no single member does, e.g. a missing closing `}` -> throws `newMemberSource holds N member declarations but does not parse as a list: ...`; Step 6 must test it)
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`
- Depends on: 1
- Effect: text-neutral (no callers until Steps 3, 8, 11)
- Lane: `implementer` (1 file, about 4 edits). Tool: `ReplaceSnippet` on each stub body.
- Change:
  - `TrySplitMemberDeclarations`: whitespace -> null; parse `class __SentinelMemberCountProbe__ { <source> }` as `DetectMultipleMemberDeclarations` does (`:2276`); return null unless the probe is one class with 2 or more members, no member is `IncompleteMemberSyntax`, and the probe has no diagnostics at all (`probe.ContainsDiagnostics` is false: a stray `}`, a missing `}`, or a malformed declaration all produce diagnostics). Normalize each returned member: remove the leading run of `EndOfLineTrivia` / `WhitespaceTrivia` from its leading trivia (keep comments, doc comments and anything after the first non-blank trivia). Reason: in a probe parse the blank lines the caller put between declarations are the NEXT member's leading trivia, and `InsertMemberDeclarationsAndFormatInsertionAsync` (Step 4) adds its own separator, so unnormalized members would produce two blank lines.
  - `ParseNewMemberDeclarations(source, unparsableMessage)`: (1) `TrySplitMemberDeclarations(source)` non-null -> return it. (2) Else, if the probe holds 2 or more members and NONE is `IncompleteMemberSyntax` (every declaration is member-shaped, but the list does not parse cleanly): if `DetectUnbalancedClosingBrace(source)` (private static, same class, `:863`) reports, throw its text; else throw `ToolInvalidArgumentException` naming the first declaration that has diagnostics, for example `newMemberSource holds 3 member declarations; declaration 2 (Method 'B') does not parse: <first diagnostic message>. The declarations are applied together or not at all.` (facts and options only, per the CLAUDE tool-text rule). (3) Else the existing single-member path unchanged: `SyntaxFactory.ParseMemberDeclaration(source)` null -> `ToolInvalidArgumentException(unparsableMessage)`; then `DetectTrailingUnparsedDeclaration(source, parsed)` non-null -> throw `newMemberSource is invalid: <issue>`; return `[parsed]`. A source of valid members plus a non-declaration fragment (for example a method followed by `return 1;`) has an `IncompleteMemberSyntax` in the probe, so it takes path (3) and is rejected with the existing `DetectTrailingUnparsedDeclaration` message (whichever of its two forms applies), NOT with the per-declaration text of (2).
  - `DescribeAddedMembers`: one member -> `DescribeParsedMember(m)` (`:2242`, unchanged output such as `Method 'A'`); N >= 2 -> `$"{N} members: {string.Join(", ", members.Select(DescribeParsedMember))}"`.
  `DetectMultipleMemberDeclarations` and `DetectTrailingUnparsedDeclaration` are not changed in this step.
- Done when: `Build` 0 errors. Dispatcher runs after Step 6.
- Parallel-ready: with Step 4 (different projects, disjoint files).

### Step 3 - DescribeAdded sees every declaration
- Done: 78116e06
- Files: `RoslynSentinel.Engines.Basic/MemberEditEffectFacts.cs`
- Depends on: 2
- Effect: text-neutral for one member (the existing `EditEffectEmitterTests.DescribeAdded_ParsesEveryDeclaredName_AndEnumMembers` input is unchanged); for N members the effect list gains the missing rows. No caller sends N until Step 13.
- Lane: `implementer` (1 file, about 2 edits).
- Change: in `AddedNames` (`:109`), non-enum branch: `members = MemberRefactoringEngine.TrySplitMemberDeclarations(source)` or, when null, `[SyntaxFactory.ParseMemberDeclaration(source)]` (a null element yields no names); apply the existing `switch` (type, delegate, method, constructor, property, event, indexer `this`, field declarators) to each member via a local function and concatenate. The enum branch is unchanged. `MemberAddTopLevelTypeAsync` (`RefactoringStructuralImpl.cs:867`) and `MemberAddEnumMemberAsync` (`:926`) also call `DescribeAdded`; they pass one type or an enum list and are unaffected.
- Done when: `Build` 0 errors. Dispatcher runs after Step 6: `EditEffectEmitterTests.DescribeAdded_ParsesEveryDeclaredName_AndEnumMembers` (unchanged, must stay green) and the new Step 6 cases.
- Parallel-ready: with Step 5.

### Step 4 - N-member insertion and formatting
- Done: 888f2f3a
- Files: `RoslynSentinel.Common/RoslynFormattingHelper.cs`
- Depends on: 1
- Effect: text-neutral. The single-member method becomes a one-line delegate to the new one, which must produce byte-identical output for N = 1.
- Lane: `implementer` (1 file, about 3 edits). Tool: `ReplaceSnippet` on the new method's stub body, then on the old method's body.
- Change: implement `InsertMemberDeclarationsAndFormatInsertionAsync` as the generalisation of the body at `:279-334`: `precedingMember` = `members[insertIndex - 1]`, `followingMember` = `members[insertIndex]` when present; indent from the preceding (else following) sibling, default four spaces; member i gets leading trivia `[EndOfLine(eol) when precedingMember != null or i > 0] + Whitespace(indent) + own leading trivia`, trailing trivia one `EndOfLine(eol)`, and `Formatter.Annotation`; `members.InsertRange(insertIndex, annotated)`; the following sibling's missing-blank-line fix then targets `newMembersList[insertIndex + N]` instead of `[insertIndex + 1]`; the final `FormatAnnotatedSpansAsync` call is unchanged. Then replace the body of `InsertMemberDeclarationAndFormatInsertionAsync` with a call to the new method with `[newMember]` (keep its signature and XML summary; the three engine callers and `InsertMemberFormatCharacterizationTests` still compile).
- Done when: `Build` 0 errors. Dispatcher runs: `InsertMemberFormatCharacterizationTests` (3 byte-exact tests; they prove the delegation is equivalent for N = 1 and say nothing about N >= 2), `MemberRefactoringTests.AddMember_AppendsWithBlankLineAndDoesNotReformatSiblings`, `MemberInsertAfterEolTests`, then Step 7 for N >= 2.
- Parallel-ready: with Step 2 and with Step 5.

### Step 5 - Binding check for N members (one combined trial)
- Done: 55355155
- Files: `RoslynSentinel.Engines.Basic/AddMemberCallBindingCheck.cs`
- Depends on: 1
- Effect: text-neutral. The single-member `RequireKeepsBindingsAsync` and `CheckAsync` become delegates to the list overloads with `[newMember]`; N = 1 behaviour and refusal text are unchanged.
- Lane: `implementer` (1 file, about 6 edits).
- Change (from source): `CallBindingExpectations.AddedMemberIds` is already an `IReadOnlyCollection<string>` (`CallBindingCheck.cs:94`) and `CompareCore` turns it into a `HashSet` (`CallBindingCheck.cs:426`); occurrences inside any listed member are exempt from the count rule. Only `LocateNewMemberAsync` assumes one member. So extend the check, not loop it: per-member trials would each lack the other new members, which breaks the caller-before-helper batch (the caller fails to compile in its own trial and the check is skipped, or a spurious refusal is raised).
  - `RequireKeepsBindingsAsync(list)`: checked names = distinct non-null `CheckedNameOf(m)` for which `NameAppearsAsync` is true (per name, as today); none -> return. Otherwise one `CheckAsync(list)`.
  - `CheckAsync(list)`: `CallBindingScope` over all checked names; one capture of the original; one trial; `LocateNewMembersAsync` (rename of `LocateNewMemberAsync`, list form): container match with `Members.Count == container.Members.Count + N`, the members at `insertIndex .. insertIndex + N - 1` match the input list by kind and `CheckedNameOf`, in order; if any added member has an error diagnostic of its own return `Ok(0)` (the write's compile gate reports it, as today); collect ids with `GetDeclaredSymbol(..).OriginalDefinition.GetDocumentationCommentId()` for EVERY added member, one per declarator for a field or event field; a member whose id cannot be read -> `Failed(...)` (fail closed). Apply `WithoutAddedCandidate` once per id. `AddedMemberIds` = all N ids (so a new caller's calls to a new helper are exempt).
  - Refusal text: for N = 1 identical to today. For N >= 2 use `add members <Kind 'name', ...> to '<container>'` and the same two fix clauses in plural ("rename the new member or members, or cast ...").
- Done when: `Build` 0 errors. Dispatcher runs: `AddMemberCallBindingTests` (13 engine-level tests; until Step 11 they reach the single-member method, now a delegate to the list overload, through the old bodies of `AddMemberAsync`, `InsertMemberAfterAsync` and `InsertMemberBeforeAsync`, so they cover the N = 1 delegation), then Step 17.
- Parallel-ready: with Steps 2, 3 and 4 (pairs only).

### Step 6 - Tests: splitter, parse errors, DescribeAdded (proposal test 6 and the Q5 check)
- Done: f5077193
- Files: `RoslynSentinel.Tests.Tools.Basic/MemberSplitDeclarationsTests.cs` (new; `InternalsVisibleTo` for `Engines.Basic` is granted to this project only)
- Depends on: 2, 3
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change: NUnit 5; pure unit tests, no workspace.
  - `TrySplitMemberDeclarations`: two methods; field plus method; method plus helper; three declarations; blank lines 0, 1 and 3 between declarations and a CRLF source all yield members whose leading trivia starts with no end-of-line; a doc comment on the second member is kept; returns null for: one declaration, whitespace, a stray `}` after the last member, a missing `}`, a malformed second declaration, a valid declaration followed by a body fragment such as `return 1;` (`IncompleteMemberSyntax`).
  - `ParseNewMemberDeclarations`: one valid member returns one element; one malformed member throws the unchanged `newMemberSource is invalid: The source does not parse as a single valid declaration ...` text; three declarations with the second malformed throws and the message names `declaration 2` and `Method 'B'`; stray `}` after two valid declarations throws the `extra or unbalanced '}'` text; a valid method followed by `return 1;` throws the existing `DetectTrailingUnparsedDeclaration` message and does NOT contain `declaration 2`; the `unparsableMessage` argument is the text thrown for a null single parse (both default `Failed to parse new member.` and an explicit `Cannot edit: invalid member source.`); a list with a missing closing `}` (probe has diagnostics, no single member does) throws `newMemberSource holds N member declarations but does not parse as a list: ...` (Step 2 fallback); nothing is returned in any throwing case.
  - `DescribeAddedMembers`: `Method 'A'` for one, `2 members: Method 'A', Field '_b'` for two.
  - `MemberEditEffectFacts.DescribeAdded`: two methods give two rows in source order; `private int _a, _b;` plus a method gives three rows; a one-member input gives the same rows as before.
- Done when (dispatcher): all new tests in `MemberSplitDeclarationsTests` pass; `EditEffectEmitterTests.DescribeAdded_ParsesEveryDeclaredName_AndEnumMembers` still passes.
- Coverage note: these tests cover the splitter and parser as functions. They do not cover insertion order, atomicity or the binding check; Steps 7, 9, 15-18 do.
- Parallel-ready: with Step 7 (different projects).

### Step 7 - Tests: N-member insertion formatting (proposal tests 1 and 2, helper level)
- Done: 46152134
- Files: `RoslynSentinel.Tests.Basic/InsertMembersFormatCharacterizationTests.cs` (new; same pattern as `InsertMemberFormatCharacterizationTests`, which calls the Common helper directly)
- Depends on: 4
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change: byte-exact assertions on the returned text, source members produced by `SyntaxFactory.ParseMemberDeclaration` or the Step 2 splitter: N = 2 appended at the end of a container with siblings (exactly one blank line between each pair, none before the closing brace); N = 3 inserted at index 0 (the following sibling gains its blank line, first new member has no leading blank line); N = 2 in the middle (contiguous, source order, siblings' interior spacing untouched); an empty container with N = 2; a CRLF document with an LF source and an LF document with a CRLF source (the `eol` rule); a one-element list gives byte-identical text to the single-member method on the same inputs (this is the N = 1 characterization for the delegation of Step 4).
- Done when (dispatcher): the new tests pass.
- Parallel-ready: with Step 6; and with Step 3 or 5.

### Step 8 - AddMembersAsync body and the typed-caller guard (text-neutral)
- Done: 3e28b49e
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`
- Depends on: 2, 4, 5
- Effect: text-neutral. `AddMembersAsync` has no caller yet (Step 9 tests it directly); `AddMemberAsync` keeps its old body, so every existing caller is unchanged. The typed-caller guard emits the text those callers get today (`newMemberSource is invalid: <add-branch text of DetectMultipleMemberDeclarations>`); the only difference is order: the guard now runs before the container and enum checks.
- Lane: `implementer` (1 file, about 5 edits)
- Change:
  - Implement `AddMembersAsync` as a copy of `AddMemberAsync` (`:2566-2630`) with these differences only: parse with `ParseNewMemberDeclarations(newMemberSource)` after the container and enum checks (so an enum container still gets the existing `ToolTargetIneligibleException`; default null-parse text, which is the add path's existing "Failed to parse new member."), description `DescribeAddedMembers(members)`, insert with `RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync(.., typeContainer.Members.Count, members, ..)`, bindings with the list overload of `RequireKeepsBindingsAsync`, `Message = $"// Added {description}."` (so one member still reads `// Added Method 'X'.`).
  - Typed-caller guard: add a private static helper `RequireSingleDeclaration(string source)` that does `if (DetectMultipleMemberDeclarations(source, "addMember") is { } issue) throw new ToolInvalidArgumentException($"newMemberSource is invalid: {issue}");`, and call it with the built source in `AddFieldAsync` (const branch `:2318` and non-const branch `:2340`; bind the source to a local first) and `AddPropertyAsync` (`:2396`). Reason: Step 11 makes `AddMemberAsync` accept N, and the typed tools build their source from caller-supplied strings (an `initializer` of `1; private int z = 2` produces two declarations).
- Done when: `Build` 0 errors. Dispatcher runs: `MemberRefactoringTests` (AddMember_*), `AddMemberCallBindingTests`, `MemberAddExpressionBodyIndentTests`, `MemberSingleDeclarationTests` (all still green here: the tool still rejects), then Step 9.

### Step 9 - Tests: AddMembersAsync directly (engine level) and the typed-caller guard (proposal tests 1, 6 at engine level)
- Done: 7c16f4c3
- Files: `RoslynSentinel.Tests.Basic/AddMembersAsyncTests.cs` (new; public API only, fixture pattern of `AddMemberCallBindingTests`: `SetFiles`, `_memberRefactoringEngine`)
- Depends on: 8
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change:
  - Parity (N = 1): for several single-member sources (method, property, field, expression-bodied method over two lines) `AddMembersAsync` and `AddMemberAsync` return identical `UpdatedText` and identical `Message` (`// Added Method 'X'.`); nothing is written by either, so both run on one workspace.
  - N = 2 and N = 3 appended: members in source order after the last sibling, exactly one blank line between members, `Message` is `// Added 2 members: Method 'A', Method 'B'.` form, `Outcome` is `Modified`.
  - Rejections: enum container throws `ToolTargetIneligibleException` containing `ModifyEnum`; unknown container throws `ToolNotFoundException`; second declaration malformed throws `ToolInvalidArgumentException` naming `declaration 2`; a valid method plus `return 1;` throws the existing message without `declaration 2`; stray `}` after two declarations throws the brace text. Each asserts the document text is unchanged.
  - Typed guard: `AddFieldAsync` with `initializer: "1; private int z = 2"` (non-const) and with `isConst: true, initializer: "1; private const int z = 2"`, and `AddPropertyAsync` with `propertyName: "P { get; set; } public int Q"`, each throw `ToolInvalidArgumentException` whose message contains `2 member declarations` and `addMember takes exactly one member per call`; the same calls with an ordinary initializer or name still succeed.
- Done when (dispatcher): the new tests pass.
- Parallel-ready: with Step 10 (a queue submission, no implementing agent).

### Step 10 - LIVE: same-commit baseline (queue jobs; holds Step 11)
- Done: 2026-11-14, no code commit. 15 jobs, all FINISHED-PASS at tested commit 39b111c1 (steps 1-9 plus a peer's ReasoningReplayPolicy commit): host 113 qwen3.5-9b-mtp ten-chain x5 (20261114-090609-001..005) and ShippingKindStep -Step 3 x5 (090611-001..005); host 112 qwen3.5-4b-mtp ten-chain x5 (090610-002..005 plus resubmit 102706-001 for the crashed 090610-001). Deviation: host 85 LM Studio was unreachable, 112 replaced it; Step 20 must use the same hosts and models. The rejection counts per the Step 20 rule have not been taken yet; they come from the run folders under `ModelTestingResults/{112,113}/`.
- Files: none (the queue reads the working tree and writes `ModelTestingResults/**`)
- Depends on: 8 committed and the tree compile-green (Steps 1-8 are text-neutral, so this tree behaves like today's server for every model-visible text). Step 9 may be in progress.
- Effect: measurement only. HOLD: Step 11 and every later behaviour-changing step must not be started until the LAST baseline job has started (each job builds the working tree when it starts, so a behaviour change in the tree before then contaminates that job).
- Lane: `modeleval-runner` (a model under test is involved). Use the queue, never a direct `roslynsentinel-modeleval.ps1` call (a direct run on a shared host is invisible to peers and collides on `_scratchbuild_<host>`); `-Session` is the first line of `ListAgents`. Flag the cost before starting: ten-chain runs took roughly 10 to 37 minutes each in the last measured batches and `-Repeats 5` makes 5 separate jobs per host, so the hold on Step 11 lasts until the 5th job of every host has started, probably several hours; Steps 6, 7 and 9 and the Step 8 review can proceed meanwhile.
- Change: submit 15 jobs (model key: the one used by the proposal's baseline runs; look it up in those run folders, not verified here). `-OnTreeChange RunAnyway` is required so that the test-only edits made during the hold do not end a queued job as `TreeChanged`; keep the tree compile-green, and do not edit non-test `*.cs` files in the Step 11 set while any job is queued:
  `.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Test OrderPricingRefactorChain10 -Model <key> -Repeats 5 -OnTreeChange RunAnyway -Session <name> -Purpose 'addMember multi-declaration, baseline'`
  the same for `-HostAddress 85`, and
  `.\scripts\roslynsentinel-evalqueue.ps1 submit -HostAddress 113 -Test ShippingKindStep -Model <key> -Repeats 5 -OnTreeChange RunAnyway -Session <name> -Purpose '...' -EvalArgs '-Step 3'`
  (aliases in `roslynsentinel-modeleval.ps1:370-374`: `OrderPricingRefactorChain10` = `Model_AppliesTenChainedRefactors`, `ShippingKindStep` = `Model_CompletesShippingKindStep`; the script was uncommitted-dirty in another session when this was checked). Record each job id, the commit hash and `Git(status)` at submit; use `status` to see when the last job of each host has started, and `wait -Id` in the background for the results. Count: 5 runs per fixture and host is a starting point, not a power calculation.
- Outcome classes reported (one per job): FINISHED-PASS and FINISHED-FAIL (usable baseline rows: record pass/fail, turn count, and the rejection count by the counting rule in Step 20); TIMED-OUT, CRASHED or TREE-CHANGED (not a baseline row; resubmit that job before the hold is released).
- Done when: all 15 jobs have ended in FINISHED-PASS or FINISHED-FAIL, or the failed ones are resubmitted and have started; the hold on Step 11 is released when the last job has started.

### Step 11 - Insert anchors accept N; AddMemberAsync delegates (the behaviour change)
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`
- Depends on: 8 and 10 (hold: the last baseline job has started); 9 should be green first
- Effect: behaviour-changing. `Member(insertAfterMemberName / insertBeforeMemberName)` and `AddMemberAsync` with a multi-declaration source now insert instead of being rejected (the engine; the tool is wired in Step 13). Starts the red window of `MemberSingleDeclarationTests.AddMember_WithTwoMembers_*` (see Execution rules).
- Lane: `implementer` (1 file, about 12-14 edits; at the depth limit: if the implementer reports it needs more than 15 edits or 35 calls, split off the `AddMemberAsync` delegate and the two TODO comments as their own step)
- Change:
  - `InsertMemberAfterAsync` (method at `:2048`, guard call at `:2086`) and `InsertMemberBeforeAsync` (`:2108`, guard `:2146`): replace the `ParseMemberDeclaration` / `DetectMultipleMemberDeclarations` / `DetectTrailingUnparsedDeclaration` / `DescribeParsedMember` block with `ParseNewMemberDeclarations(newMemberSource, "Cannot edit: invalid member source.")` (so the N = 1 null-parse text stays byte-identical) and `DescribeAddedMembers`; call `RoslynFormattingHelper.InsertMemberDeclarationsAndFormatInsertionAsync` with the list and the existing index (`ResolveInsertAnchorIndex(...) + 1` for after, the index itself for before); call the list overload of `RequireKeepsBindingsAsync`. The enum fallback (`:2077`, `:2137`) calls `AddMembersAsync`.
  - `AddMemberAsync`: replace the body with `return AddMembersAsync(filePath, containerName, newMemberSource, contextSnippet, lineBefore, lineAfter, cancellationToken);` (signature kept; typed callers keep calling it and are protected by the Step 8 guard). Add `// TODO fold the AddMemberAsync wrapper into callers once TypedMember callers move to AddMembersAsync`.
  - `DetectMultipleMemberDeclarations` (`:2276`): do NOT delete the add branch (the Step 8 typed-caller guard uses it, with its text unchanged). Its XML doc (`:2261-2272`) still describes addMember as a general multi-member rejection and must be updated to say that the `addMember` branch now serves only the `AddFieldAsync` / `AddPropertyAsync` guard and that `replace` is the live rejection; the replace text (`:2299`) is left byte-for-byte as it is. Add `// TODO drop the operation parameter of DetectMultipleMemberDeclarations once the typed callers move to AddMembersAsync` there, and `// TODO replace still takes one declaration; allow N when exactly one declaration matches the target, once the addMember batching is measured` beside the replace rejection in `ReplaceMemberAsync` (`:2473`) (plain comments, no doc IDs).
- Done when: `Build` 0 errors. Dispatcher runs: `MemberRefactoringTests.InsertMemberAfter_*`, `InsertMemberBefore_*`, `MemberNameEventIndexerAnchorTests`, `AddMemberCallBindingTests` (13 tests: with the delegate they now reach the list overload of the binding check for N = 1), `AddMembersAsyncTests` (Step 9). Expected to fail until Step 19: `MemberSingleDeclarationTests.AddMember_WithTwoMembers_*` (see Execution rules).

### Step 12 - Anchor-not-found hint for an anchor that names an added member
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`
- Depends on: 11
- Effect: behaviour-changing (error text: one appended fact)
- Lane: `implementer` (1 file, about 5 edits)
- Change: `ResolveInsertAnchorIndex` (`:2204`) gains an optional `IReadOnlyCollection<string>? newMemberNames = null` and passes it to `BuildInsertAnchorNotFoundMessage` (method at `:2173`, call at `:2219`), which gains the same optional parameter; when the anchor name equals one of the new names it appends the fact `'<name>' is declared in newMemberSource; an anchor must name a member already in the container`. `InsertMemberAfterAsync` and `InsertMemberBeforeAsync` pass the names of the parsed members (declared names, as `DescribeAdded` lists them). The two `AddEnumMemberAsync` callers (`:791`, `:801`) keep the default.
- Done when: `Build` 0 errors. Dispatcher runs `MemberRefactoringTests.InsertMemberAfter_*` and `MemberNameEventIndexerAnchorTests` (anchor not-found text for N = 1 without a clash is unchanged), then Step 16.
- Parallel-ready: with Step 13 or Step 14 (different projects).

### Step 13 - Tool layer: addMember path uses AddMembersAsync, result text, schema description
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`, `RoslynSentinel.Tools.Basic/RefactoringStructuralTools.cs`
- Depends on: 3, 11
- Effect: behaviour-changing. `Member(addMember)` accepts N declarations with and without anchors; `statusMessage` for N >= 2 reads `Added 2 members: Method 'A', Method 'B' to 'C' in F.cs.`; the `newMemberSource` description changes.
- Lane: `implementer` (2 files, about 4 edits)
- Change:
  - `MemberAddToContainerAsync` (`:931`): in the no-anchor branch call `_memberRefactoringEngine.AddMembersAsync(...)` (same arguments as the `AddMemberAsync` call at `:950`). The anchor branches already compose their text from the engine `Message` through `DescribeMemberOutcome` (`:99`), which strips `// ` and the trailing period, so the listing form flows through unchanged; the fallback labels `Added new member` / `Inserted new member` stay. The pre-existing `// TODO Member dry run reports "Added new member" ...` at `:951` stays. `RecordAddedEffect(MemberEditEffectFacts.DescribeAdded(newMemberSource, ...))` at `:978` needs no edit (Step 3 made it N-aware).
  - `RefactoringStructuralTools.cs:130`: change `[Description("Exactly one member declaration (signature and body; a doc comment in it replaces the old one, and with none the old doc comment is kept); for addTopLevelType, the full type declaration.")]` to a form that allows several for addMember and keeps the replace constraint explicit, for example `One member declaration for replace; for addMember one or more, each with signature and body (a doc comment in it replaces the old one on replace, and with none the old doc comment is kept); for addTopLevelType, the full type declaration.` Net token change is a few tokens on a parameter already in the lean surface. The `operation` description string is unchanged.
- Done when: `Build` 0 errors, and `Search(mode: text, query: "Exactly one member declaration")` returns no hit in `RoslynSentinel.Tools.Basic`.

### Step 14 - Oversize advice and the gotchas reference
- Files: `RoslynSentinel.Common/WriteToolAdviceHelper.cs`, `RoslynSentinel.Tests.Server/WriteToolAdviceHelperTests.cs`, `docs/current/references/reference_edit_tool_gotchas.md`
- Depends on: none technically; schedule with or after Step 13 so the advice never describes a tool that does not exist yet (and after the Step 10 hold: it is a `*.cs` change that changes model-visible text)
- Effect: behaviour-changing (advice sentence for `RejectedContentShape.MultipleMembers` and `FileLevel`)
- Lane: `implementer` (3 files, about 6 edits)
- Change:
  - `AdviseForOversizedEdit` (`:166`): `MultipleMembers` case (`:181`) currently tells the caller `Member(replace)` takes exactly one and to make one Member call per member. New text: new declarations go in ONE `Member(operation: addMember)` call (newMemberSource holds all of them); an existing member that is being replaced needs one `Member(operation: replace)` call each; none of the calls has a size limit. `FileLevel` (`:184`): change the using lines with a small rejecting-tool call, then one `addMember` call with all new members (or `replace` per existing member). Update the XML comments that say "takes exactly one member per call" (`:163-164`, `:174`). The default and `SingleMember` text is unchanged.
  - `WriteToolAdviceHelperTests.cs`: `AdviseForOversizedEdit_MultipleMembersShape_SaysSeveralMembersNotWholeMember` (`:176`) asserts `"several members"`, `"Member call"` and the absence of `"whole method/property/constructor"`; keep those that remain true, add `"addMember"` and an assertion that the sentence does not say one call per member. Update the stale comment at `:178`. Add a case for proposal test 8: with a helper that does not expose `Member` (`new WriteToolAdviceHelper([WholeFileWriteClass])`) the `MultipleMembers` and `FileLevel` sentences do not contain `"Member"` or `"addMember"`. Coverage note: the existing `AdviseForOversizedEdit_EveryShape_NamesOnlyExposedToolsWithAndWithoutMember` (`:215`) checks only `advice.ToolNames`, not the sentence prose, so it would not catch a gated tool named in the text; the new case does.
  - `reference_edit_tool_gotchas.md:19`: `Member(addMember) takes one declaration per call` -> addMember takes one or more declarations (atomic, one changeId); replace takes one; `addTopLevelType` for a new top-level type.
- Done when: `Build` 0 errors. Dispatcher runs `WriteToolAdviceHelperTests` (all) and `RejectedContentClassifierTests` (unchanged; `RejectedContentClassifier` still mirrors the probe parse, its comment at `:33` stays true).
- Parallel-ready: with Step 12 or 13 (disjoint projects: Common and Tests.Server versus Engines.Basic or Tools.Basic).

### Step 15 - Tests: tool-level N-member add (proposal tests 1, 2 and 4)
- Files: `RoslynSentinel.Tests.Tools.Basic/MemberAddManyTests.cs` (new; `InMemoryWorkspace` and `MemberTool`, the pattern of `MemberSingleDeclarationTests`)
- Depends on: 13
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change:
  - Fixture like `SingleDeclTarget` (`Alpha`, `Beta`), then: N = 2 and N = 3, no anchor: all members present in source order after the last member, exactly one blank line between members, container unchanged otherwise, `statusMessage` contains `Added 2 members` / `Added 3 members` and every `Kind 'Name'`.
  - N = 2 with `insertAfterMemberName: "Alpha"`, and with `insertBeforeMemberName: "Beta"`: block between `Alpha` and `Beta` (contiguous, source order); also the ShippingKind step 3 shape: three methods (`IsKnown`, `IsExpedited`, `Normalize`) after an anchor.
  - Caller before helper, one call: `public int Run() => Helper() + 1;` plus `private int Helper() => 41;` passes the compile gate. Control in the same file: the caller alone is refused with CS0103 (proves the gate runs on the in-memory workspace; if it does not, move these two cases to the real-workspace pattern of `EditEffectUndoTests`).
  - `dryRun: true` with N = 2 returns the same envelope and writes nothing.
  - N = 1 regression through the new path: `Member(addMember)` with one method gives the old text `Added Method 'X' to 'C' in F.cs.`, and a null-parse input keeps its existing text (`Failed to parse new member.` on the add path, `Cannot edit: invalid member source.` with an anchor).
- Done when (dispatcher): the new tests pass.
- Parallel-ready: with Step 17 (different projects; preferred pair).

### Step 16 - Tests: atomicity, the silent-drop guard, anchor hint (proposal tests 3 and 6)
- Files: `RoslynSentinel.Tests.Tools.Basic/MemberAddManyGuardTests.cs` (new)
- Depends on: 13, 12
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change: each case asserts the error code (`ToolErrorCode.InvalidArgument` unless stated), the message, and `workspace.ReadText(...)` equals the text before the call.
  - Atomicity: N = 2 where the second declaration has a parse error (names `declaration 2`); where the second declaration compiles as syntax but fails the compile gate (an undefined type: the first member is not written either).
  - Silent-drop regression (these have no add-side counterpart today; the existing add-side coverage is only `MemberSingleDeclarationTests.AddMember_WithStrayClosingBrace_NamesTheExtraBraceAsync`): valid first plus malformed second (the old CS1073 case); a valid declaration followed by a body fragment (`return 1;`, `IncompleteMemberSyntax`) rejected with the existing message and not the per-declaration text; stray `}` after two valid declarations (message contains `extra or unbalanced '}'`); a single malformed declaration still reports the single-declaration text.
  - Anchor naming a member that is being added: error code `ToolErrorCode.NotFound` (exception `ToolNotFoundException`), message lists the existing members and states that the name is declared in newMemberSource.
- Done when (dispatcher): the new tests pass.
- Parallel-ready: weak, with Step 18 only if the dispatcher accepts two agents in `Tests.Tools.Basic` (both are new files in the same project). Safe to run alone.

### Step 17 - Tests: binding check refuses a batch member that re-points a call (proposal test 5)
- Files: `RoslynSentinel.Tests.Basic/AddMemberManyCallBindingTests.cs` (new; same fixture pattern as `AddMemberCallBindingTests`: `SetFiles`, `_memberRefactoringEngine`)
- Depends on: 8 (11 for the anchor variant)
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change (engine level, through `AddMembersAsync` and `InsertMemberAfterAsync`):
  - Batch `[ public void Save(int v) { }, public void Other() { } ]` where `Run` calls `Save(1)` bound to `Save(long)`: `ToolInvalidArgumentException`, message quotes `Save(1)`, names `Save(long)` and `Save(int)`, ends `Nothing was written.`, the document is unchanged; the refusal names the added members in the plural form.
  - Batch where the capturing member is the SECOND declaration (checks that all N are compared, not only the first).
  - Batch of a new caller and a new helper with no existing call to either: succeeds (the helper's occurrences inside the new caller are exempt via `AddedMemberIds`).
  - Batch of a new field with an initializer that calls an added helper, and a batch with a member kind that is not checked (field only): succeeds (id collection for field declarators).
  - Names that appear in no document skip the check (batch of two uniquely named methods succeeds).
  - Same batch through `InsertMemberAfterAsync` is refused identically.
- Done when (dispatcher): the new tests pass; `AddMemberCallBindingTests` still pass.
- Parallel-ready: with Step 15 (preferred pair; different projects).

### Step 18 - Tests: one changeId, undo reverts the batch, N effect rows (proposal test 3, UndoLastApply; Q5 end to end)
- Files: `RoslynSentinel.Tests.Tools.Basic/MemberAddManyUndoTests.cs` (new; real on-disk workspace, the `EditEffectUndoTests` setup: `PersistentWorkspaceManager`, `TestSolutionFixture`, `NewEditImpl`; `InMemoryWorkspace` is documented as unsuitable for undo-from-disk)
- Depends on: 13
- Effect: tests only
- Lane: `test-writer` (1 new file)
- Change: `Member(addMember)` with 2 methods through `Battery.Basic.TestTools.RefactoringStructuralImpl(...)`: one `changeId` in the result; `UndoLastApply(changeId)` restores the file text byte-for-byte (both members gone). With a `ToolCallContext` started as in `EditEffectEmitterTests.StartCall`: `TakePendingEffects()` holds two `EditEffectKind.Added` effects (display `C.A`, `C.B`) after a successful add, none after a refused add and none for `dryRun`; after the undo the committed effects are reverted (the `index.Find(...)` pattern of `EditEffectUndoTests`).
- Done when (dispatcher): the new tests pass.
- Parallel-ready: weak (same project as Steps 15, 16, 19); run alone or with Step 17.

### Step 19 - Convert the existing rejection tests
- Files: `RoslynSentinel.Tests.Tools.Basic/MemberSingleDeclarationTests.cs`
- Depends on: 13
- Effect: tests only
- Lane: `test-writer` (1 file, about 3 edits)
- Change:
  - `AddMember_WithTwoMembers_ReportsCountNamesAndOneCallPerMemberAsync` (method `:151`, `[TestCase]` lines from `:148`, cases no anchor, `"Alpha"`, `"Beta"`): becomes an acceptance test (rename, for example `AddMember_WithTwoMembers_AddsBothInOrderAsync`), same three cases, asserting `!result.IsError`, both `Gamma` and `Name` present in order, the position per case, and that the old text `addMember takes exactly one member per call` is gone.
  - Keep unchanged: `Replace_WithThreeMembers_ReportsCountNamesAndRecoveryInsteadOfInvalidDeclarationAsync` (replace keeps rejecting, with its text, asserted at `:65`), `Replace_WithSingleMalformedMember_*`, `Replace_WithStrayClosingBrace_*`, `AddMember_WithStrayClosingBrace_*`.
  - Update the class summary (`:9-16`) so it says addMember takes one or more and replace takes exactly one.
- Done when (dispatcher): `MemberSingleDeclarationTests` all pass (this ends the Step 11 red window), then the whole `RoslynSentinel.Tests.Tools.Basic` project.

### Step 20 - LIVE: measure against the Step 10 baseline (live / eval step; no implementer)
- Files: none (reads `ModelTestingResults/**`)
- Depends on: 19 green, 10 (the same-commit baseline jobs, all ended), `Build(level: fullBuild)`, own-server restart, all code committed
- Effect: measurement only
- Lane: `modeleval-runner`. Same queue rules and cost warning as Step 10.
- Change:
  1. Before snapshot = the Step 10 jobs (same commit lineage, same hosts, same model). Count with the proposal's rule: `Turn \d+: Member FAILED` lines whose neighbouring tool result contains `takes exactly one member` (not the raw phrase count, which double counts). Record per run: rejections, operation (addMember or replace), the turns between a failure and the next successful `Member` call, pass/fail and turn count. The historical runs in the proposal are context for what a rejection looks like, not part of the comparison.
  2. Submit the same 15 jobs as Step 10, with `-Purpose 'addMember multi-declaration, after'` (no `-OnTreeChange RunAnyway` needed: the tree is committed and must stay unchanged); `wait` on the job ids in the background.
  3. After snapshot with the same counting rule, plus: for each run the number of `Member(addMember)` calls with N >= 2 declarations (parsed from `transcript.json`) and whether each succeeded.
- Outcome classes reported (one per run, then counted):
  - CLEARED (ideal): no addMember call is rejected for holding several declarations, and the run passes within the baseline turn count.
  - REPLACE-SHAPE (limit, expected): the replace rejection `replace takes exactly one member` still appears; count them, they are the input for the option 2 decision.
  - BATCH-REJECTED (limit, new failure shape): an addMember call with N >= 2 fails for a new reason (per-declaration parse text, binding refusal, compile gate) and the model needed extra turns; quote the turn and message.
  - OVER-BATCH (limit): addMember with N >= 2 where the task asked for one member, or a payload that mixes an existing member's replacement with new members (the replace shape sent to addMember); quote the call.
  - REGRESSION: pass rate or mean turn count worse than the Step 10 baseline.
- Done when: the before and after counts are written into a note on the proposal or a finding doc (the owner decides which when reading the numbers); no files are edited by this step.

### Step 21 - Closing: stub sweep, full build and test run, bookkeeping
- Files: stub sweep over `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Common/RoslynFormattingHelper.cs`, `RoslynSentinel.Engines.Basic/AddMemberCallBindingCheck.cs`; the one cleanup edit is in `AddMemberCallBindingCheck.cs`
- Depends on: 19 (can run before 20; 20 needs a committed tree)
- Effect: text-neutral
- Lane: dispatcher with `implementer` for the one cleanup edit
- Change:
  1. `Search(mode: text, query: "NotImplementedException", fileGlob: "**/{MemberRefactoringEngine,RoslynFormattingHelper,AddMemberCallBindingCheck}.cs")` and the same with query `TODO implement`; any hit fails the step.
  2. `Search(mode: references)` for the single-member `AddMemberCallBindingCheck.RequireKeepsBindingsAsync`; at 0 callers delete it (the single-member `CheckAsync` stays, `AddMemberCallBindingTests.cs:221` calls it).
  3. `Build(level: fullBuild)` 0 errors; one full `RunTest`; compare with `docs/known-failing-tests.{Basic,Advanced,Solution}.txt` and `docs/known-build-warnings.*.txt`; report only new failures (the recorded baseline is 0 failures).
  4. Bookkeeping: set this plan's Status to IMPLEMENTED with the commit hashes and move it to `docs/current/plans/closed/`; move the READY entry for this plan from `TODO.md` to `CLOSED.md`; add a `TODO.md` open entry for option 2 that points at the proposal and at Step 20's numbers.
- Done when: the stub searches return nothing; `Build(level: fullBuild)` 0 errors; `RunTest` shows no failure outside the baseline.

### Dependency summary and parallel pairs

Waves: 1 | 2+4 | 3+5 | 6+7 | 8 | 9 with 10 (queue) | hold until the last Step 10 job has started | 11 | 12+13 (or 12+14, 13+14) | 15+17 | 16 | 18 | 19 | 20 | 21. Preferred pairs (different projects): (2,4), (3,5), (6,7), (12,13), (12,14), (13,14), (15,17). Weak pairs (same project, only if the dispatcher accepts it): (16,18). Everything else runs one at a time.

## Out of scope

- Option 2 (replace accepts N: exactly one declaration matches the target by name and parameter types, the rest are added after it; the matching rule, syntactic versus semantic parameter types, proposal open question 4). Follow-up, decided after Step 20. The replace rejection and its text stay as they are, `ReplaceMemberAsync` is not touched beyond a TODO comment.
- Option 3 (recovery text only) and option 4 (a separate batch operation or tool): not pursued (proposal, "Alternatives considered").
- Proposal open question 7 (the stale `OPEN` status lines of the two closed silent-drop blockers).
- A different anchor per member, partial success, or any N-sequential-single-write implementation (rejected in the proposal: N changeIds, N compile gates, the CS0103 ordering problem returns).
- `TypedMember` generated fields and properties accepting N (they keep the single-declaration guard of Step 8), `AddTopLevelTypeAsync`, enum members: unchanged.

## Risks and open decisions

Open decisions (genuinely open; none blocks the run):

- **Replace rejection text is left unchanged and is now suboptimal.** It still says to call addMember "once for each member that is new"; after this plan one addMember call can add all the new members. Options: leave it (as scoped), or add one clause (`one addMember call can add several new members`). A one-line change in `DetectMultipleMemberDeclarations`; recommended after Step 20 together with the option 2 decision, because the text is asserted at `MemberSingleDeclarationTests.cs:65` and a replace payload of method plus new helper is exactly the observed shape.
- **Legacy wording on the add path for a fragment.** A valid method followed by a non-declaration fragment (`return 1;`) is rejected with the existing `DetectTrailingUnparsedDeclaration` text (Step 2), whose first form still says "Split into separate calls, one declaration per call" when the fragment parses as a member. After this plan that advice is half right (several declarations are accepted). Options: leave it (the second form, "does not parse as a single valid declaration", is the one fragments normally get), or reword the first form for the add path. Recommend leaving it and checking the Step 20 transcripts for it.

Risks:

- **Probe-trivia normalization (Step 2) is the most likely formatting defect.** Declarations the model separates with 0, 1 or several blank lines, and CRLF versus LF sources, must all land with exactly one blank line between members; Steps 6, 7 and 9 pin it, and `finding_member_replace_drops_leading_blank_line_and_verify_gap.md` is the precedent for this class of bug. Test coverage that exists today (`InsertMemberFormatCharacterizationTests`, `MemberInsertAfterEolTests`) covers N = 1 only.
- **Binding check cost and false refusals.** One combined trial for N members costs one more capture than N = 1, not N. A false refusal would come from a member kind whose declared symbol id cannot be read; Step 5 fails closed there and Step 17 pins the field-declarator case. Static constructors, operators, indexers and nested types stay unchecked, as the class comment states.
- **Single-member byte identity.** The two existing null-parse texts stay (`ParseNewMemberDeclarations` takes the text), `AddMemberAsync` becomes a delegate only in Step 11, and Step 9 (parity of `AddMembersAsync` and `AddMemberAsync` outputs) and Step 15 (tool-level N = 1 text) are the checks. The parity test compares the two engine methods on the same inputs; it does not cover every kind of member source.
- **Baseline hold (Step 10).** The hold on Step 11 can last several hours; text-neutral and test-only steps proceed meanwhile. A baseline job that starts after a behaviour change lands is contaminated: the dispatcher must check `ListAgents` and the queue `status` before releasing the hold. Another plan in the tree (the reasoning-replay work) may change model behaviour between Step 10 and Step 20 and confound the comparison; it was not read for this plan, so that confounder is unverified.
- **Typed-caller guard order (Step 8).** The guard runs before container resolution, so a typed call with both a missing container and a multi-declaration argument now reports the multi-declaration text first. No test pins that order.
- **Weak models may over-batch** once the description allows several (proposal, "Cross-cutting design"); unmeasured, Step 20's OVER-BATCH class counts it.
- **Verification boundary.** Nothing in this plan was run. The source claims above were read from the current tree on 2026-11-14; the test-coverage statements name which path each cited test covers. A green `MemberSingleDeclarationTests` before Step 19 proves only the rejection path and the single-member parse, not N-member insertion, atomicity or bindings.
