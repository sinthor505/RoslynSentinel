# Plan: Every not-found error ranks candidates, says plainly when none exist, and prefills one orientation call

**Status:** IN PROGRESS 2026-11-12. Steps 1, 2 done; 3a, 3b, 4a, 5d-1 done (f45d009b, 2f5165ef, 117d7507, 5bc6cc8e, 3d33d265, 44f5b3ef, f9f6b65b); 3c/3d and further wire-text changes on hold until the plan_scenario_eval_followups live baseline releases them.

Related: [plan_scenario_eval_followups.md](./plan_scenario_eval_followups.md) (written in parallel: the stop-adherence scenario family and the seeded `RenameSymbolTypo` used in Step 6). Commit trailer for every slice: `Plan: plan_not_found_error_orientation` (the last slice adds `Closes:`).

## Problem

A model that names a file or symbol that does not exist gets, from most tools, an error that either offers
no candidates and no statement that none exist, or orders a listing that cannot help. Measured with
qwen3.5-9b-coder in the 2026-11-12 live scenario evals (run ids are not recorded in this plan; Step 6 records
them for the reruns):

| Scenario | Result | What the model did |
| --- | --- | --- |
| `RenameSymbolNoSuchMethod` (prompt names `ApplyLoyaltyBonus`; the method is `CalcDisc`) | 2 of 10 passed | Never called `RenameSymbol`. Called `Search(mode: symbol)`, got not-found with no candidates; 5 samples guessed name variants to the turn cap, 3 edited something instead. Its earlier `ListAll` had already listed `CalcDisc`, but nothing said no similar name exists. |
| `ReplaceSnippetWrongFileNearMatch` (path misspelt `OrderPricingCalculater.cs`) | 10 of 10 passed | Recovered, but not through a near match (see evidence 2). |
| `ReplaceSnippetWrongFileNoMatch` (names `ShippingLabelPrinter.cs`, which does not exist) | 0 of 10 passed | Same message orders a listing and offers `CreateFile` but never says no file has a similar name: 4 samples edited a different file, 5 looped on listing/search, 1 tried `CreateFile`. |

The causes, each verified against source on 2026-11-12:

1. **Symbol search gives no candidates, and its one hint cannot help a typo.** `SymbolNavigationImpl.cs:60-77`
   returns "Symbol 'X' not found in the solution ... Try exactMatch=false ... or call ListAll(...)". The only
   test-project aid is the hidden-test-project note (`:62-70`). `exactMatch=false` is a substring test
   (`SymbolNavigationEngine.cs:277`: `n.Contains(simpleName, OrdinalIgnoreCase)`), so `CalcDsc` does not
   contain-match `CalcDisc` and neither does `ApplyLoyaltyBonus`: the hint steers to a call that returns
   nothing for exactly the two shapes of mistake the scenarios test.
2. **File candidates are same-file-name only.** `DocumentLookup.FindClosestPaths` (`DocumentLookup.cs:276-288`)
   matches identical file names, ignoring case. `OrderPricingCalculater.cs` therefore got no candidate; the
   model recovered because `FileNotFoundMessage` (`FileNotFoundMessage.cs:56-64`) says "You MUST call
   ListSolutionItems(reason, kind: all)". That is a recovery by order-following, not by a near match.
3. **The no-candidate branch is silent about "nothing similar" and gives no way out.** The same text
   (`FileNotFoundMessage.cs:56-64`) says "no file-name match" (identical names only), offers `CreateFile`
   (`:60-61`), then orders the listing (`:62-63`). It does not say no similar file exists, and it never offers
   "tell the user the file does not exist". Existing tests pin the order wording
   (`CallableToolHintTests.cs:89,122,127,150`, `FileNotFoundMessageTests.cs:162-163`).
4. **docCommentId tools** (RenameSymbol, DeleteUnusedSymbol at `WorkspaceProjectManagementImpl.cs:918`,
   PreviewRenameImpact at `DiscoveryEngine.cs:542`) share `PersistentWorkspaceManager.ResolveFromWireAsync`
   (`RoslynSentinel.Common/PersistentWorkspaceManager.cs:3548-3590`; the file lives in Common, not Engines.Basic):
   "Symbol '{id}' no longer resolves - may have been renamed, moved, or removed. Re-run Search(mode: symbol)."
   (`:3578`). No candidates. The TODO at `:3572` is narrower than "candidates": it asks that the message first
   rule out a missing parameter list. Overload candidates exist only inside RenameSymbol, through
   `MissingParameterListResolver` (`RefactoringSignatureImpl.cs:85-111`).
5. **A good model is already in the tree and barely used.** `MemberNotFoundDetail.BuildAsync`
   (`MemberNotFoundDetail.cs:41-117`, similarity and edit distance at `:196-252`) gives closest names, a
   container check, names grouped by kind, other-file declarations and a next call. Production callers:
   `MemberRefactoringEngine.cs:2338` (ReplaceMember), `:2633` (RemoveMember),
   `SymbolNavigationEngine.cs:2943` and `:2967` (the member read behind ReadFile(memberName) and
   Member(view)). Nothing else uses it. Even it does not say "no similar name; report that" and prefills no
   per-candidate call.
6. **Scale.** `new ToolNotFoundException(` appears at 291 non-test sites; `ToolErrorCode.NotFound` at 70
   non-test sites; `DocumentLookup.RequireDocument` / `GetDocumentOrThrow` at 160 non-test call sites, all of
   which reach `FileNotFoundMessage.Build` through `DocumentLookupResult.Describe` (`DocumentLookup.cs:120`).
   So one fix in `FileNotFoundMessage` reaches the file lookups of Member (34 sites in
   `MemberRefactoringEngine.cs`), ReplaceSnippet (`WorkspaceFileEditImpl.cs:1229`, batch `:1550`) and the read
   tools (`WorkspaceReadNavigationImpl.cs:79-80,264,391`, `WorkspaceFileEditImpl.cs:544-545,695`); the symbol,
   member, class and project literals each need their own change.

CLAUDE.md states the rule: a not-found/zero-result error must rule out scope, name-shape, container and kind
mistakes before claiming absence, state what was searched, and name the next call. Per the failure doctrine
the scenario failures are an environment defect (the error text), not a model defect. One confounder is
recorded rather than resolved: the `RenameSymbolNoSuchMethod` prompt describes `ApplyLoyaltyBonus` as the
method that "computes an order's discounted total", which is also what `CalcDisc` does (the
`RenameSymbolTypo` prompt describes `CalcDsc` in the same words, `FaultScenarios.cs:22-42,59-80`). A model that
saw `CalcDisc` in an earlier `ListAll` and edited it may have reasoned by purpose, not guessed; the three
"edited something instead" samples were not re-read for this plan, so that is a hypothesis. It is a scenario
design question, tracked in `plan_scenario_eval_followups.md`, and it bounds what the error text alone can fix
(see Risks).

### Owner question (2026-11-12): do tools give fallback orientation hints for not-found?

"Partly." A hint exists on the shared file lookup, Search(mode: symbol), the docCommentId resolver, the
member read and about a dozen engine literals; none is complete, because:

- Hints are generic: arguments are not filled from the request, except where a literal already has the path or
  name in hand (the "Searched:" literals below do fill `filePath` and `query`).
- No hint covers "orientation found nothing either": no producer says no similar name exists, and none offers
  to report the target as non-existent.
- Some hints cannot help the case they target (cause 1), and one is an order to call a listing when the
  candidate list is the thing missing (cause 3).
- Some producers name nothing at all.

Seed inventory by producer family (verified 2026-11-12; Step 1 turns it into one row per site). Key for the
last column, the CLAUDE.md rule items met: S = scope ruled out or stated, N = name-shape (case/typo/exactness)
addressed, C = container, K = kind, W = what was searched is stated, X = a next call is named. "-" = none.

| # | Producer (tool) | Location | Candidates | Next call named | Args prefilled from request | Rule items |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Shared file lookup: `DocumentLookupResult.Describe` -> `FileNotFoundMessage.Build` (ReplaceSnippet, Member, ~160 sites) | `FileNotFoundMessage.cs:23-64`, `DocumentLookup.cs:96,120,276-288` | yes, same file name only (max 5) plus files declaring a type named like the file stem | yes: `ListSolutionItems(kind: all)` as "You MUST call", `CreateFile`, `LoadSolution` | `kind: all` only; `CreateFile`/`ListSolutionItems` carry no request value | S, N (casing only), W, X |
| 2 | Read tools file miss: ReadFile, GetMethodSource, GetFileOutline | `WorkspaceFileEditImpl.cs:544-545,695`, `WorkspaceReadNavigationImpl.cs:79-80,264,391` | same as #1 (same builder) | same as #1 | same as #1 | S, N (casing only), W, X |
| 3 | Search(mode: symbol) / LocateSymbol | `SymbolNavigationImpl.cs:60-77` | no | yes: `exactMatch=false` (substring only), `ListAll` | none | S (test-project note), W (partial), X |
| 4 | docCommentId resolver (RenameSymbol, DeleteUnusedSymbol, PreviewRenameImpact) | `PersistentWorkspaceManager.cs:3571-3581` (TODO `:3572`) | no | yes: `Search(mode: symbol)` | none | X |
| 5 | docCommentId shape check (no kind prefix) | `PersistentWorkspaceManager.cs:3559-3569` | n/a (an ID that cannot resolve) | yes: `Search(mode: symbol)` | none | W, X |
| 6 | RenameSymbol missing parameter list | `RefactoringSignatureImpl.cs:85-111` | yes, overload ids (or the single unique id) | the retry is the full id given | the ids themselves | N (parameter list), X |
| 7 | Member / member read: `MemberNotFoundDetail` | `MemberNotFoundDetail.cs:41-117` | yes: top 3 similar, same-file members by kind, other-file declarations | yes: `ReadFile(filePath)` and `GetFileOutline(filePath)`; for another file `ReadFile(filePath, memberName)` | `filePath`, `memberName` | S (file, container), C, K (grouping), W, X |
| 8 | Container not found (InsertMember*, AddMember) | `SyntaxTargetResolver.cs:118-190`, `MemberRefactoringEngine.cs:1952,2012,2413` | lists up to 10 types of the file (unranked) plus up to 3 other files declaring the name | "Pass one of those as containerName" (no call) | no | C, W |
| 9 | Insert anchor not found | `MemberRefactoringEngine.cs:2055-2067` (thrown `:789,799,2091`) | lists up to 25 anchors (unranked) | yes: `Member(operation: view, containerName)` | `operation`, `containerName` | C, W, X |
| 10 | MethodSignature view | `MemberRefactoringEngine.cs:1165` | no | yes: `Search(mode: text, query: methodName)`, `GetFileOutline(filePath)` | `query`, `filePath` | W, X |
| 11 | MethodSignature add / remove | `MemberRefactoringEngine.cs:1214,1276` | no | no ("Cannot edit: method 'X' not found.") | - | - |
| 12 | ConstructorParameter class / parameter | `MemberRefactoringEngine.cs:176,441,471,575` | no | `:575` only: `GetFileOutline(filePath)` | `:575` only | `:575`: W, X; the rest: - |
| 13 | MoveMember file | `MemberRefactoringEngine.cs:2937-2941` (hand-rolled `GetDocumentIdsWithFilePath`, bypasses `DocumentLookup`; also `:4911`) | no | no ("File 'X' not found.") | - | - |
| 14 | MoveMember class | `MemberRefactoringEngine.cs:2953,4923` | no | no | - | - |
| 15 | MoveMember members (`MemberSelection.SelectMembers`) | `MemberSelection.cs:287,295` | lists every member of the class (unranked) | no (a partial-class hint only) | no | C, W |
| 16 | ChangeSignature | `BasicRefactoringEngine.cs:251` | no | no ("... was not found in '{filePath}' (a constructor is matched by its class name). Nothing was changed.") | - | W (partial) |
| 17 | AnalyzeControlFlow / AnalyzeDataFlow / GetSummaryComment ("Searched:" literals) | `BasicRefactoringEngine.cs:2080,2122,2525` | no | yes: `GetFileOutline(filePath)` and `Search(mode: method\|symbol, query: name)` | `filePath`, `query` | W, X |
| 18 | Other bare engine literals | `BasicRefactoringEngine.cs:1452` (project), `:1500` (class), `:1751` (indexer), `:2277` (using), `:2336,2481` ("target not found"), `:2786` ("Method not found."); `MemberRefactoringEngine.cs:641,823,847,1594-1917 (6 sites),2225,4497,4517,5369,5416` | no | no | - | - |
| 19 | Namespace not found (AddTopLevelType) | `MemberRefactoringEngine.cs:2499` | lists all namespaces ("Available: ...") | no | no | W |
| 20 | Tools.Advanced file miss | `AdvancedRefactoringTools.cs:81` (used by `:276,574,610`), `GenerationTools.cs:139-141,251-253` | no | no ("Verify the path is correct and the solution is loaded") | no | W (project count only) |
| 21 | Tools.Advanced project miss | `GenerationTools.cs:194-196` (lists loaded projects), `ScanTools.cs:1096-1101` | the list of all project names (`:194-196`), no ranking; `ScanTools` none | no | no | W |
| 22 | Tools.Advanced other | `AdvancedRefactoringTools.cs:708` (variable not found), `:109` (zero anonymous objects: a zero-result, not a lookup) | no | not read for this plan | - | not read |

Final counts (Step 1, 2026-11-12, [reference_not_found_error_inventory.md](../references/reference_not_found_error_inventory.md)):
189 rows covering 597 non-test sites; 161 rows (509 sites) are lookup misses, 28 rows (88 sites) are non-miss
conditions coded NotFound. 83 rows (277 sites) name a next call, 40 rows (56 sites) prefill request values, 37 rows
(203 sites) list candidates, and 76 miss rows (227 sites) give no orientation at all. Without the shared file
lookup (157 sites in one row) only 82 rows / 120 sites name a next call. So the answer to the owner question stays
"partly": about half the miss rows name a next call, a quarter prefill it from the request, and almost half give
nothing. The seed-table line numbers below have drifted; the inventory doc is authoritative.

Rows 18, 21 (ScanTools) and 22 were classified from the quoted preview line, not the whole message; Step 1
re-reads each. The 291 + 70 + 160 site counts above are measured; rows 11-19 cover the producers named in the
brief plus the ones found while verifying it, not all 291. The remaining sites (other engines, other tool
classes) are what Step 1 enumerates.

Brief-versus-source corrections found while verifying: (a) the docCommentId TODO is at `:3572`, not `:3574`,
and concerns the parameter list, not candidates; (b) `ListAll` has no file, container or "members" scope -
its parameters are `kind` (all, namespace, class, interface, method, property, struct, record, enum,
enum member, constructor, field), `projectName`, `includeTests`
(`WorkspaceReadNavigationTools.cs:51-61`, `ToolEnums.cs`), so a prefilled "ListAll of the named file or type"
cannot be built; the file-scoped orientation call is `GetFileOutline(filePath)`, and `Member(operation: view,
containerName)` still needs a `filePath`; (c) the "Searched:" literals (`:2080,2122,2525`) already name a next
call and prefill `filePath`/`query`, they just lack candidates; (d) `MoveMember`'s file lookup is a
hand-rolled one that also skips `DocumentLookup.TryGetDocument`'s bare-file-name rule; (e) Tools.Advanced and
Tools.Basic both already reference `Engines.Basic` (`RoslynSentinel.Tools.Advanced.csproj:26-30`), so the
reason to put the shared helper in `Common` is that `DocumentLookup`, `FileNotFoundMessage` and
`PersistentWorkspaceManager` live in Common and cannot see `Engines.Basic`, not that the Tools projects cannot.

## Decision

Owner decisions, 2026-11-12:

1. **A no-match error offers, in generic words, the option to report that the file or symbol does not exist.**
   Tools stay eval-agnostic: no error text mentions BLOCKED, scenarios, graders or any eval wording.
2. **One standard for every not-found error, in this order:**
   - (a) Candidates when any exist: edit distance on names, same kind first, each with its file and container,
     each with the exact next call prefilled (top 3).
   - (b) When none exist, say so plainly ("no file/symbol in the solution has a similar name; searched:
     <scope>"), then one orientation call with arguments prefilled from the request (for example
     `GetFileOutline(filePath)` for a named file, `Search(mode: symbol, query: <name>, ...)`,
     `ListSolutionItems(kind: all)`), then the report-it-does-not-exist option.
   - (c) No "You MUST" order to call a listing when the listing cannot help.

Owner decisions, 2026-11-12 (implement-plan settle, after a source review of this plan):

3. **Parameter-list check moves to Common.** `MissingParameterListResolver` (Engines.Basic; uses only Roslyn and
   `ProjectLookup`) moves to `RoslynSentinel.Common` and runs inside `ResolveFromWireAsync`, so RenameSymbol,
   DeleteUnusedSymbol and PreviewRenameImpact all get it (today only `RefactoringSignatureImpl.cs:89` calls it) and
   the `:3572` TODO closes. RenameSymbol's unique-resolve and overload-list branches (`RefactoringSignatureImpl.cs:88-110`)
   move into the shared path. Migration-first: the Common type beside the old, old `[Obsolete]`.
4. **Unit-test fixture:** a static `OrderPricingCalculator` fixture source (members `CalcDisc`, `OrderCheckout`,
   `GetTotal`; no `ApplyLoyaltyBonus`) in the existing `RoslynSentinel.Tests.Support.Basic` (already referenced by
   Tests, Tests.Basic and Tests.Tools.Basic), built with `TestSolutionBuilder`. The eval fixture in Tests.ModelEval
   is not touched (scenarios are out of scope). "The fixture" in Steps 2-4 means this source.
5. **No-file orientation call for a missing symbol (open item 3, settled):** if the query or docCommentId names a
   container that resolves (for example `OrderPricingCalculator.ApplyLoyaltyBonus`), prefill
   `GetFileOutline(filePath: <that type's file>)`; otherwise `ListAll(kind: <mapped>, projectName: <if given>)`, size
   left to the response pipeline.
6. **Step 6 target (open item 6, settled):** at least 6 of 10 for each of `RenameSymbolNoSuchMethod` and
   `ReplaceSnippetWrongFileNoMatch`.
7. **Wire shape (open item 4):** text only, as the plan default; `AmbiguousCandidate` is not changed.
8. **Run mode:** Batched, owner present. A = Step 1 (read-only explore jobs, then one doc write; runs alongside B),
   B = Steps 2-3, C = Step 4, D = Steps 5a, 5b, 5d, E = Steps 5c, 5e, then Step 6 (needs
   `plan_scenario_eval_followups` Steps 1 and 6). Slice splits are noted in each step.

9. **Step 5e scope (2026-11-12, after the Step 1 inventory):** 5e covers the leftover real-miss producers in
   Engines.Basic, Tools.Basic and Common only. Engines.Advanced (136 sites), Tools.Experimental and the `GitImpl`
   family go to a TODO.md line that points at `reference_not_found_error_inventory.md`; the plan closes without them.

Approach: build one ranking helper in `RoslynSentinel.Common` (lifted from `MemberNotFoundDetail`), make the two
shared lookups (file: `DocumentLookup` + `FileNotFoundMessage`; symbol: `LocateSymbol` + the docCommentId
resolver) use it, then move the remaining literals onto the same helpers in slices by project. Migration-first
throughout: new member beside the old, old marked `[Obsolete("Use X")]`, callers moved in slices, old removed
last; CS0618 warnings in between are expected.

Sketch of the two no-candidate texts (wording is the implementer's to refine; the content is the decision):

- File: "File 'X' was not found in the loaded solution (searched N project(s) by exact path, bare file name and
  similar file name; casing is not the cause). No file in the solution has a similar name. To see every file,
  call ListSolutionItems(kind: all). If the file is not in that list it does not exist in this solution: tell
  the user so instead of editing a different file." The `CreateFile` sentence stays when callable.
- Symbol: "Symbol 'X' (<kind>) was not found in <scope: N project(s), test projects included|excluded). No symbol
  in the solution has a similar name. To see what exists in the file you named, call GetFileOutline(filePath:
  <file>). If it is not there, the symbol does not exist: tell the user so instead of renaming or editing
  another symbol."

## Execution rules

- Every step ends compile-green: `Build` 0 errors, and `scripts/build.ps1 -Flavor Solution` for any change in
  `Common` or `Engines.*` (Advanced is additive; Tools.Advanced must still build).
- Per the current workflow (commit 53715a38), implementers never run tests; the dispatcher starts the per-slice
  filtered run detached in a fresh worktree. "Done when" test clauses below are for the dispatcher's run.
  Compare with the baselines `docs/known-failing-tests.{Basic,Advanced,Solution}.txt`; report only new
  failures.
- A hint that names a tool must keep honouring `isCallable` (a tool is named only when callable on the
  active surface), exactly as `FileNotFoundMessage` does today. Build a hint only when it will be rendered:
  `ToolCallHint.Call` records every hint it builds as issued (`ToolCallHint.cs`, the `ToolGuidanceCollector`
  call), even one later discarded.
- Tests that pin the old wording change deliberately in the step that changes the text. The reviewer confirms
  each changed assertion still checks the behaviour (candidate present, prefilled call present, no order
  when the listing cannot help) and was not merely deleted or loosened.
- A test that exercises only the shared builder says nothing about a tool path that bypasses it (rows 13-14,
  20-21). Each step's "Done when" names the path each test covers; a step is not done on a green builder test
  alone.
- Inline `// TODO` markers for anything noticed and deferred; none is needed for what this plan already lists.

## Steps

### Step 1 - Inventory of every not-found producer (doc step)
- Files: new `docs/current/references/reference_not_found_error_inventory.md` (genre: reference, follow
  `docs/current/templates/reference.md`); the seed table above stays in this plan as the owner-question answer
  and is updated with the final counts. No `.cs` edits.
- Change: enumerate with `Search(mode: text)` over non-test projects: `new ToolNotFoundException(` (291),
  `ToolErrorCode.NotFound` (70), `"FileNotFound"` / `FileNotFoundMessage.Build`, `EngineErrorCode.SymbolNotResolved`
  (4 production sites), `DocumentLookup.RequireDocument|GetDocumentOrThrow` (160, grouped as one row because
  they share row 1), and any `not found` / `was not found` literal outside those constructors. One row per site,
  or one row per group of sites with an identical message text, with the columns of the seed table (tool,
  file:line, candidates yes/no, next call named yes/no, arguments prefilled yes/no, rule items S/N/C/K/W/X met).
  Read the full message of every row, not a preview. Add a "string-coupled consumers" section (so far
  `ScanTools.cs:1096`, which catches on `ex.Message.Contains("not found in solution")`). Add the answer to the
  owner question with the final counts of rows that name a next call and rows that prefill arguments.
- Done when: every site returned by the searches above is in a row or an explicit group; the doc states the
  site counts and the commands used so the table can be regenerated; rows 11-22 of the seed table are re-read
  and corrected; the owner question has a one-paragraph answer with counts. A reviewer spot-checks 10 random
  rows against source.
- Done: f45d009b, 2f5165ef (all rows read; 10-row spot-check, 5 corrected). Real text-coupled consumers:
  `ScanTools.cs:1096` (fixed f9f6b65b) and `AsyncifyTools.cs:2050-2052`; `CodeTransformTools` verified not coupled.

### Step 2 - Shared name-candidate helper in Common
- Files: new `RoslynSentinel.Common/NameCandidates.cs` (name open, see Risks) and
  `RoslynSentinel.Tests.Basic` (or the Common test project) unit tests; `RoslynSentinel.Engines.Basic/MemberNotFoundDetail.cs`
  (`:196-252`: `Compare`, `IsSubsequence`, `EditDistance`).
- Change: move the similarity rule and edit distance (`Compare`: distance <= max(2, 40% of the longer name), or
  a 4+ character shared prefix, or for 4+ character names containment or subsequence) into one public static
  helper that ranks any sequence by name: input the wanted name, the items, a name selector and a cap (default
  3), output the similar items, closest first, plus the distance. The members are private to
  `MemberNotFoundDetail` with that class as the only user, so the private copies are deleted in this slice and
  `MemberNotFoundDetail` calls the helper (nothing to mark `[Obsolete]`); its output text is unchanged.
- Done when: `Build` 0 errors including `-Flavor Solution`; the existing `MemberNotFoundDetail` tests pass
  unchanged (these cover the Member / member-read path, not the file or symbol path); new unit tests cover the
  helper directly: `CalcDsc` -> `CalcDisc` ranks first among `{CalcDisc, OrderCheckout, GetTotal}`;
  `ApplyLoyaltyBonus` against the member names of `OrderPricingCalculator` returns no candidate; ordering by
  distance; cap of 3; case-insensitive; empty input and a 1-2 character name (noise guard) return nothing.
  The `ApplyLoyaltyBonus` case is a negative test that decides the threshold question in Risks: if it returns a
  candidate the rule is too generous for solution-wide use and a stricter rule is added here, not later.
- Done: 117d7507 (negative tests passed with the existing rule; not tightened).

### Step 3 - File-name candidates and the `FileNotFoundMessage` standard
- Files: `RoslynSentinel.Common/DocumentLookup.cs` (`FindClosestPaths` `:276-288`, `TryGetDocument` `:255`),
  `RoslynSentinel.Common/FileNotFoundMessage.cs` (`Build` `:23-64`), `DocumentLookupResult` (`DocumentLookup.cs`,
  `Describe` `:120`), callers `WorkspaceFileEditImpl.cs:545`, `WorkspaceReadNavigationImpl.cs:80`; tests in
  `RoslynSentinel.Tests.Tools.Basic` (`FileNotFoundMessageTests.cs`, `CallableToolHintTests.cs`,
  `FileNotFoundDeclaringTypeTests.cs`).
- Change: add `DocumentLookup.FindFileCandidates` returning records (path, distance, same-file-name flag),
  same-name matches first, then edit distance on the file stem (ignoring case and extension) from the Step 2
  helper, cap 3; mark `FindClosestPaths` `[Obsolete("Use FindFileCandidates")]` and move its three callers
  (`DocumentLookup.cs:255`, `WorkspaceFileEditImpl.cs:545`, `WorkspaceReadNavigationImpl.cs:80`). Add a
  `FileNotFoundMessage.Build` overload taking the candidate records and mark the old signature `[Obsolete("Use the
  overload taking file candidates")]` (13 test call sites keep compiling until moved). Rendering: with
  candidates, "Did you mean <path>?" per candidate with its prefilled next call (default `ReadFile(filePath:
  <path>)`, a neutral confirm-the-file call that works for every tool; see Risks for the alternative of naming
  the calling tool); with none, the decision 2(b)/(c) text: the plain "no file has a similar name; searched:
  exact path, bare file name, similar file name in N project(s)", one prefilled `ListSolutionItems(kind: all)`
  offer (not an order), the `CreateFile` and earlier-rename sentences as they are today, and the generic
  report-it-does-not-exist sentence. The disk branches (directory, project file, outside-solution) keep their
  wording. Update the assertions listed in cause 3 deliberately.
- Done when: `Build` 0 errors; unit tests pass: `OrderPricingCalculater.cs` against the fixture file list
  yields `OrderPricingCalculator.cs` as the first candidate with the prefilled call (builder path:
  `FileNotFoundMessage.Build`); `ShippingLabelPrinter.cs` yields no candidate and the text contains the
  "no file ... similar name", the `ListSolutionItems(kind: all)` offer, the report-it-does-not-exist sentence and
  no "You MUST" (same builder, no-candidate branch; if the rule returns a candidate for it, the threshold
  is tightened, not the test); `isCallable` false for `ListSolutionItems` renders no listing call. And one test
  per consumer path, not just the builder: ReadFile (`WorkspaceFileEditImpl.cs:695`), GetFileOutline
  (`WorkspaceReadNavigationImpl.cs:391`), ReplaceSnippet single (`:1229`) and batch (`:1550`), and a Member
  operation through `RequireDocument` each return the new text for the misspelt-file case.
- Slices (2026-11-12): 3a Common candidate record, `FindFileCandidates`, the new `Build` overload (it must carry the
  existing `isCallable` and `declaringTypeFiles` parameters; `DocumentLookupResult.CandidatePaths` also feeds the
  Ambiguous branch) and builder tests; 3b move the 3 callers and update the pinned assertions; 3c consumer-path
  tests (`test-writer`).
- Done: 3a 5bc6cc8e, 3b 3d33d265 (first wire-text change; the plan_scenario_eval_followups A/B baseline is pinned
  at 5bc6cc8e, owner decision 2026-11-12). 3c HELD: `FileNotFoundConsumerPathTests.cs` written, uncommitted; the
  ReplaceSnippet single/batch cases fail because the `ResolveFromWire` path guard (`WorkspaceFileEditImpl.cs:1164`,
  batch `:1541`/`:1671`) returns "'filePath' could not be resolved." before the candidate lookup runs.
- 3d (owner decision 2026-11-12, after the hold is released): the ReplaceSnippet path guard (single and batch) routes
  a file-shaped miss through `DocumentLookup` / `FileNotFoundMessage`; 3c's failing cases then pass and 3c commits
  with it. Step 6's `ReplaceSnippetWrongFileNoMatch` goes through this path.

### Step 4 - Symbol-name candidates: Search(mode: symbol) and the docCommentId resolver
- Files: new shared builder in `RoslynSentinel.Common` (for example `SymbolNotFoundDetail`, name open) that works
  on Roslyn `Solution`/`Compilation`; `RoslynSentinel.Tools.Basic/SymbolNavigationImpl.cs` (`:60-77`);
  `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs` (`LocateSymbolAsync`, `:168-378`; the `exactMatch=false`
  branch `:277`); `RoslynSentinel.Common/PersistentWorkspaceManager.cs` (`:3559-3581`, TODO `:3572`); tests in
  `RoslynSentinel.Tests.Tools.Basic` and `RoslynSentinel.Tests.Basic`.
- Change: the builder collects declared symbol names of the searched scope (the same projects and test-project
  filter `LocateSymbol` used, so the error states the same scope it searched), ranks them with the Step 2
  helper, same kind first, and returns up to 3 candidates each with kind, container, file and the exact next
  call prefilled (`RenameSymbol`'s next call is its `docCommentId`, which `SymbolLocation` already carries).
  With none, the 2(b) text: scope searched, "no symbol in the solution has a similar name", one orientation
  call prefilled from the request (decision table by request shape: a `filePath` -> `GetFileOutline(filePath)`;
  otherwise `ListAll(kind: <mapped from symbolKind>, projectName: <if given>)`; see Risks for the cost of the
  solution-wide case), the report-it-does-not-exist sentence. Drop the misleading "Try exactMatch=false" advice
  from the typo case: `exactMatch=false` is a substring test, so it is suggested only when it would match
  (the wanted name is a substring of a declared name), and the candidate list replaces it otherwise.
  The docCommentId resolver parses the member name (text after the last `.` before `(`) and the container from
  the ID, runs the same builder in the named project, and resolves the `:3572` TODO by first running the
  missing-parameter-list check for every caller, not only RenameSymbol (check whether DeleteUnusedSymbol and
  PreviewRenameImpact already call `MissingParameterListResolver`; not verified for this plan).
- Done when: `Build` 0 errors including `-Flavor Solution`; unit tests pass on three paths, each named in the
  test: `Search(mode: symbol, query: CalcDsc)` against the fixture returns a candidate `CalcDisc` with file,
  container and prefilled call (`LocateSymbol` path); `query: ApplyLoyaltyBonus` returns no candidate and the
  text contains the scope, "no symbol ... similar name", a `GetFileOutline` or `ListAll` call and the
  report-it-does-not-exist sentence, with no "Try exactMatch=false" (same path, no-candidate branch);
  `RenameSymbol` with `docCommentId: M:...OrderPricingCalculator.CalcDsc(...)` returns the same candidate
  (resolver path, `PersistentWorkspaceManager.cs:3571-3581`, via `RefactoringSignatureImpl.cs:113-119`).
  `RenameSymbolTests.cs:52` and `RenameSymbolMissingParameterListTests.cs:107` (which assert "no longer
  resolves") are updated deliberately. A hidden-test-project hit still produces the existing
  `includeTests: true` note.
- Layering (2026-11-12 review): `SymbolLocation` and `TestProjectDetector` are Engines.Basic, so the Common builder
  takes the already-filtered project list (or a predicate) from its caller. Slices: 4a Common builder and tests;
  4b the `LocateSymbol` path (`SymbolNavigationImpl`, scope passed in); 4c move `MissingParameterListResolver` to
  Common (decision 3) and the resolver path with its 3 callers.
- Done: 4a 44f5b3ef (`SymbolNotFoundDetail.BuildAsync`, no callers). 4b, 4c held (wire text).

### Step 5 - Sweep the bare literals onto the helpers (implementer-sweep lane, slices by project)
- Files: per slice below. Each slice is one operation across one project, compile-green on its own.
- Change, in slices:
  - 5a `Engines.Basic` - the existing good producers to the standard: `MemberNotFoundDetail` (add the 2(b)
    "no similar name" sentence and the report-it-does-not-exist option, prefill a call per candidate),
    `SyntaxTargetResolver.BuildContainerNotFoundMessage*` (`:118-190`; rank the listed types),
    `MemberRefactoringEngine.BuildInsertAnchorNotFoundMessage` (`:2055-2067`; rank the anchors),
    `MemberSelection.SelectMembers` (`:287,295`; rank, add next call).
  - 5b `Engines.Basic`, `BasicRefactoringEngine.cs` - rows 16-18 (`:1452` belongs to 5d, after the
    `ScanTools` catch change): `:251`, `:1500`, `:1751`,
    `:2080`, `:2122`, `:2277`, `:2336`, `:2481`, `:2525`, `:2786`.
  - 5c `Engines.Basic`, `MemberRefactoringEngine.cs` - rows 11-14, 18-19: the hand-rolled file lookups at
    `:2937-2941` and `:4911` move onto `DocumentLookup.RequireDocument` (this also gives MoveMember the bare-file-name
    rule it lacks), then the bare literals (`:176,441,471,641,823,847,1214,1276,1594-1917,2225,2499,4497,4517,
    5369,5416` and the class lookups `:2953,4923`).
  - 5d `Tools.Advanced` - rows 20-22: `AdvancedRefactoringTools.cs:81` and `GenerationTools.cs:139-141,251-253`
    route to `DocumentLookup` / `FileNotFoundMessage` (they now report only the base file name and a project
    count); project-name misses (`BasicRefactoringEngine.cs:1452`, `GenerationTools.cs:194-196`,
    `ScanTools.cs:1096-1101`) use the Step 2 helper on project names. `ScanTools.cs:1096` stops matching on
    message text: find which engine call throws "not found in solution" and catch the exception type instead.
  - Slice sizes (2026-11-12 review; none of 5a-5d fits one tier as written): 5a three D1 slices
    (`MemberNotFoundDetail`; container resolver plus insert anchor; `MemberSelection`); 5b two D1 slices; 5c about
    five (file lookups; constructor/class/enum; method parameter; the identical "target not found" sites through one
    helper; the rest), dispatched only once no other session has uncommitted edits in `MemberRefactoringEngine.cs`;
    5d three, in order (`ScanTools` catch change; file-miss routing; project-name misses).
  - 5e Anything the Step 1 inventory lists that is not in 5a-5d (other engines, other tool classes), as further
    slices; scope limited by Decision 9 (Engines.Basic, Tools.Basic, Common). The inventory's corrected anchors
    (for example MoveMember file lookups now `:2980`/`:4950`, class lookups `:2992`/`:4962`) supersede the line
    numbers in the seed table and in 5c; re-verify every anchor before briefing.
- Done when (each slice): `Build` 0 errors, `-Flavor Solution` green; the slice's rows in the reference doc
  are updated to candidates yes, next call yes, arguments prefilled yes, rule items met; one unit test per
  changed producer asserts the candidate case and the no-candidate case through that tool's real entry point
  (a test on the shared helper alone does not count); no remaining bare "not found." literal in the slice's
  files (checked with `Search(mode: text)`).

### Step 6 - LIVE verification
- Files: none changed. Results recorded in this plan (a results section) and linked from the new run folders.
- Change: after checking `http://192.168.1.113:1234/api/v1/models` (the expected model is loaded) and building
  and restarting a server that contains Steps 2-5 (see `docs/current/references/reference_live_server_freshness.md`
  for what `Build` outputs and when the running server is stale), run LIVE, 10 samples each, through
  `scripts/roslynsentinel-evalqueue.ps1`: `RenameSymbolNoSuchMethod`, `ReplaceSnippetWrongFileNearMatch`,
  `ReplaceSnippetWrongFileNoMatch`, the stop-adherence family (`StopAdherenceMissingFile`,
  `StopAdherenceMissingMethod`, `StopAdherenceControl`, defined by `plan_scenario_eval_followups.md` Step 1), and
  the seeded `RenameSymbolTypo` (`plan_scenario_eval_followups.md` Step 6). Record per scenario: passed of 10,
  failure class, run id and the commit stamp.
- Done when: the table below is filled and compared with the baselines.

  | Scenario | Baseline (2026-11-12, qwen3.5-9b-coder) | Target | After |
  | --- | --- | --- | --- |
  | `RenameSymbolNoSuchMethod` | 2/10 | at least 6/10 (Decision 6); no sample edits a file | recorded here |
  | `ReplaceSnippetWrongFileNearMatch` | 10/10 | stays 10/10, recovery via the candidate, not the listing order | recorded here |
  | `ReplaceSnippetWrongFileNoMatch` | 0/10 | at least 6/10 (Decision 6); no sample edits a different file or calls `CreateFile` | recorded here |
  | stop-adherence family and `RenameSymbolTypo` | baseline from `plan_scenario_eval_followups.md` Steps 2 and 4 | no regression; `RenameSymbolTypo` recovers via the candidate | recorded here |

  Numeric thresholds beyond "more than baseline" are not set (open, see Risks). A scenario that does not
  improve gets its transcript read (turn by turn, per the failure-analysis skill) before any further change is
  proposed; the doc then records which environment change is next, not a model-capability conclusion. Unit
  tests in Steps 2-5 prove the text, not the behaviour; only this step shows what the model does with it.

## Out of scope

- Changing any scenario, prompt, grader or stop wording; that is `plan_scenario_eval_followups.md`. Tools stay
  eval-agnostic.
- Zero-result answers that are not lookups failing (for example "no anonymous object found",
  `AdvancedRefactoringTools.cs:109`): they are not not-found errors in the CLAUDE.md sense unless Step 1
  reclassifies one.
- `OrientationCircuitBreaker` (`OrientationCircuitBreaker.cs`): it counts consecutive zero-match
  `SearchSolutionText` calls, trips at 10 (default) and also uses "You MUST call". Both facts are noted, neither
  is changed here; ten calls exceed the 12-turn scenario cap in practice, so it does not act inside these evals.
- Models other than the qwen3.5-9b-coder baseline; a second model is an optional follow-up, not a Done-when.
- The numeric error-code scheme and telemetry ([proposal_tool_error_code_taxonomy.md](../proposals/proposal_tool_error_code_taxonomy.md)); error codes are unchanged.

## Risks and open decisions

Open (not decided; do not route around):

1. **Similarity threshold for solution-wide use.** The `MemberNotFoundDetail` rule is tuned for the members of
   one file (a 4-character prefix or a subsequence of a 4+ character name counts as similar). Over every
   declared name in a solution it will produce noise (any `Appl*` name for `ApplyLoyaltyBonus`). Step 2 sets the
   rule with the negative test above; the real-solution noise level is unmeasured. Suggested measurement in
   Step 2: run the helper over this repository's symbol table for the two eval names and about 20 typos and
   record the false-candidate rate.
2. **How the per-candidate next call is prefilled.** `DocumentLookupResult.Describe` and `FileNotFoundMessage`
   do not know the calling tool, so the default is the neutral `ReadFile(filePath: <candidate>)`. The better
   call is the calling tool with `filePath` replaced (`ReplaceSnippet(filePath: <candidate>, ...)`), which needs
   an optional caller-tool parameter threaded through `Describe`/`GetDocumentOrThrow` (default null, so the 160
   sites compile unchanged). `NameTargetLookup.RequireSingle` already takes a `retryCall` delegate
   (`NameTargetLookup.cs`), a precedent. Decide after Step 3 whether the neutral call was enough in Step 6.
3. **Orientation call for a missing symbol with no file given.** No tool lists the members of a type without a
   file (`ListAll` has no file/container filter; `Member(view)` needs `filePath`). The solution-wide
   `ListAll(kind: ...)` can be very large on a real solution; whether to cap it, or to prefer a narrower
   `Search(mode: <kind>, query: <prefix>)`, is open and is a candidate tool gap if it matters (`TOOL-GAP:`
   report, not a brute-force workaround).
4. **Wire shape.** Plan default: text only, no wire change. `ToolNotFoundException` already has a
   `Candidates` property that surfaces as `StructuredDetail` (`ToolException.cs:65-102`, uses
   `AmbiguousCandidate`: FilePath, Line, Kind, Signature, ContainingType, ContextSnippet), which fits symbol
   candidates but not file candidates (no line). If `errorData` gains a new candidates field, follow the
   dto-conventions skill (NEW-DTO block) and `docs/current/references/reference_tool_response_pipeline.md`
   first; tools return DTOs and never measure or trim them.
5. **Does the 2(b) text suffice when the model has purpose-based evidence?** If a model has seen `CalcDisc` in
   a listing and the task describes it by purpose, a statement that no similar name exists may not stop an
   edit of `CalcDisc`. That is a property of the scenario prompt as much as of the tool text (see Problem);
   Step 6 shows which, and the answer is recorded, not assumed.
6. **Numeric Step 6 thresholds** are not set; the owner may want a target (for example 8 of 10) before the
   run.
7. **Docs-claim scope.** Rows 18, 21 (ScanTools) and 22 of the seed table were classified from preview lines;
   Step 1 corrects them.

Risks:

- **Error text grows (token cost).** Keep candidate lists to the top 3; the no-candidate text replaces, not
  adds to, the current "no file-name match ... You MUST call" paragraph. A hint that names a tool for each of
  three candidates costs three tool-call strings; if Step 6 shows that is too much, show one prefilled call and
  list the rest as names.
- **Wording pinned by tests and by one consumer.** Test assertions on the old text (cause 3, Step 4) change on
  purpose; `ScanTools.cs:1096` matches on message text and breaks silently if a producer is reworded (handled in
  5d, but a producer reworded earlier in the plan would regress it: do 5d's catch change before rewording the
  project-not-found literals it catches).
- **Edit-distance noise on short names** (open item 1); the helper never returns candidates for names of 1-2
  characters and caps at 3.
- **Hidden-scope mistakes.** The symbol builder must report the scope it actually searched (projects,
  test projects excluded or included); stating a wider scope than was searched would claim absence the search
  did not establish, which CLAUDE.md forbids.
- **Role boundaries for coverage.** The planner (this plan) defines what each step's tests must cover: the
  named consumer path, not only the builder. The reviewer confirms those paths are covered and that changed
  old assertions were not weakened. The implementer's job is a green build and the tests it is given; an
  all-green run with a builder-only test would leave rows 13-14 and 20-21 untested, and that would be a
  planner/reviewer gap, not an implementer failure.
- **Server freshness.** Steps 2-5 change `Common` and `Engines.*`; the running server is stale until rebuilt
  and restarted, so Step 6 must not run against an old binary (`isServerBinaryStale` in a response).
