# Reference: inventory of every not-found error producer and what orientation each gives

**Status:** CURRENT 2026-11-12. Five read-only survey passes plus two follow-up read passes that closed every "not read" row (all rows now read, a few helper bodies excepted, see Gotchas); message text was read from source; line numbers measured 2026-11-12 at workspaceVersion 9-33 while other sessions were editing `MemberRefactoringEngine.cs`, `DocumentLookup.cs`, `FileNotFoundMessage.cs` and `WorkspaceReadNavigationImpl.cs`, so re-measure before editing.

## Purpose

This is the Step 1 inventory of `plan_not_found_error_orientation.md` (it supersedes the plan's seed table,
rows 1-22, for rows 11-22 and the site counts; section 5 lists the corrections). Consult it to:

- decide which producer a slice touches and whether it is a lookup miss that needs candidates or only a
  reclassification (sections 6 and 7);
- answer "what does the agent get when a name does not resolve" without re-surveying (section 8);
- find the code that parses error text instead of codes before changing a message (section 4).

Each Step 5 slice of the plan updates its rows here (candidates, next call, prefill, rule items). To regenerate
the counts, use the commands in Usage; the table itself was built by reading each message, so a re-survey means
re-reading the sites, not only re-running the counts.

Provenance and limits: the survey was read-only and ran no tests, so nothing here says a message is covered by a
test. Every row has been read; the few helper bodies and base texts that were not opened are listed in Gotchas
("Residual unread"), and a row says so where it depends on one.

### Key for the table columns

The CLAUDE.md rule items, applied strictly:

- **S** = scope stated or ruled out (file / whole solution / project count / test projects).
- **N** = name-shape addressed (case, typo, exactness).
- **C** = container checked.
- **K** = a declaration of another kind with that name is considered. A message that only says "Class not
  found" does NOT meet K. Some survey reporters marked K merely because the message names the kind; those
  marks are written "-" here (see Gotchas, "K policy").
- **W** = what was searched is stated.
- **X** = a next call is named.
- **-** = none.

Other columns:

- **Cand** = the message lists or ranks similar names or items (`y` or `n`). A row whose note says
  `Conditional Cand.` lists candidates only on some paths (for example only when closest windows or a near-miss
  exist); it is counted `y`.
- **Next call named** = the message names a tool to call next, in a hint or in prose (`y` or `n`; a tool named only
  in prose counts `y`).
  "Pass one of those as containerName" is a parameter instruction, not a call, and counts as `n`.
- **Prefill** = request values (filePath, name, query) are put into the suggested call: `y` (request values
  inserted), `part` (only a fixed argument such as `kind: all` or `mode: symbol`, or literal placeholders such as
  {file}), `n`. Echoing the request value in the sentence is not prefill.
- **Non-miss.** A note starting `Sites N. Non-miss.` marks a row that is not a lookup miss (an ineligible,
  precondition, zero-result, ambiguous or internal condition worded or coded as not-found). Those rows need a
  reclassification, not candidates; section 8 counts them separately.
- **Note** starts with `Sites N.` (the number of raising places the row stands for) and then the detail.

## How it works

### 1. What produces a not-found error today

Not-found reaches the agent by five routes, measured 2026-11-12 (section 2 has the counts):

1. `new ToolNotFoundException(...)` thrown in an engine or tool (291 non-test sites; the exception's code is
   `ToolErrorCode.NotFound`, `RoslynSentinel.Common/ToolException.cs:67`).
2. A result record whose error code is `ToolErrorCode.NotFound` (70 hits, of which most are producers in
   engines and tools; in Common only `ToolException.cs:67` produces, the other 9 are mappers, consumers or comments).
3. The shared file lookup: `DocumentLookup.RequireDocument` / `GetDocumentOrThrow` then
   `DocumentLookupResult.Describe` then `FileNotFoundMessage.Build` (157 call sites outside Common). This is
   the only path that already carries candidates for a file miss.
4. `EngineErrorCode.SymbolNotResolved` from the docCommentId resolver in `PersistentWorkspaceManager.cs`
   (2 producers, 3 consumers).
5. Specialised builders that produce rich text: `SyntaxTargetResolver.BuildContainerMissDetailAsync`,
   `WorkspaceReadNavigationImpl.BuildMethodNotFoundErrorAsync`, `MemberNotFoundDetail.BuildAsync`,
   `SnippetErrorFormatter`.

A sixth, non-throwing route exists for git (`GitErrorCodes.GitPathNotFound` / `GitTargetNotFound` mapped to
`ResultStatus.NotFound`, `GitImpl.cs:92`); it is its own family and is listed in the table (row TB-T9).

### 2. Site counts per pattern (measured 2026-11-12)

| Pattern | Count | Breakdown |
| --- | --- | --- |
| `new ToolNotFoundException(` | 291 non-test | Engines.Basic 140 (MemberRefactoringEngine 50, other A-M files 31, N-Z files 59: ProjectStructureEngine 13, SymbolNavigationEngine 16, StructuralRefinementEngine 10, SyntaxUpgradeEngine 9, ThreadSafetyEngine 6, SemanticRefactoringEngine 4, SyntaxTargetResolver 1); Engines.Advanced 136 (AdvancedStructuralEngine 37, AsyncOptimizationEngine 22, AntiPatternEngine 13, LogicOptimizationEngine 11, CodeGenerationEngine 10, ModernizationEngine 9, InstrumentationEngine 6, TestingEngine 6, DependencyInjectionEngine 4, MappingEngine 4, AdvancedRefactoringEngine 3, ApiGenerationEngine 2, CodeStyleEngine 2, DocumentationEngine 2, SecurityAndSafetyEngine 2, CodeHealingEngine 1, ControlFlowEngine 1, DeadCodeEngine 1); Common 13 (SnippetErrorFormatter 8, PersistentWorkspaceManager 3, DocumentLookup 2); Tools.Basic 2 (WorkspaceFileEditImpl 2); Tools.Advanced 0 |
| `RoslynSentinel\.Common\.ToolNotFoundException\(` | 2 | the qualified-name sites the literal misses: `MemberRefactoringEngine.cs` ~4142, ~4200 |
| `ToolErrorCode\.NotFound` | 70 | Engines.Basic 20 (MemberRefactoringEngine 3 at ~929/1433/5512 `BatchEditFailure`; NamedArgumentsEngine 3, NullForgivingRemover 1, SemanticReplaceEngine 1, SymbolNavigationEngine 1, TestCategoryTaggingEngine 1, TestInstanceLauncher 3, others in A-M 7); Engines.Advanced 5 (OutParamRefactoringEngine 2, AsyncBatchEngine 1, TestingEngine 1, CodeGenerationEngine 1 result); Common 10 (6 files; only `ToolException.cs:67` is a producer); Tools.Basic 22 (AdminTools 4, WorkspaceReadNavigationImpl 4, DocumentationTools 3, SymbolNavigationImpl 3, WorkspaceFileEditImpl 3, WorkspaceProjectManagementImpl 3, RefactoringSignatureImpl 1, WholeFileWriteTools 1); Tools.Advanced 11 (AdvancedRefactoringTools 5, GenerationTools 3, IntelligenceTools 1, ScanTools 1, SubAgentChildServerLauncher 1); Server.Advanced 1; Tools.Experimental 1 |
| `FileNotFoundMessage\.Build` | 3 outside its own file | Common `DocumentLookup.cs:120`; Tools.Basic `WorkspaceFileEditImpl.cs:545`, `WorkspaceReadNavigationImpl.cs` ~75-80; 0 in Engines.*. All live callers use the `FileCandidate` overload; the obsolete string-list overload has 0 callers and `FindClosestPaths` has 0 callers (v33; another session is migrating) |
| `"FileNotFound"` string code | 3 producers, 1 consumer | producers Tools.Basic `WorkspaceFileEditImpl` 545, 557; `WorkspaceReadNavigationImpl` ~80; consumer Common `NotFoundEvidence.cs:44` |
| `"MethodNotFound"` string code | 2 producers, 1 consumer | producers `WorkspaceReadNavigationImpl` 186, 318; consumer `NotFoundEvidence.cs:44` |
| `"SyntaxRootNotFound"` | 2 | `WorkspaceReadNavigationImpl` 274, 401 (not a `ToolErrorCode` value) |
| `EngineErrorCode\.SymbolNotResolved` | 5 | 2 producers in `PersistentWorkspaceManager.cs` (3564 shape check, 3577 no longer resolves); 3 consumers or mappers (`RefactoringSignatureImpl.cs:117`, `WorkspaceProjectManagementImpl.cs:918`, `DiscoveryEngine.cs:542`, which rewraps as ToolNotFound; a non-SymbolNotResolved code there becomes a raw `InvalidOperationException`) |
| `DocumentLookup\.RequireDocument\|GetDocumentOrThrow` | 157 call sites outside Common | Engines.Basic 88 (RequireDocument 85 + GetDocumentOrThrow 3; MemberRefactoringEngine 30 at lines 191, 323, 423, 557, 623, 908, 1148, 1197, 1258, 1402, 1577, 1630, 1681, 1731, 1806, 1884, 1934, 1994, 2219, 2279, 2316, 2434, 2509, 2585, 4496, 4548, 4599, 5390, 5437, 5490; BasicRefactoringEngine 19+, SyntaxUpgradeEngine 11, SymbolNavigationEngine 10, MsToolAugmentEngine 7, others); Engines.Advanced 69 (25 in the 15 smaller files); Tools.Basic 0; Tools.Advanced 0; Common 5 (definitions and internal: `RequireDocument` def `DocumentLookup.cs:267`, `FindDocumentOrNull` 275, `GetDocuments` call 413, message 423). The plan said 160 |
| Case-insensitive `not found\|was not found` outside constructors | counts include comments | Engines.Basic MemberRefactoringEngine 59 (4 "was not found": 575, 1165, 4142, 4200); Common 20 across 11 files; Tools.Basic 22 across 8 files; Engines.Advanced 155; Tools.Advanced 10; Tools.Experimental 13; Server.Advanced 0. Repo-wide unfiltered 398 |

Row counts in the inventory table (section 3) do not reconcile to these regex counts one for one: the table
adds `Refuse(` producers, result-error producers and `ToolInvalidArgumentException` sites with not-found wording,
and it counts the 157 shared `RequireDocument` call sites once (row C1) instead of listing them.

### 3. Inventory table

Grouped by project then file; one row per site or per group of identical-text sites. Row ids: C = Common,
TB = Tools.Basic, E = Engines.Basic (MRE = MemberRefactoringEngine, BRE = BasicRefactoringEngine,
SN = SymbolNavigationEngine, PS = ProjectStructureEngine, SR = StructuralRefinementEngine, SRF = SemanticRefactoringEngine,
SU = SyntaxUpgradeEngine, TS = ThreadSafetyEngine, STR = SyntaxTargetResolver; the other E- ids abbreviate the like-named
engine in the Tool/op column), A = Engines.Advanced, TA = Tools.Advanced, TX = Tools.Experimental,
SV = Server.Advanced.

#### Common

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| C1 | Shared file lookup (Member, ReplaceSnippet and every `RequireDocument` op) | `DocumentLookup.cs:96` (GetDocumentOrThrow), `:124-129` (ToResultError) to `FileNotFoundMessage.cs:60-103` (branches `:39`, `:43` outside-solution and no-match, `:72` candidates, `:91`, `:101-102`; `DeclaringTypeSentence` `:134-151`) | y | y | part | S, N (casing), C (partial: files declaring a type named like the file stem), W, X | Sites 157 call sites outside Common (Engines.Basic 88, Engines.Advanced 69). Candidates: same-name files, did-you-mean with a `ReadFile` call per candidate. Next calls: `ListSolutionItems(kind: all)` and `CreateFile` (callable-gated), "You MUST call". Prefill is `kind: all` plus the per-candidate `ReadFile`. Engines.Advanced `RequireDocument` passes no `isCallable`, so it names `ListSolutionItems` / `CreateFile` unconditionally (hypothesis, not traced). |
| C2 | Project miss in `GetDocuments` | `DocumentLookup.cs:423` (call `:413`) | y | n | n | S, W | Sites 1. "Project '{key}' was not found in the loaded solution. Projects: {names}." All project names, unranked. |
| C3 | docCommentId shape check (SymbolNotResolved) | `PersistentWorkspaceManager.cs:3564-3565` | n | n | n | N (example only), W | Sites 1. "is not a documentation-comment ID ... for example 'T:MyApp.Orders.OrderService'". The survey reads the `Search(mode: symbol)` mention as an example, not a call; the plan seed row 5 says it names the call (see section 5). Re-verify. |
| C4 | docCommentId no longer resolves (SymbolNotResolved) | `PersistentWorkspaceManager.cs:3577-3578` | n | y | part | S, X | Sites 1. Prefill is a fixed argument only: the hint is `ToolCallHint.Call("Search", ("mode","symbol"))`, no query (spot-check). (Surfaced by RenameSymbol `RefactoringSignatureImpl.cs:117`, DeleteUnusedSymbol `WorkspaceProjectManagementImpl.cs:918`, PreviewRenameImpact `DiscoveryEngine.cs:542` rewrap). "Symbol '{id}' no longer resolves - may have been renamed, moved, or removed. Re-run Search(mode: symbol)." The id is echoed only. |
| C5a | Solution path resolve | `PersistentWorkspaceManager.cs:427` | y | n | n | S, W | Sites 1. "Could not resolve relative solution path ... Tried: ..." The tried-paths list. |
| C5b | LoadSolution load failure under NotFound code | `PersistentWorkspaceManager.cs:915`, `:983` | n | n | n | - | Sites 2. Non-miss. "Solution '{path}' failed to load: ..." A load failure, not a lookup miss (misclassified). |
| C6a | Snippet match: empty snippet | `SnippetMatching/SnippetErrorFormatter.cs:122` | n | n | n | S, W | Sites 1. "nothing was searched. No candidates exist". |
| C6b | Snippet match: no candidate | `SnippetErrorFormatter.cs:169` (BuildNoMatch) | n | n | n | W | Sites 1. "re-read the file and check the file path" (prose only). |
| C6c | Snippet match: closest windows | `SnippetErrorFormatter.cs:180` | y | n | n | W, X (partial) | Sites 1. Window line and score; "copy verbatim". |
| C6d | Snippet match: selector mismatch | `SnippetErrorFormatter.cs:333` (BuildSelectorMismatch) | y | n | y | S, C, W | Sites 1. "was found (N), but not at the selector you supplied"; per-candidate matchLine / withinMember; prefills the selector values. |
| C6e | Snippet match: adjacent-line mismatch | `SnippetErrorFormatter.cs:355` (BuildAdjacentLineMismatch) | y | n | y | S, N, W | Sites 1. lineBefore / lineAfter per candidate. |
| C6f | Snippet match: invalid disambiguator | `SnippetErrorFormatter.cs:468`, `:474`, `:479` (BuildInvalidDisambiguator) | n | n | n | - | Sites 3. Non-miss. InvalidArgument-type text under the NotFound code (misclassified). |

#### Tools.Basic

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| TB-T1a | File miss in WorkspaceFileEditImpl (ReadFile via `BuildFileNotFoundError`, ReplaceSnippet single, ReplaceSnippetBatch) | `WorkspaceFileEditImpl.cs:544-545` (also `:695`), `:1229` (`lookup.ToResultError`), `:1551` (`lookup.Describe()`) | y | y | part | S, N, W, X | Sites 4. Same text as C1. |
| TB-T1b | ReadFileWithoutSolution | `WorkspaceFileEditImpl.cs:557` | n | y | n | S, X | Sites 1. "'{file}' does not exist at '{absolutePath}'" plus `SolutionNotLoadedMessage` (`SolutionLoadState.cs:75-151`: "No solution is loaded. Call LoadSolution with a .sln, .slnx, or .csproj path."; the LoadCancelledAfterTimeout variant prefills `timeoutSeconds`). |
| TB-T1c | ReplaceSnippet on an empty file (`DescribeEmptyFile`) | `WorkspaceFileEditImpl.cs:1175` (helper `:927-945`) | n | y | part | S, X | Sites 1. Non-miss. "'{relativePath}' is empty, so there is no text for oldContent to match." Names `UndoLastApply` / `CreateFile` / `WriteFile(operation: ReplaceFile)`, callable-gated. Not a lookup miss. |
| TB-T1d | Rethrow with rejected-origin and rewrite notes | `WorkspaceFileEditImpl.cs:1311` (rejected-origin note; `:1278` is a comment), `:1316` (rewrite note), `:1614-1621` (batch per-edit catch; TODO at `:1614`) | n | n | n | S (file:line only, rewrite note) | Sites 3. Appends the rejected-origin note ("This oldContent comes from your rejected edit's newContent ... read it again before editing", no call) and the `RewrittenLineMemory.Explain` note (`RewrittenLineMemory.cs:103-135`: "F:N was rewritten by S earlier: it now reads 'X'." up to 3, then "Re-read the file or use the new text."; prose, no call). The batch catch appends both to "batchEdits[i] (F): " plus the base message, which comes from `ResolveEditMatch` to `SnippetErrorFormatter.Build` (the C6 family). `:1598` is a separate InvalidArgument branch (`DescribeLeakedNewContentMarkup`, not a not-found; helper body not read). |
| TB-T2a | File miss in ReadFile / GetMethodSource / GetFileOutline (`BuildFileNotFoundError`) | `WorkspaceReadNavigationImpl.cs` ~74-80 (callers ~259/264, 386/391; moved at v33) | y | y | part | C, N, W, X | Sites 1 (4 callers). Same text as C1 through a duplicate builder. The plan expects S; the survey marked C instead (not reconciled). |
| TB-T2b | GetMethodSource: method not in file (`BuildMethodNotFoundErrorAsync`, code `MethodNotFound`) | `WorkspaceReadNavigationImpl.cs:87-186` | y | y | part | S (tests included), N, K, W, X | Sites 1. "Method or constructor '{name}' not found in '{file}'", qualified-name hint, kind mismatch ("exists as a property/field/event/local function, not a method; read it with Member(operation: view, memberName)"), top-5 solution declarations ("declared in"), `Search(mode: method, query: name, exactMatch: false)` retry. Retry args: GetMethodSource template n; Search method y; Member view y. The best message in Tools.Basic. |
| TB-T2c | GetMethodSource: container miss | `WorkspaceReadNavigationImpl.cs:308-318` to `SyntaxTargetResolver.BuildContainerMissDetailAsync` (`SyntaxTargetResolver.cs:837`, core ~263-349, `BuildContainerNotFoundMessageAsync` 151-200) | y | y | part | S, N, C, W, X | Sites 1. Prefill is template placeholders only ({file}, {member}, {container}); request values are not inserted (spot-check). Whether the ReadFile line-range hint carries request values was not re-read. "Type 'C' declares no method or constructor named 'M' (searched the whole file; type arguments optional, case ignored)"; declared-in list (max 10, with lines); `GetFileOutline` when none; retry template max 3; ReadFile line range. Survey marked K; not carried (kind named in the text, no other-kind lookup shown). |
| TB-T2d | SyntaxRootNotFound | `WorkspaceReadNavigationImpl.cs:274`, `:401` | n | n | n | - | Sites 2. Non-miss. Not a `ToolErrorCode` value; an internal read failure. |
| TB-T2e | PageEnvelope: no pageable node | `WorkspaceReadNavigationImpl.cs:1769` | n | n | n | S | Sites 1. |
| TB-T2f | PageEnvelope: path miss | `WorkspaceReadNavigationImpl.cs:1781-1782` | y | y | part | S, N, W, X | Sites 1. "Path '{p}' was not found in the stored result. Pageable paths: {list}" plus `LargeResultHints.FetchCall`. |
| TB-T2g | Stored result from older server | `WorkspaceReadNavigationImpl.cs:1886` | n | n | n | - | Sites 1. "repeat the original call" (prose). |
| TB-T2h | GetLargeResult: file not found | `WorkspaceReadNavigationImpl.cs:1952` | n | n | n | W | Sites 1. "Result file not found. Supply a valid resultId or filePath ..." (`:1943` is SolutionNotLoaded, not NotFound.) |
| TB-T3a | Search(mode: symbol) / LocateSymbol | `SymbolNavigationImpl.cs:55-80` (message `:75`; hidden-test note `:62-70`) | n | y | n | S (test projects), N (advice only), W, X | Sites 1. "Symbol 'X' not found in the solution (project: P). Try exactMatch=false ... verify the symbol name and symbolKind, or call ListAll". The `exactMatch=false` advice is substring-only and cannot match a typo. Survey marked K; not carried (kind named). |
| TB-T3b | InspectSymbol: unresolved | `SymbolNavigationImpl.cs:131` | n | y | n | N, W | Sites 1. Generic unresolved text; names `GetMethodSource` / `GetFileOutline` in prose. Survey also marked K; not carried. |
| TB-T4 | RenameSymbol: missing parameter list (`MissingParameterListResolver`) | `RefactoringSignatureImpl.cs:85-111` (`:117` maps SymbolNotResolved to NotFound = C4) | y | y | y | N (parameter list), X | Sites 1. Overload ids; the retry is the same tool with the full id. |
| TB-T5a | ListSolutionItems: project miss | `WorkspaceProjectManagementImpl.cs:90` | n | n | n | S (partial) | Sites 1. "Project '{projectName}' not found." |
| TB-T5b | ListWorkspaceSolutions: directory miss | `WorkspaceProjectManagementImpl.cs:208` | n | n | n | - | Sites 1. Non-miss. "Directory not found" under code InvalidArgument (misclassified). |
| TB-T5c | LoadSolution failure catch | `WorkspaceProjectManagementImpl.cs:765` | n | y | n | X | Sites 1. NotFound adds `ToolCallHint.Next` `ListWorkspaceSolutions` (no args); otherwise "Check the server log..." plus `LoadSolution(forceReload: true)`. (`:918` DeleteUnusedSymbol is the C4 mapper.) |
| TB-T6a | ExternalFileDrift Acknowledge: names match no tracked change | `AdminTools.cs:159`, `:164` | y | y | part | S, N, X | Sites 2. "Nothing cleared: '{names}' match no tracked change. Tracked changes: ... Call ExternalFileDrift(operation: List)". Names echoed at `:159` only. |
| TB-T6b | Ledger op: no ledger open | `AdminTools.cs:273` | n | n | n | S | Sites 1. "Nothing changed: no ledger is open." |
| TB-T6c | Ledger op: unknown entry ids | `AdminTools.cs:294` | n | y | part | S, X | Sites 1. "unknown entry id(s) ... Call {listCall}"; ids echoed; the call text is a variable. |
| TB-T7a | ProjectDoc: DocReadResult mapping fallback | `DocumentationTools.cs:15-30` | n | y | part | S (docType), X | Sites 1. The `Message` default at `:27` is only a fallback; the live text is `:601` (TB-T7d), used only when Error is null and Found is false (`:482`, migration-state.yaml). Other ProjectDoc misses (`:237`, `:263`, `:292`, `:308`) carry their own text. Zero-result: the file is absent. Prefill is the fixed `action: list` only. |
| TB-T7b | ProjectDoc: path-qualified miss | `DocumentationTools.cs:239-242` | n | y | part | S, N, W, X | Sites 1. "'{filename}' was not found under docType ... no basename fallback ... Call ProjectDoc(action: "list")". Prefill is the fixed `action: list` only; the name is echoed. |
| TB-T7c | ProjectDoc: stem miss | `DocumentationTools.cs:308` | n | y | part | S, N, W, X | Sites 1. "No file matching '{stem}' (with or without extension) was found under docType=...". The Ambiguous branch returns "Did you mean". Prefill is the fixed `action: list` only; the name is echoed. |
| TB-T7d | ProjectDoc: document miss | `DocumentationTools.cs:601` | n | y | part | S, X | Sites 1. "Document not found: '{name}' (docType ...). Use ProjectDoc(action: "list")". Prefill is the fixed `action: list` only; the name is echoed. |
| TB-T8 | DeleteFile: path does not exist | `WholeFileWriteTools.cs:139` | n | y | n | S, X | Sites 1. "'{path}' does not exist." plus a `Search(mode: "text")` hint; the pattern is not prefilled. |
| TB-T9 | Git: path / target not found (separate family) | `GitImpl.cs` sites 771-773, 815-817, 899-901, 1017, 1057-1058, 1404, 1422-1423, 1498-1500, 1883, 1897-1898, 3025-3026, 3043-3044, 3240 (codes `GitErrorCodes` 57, 71, mapped to NotFound at `:92`) | n | y | n | X (TargetNotFound only) | Sites 13. Hint `:115` names `GitRemote(operation: "tag")` for TargetNotFound; `:103` PathNotFound hint says to use repo-relative paths (prose). |

#### Engines.Basic - MemberRefactoringEngine.cs

Line numbers as of v12; sites after ~2431 had shifted +3 by the time of the later reads.

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| E-MRE1a | ConstructorParameter: class lookup | `MemberRefactoringEngine.cs:176`, `:441` | n | n | n | - | Sites 2. "Cannot edit: class not found." |
| E-MRE1b | ConstructorParameter: snippet not in a constructor | `:462` | n | n | n | - | Sites 1. "contextSnippet is not inside any constructor with parameter...". |
| E-MRE1c | ConstructorParameter: parameter miss | `:471` | n | n | n | - | Sites 1. "Constructor parameter 'p' not found on 'C'." |
| E-MRE1d | GetConstructorParameters: class miss | `:575` | n | y | y | S, W, X | Sites 1. "Class 'C' was not found in 'F'. Searched: class, interface, struct, record and enum declarations ... Next: GetFileOutline(filePath)". |
| E-MRE2a | ModifyEnum: enum miss | `:641` | n | n | n | - | Sites 1. "Cannot edit: enum 'E' not found." |
| E-MRE2b | ModifyEnum: member miss | `:823`, `:847` | n | n | n | C | Sites 2. "Cannot edit: member 'M' not found in enum 'E'." Names the enum. |
| E-MRE2c | AddEnumMember: insert anchor | `:789`, `:799` (`BuildInsertAnchorNotFoundMessage`, kind enum) | y | y | y | S, N, C, W, X | Sites 2. Updated 1df84f16 / fe4c572c: anchors ranked (similar first, cap 25), "Did you mean ... Retry with <param>" per candidate (top 3), the no-similar-name fact otherwise; `Member(operation: view)` with `containerName`. Facts and options only. |
| E-MRE3a | MethodSignature view | `:1165` | n | y | y | S, W, X | Sites 1. "Method 'M' was not found in 'F'. Searched: method declarations ... Next: Search(mode: text, query: M) or GetFileOutline(filePath)". |
| E-MRE3b | MethodSignature add / remove | `:1214`, `:1276` | n | n | n | - | Sites 2. "Cannot edit: method 'M' not found." Names the kind only. (`:1282` "has no parameters" is not a lookup miss.) |
| E-MRE4a | ChangeAccessibility, AddModifier, RemoveModifier, AddAttribute, ReplaceAttribute, RemoveAttribute | `:1594`, `:1647`, `:1698`, `:1780`, `:1851`, `:1917` | n | n | n | - | Sites 6. Six identical "Cannot edit: target not found." |
| E-MRE4b | ReplaceAttribute: attribute miss | `:1859` | n | n | n | - | Sites 1. "attribute 'A' not found on target." |
| E-MRE4c | Batch edit failures (`BatchEditFailure` NotFound) | `:929` (ApplyModifierBatch), `:1433` (ApplyAttributeBatch), `:5512` (ApplyBaseTypeBatch) | n | n | n | - | Sites 3. "batchEdits[i] (name): target not found." and "type not found." |
| E-MRE4d | Non-throw result messages | `:1713`, `:5464` | n | n | n | - | Sites 2. "// Cannot edit: modifier not found.", "// Cannot edit: base type not found." |
| E-MRE5a | Container not found (InsertMemberAfter, InsertMemberBefore, AddMember) via `SyntaxTargetResolver.BuildContainerNotFoundMessageAsync` (`SyntaxTargetResolver.cs:118-200`) | `:1952`, `:2012`, `:2452` | y | n | n | S, N, C, W | Sites 3. Updated 580e2a55 / fe4c572c (5a-2a): types ranked with `NameCandidates` (similar first, "Did you mean 'X'? Retry with containerName: 'X'." per candidate, cap 3); with none "No type declared in this file has a similar name" plus a prefilled `GetFileOutline(filePath)` when the path is known; up to 3 other files declaring the name. "Pass one of those as containerName" stays (parameter option). Tool-level tests in `MemberInsertAnchorTests` (typo type, unknown type). `ComposeFailure` now keeps kind, name, snippet and candidates. |
| E-MRE5b | Insert anchor not found, container kind | `:2091` (`ResolveInsertAnchorIndex`; builder `:2055-2067`) | y | y | y | S, N, C, W, X | Sites 1. Updated 1df84f16 / fe4c572c (5a-2b): same as E-MRE2c; tool-level tests `Member_AddWithTypoAnchor_...` and `...NoSimilarMember...` in `MemberInsertAnchorTests`. |
| E-MRE5c | SortMembers: container miss | `:2225` | n | n | n | - | Sites 1. "Cannot edit: container not found." |
| E-MRE6a | AddRemoveParams: method miss | `:2285` | n | y | y | X | Sites 1. "Method 'M' not found." plus `ToolCallHint.Next(GetFileOutline)` with filePath. |
| E-MRE6b | ReplaceMember (`:2338`) and RemoveMember (`:2670`) via `MemberNotFoundDetail.BuildAsync(includeHead: true)` | `:2338`, `:2670` (`MemberNotFoundDetail.cs:41-117`, similarity `:196-252`) | y | y | y | S, N, C, K, W, X | Sites 2. Top 3 similar, same-file members grouped by kind, other-file declarations; `ReadFile` / `GetFileOutline` (and `ReadFile(memberName)` for another file); prefills filePath and memberName. Updated e7446193 / fe4c572c (5a-1): a prefilled `ReadFile(filePath, memberName[, containerName])` per candidate; with no candidate "No member of <scope> has a similar name" and the does-not-exist fact. Tests in `MemberNotFoundDetailTests` (shared member read). |
| E-MRE6c | RemoveMember: type-level declaration | `:2664` | n | y | n | K, X | Sites 1. "'M' is a type-level declaration (kinds) that Member(remove) could not delete here..." Names `ModifyEnum` / `DeleteUnusedSymbol` / `ReplaceSnippet` / `ApplyDiff`, callable-gated. |
| E-MRE7a | AddTopLevelType: namespace miss | `:2538` | y | n | n | W (partial) | Sites 1. "Namespace 'N' not found. Available: ..." All namespaces, unranked. |
| E-MRE7b | AddTopLevelType: ambiguous namespace | `:2547` | y | n | n | - | Sites 1. Non-miss (ambiguous, coded NotFound: code mismatch). "File has N namespaces (...); pass namespaceName to disambiguate." Candidates unranked; the instruction is a parameter, not a call. |
| E-MRE8a | MoveMember: hand-rolled file / syntax-root / class lookups (bypass `DocumentLookup`, no bare-file-name rule) | `:2980` ("File 'F' not found."), `:2986` ("Failed to get syntax root"), `:2992` ("Class 'C' not found in 'F'.") | n | n | n | - | Sites 3. |
| E-MRE8b | MoveMember: not an existing base class | `:3140` | n | n | n | C | Sites 1. Non-miss. "Cannot move instance member(s) ... not an existing base class" (prose fix; not a lookup miss). |
| E-MRE8c | MoveMembersToBaseType and target resolution | `:3596` (external assembly), `:3602` ("Base class source file not found."), `:3608` ("Base class source document not found at 'F'."), `:3614` (syntax root), `:3620` ("Base class 'B' not found in 'F'."), `:3718` ("Could not resolve target class 'T' to rewrite call sites against it.") | n | n | n | - | Sites 6. |
| E-MRE8d | MoveInstanceMembers: qualified-ctor class miss | `:4142`, `:4200` | n | n | n | S | Sites 2. "Class 'C' was not found in the file." (qualified `RoslynSentinel.Common.ToolNotFoundException(`). |
| E-MRE8e | PreviewInstanceMoveCallSites | `:4950` ("File 'F' not found."), `:4956` (syntax root), `:4962` ("Class 'C' not found in 'F'."), `:4971` (semantic model) | n | n | n | - | Sites 4. |
| E-MRE9 | ConvertMethodToProperty, MakeMethodStatic, AddBaseType, RemoveBaseType | `:4536`, `:4556` ("Method not found."), `:5408`, `:5455` ("Type not found.") | n | n | n | - | Sites 4. |
| E-MRE10 | Syntax-root group (ToolInvalidArgumentException with "syntax root not found" wording; not NotFound) | `:207`, `:330`, `:429`, `:914`, `:1203`, `:1265`, `:1408`, `:1582`, `:1636`, `:1687`, `:1890`, `:5395`, `:5442`, `:5496` | n | n | n | - | Sites 14. Non-miss. Wording says not found; code is InvalidArgument. |
| E-MSEL | MoveMember members (`MemberSelection.SelectMembers`) | `MemberSelection.cs:287`, `:295` | y | y | part | S, N, W, X | Sites 2. Updated 12a0cc97 / fe4c572c (5a-3): found list ranked (similar first), "Did you mean 'X' for '<entry>'?" per missing entry with one prefilled retry (`MoveMember(memberNames: [...])`), with no candidate "No member of class 'C' has a similar name". `// TODO` x2: no `filePath` reaches `SelectMembers`, so no `GetFileOutline` prefill. Tests: `MoveMemberNotFoundTests` (MoveMember tool). |

The 30 `RequireDocument` sites in MemberRefactoringEngine are part of C1.

#### Engines.Basic - other A-M files

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| E-BRE1 | ChangeSignature (a `Refuse()`, not a throw) | `BasicRefactoringEngine.cs:251` | y | y | part | S, N, W, X | Sites 1. Updated 9c5fbc1c / fe4c572c (5b-1): original sentence kept, plus `DeclarationNotFoundDetail.Build` (ranked method and constructor names, "Retry with methodName: 'X'", or the no-similar-name fact and a prefilled `GetFileOutline`). Tool-level tests in `BasicRefactoringNotFoundTests`. (`:248` primary-constructor refusal is not a lookup miss.) |
| E-BRE2 | MoveAllTypesToFilesInProject: project miss | `:1452` | y | n | n | S, N, W | Sites 1. Updated 2f60a0a2 (5d-3a): `ProjectNotFoundDetail.Build` (count fact, "Did you mean ... Retry with projectName", or the no-similar-name fact, loaded project list similar-first, cap 10). Tool-level tests (`ProjectNameMissTests`, MoveAllTypesToFilesTool). |
| E-BRE3 | ExtractInterface: class miss | `:1500` | y | y | part | S, N, W, X | Sites 1. Updated 9c5fbc1c (5b-1): ranked class names or the no-similar-name fact plus prefilled `GetFileOutline`. Tool-level tests via `ExtractMembersTool(newType: interface)` in `ExtractInterfaceNotFoundTests` (0e556f10). |
| E-BRE4 | Indexer miss | `:1751` | n | y | y | S, W, X | Sites 1. Updated 9c5fbc1c (5b-1): "No indexer is declared in 'F'." plus a prefilled `GetFileOutline`. An indexer has no name, so no candidate case. ConvertIndexerToMethod has no tool: the test (0e556f10) is engine-direct. |
| E-BRE5a | Snippet lookup (extract local / constant): no match | `:1795` | y | n | n | W | Sites 1. Conditional Cand (candidates only when closest windows exist). Text comes from `SnippetLookup.TryResolve` then `SnippetErrorFormatter.Build` (the C6 family), not `ContextHelper`; the "?? Context snippet not found." fallback at `:1795` is unreachable. Ambiguous snippet outcomes under `TryResolve` are non-miss and coded InvalidArgument at these sites. |
| E-BRE5b | Snippet lookup (extract local / constant): no match with advice | `:1873` | y | y | n | W, X | Sites 1. Conditional Cand. Same text as E-BRE5a plus " Re-check the snippet against GetMethodSource/GetFileOutline output, or add lineBefore/lineAfter ... to disambiguate." (tools in prose, no args). |
| E-BRE5c | Extract local / constant: ineligible or internal conditions | `:1802`, `:1808`, `:1898`, `:1905`, `:1912`, `:1918`, `:1986` | n | n | n | - | Sites 7. Non-miss (ineligible or internal; TargetIneligible). "literal not found", "containing type not found", "contextSnippet was found at line N, but it does not align to a single whole extractable expression", "containing method / statement / block not found", "statement index not found". |
| E-BRE6 | AnalyzeControlFlow, AnalyzeDataFlow, GetSummaryComment ("Searched:" literals) | ~`:2079-2080` (spot-check: AnalyzeControlFlow throw is at `:2079`), ~`:2122`, ~`:2525` | y | y | y | S, N, W, X (updated ee2edf36, 5b-2: ranked names appended via `DeclarationNotFoundDetail.Build(includeOutlineCall: false)`; tool-level tests in `BasicRefactoringNameMissTests` through `ScanTools.AnalyzeMethodTool` and `SummaryCommentTool`) | Sites 3. "Method 'M' not found in 'F'. Searched: method declarations in the file by name. Next: GetFileOutline / Search(mode method)"; `:2525` "Target 'T' not found in 'F'. Searched: symbols named 'T' in the file. Next: Search(mode symbol) or GetFileOutline". Prefills filePath and query. No N. |
| E-BRE7 | Using directive miss | `:2277` | y | y | part | S, N, W, X | Sites 1. Updated 9e213c6b (5b-3): sentence kept plus ranked using names ("Retry with namespaceName: 'X'") or the no-similar-name fact, or "No using directive is declared in 'F'" plus `GetFileOutline`. Tool-level tests through `UsingDirectiveTool`. |
| E-BRE8 | Add / RemoveSummaryComment | `:2336`, `:2481` | y | y | part | S, N, W, X | Sites 2. Updated 9e213c6b (5b-3): sentence kept plus ranked declaration names (shared `DeclarationNamesIn`) or the no-similar-name fact with `GetFileOutline`. Tool-level tests through `SummaryCommentTool` add and remove. |
| E-BRE9a | WrapInTryCatch: no block / statements in line range | `:2567`, `:2573` | n | y | n | X | Sites 2. "Could not find a suitable block ... use GetFileOutline to find the lines." and "Could not find any statements in the specified range ... use GetFileOutline"; no arguments. |
| E-BRE9b | WrapInTryCatch: snippet variants | `:2642`, `:2659` | n | y | n | S, X | Sites 2. "... use GetFileOutline". |
| E-BRE10 | UpdateXmlDocsFromSignature | `:2786` | y | y | part | S, N, W, X | Sites 1. Updated ee2edf36 (5b-2): "Method 'M' not found in 'F'." plus ranked method names or the no-similar-name fact with `GetFileOutline`. Test is engine-direct: its only host tool (`CodeTransformTools.ApplyMethodCodeTransform`, Tools.Experimental) has no test factory. |
| E-BRE11 | Syntax-root group (InvalidArgument) | `:2199`, `:2269`, `:2325`, `:2470`, `:2514`, `:2553`, `:2624` | n | n | n | - | Sites 7. Non-miss. Not-found wording under InvalidArgument. |
| E-DISC1 | DiscoveryEngine.FindProject | `DiscoveryEngine.cs:238` | n | n | n | S, W (partial) | Sites 1. "Project 'P' not found in solution." (the phrase `ScanTools` matches on; section 4). |
| E-DISC2 | DiscoveryEngine file misses | `:400`, `:557` | n | n | n | - | Sites 2. "File not found: {f}". |
| E-DISC3 | DiscoveryEngine.RequireSingle | `:402` | n | n | n | - | Sites 1. "Type 'C' not found." |
| E-DISC4 | PreviewRenameImpact resolver rewrap | `:542-543` | n | y | n | S, X | Sites 1. SymbolNotResolved becomes `ToolNotFound(message)` with the C4 text; any other code becomes a raw `InvalidOperationException` (leak, section 6). |
| E-DISC5 | DiscoveryEngine: project miss | `:609` | n | n | n | S, W (partial) | Sites 1. "Project 'P' not found in the loaded solution." |
| E-DIAG | DiagnosticEngine | `DiagnosticEngine.cs:38` ("File not found: {f}"), `:53` ("Project not found.") | n | n | n | - | Sites 2. |
| E-CONSTR | ConstructionReplacer | `ConstructionReplacer.cs:46`, `:84` | n | y | n | S (partial), X | Sites 2. "symbol: no symbol found for docCommentId '{id}'. {SymbolHint}"; `:84` is the factory variant. Names `Search(mode symbol)` / constructor hint. |
| E-INV1 | InventoryEngine: file not in solution | `InventoryEngine.cs:36`, `:64` | n | n | n | S, W | Sites 2. "File not found in solution: {p} (existsOnDisk, projectsLoaded=N). The owning project may have failed to load..." (the phrase `ScanTools` may match on; section 4). |
| E-INV2 | InventoryEngine: syntax root | `:43`, `:71` | n | n | n | - | Sites 2. Non-miss. Raw `InvalidOperationException` "Syntax root not found." (not a ToolException). |
| E-IMP1 | ImpactAnalyzer: document | `ImpactAnalyzer.cs:39` (no code, surfaced as NotFound), `:201` (ToolNotFound) | n | n | n | - | Sites 2. "Document not found: {f}". |
| E-IMP2 | ImpactAnalyzer: no symbol at position | `:73`, `:226` | n | n | n | - | Sites 2. "No symbol found at the specified position ..." (reached by InspectSymbol blastRadius `SymbolNavigationImpl.cs:154`). |
| E-IMP3 | ImpactAnalyzer: symbol not declared | `:79` | n | n | n | N (advice only), W | Sites 1. "Not found: '{sym}' is not declared in this solution ... Pass the declared name with lineBefore/lineAfter" (advice, no call). |
| E-IMP4 | ImpactAnalyzer.GetDataFlow | `:153` (was `:152`) | n | n | n | - | Sites 1. "Document not found: {filePath}". |
| E-MSA | MsToolAugmentEngine | `MsToolAugmentEngine.cs:122` ("Field 'f' not found in file."), `:1261` ("Type 't' not found in file."), `:1474` ("No method named '{contextSnippet}' found in file."), `:1876` ("File not found: {f}") | n | n | n | S | Sites 4. At `:1474` the name slot holds the snippet (section 6). |
| E-CSW | ChangeSignatureWrapper | `ChangeSignatureWrapper.cs:30` | n | y | part | X | Sites 1. "could not resolve 'M' to a method or constructor symbol." then "Retry with" `ChangeSignature(oldSignature: "remove", ...)` (literal arguments). |
| E-NAE1 | NamedArgumentsEngine: project | `NamedArgumentsEngine.cs:144` | y | n | n | - | Sites 1. "No project named ..." plus the available list. |
| E-NAE2 | NamedArgumentsEngine: file / symbol | `:164`, `:194` | n | n | n | - | Sites 2. "File ... is not part of the loaded solution."; "No symbol found for targetSymbol docCommentId". |
| E-NFR | NullForgivingRemover | `NullForgivingRemover.cs:42` | n | y | part | S, W, X | Sites 1. "no symbol found for docCommentId 'X' in any project of the loaded solution ... (from Search(mode: symbol) or Search(mode: property))"; two Search calls, `mode` literal. |
| E-SRE1 | SemanticReplaceEngine: symbol | `SemanticReplaceEngine.cs:64` | n | n | n | - | Sites 1. "No symbol found for docCommentId". |
| E-SRE2 | SemanticReplaceEngine: document | `:1256` | n | n | n | - | Sites 1. "Document not found for file" under TargetIneligible (code and message disagree). |
| E-TCT | TestCategoryTaggingEngine | `TestCategoryTaggingEngine.cs:155` | y | n | n | - | Sites 1. "testScope names no project called ..." plus a list. |
| E-TIL1 | TestInstanceLauncher: precondition | `TestInstanceLauncher.cs:347` | n | n | n | - | Sites 1. Non-miss. No server dll / apphost; a precondition, misclassified. |
| E-TIL2 | TestInstanceLauncher: pid not tracked | `:423` | n | y | y | X | Sites 1. Names `McpServerControl` ListTestInstances. |
| E-TIL3 | TestInstanceLauncher: build skipped | `:535` | n | y | n | X (prose) | Sites 1. "build Skip but no output"; names Build / LaunchTestInstance in prose. |
| E-TRE | TestRunEngine | `TestRunEngine.cs:136` | n | n | n | S | Sites 1. "Project ... was not found in the loaded solution." EngineError InvalidInput, not ToolNotFound. |

Not producers (verification strings, hints or warnings that contain the words): `ChangeSignatureCallBindingCheck.cs:172`,
`MoveMemberCallBindingCheck.cs:85,158,159`, `MovePositionMap.cs:569`, `CompilerErrorLookupHelper.cs:387`,
`DocTrailerEngine.cs:772`.

#### Engines.Basic - N-Z files

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| E-SN1 | SymbolNavigationEngine: method miss | `SymbolNavigationEngine.cs:1224`, `:1357` | n | y | y | S, N, X | Sites 2. "Method 'M' not found in 'F'. Ensure the method name exactly matches (case-sensitive). Next: Search(mode: method, query: M) or GetFileOutline(filePath: F)". |
| E-SN2 | Snippet no match, wrapped with a FindReferences next call | `:1569` (kind: callers), `:1925` (kind: implementations) | y | y | y | W (inherited), X | Sites 2. Conditional Cand (the C6 closest windows, when any). Snippet NoMatch text plus " Next: FindReferences(symbolName, kind: callers, filePath, contextSnippet: <decl line>)" (empty when the file holds no declaration); the declaration line is derived. Both catch only `ToolNotFoundException`, so ambiguous snippet outcomes propagate as `ToolAmbiguousMatchException`. Hypothesis: `FindReferences` declares contextSnippet schema-required (`SymbolRelationshipTools.cs:70`) while `FormatFindReferencesNextCall` (~`:2692`) omits it for type targets, so the rendered call may carry a placeholder. |
| E-SN3 | Snippet did not resolve | `:1575-1590` | y | y | y | S, X | Sites 1. "contextSnippet did not resolve to a symbol in 'F'. This is NOT a confirmed zero-references result ... WAS found declared at: line N" (up to 3, name-only candidates) plus a `FindReferences` call. |
| E-SN4 | symbolName not declared in filePath | `:1619-1622` | n | y | y | S, X | Sites 1. "symbolName 'X' was not found declared in 'F'. This is NOT a confirmed zero-references result ... Verify the name against GetFileOutline(filePath: F)". |
| E-SN5 | contextSnippet no match | `:1710-1716` | y | y | y | S, W, K, X | Sites 1. Up to 10 ranked candidates plus a count of the rest; `FindReferences` for the best candidate with the verbatim line; a type hint (K). |
| E-SN6 | Unresolved anywhere | `:1744-1749` | y | y | part | S, W, K, X | Sites 1. Conditional Cand. "symbolName X could not be resolved anywhere in the solution ... Verify via Search(mode: symbol)/GetFileOutline ... Did you mean: ...?" Candidate only when a near-miss exists; the generic call has a fixed mode, the near-miss call is request-derived. |
| E-SN7a | FindImplementations: snippet did not resolve | `:2068-2080` | y | y | y | S, W, K, X | Sites 1. "contextSnippet did not resolve ... candidate location(s)" plus `FindReferences(kind: implementations)`. |
| E-SN7b | FindImplementations: unresolved anywhere | `:2137-2145` | y | y | part | S, K, X | Sites 1. Conditional Cand. "could not be resolved anywhere in the solution ... NOT a confirmed zero-implementations result ... Verify via Search(mode: symbol)/GetFileOutline"; candidate only when a near-miss exists. |
| E-SN8 | VariableLifetimeReport | `:2295` | n | n | n | - | Sites 1. NotFound. |
| E-SN9 | Container not found | `:2869` | n | y | y | S, W, K, X | Sites 1. "Container 'C' was not found in 'F'. Searched: class, interface, struct, record and enum declarations matching this name ... Next: GetFileOutline(filePath: F)"; filePath and query prefilled. |
| E-SN10 | Container miss via `BuildContainerMissDetailAsync` | `:2946` | y | y | part | S, N, C, W, X | Sites 1. Same builder and text as TB-T2c, so the same placeholder-only prefill caveat. |
| E-SN11 | Member miss via `MemberNotFoundDetail.BuildAsync` | `:2966` | y | y | y | S, N, C, K, W, X | Sites 1. Member read behind `ReadFile(memberName)` / `Member(view)`; no "no similar name" sentence. |
| E-SN12 | Bare member miss | `:2976` | n | n | n | - | Sites 1. "Member 'M' not found". |
| E-SN13 | TypeHierarchyReport.Error (returned, not thrown) | `:3129` | n | n | n | S | Sites 1. "Type 'T' not found in solution." Callers of `TypeHierarchyReport.Error` were not read. |
| E-PS1 | ProjectStructureEngine: file misses | `ProjectStructureEngine.cs:23`, `:543` | n | n | n | - | Sites 2. "File not found: {f}". |
| E-PS2 | Outside project directory | `:30` | n | y | n | X | Sites 1. Non-miss (precondition). "File 'F' is outside the project directory ... Next: ListAll to confirm the owning project"; no args. |
| E-PS3 | No namespace declaration | `:68` | n | y | n | S, X | Sites 1. Non-miss (zero-result). "No namespace declaration found in 'F' ... Next: ReadFile"; no args. |
| E-PS4 | Project directory not found | `:77` | n | y | n | X | Sites 1. Non-miss (precondition). "Project directory not found for 'F'. Next: ListAll"; no args. |
| E-PS5 | Project / class / solution-path misses | `:385`, `:545`, `:1063`, `:1515`, `:1540`, `:1797`, `:1798`, `:1886` | n | n | n | - | Sites 8. `:385` "Project 'P' not found in the solution.", `:545` "Class not found.", `:1063` "Project ... not found in the loaded solution.", `:1515`/`:1540` "Project ... not found.", `:1797`/`:1798` "Solution path not found.", `:1886` "Source project not found." (`:62`, `:142` are `RequireDocument` = C1.) |
| E-SR1 | StructuralRefinementEngine: top-level type miss | `StructuralRefinementEngine.cs:55` | y | y | y | S, W, X | Sites 1. "No top-level type named 'T' found in 'F'. Top-level types present: ... Next: Search(mode: class, query: T)"; all top-level types, unranked; the query is a request value, the mode is fixed. |
| E-SR2 | No type declaration | `:65` | n | y | n | S, X | Sites 1. Non-miss (zero-result). "No type declaration found in 'F'. Next: ReadFile" with no args. |
| E-SR3 | File not in loaded solution | `:109`, `:168`, `:349` | n | y | n | S, X | Sites 3. "File 'F' is not in the loaded solution. Next: ListAll"; no args. |
| E-SR4 | No symbol at line/column | `:122` | n | y | n | X | Sites 1. "Next: ReadFile". |
| E-SR5 | No symbol named (branch A of the block now read at ~`:211`) | `:175` | n | y | y | S, X | Sites 1. "No symbol named 'X' found in 'F'. Next: Search(mode: symbol, query: X)". Line drifted: read again at `:211`. |
| E-SR6 | Declarations do not match contextSnippet (branch B, read at ~`:211`) | `:183` | n | n | n | S | Sites 1. "'X' has N declaration(s) in 'F', and contextSnippet did not match any of them. Provide a contextSnippet that is a verbatim substring ...". |
| E-SR7 | No symbol resolved at matched location | `:191`, `:219` | n | y | n | X | Sites 2. Names `ReadFile`. |
| E-SR8 | Single-candidate branch (RequireSingle) | `:198` (was `:211`) | y | y | y | X | Sites 1. Non-miss (ambiguous, coded ToolAmbiguousMatch). "matches N declarations ... Next: retry ... DeleteUnusedSymbol(filePath: F, symbolName: X, contextSnippet: <line>)"; lists all N. `// TODO align SyntaxTargetResolver.ResolveBySnippetOrThrow`. |
| E-SR9 | Declaration not found | `:381` | n | y | y | X | Sites 1. "Declaration of symbol 'S' not found in 'F'. Next: Search mode symbol"; query prefilled. |
| E-SRF1 | SemanticRefactoringEngine: class miss | `SemanticRefactoringEngine.cs:184` | n | y | y | S, W, X | Sites 1. "Class 'C' was not found in 'F'. Searched: class declarations in this file. Next: Search(mode: class, query: C)". |
| E-SRF2 | Property miss | `:189` | n | y | y | C, X | Sites 1. "Property 'P' was not found in class 'C'. Next: Search(mode: property, query: P)". |
| E-SRF3 | No statements in line range | `:225` | n | y | n | S, W, X | Sites 1. Line-range miss. "No statements found in lines A-B of 'F'. Searched: statements inside a block body whose span falls within that line range ... GetFileOutline"; no args. |
| E-SRF4 | SnippetNodeKindError.Describe | `:269` | n | y | n | W, X | Sites 1. Non-miss (matched, wrong node kind; coded NotFound: code mismatch). "contextSnippet matched at line N, but no statement is there; the innermost node is K." plus Searched and `GetFileOutline`; no args. |
| E-SU1 | SyntaxUpgradeEngine: lookup misses | `SyntaxUpgradeEngine.cs:155`, `:161`, `:405`, `:632` | n | n | n | - | Sites 4. "Method not found.", "Switch statement not found.", "Class 'C' not found in file.", "Method 'M' not found in file." |
| E-SU2 | Zero-result messages | `:43`, `:373`, `:597`, `:710` | n | n | n | - | Sites 4. Non-miss. "No if-throw guard clause patterns found", no backing-field pairs, no block-form namespace, no replacements found. Zero-result, not a lookup miss. |
| E-SU3 | Passthrough of `SnippetLookup.TryResolve` text | `:121` | y | n | n | W | Sites 1. Conditional Cand. Wraps every `SnippetLookup` outcome verbatim (C6 text) in `ToolNotFoundException`. NoMatch is a miss (candidates only with closest windows); ambiguous outcomes are non-miss and coded NotFound (code mismatch). |
| E-TS1 | ThreadSafetyEngine: method miss | `ThreadSafetyEngine.cs:35` | n | y | y | X | Sites 1. "Method 'M' not found." plus Next `GetFileOutline`. |
| E-TS2 | Containing type / body / type misses | `:46`, `:120`, `:123` | n | n | n | - | Sites 3. "Containing type not found for method", "Method or body not found: ... in file:", "Type not found for method". |
| E-TS3 | No lock statement | `:129`, `:142` | n | n | n | - | Sites 2. Non-miss. Zero-result. |
| E-STR | `SyntaxTargetResolver` container miss and re-wrap | `SyntaxTargetResolver.cs` ~`:257`; `ComposeFailure` ~`:740-760` (reported `:694` / `:757`) | y | y | part | S, N, C, W, X | Sites 1. "Type 'C' declares no {kind} named 'M' (searched the whole file; type arguments optional, case ignored)"; retry template partial. `ComposeFailure` re-creates the exception from the message alone and drops `MissingTargetKind`, `MissingTargetName`, `Candidates`, `Snippet` (section 6). Survey marked K; not carried. |

#### Engines.Advanced

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| A1 | Bare file-not-found `ToolNotFoundException(msg [, "file", path])` | DeadCode 21; DependencyInjection 39, 249; Instrumentation 25, 62, 103; SecurityAndSafety 37, 79; Testing 45, 78; Modernization 662, 833; Documentation 23; AdvancedRefactoring 54; AdvancedStructural 2057, 2248, 2304, 2368, 1221, 225, 852; AsyncOptimization 433, 696, 1897, 3323 | n | n | n | - (S where the text says solution / loaded) | Sites 25. No candidates, next call or searched-scope statement. Documentation 23 reads "File not found in solution"; AsyncOptimization 1897 says "Ensure LoadSolution has been called"; 3323 has no name. |
| A2 | Bare file / class miss that names `GetFileOutline` | `AdvancedStructuralEngine.cs:178`, `:190` | n | y | y | S, X | Sites 2. `:178` "Source file 'N' not found in solution. Next: GetFileOutline(filePath: "<sourceFilePath>") to check the file and class names."; `:190` "Class 'C' not found in 'F'. Next: GetFileOutline(filePath: ...)". |
| A3 | Method miss that names `GetFileOutline` | `AsyncOptimizationEngine.cs:141`, `LogicOptimizationEngine.cs:1207` | n | y | y | X (S at `:1207`) | Sites 2. `:141` "Method 'm' not found. Next: GetFileOutline(filePath: ...)"; `:1207` "Method 'm' not found in 'F'. Next: GetFileOutline(filePath: ...)". |
| A4 | AdvancedStructuralEngine: class / member / expression lookups | 58, 98, 497, 539, 566, 550, 626, 720, 787, 632, 726, 754, 747, 833, 713, 865, 1293, 1301, 1684, 2076, 2310 | n | n | n | - | Sites 21. Class, field/method, parameter, expression (via `SnippetNodeKindError.Describe`), containing class/method/expression/statement (no name argument), type, member. |
| A5 | AsyncOptimizationEngine: method / project lookups | 336, 448, 705, 1907, 3071, 652, 883, 1115, 1281 (method in file); 2480, 3728 (project, `ProjectLookup` template) | n | n | n | - | Sites 11. |
| A6 | LogicOptimizationEngine: method / loop lookups | 68 (no name), 703, 748, 793, 828 (method), 906, 987, 1031 (loop at line) | n | n | n | - | Sites 8. |
| A7 | AntiPatternEngine: project miss (`ProjectLookup.FindProject` template) | 71, 1004, 1084, 1260, 1359, 1795, 1868, 1934, 2076, 2147 | n | n | n | - | Sites 10. "Project not found in the loaded solution." Prefill only kind / name arguments of the template. Share one helper. |
| A8 | CodeGenerationEngine: class / interface / property / snippet lookups | 208, 515, 881, 908, 1044 (class / interface / property), 1036 (property snippet), 1225 (snippet), 382 (plain `InvalidOperationException`) | n | n | n | - | Sites 8. |
| A9 | ModernizationEngine: class / record / method | 43, 134, 153, 229, 608, 664, 775 | n | n | n | - | Sites 7. Nothing beyond kind and name. |
| A10 | TestingEngine: method / class / result | 51 (method), 83 (class), 192 (result, code NotFound) | n | n | n | - | Sites 3. |
| A11 | DependencyInjectionEngine: class via `RequireSingle` | 43, 252 | n | n | n | - | Sites 2. |
| A12 | CodeStyle, Documentation, ControlFlow, Instrumentation | CodeStyleEngine 100 (property), 297 (project); DocumentationEngine 106 (class); ControlFlowEngine 340 (project); InstrumentationEngine 64 | n | n | n | - | Sites 5. |
| A13 | OutParamRefactoringEngine (result code NotFound) | 47 (file), 73 (method) | n | n | n | - | Sites 2. |
| A14 | AsyncBatchEngine | 251, 254 ("Method not found", result Code NotFound mapping), 1209 ("Caller file not found on disk."), 1587, 1621 ("Document not found in workspace") | n | n | n | - | Sites 5. `:1209` text is matched by `AsyncifyTools.cs:2050-2052` (section 4). |
| A15 | AsyncBatchEngine: caller method missing | 1383 | n | n | n | - | Sites 1. A silent `return source;`: no error at all. |
| A16 | Misclassified ToolNotFoundException (not a lookup miss) | AdvancedStructural 101 (ineligible ctor replace), 927 (internal nested-type removal failure), 2063 (syntax root), 2101, 2107 (inline method ineligibility / semantic resolution), 2370 (anonymous type, no kind); AsyncOptimization 654 (method not a direct class member); LogicOptimization 709, 754 (method has no if statement); CodeGeneration 1235, 1241 (string.Format call with no arguments); CodeHealing 90, Instrumentation 30, 109 (expression-bodied or abstract method has no body; message "Method or body not found"); Testing 331 (internal syntax-root read failure) | n | n | n | - | Sites 15. Non-miss. Need a reclassification (`TargetIneligible` or similar), not candidates. |
| A17 | `ToolInvalidArgumentException` with "syntax root ... not found" wording | LogicOptimization 31, 56, 62, 123, 158; CodeHealing 32, 74; Documentation 27, 100; Modernization 240, 598, 642, 837 | n | n | n | - | Sites 13. Non-miss. Not NotFound. |
| A18 | ControlFlowEngine not-found results carrying `ToolErrorCode.Exception` | ControlFlowEngine 152, 163 (`ControlFlowAnalysisResult`), 248, 258 (`DataFlowAnalysisResult`) | n | n | n | - | Sites 4. Code is Exception, not NotFound (code and message disagree). |
| A19 | AntiPatternEngine: method / class in file | `AntiPatternEngine.cs:2841`, `:2915` | n | y | y | S, W, X | Sites 2. Spot-check of `:2841`: "Method 'M' not found in 'F'. Searched: method declarations named ... with class filter ... Next: Search(mode: method, query: name)"; no candidate list, N and C not addressed. `:2915` "Class 'C' not found in 'F'. Searched ... Next: Search(mode: class, query: C)". |
| A20 | AntiPatternEngine: method node found, symbol unresolved | `:2852` | n | y | n | S, W, X | Sites 1. Non-miss (internal: the method node was found but its symbol did not resolve). "Next: Build", no args. |
| A21 | CodeGenerationEngine: interface in scope | `CodeGenerationEngine.cs:770-772` | n | y | y | S, N, W, X | Sites 1. "Interface 'I' not found in <project 'p' or all loaded projects>. Searched ... Ensure the interface name matches exactly (including leading 'I') ... Next: Search(mode: interface, query: I)". |
| A22 | CodeGenerationEngine: class or record (result) | `:623-624` | n | n | n | W (partial) | Sites 1. "Class or record not found, Check spelling"; tuple inferred from the quoted text. |
| A23 | AdvancedRefactoringEngine: class / interface | `AdvancedRefactoringEngine.cs:247`, `:295` | n | y | y | S, W, X | Sites 2. `:247` "Class 'C' was not found in 'F'. Searched: class declarations in this file. Next: Search(mode: class, query: C)"; `:295` "Interface 'I' was not found in 'F' or any other loaded document. Searched: all project documents. Next: Search(mode: interface, query: I)". No N, C or K. |
| A24 | ApiGenerationEngine | `ApiGenerationEngine.cs:27`, `:121` | n | y | y | S, W, X (N at `:121`) | Sites 2. `:27` "Controller class 'C' not found in 'F'. Searched: class declarations in the file. Next: Search(mode: class, query: C) ... or GetFileOutline"; `:121` "... after adding the using directive. Next: Search(mode: class, query: C) ... (case-sensitive)" (case advice). |
| A25 | TestingEngine: class | `TestingEngine.cs:338` | n | y | y | S, W, X | Sites 1. "... Searched: class declarations in the file. Next: Search(mode: class, query: C)"; request values, not placeholders. |
| A26 | MappingEngine: type resolve | `MappingEngine.cs:81` | n | n | n | S, N, W | Sites 1. "Could not resolve type 'A' or 'B'. Searched: compilation by fully-qualified metadata name, then by simple name. Next: pass the simple class name (e.g. 'CreateRecipeCommand')": an example and an instruction, no call. |
| A27 | MappingEngine: document / types | `:145` | n | y | y | S, W, X | Sites 1. "Could not find the document or the types ... Searched: the document and both type symbols. Next: Search(mode: class) for each type". |

The 69 Engines.Advanced `RequireDocument` call sites (25 in the 15 smaller files, the rest in AdvancedStructural,
AsyncOptimization, AntiPattern, LogicOptimization, CodeGeneration) are part of C1.

#### Tools.Advanced, Tools.Experimental, Server.Advanced

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| TA1 | AdvancedRefactoringTools: file miss | `AdvancedRefactoringTools.cs:81` (used by `:276`, `:574`, `:610`) | y | y | part | S, N, W, X | Sites 1 (3 callers). Updated 348975ba (5d-2): `DocumentLookup.TryGetDocument` + `FileNotFoundMessage` text (did-you-mean with prefilled `ReadFile`, or the no-similar-name fact and the isCallable-gated options); no solution loaded states that fact with a `LoadSolution` option. Tests: `ToolsAdvancedFileMissTests` (IntroduceParameterObject, SyncInterface). |
| TA2 | AdvancedRefactoringTools: zero anonymous objects | `:109` | n | n | n | - | Sites 1. Non-miss. A zero-result, not a lookup. |
| TA3 | AdvancedRefactoringTools: variable not found in file | `:708` | n | n | n | S | Sites 1. "variable 'name' not found in 'F'." (the engine returned empty text). |
| TA4 | GenerationTools: file miss | `GenerationTools.cs:139-141`, `:251-253` | y | y | part | S, N, W, X | Sites 2. Updated 348975ba (5d-2): same lookup as TA1 (GenerateHttpClient, InterpolateStringSafe). Tests: `ToolsAdvancedFileMissTests`. |
| TA5 | GenerationTools: project miss | `:194-196` | y | n | n | S, N, W | Sites 1. Updated a0a54a10 (5d-3b): `ProjectNotFoundDetail.Build` over every project name. Tests: `ToolsAdvancedFileMissTests`; `BatteryNineteenTests` assertion changed to the remaining facts. |
| TA6 | ScanTools: project miss rethrow | `ScanTools.cs:1096-1101` | n | n | n | S | Sites 1. Catches `ToolNotFoundException` / `ArgumentException` when `ex.Message.Contains("not found in solution")`, rethrows NotFound "Project '...' not found in solution." (text-coupled; section 4). |
| TA7 | IntelligenceTools: NotFound hint on TraceVariableLifetime | `IntelligenceTools.cs:258` | n | y | y | X | Sites 1. Appends " Call GetFileOutline(filePath: "<raw filePath>") on the file to confirm the enclosing method's line range, then retry with a lineNumber inside it." The base text comes from `SymbolNavigationEngine` ~`:2295` (E-SN8, not read here). |
| TA8 | SubAgentChildServerLauncher | `SubAgentChildServerLauncher.cs:117-122` | n | n | n | - | Sites 1. Non-miss. NotFound for a repo-layout precondition (misclassified). |
| TX1a | CodeTransformTools: convert_expression_body found nothing | `CodeTransformTools.cs:488` | n | y | y | S, X | Sites 1. Non-miss (zero-result). "found nothing to convert for 'm' in 'F'" plus "Possible causes: member not found (...), ... Use GetFileOutline(filePath: F)". |
| TX1b | CodeTransformTools: make_method_thread_safe method miss | `:664` | n | y | y | S, N, X | Sites 1. "make_method_thread_safe: method 'm' not found in 'F'." plus "Use GetFileOutline(filePath: F) to verify the method name (case-sensitive)". |
| TX2a | CodeTransformTools: method not found or not eligible | `:519` | n | y | y | S, X | Sites 1. Spot-check: the `GetFileOutline` hint prefills `("filePath", resolvedFilePath)`; the noOp text is "The method must have exactly one parameter and return a value." |
| TX2b | CodeTransformTools: method not found or not eligible / not an extension method | `:552`, `:592`, `:607`, `:624`, `:649`, `:694` | n | y | y | S, X | Sites 6. "<tool>: method 'm' not found or not eligible [or: not an extension method] in 'F'." plus "Use GetFileOutline(filePath: F) to verify" (each read individually). Miss and ineligible conditions are combined in one message: split it (section 7). |
| TX3 | CodeTransformTools: file / class / method not found | `:1102`, `:1133`, `:1184` | n | n | n | - | Sites 3. "file not found, class not found, or method not found" / "file not found or class not found"; `NoOpHandling.ErrorUnlessText`, which does not inspect the text (section 4); end with "Ensure the solution is loaded." and name no real call. |
| SV1 | CallToolFailureHandler: tool-name miss | `Server.Advanced/CallToolFailureHandler.cs:130-131` | n | y | part | S, N (advice only), X | Sites 1. "There is no tool named 'X' on this server. Check the tool name; McpServerStatus(toolListing: "all") lists every tool." Fixed argument; the name is echoed. |
| SV2 | CallToolFailureHandler: tool is in a toolset | `:105-110` | n | y | part | X | Sites 1. Non-miss (TargetIneligible). Names `McpToolsetControl` with `toolSet` and a fixed `enabled`. |
| SV3 | CallToolFailureHandler: tool declared but not enabled | `:116-121` | n | y | y | X | Sites 1. Non-miss (TargetIneligible). Names `McpServerStatus` with `toolNameFilter` set to the requested tool. |

#### Mechanisms, not producers

- `ToolException.cs:67` (`ToolNotFoundException.ErrorCode`); `ToolException.cs:254-322` `ToolErrorMapper`
  (pass-through; `:275` `!message.Contains(ex.Message)` is generic wrapping).
- `NotFoundEvidence.cs` `ForCurrentCall` (`:40-50`; callers `ToolResultMarshaller.cs:170`, `Server.Advanced`
  `CallToolFailureHandler.cs:76`) and `ForZeroHitSearch` (one real call, `WorkspaceReadNavigationImpl.cs:1328`)
  append attribution notes only (`BlobNote` `:173`, `GitNote` `:81` / `:347`: who wrote the term, when, HEAD
  versus disk). They never add candidates or a next call.
- `CodeTransformTools.cs:787` (`GeneratorFailure`, a ternary, not a switch) picks a hint by code: NotFound appends " Call GetFileOutline(filePath: \"<the file>\") to confirm the class name, then retry with that exact className." (a hint on a miss, fixed placeholder); TargetIneligible appends "Add a settable property or a primary constructor parameter..." (no call).

#### Not tool not-found errors (matched the words, out of scope)

`ValidationEngine.cs:52` ("File not found: {filePath}", an RS001 diagnostic), `GitShellLocator.cs:92`,
`OperationBlobWriter.cs:140` (`System.IO.FileNotFoundException`), `OrientationCircuitBreaker.cs:100` and
`HaltInfo.cs:26` (breaker text), `PersistentWorkspaceManager.cs:399` ("baseRepoDir does not exist on this host",
an `ArgumentException`), `WorkspaceReadNavigationImpl.cs:1262`, `:1267`, `:1279` (zero-hit text-search warnings,
not errors; `:1279` names `includeTests: true`), `SymbolRelationshipImpl.cs:142` (a scope note about a
"trustworthy 'not found anywhere' signal", only when includeTests and no project), `PlanStepFile.cs:47`
(`Utilities.PlanStepRunner`, "Plan directory not found").

### 4. String-coupled consumers

Text-coupled (a message change can break these silently):

1. `RoslynSentinel.Tools.Advanced/ScanTools.cs:1096` filters on `ex.Message.Contains("not found in solution")`
   and rethrows NotFound at `:1101`. Throw sites whose text contains the phrase (survey candidates, to be confirmed):
   `DiscoveryEngine.cs:238` ("Project 'P' not found in solution.") and `InventoryEngine.cs:36`, `:64` ("File not
   found in solution: ..."). Not matching: `ProjectStructureEngine.cs:385` ("not found in the solution"), `:1063`,
   `:1515`, `:1540`. Which engine call `ScanTools` actually reaches is **to be traced in Step 5d**.
2. `RoslynSentinel.Tools.Advanced/AsyncifyTools.cs:2050-2052` maps `reason.Contains("file not found")` /
   `"not found on disk"` to `FailureReason.SymbolNotResolved`. A producer text is `AsyncBatchEngine` `:1209`
   "Caller file not found on disk."; the producers were not fully traced.
3. Inference, not a consumer: `Engines.Advanced/AntiPatternEngine.cs:1759` `InferSpecificExceptionType` maps text
   containing "not found" to `KeyNotFoundException`; it analyses source text, not tool errors.

Verified: not text-coupled. `CodeTransformTools.cs:1102`, `:1133`, `:1184` pass `NoOpHandling.ErrorUnlessText`
(`RefactoringToolHelpers.cs:267`), which sets `ProceedWhenTextPresent = true`; `GuardNothingToDo` returns null
(proceed) when `UpdatedText` is non-empty and is reached only for Outcome Modified or NoCandidatesFound. The error text is
never inspected, so a wording change there is safe. (An earlier survey listed these as text-coupled; that was wrong.)

Code- or type-coupled (not text; safe against wording changes, sensitive to code changes):

- Common `NotFoundEvidence.cs:44` matches codes "NotFound", "MethodNotFound", "FileNotFound" (via
  `ToolResultMarshaller.cs:170`).
- Common `IResultStatus.cs:58` maps NotFound or NoMatches to `ResultStatus.NotFound`; `EngineResultWrapper.cs:72`
  and `EditOutcomeExtensions.cs:25` map back to `ToolErrorCode.NotFound`.
- Tools.Basic `DocumentationTools.cs:21`; `WorkspaceFileEditImpl.cs` `is ToolNotFoundException` handling and the batch
  failure code (the rejected-origin and rewrite notes are appended at `:1311`, `:1316`, `:1614-1621`; the earlier
  lines `:1278`, `:1283`, `:1597-1603` and `:1666` drifted, re-measure); `WorkspaceBuildTestImpl.cs:320` (catches
  `ToolNotFoundException`).
- Tools.Experimental `CodeTransformTools.cs:787` (by code); Tools.Advanced `IntelligenceTools.cs:258` (by result code).
- Engines.Basic `MemberRefactoringEngine`: 12 catch blocks branch on exception type and `ex.ErrorCode` (936,
  1440, 1492, 1523, 5519 among them); none match message text there.

Coverage of this sweep: no other text consumers were found in Engines.Basic A-Z, Common (`ToolException.cs:275` is
generic wrapping) or Server.* by the regex
`\.Message\.(Contains|StartsWith|EndsWith|IndexOf)|\.Message\s*==|reason\.Contains|Message\.Equals`. The
Tools.Advanced, Engines.Advanced and Server sweep covered only the Contains / StartsWith / EndsWith / Equals shapes,
so a consumer using another comparison shape would have been missed.

### 5. Seed-table corrections (plan rows 11-22 and the Problem section)

Against `plan_not_found_error_orientation.md`, lines 83-128 and Problem causes:

- Row 3: `Search(mode: symbol)` / LocateSymbol is `SymbolNavigationImpl.cs:55-80`, message at `:75` (plan: `:60-77`).
- Row 5: the plan says the shape check names `Search(mode: symbol)`; the survey reads `PersistentWorkspaceManager.cs:3564-3565`
  as an example id only (`for example 'T:MyApp.Orders.OrderService'`). Open: re-verify. Row 4's resolver message is at `:3577-3578`
  (plan `:3571-3581`).
- Row 7 and Problem cause 5: `MemberNotFoundDetail` callers are `MemberRefactoringEngine.cs:2338` (ReplaceMember) and
  `:2670` (RemoveMember, was `:2633`); `SymbolNavigationEngine.cs:2966` and `:2946` (not `2943` / `2967`); `:2976` is a bare throw.
- Row 8: container builders are called at `:1952`, `:2012`, `:2452` (not `2413`).
- Row 9: anchor builder `:2055-2067`, thrown at `:789`, `:799`, `:2091` confirmed.
- Row 10: `:1165` confirmed.
- Row 11: `:1214`, `:1276` confirmed; the text "Cannot edit: method 'M' not found." names the kind only.
- Row 12: `:176`, `:441`, `:471`, `:575` confirmed; add `:462` (contextSnippet not inside any constructor). `:575` is compliant on S, W, X.
- Row 13: MoveMember file lookup is `:2980` (not `2937-2941`) and `:4950` (not `4911`); syntax root `:2986` / `:4956`.
- Row 14: class lookup is `:2992` and `:4962` (not `2953`, `4923`); also `:4142`, `:4200` for MoveInstanceMembers; add
  MoveMembersToBaseType `:3596-3620` and `:3718`.
- Row 15: `MemberSelection.cs:287`, `:295` confirmed.
- Row 16: `BasicRefactoringEngine.cs:251` is a `Refuse()`, not a throw; text confirmed.
- Row 17: ~`:2079-2080` (AnalyzeControlFlow throw is at `:2079`; one-line drift), ~`:2122`, ~`:2525` confirmed; rule items are S, W, X (not N).
- Row 18: "Other bare engine literals" `BasicRefactoringEngine` `:1452`, `:1500`, `:1751`, `:2277`, `:2336` / `:2481`, `:2786` confirmed.
  MemberRefactoringEngine: `:641`, `:823`, `:847`, `:2225` confirmed; "1594-1917 (6 sites)" is 7 sites (six identical "target not found"
  at 1594, 1647, 1698, 1780, 1851, 1917 plus `:1859` "attribute not found"); "4497 / 4517" is `:4536` / `:4556` ("Method not found.");
  "5369 / 5416" is `:5408` / `:5455` ("Type not found."). Row 18 also omits `BasicRefactoringEngine` `:1795-1986` and `:2567-2659`
  (WrapInTryCatch, ExtractLocalVariable / Constant).
- Row 19: the namespace miss is `:2538` (not `2499`) plus `:2547`.
- Row 20: `AdvancedRefactoringTools.cs:81`, `GenerationTools.cs:139-141`, `:251-253` confirmed: no candidates, no next call.
- Row 21: `GenerationTools.cs:194-196` lists the loaded project names (Cand y, unranked); `ScanTools.cs:1096-1101` confirmed text-coupled.
- Row 22: `AdvancedRefactoringTools.cs:708` (variable not found) and `:109` (zero anonymous objects) were read; see TA2, TA3.
- Problem cause 6 counts: 291 and 70 confirmed. `RequireDocument` call sites outside Common are 157 (plan: 160).
  `SymbolNotResolved` has 5 hits: 2 producers and 3 consumers (plan: 4 production sites).
- Drift: `DocumentLookup.cs` `RequireDocument` def `:267`, `FindDocumentOrNull` `:275`, `GetDocuments` call `:413` / message `:423`.
  `FindClosestPaths` (plan cause 2, `:276-288`) now has no callers and `FileNotFoundMessage` now takes `FileCandidate` (batch B
  edits in progress by another session).

### 6. Misclassified and code-versus-message findings

Not-found wording or code that does not match what the site does. These need a reclassification or a code fix, not a
candidate list. Each is a hypothesis about severity until a slice confirms it.

- **All non-miss rows (28 rows, 88 sites)**, tagged `Non-miss.` in the table: C5b, C6f, TB-T1c, TB-T2d, TB-T5b, E-MRE7b,
  E-MRE8b, E-MRE10, E-BRE5c, E-BRE11, E-INV2, E-SU2, E-TS3, E-TIL1, E-PS2, E-PS3, E-PS4, E-SR2, E-SR8, E-SRF4, A16, A17, A20,
  TA2, TA8, TX1a, SV2, SV3. Three of them are coded NotFound for a condition other than a miss and so mislead callers that
  branch on the code: E-MRE7b (ambiguous namespace), E-SRF4 (matched, wrong node kind), E-SU3's ambiguous outcomes (that row
  is a miss for NoMatch). E-SR8 is coded ToolAmbiguousMatch (correct code, not-found wording).
- **ToolNotFoundException for a non-lookup condition (15 sites, row A16):** AdvancedStructural 101, 927, 2063, 2101, 2107,
  2370; AsyncOptimization 654; LogicOptimization 709, 754; CodeGeneration 1235, 1241; CodeHealing 90; Instrumentation 30,
  109; Testing 331. Also `PersistentWorkspaceManager.cs:915`, `:983` (load failure, row C5b); `TestInstanceLauncher.cs:347`
  (precondition, E-TIL1); `SubAgentChildServerLauncher.cs:117-122` (repo-layout precondition, TA8);
  `SnippetErrorFormatter.cs:468`, `:474`, `:479` (invalid disambiguator, C6f);
  `WorkspaceProjectManagementImpl.cs:208` ("Directory not found" under InvalidArgument, TB-T5b).
- **NotFound wording under another code:** `ToolInvalidArgumentException` "syntax root not found" (MemberRefactoringEngine 14
  sites, BasicRefactoringEngine 7, Engines.Advanced 13: rows E-MRE10, E-BRE11, A17); `ControlFlowEngine` 152, 163, 248, 258
  carry `ToolErrorCode.Exception`; `SemanticReplaceEngine.cs:1256` ("Document not found for file") uses TargetIneligible;
  `TestRunEngine.cs:136` uses EngineError InvalidInput.
- **Error payload dropped (`SyntaxTargetResolver.ComposeFailure` ~`:740-760`):** re-creates `ToolNotFoundException` from the message
  alone, dropping `MissingTargetKind`, `MissingTargetName`, `Candidates` and `Snippet`. Verified by reading the source; likely an
  error-payload bug (consequence for callers not traced).
- **Raw exception leak:** `DiscoveryEngine.cs:542-543` turns a non-SymbolNotResolved code into a raw `InvalidOperationException`;
  `InventoryEngine.cs:43`, `:71` throw raw `InvalidOperationException` "Syntax root not found." CLAUDE.md forbids raw exceptions in a
  `ResultError`; whether these reach the agent unwrapped was not traced.
- **Silent miss:** `AsyncBatchEngine.cs:1383` returns the source unchanged when the caller method is missing (no error).
- **Combined wording:** `CodeTransformTools.cs` TX2b (`:552`, `:592`, `:607`, `:624`, `:649`, `:694`) says "not found or not
  eligible" in one message, so a miss and an ineligible method read the same; split the message.
- **Ambiguous snippet outcomes:** `SymbolNavigationEngine.cs:1569` and `:1925` catch only `ToolNotFoundException`, so an
  ambiguous snippet propagates as `ToolAmbiguousMatchException`; `SyntaxUpgradeEngine.cs:121` instead wraps ambiguous
  outcomes in `ToolNotFoundException` (code mismatch). The two sites disagree on the same input (hypothesis: neither is
  intended; not traced to callers).
- **Possible placeholder in a next call (hypothesis):** `FindReferences` declares contextSnippet schema-required
  (`SymbolRelationshipTools.cs:70`) while `FormatFindReferencesNextCall` (~`SymbolNavigationEngine.cs:2692`) omits it for
  type targets, so the rendered call may carry a placeholder; not exercised at run time.
- **Unreachable fallback:** the `"?? Context snippet not found."` fallback at `BasicRefactoringEngine.cs:1795` cannot be
  reached (the text comes from `SnippetLookup.TryResolve`).
- **Wrong name slot:** `MsToolAugmentEngine.cs:1474` "No method named '{contextSnippet}' found in file." puts the snippet where the name goes.
- **Plan seed row 5 conflict:** see section 5; the shape-check message may not name a call at all.

### 7. Producers not covered by Step 3-5d (candidate 5e slices)

Compared with plan Step 3 (`DocumentLookup`, `FileNotFoundMessage`), Step 4 (`SymbolNavigationImpl.cs:55-80`,
`PersistentWorkspaceManager.cs:3548-3590`), Step 5a (`MemberNotFoundDetail`, `BuildContainerNotFoundMessage*`,
`BuildInsertAnchorNotFoundMessage`, `MemberSelection`), 5b (`BasicRefactoringEngine` `:251, 1500, 1751, 2080, 2122, 2277,
2336, 2481, 2525, 2786`), 5c (`MemberRefactoringEngine` rows 11-14, 18-19 at the corrected lines) and 5d
(`AdvancedRefactoringTools.cs:81`, `GenerationTools.cs`, `ScanTools.cs:1096`, `BasicRefactoringEngine.cs:1452`).

Covered rows (for reference): C1, TB-T1a, TB-T2a (Step 3); TB-T3a, C3, C4, TB-T4 (Step 4); E-MRE6b, E-SN11, E-MRE5a,
E-MRE2c, E-MRE5b, E-MSEL (5a); E-BRE1, E-BRE3, E-BRE4, E-BRE6, E-BRE7, E-BRE8, E-BRE10 (5b); E-MRE1a, E-MRE1c, E-MRE2a,
E-MRE2b, E-MRE3b, E-MRE4a, E-MRE4b, E-MRE5c, E-MRE7a, E-MRE8a (`:2980`, `:2992`), E-MRE8e (`:4950`, `:4962`),
E-MRE9 (5c); TA1, TA4, TA5, TA6, E-BRE2 (5d). Plan row 22 is named in 5d but the 5d change text lists no change for
`AdvancedRefactoringTools.cs:708` or `:109` (TA2, TA3).

Everything below is **not** in any step. "Lookup" means the site is a real miss that needs candidates and a next call;
"reclassify" means it is a zero-result, ineligible or precondition message that needs only a code change (section 6).

**Engines.Basic - MemberRefactoringEngine.cs (slice 5e-1, dispatch only when no other session has uncommitted edits in the file)**

- Lookup: E-MRE1b `:462`; E-MRE6a `:2285` (has a next call, no candidates); E-MRE4c `:929`, `:1433`, `:5512` (batch, 3 sites);
  E-MRE4d `:1713`, `:5464` (2 sites); E-MRE8c `:3596-3620`, `:3718` (6 sites); E-MRE8d `:4142`, `:4200` (2 sites);
  E-MRE8a `:2986` and E-MRE8e `:4956`, `:4971` (syntax root and semantic model, 3 sites).
- Already near standard, only rank or add the "no similar name" sentence: E-MRE1d `:575`, E-MRE3a `:1165`, E-MRE6c `:2664`.
- Reclassify: E-MRE7b `:2547` (ambiguous, coded NotFound), E-MRE8b `:3140` (prose fix), E-MRE10 (14 syntax-root sites, InvalidArgument wording).

**Engines.Basic - BasicRefactoringEngine.cs**

- Lookup (not in 5b): E-BRE5a/b `:1795`, `:1873` (2 snippet-family sites; text from `SnippetErrorFormatter`, so the fix is in the C6 family), E-BRE9a/b `:2567-2659` (4 line-range sites, `GetFileOutline` named with no args).
- Reclassify: E-BRE5c `:1802`, `:1808`, `:1898`, `:1905`, `:1912`, `:1918`, `:1986` (7 sites, ineligible or internal), E-BRE11 (7 syntax-root sites).

**Engines.Basic - other files (slices by file)**

- SymbolNavigationEngine (E-SN1 to E-SN13 including E-SN7a/b, 16 sites; already near standard: E-SN3, E-SN5, E-SN7a, E-SN10, E-SN11; E-SN11 is covered by 5a; note the ambiguous-outcome and possible-placeholder findings in section 6).
- ProjectStructureEngine (E-PS1 to E-PS5, 13 sites; E-PS2, E-PS3, E-PS4 are non-miss: reclassify).
- StructuralRefinementEngine (E-SR1 to E-SR9, 12 sites; E-SR2 and E-SR8 are non-miss: reclassify; E-SR8 (`:198`) carries a TODO to align with `SyntaxTargetResolver.ResolveBySnippetOrThrow`).
- SemanticRefactoringEngine (E-SRF1 to E-SRF4, 4 sites; E-SRF4 `:269` is non-miss: reclassify).
- SyntaxUpgradeEngine (E-SU1 4 lookup sites; E-SU2 4 zero-result sites: reclassify; E-SU3 `:121` 1 snippet-family site that also wraps ambiguous outcomes as NotFound).
- ThreadSafetyEngine (E-TS1 to E-TS3, 6 sites; E-TS3 zero-result).
- DiscoveryEngine (E-DISC1 to E-DISC5, 6 sites), DiagnosticEngine (2), InventoryEngine (E-INV1, E-INV2: 4 sites),
  ImpactAnalyzer (E-IMP1 to E-IMP4: 6 sites; reached by InspectSymbol blastRadius, so check against Step 4),
  MsToolAugmentEngine (4), NamedArgumentsEngine (3), NullForgivingRemover (1), SemanticReplaceEngine (2),
  TestCategoryTaggingEngine (1), TestInstanceLauncher (3, reclassify `:347`), TestRunEngine (1), ChangeSignatureWrapper (1),
  ConstructionReplacer (2), `SyntaxTargetResolver` E-STR (1; also fix the `ComposeFailure` payload drop).

**Engines.Advanced (136 `ToolNotFoundException` sites plus result producers; slice by file group)**

- File misses: A1, A2 (27 sites); they should route to `DocumentLookup` / `RequireDocument` (C1) as the 157 call sites already do.
- Class / method / member lookups: A4 AdvancedStructural (21), A5 AsyncOptimization (11), A6 LogicOptimization (8),
  A8 CodeGeneration (8), A9 Modernization (7), A10 Testing (3), A11 DependencyInjection (2), A12 (5), A13 OutParam (2),
  A14 AsyncBatch (5), A3 (2).
- Project misses: A7 AntiPattern (10 sites) plus A5 `:2480`, `:3728` and A12 `:297`, `:340` share the `ProjectLookup.FindProject` template, so one helper change covers them.
- Silent miss: A15 `AsyncBatchEngine.cs:1383` (add an error).
- Reclassify only: A16 (15 sites), A17 (13), A18 (4).
- Already near standard (check for a "no similar name" sentence only): A19, A20, A21, A23 to A27; A22 needs a next call.

**Tools.Basic**

- `SnippetErrorFormatter` C6a to C6f (8 sites; C6f reclassify), `WorkspaceFileEditImpl` TB-T1b to TB-T1d (5 sites),
  `WorkspaceReadNavigationImpl` TB-T2d to TB-T2h (6 sites; TB-T2b and TB-T2c are already compliant),
  `SymbolNavigationImpl.cs:131` TB-T3b, `WorkspaceProjectManagementImpl` TB-T5a to TB-T5c (TB-T5b reclassify),
  AdminTools TB-T6a to TB-T6c (4 sites), DocumentationTools TB-T7a to TB-T7d (4 sites; TB-T7a is a fallback mapping whose live text is TB-T7d),
  WholeFileWriteTools TB-T8 (1), GitImpl TB-T9 (13 sites, its own family).

**Common**

- `DocumentLookup.cs:423` C2 (project miss, candidates exist, no ranking, no next call); `PersistentWorkspaceManager.cs:427` C5a;
  C5b reclassify.

**Tools.Advanced / Tools.Experimental / Server.Advanced**

- TA2 (non-miss: reclassify), TA3 (named in 5d row 22 but no change specified), TA7 (a consumer that already names `GetFileOutline`
  with filePath; verify it still fits after Step 4), TA8 (reclassify).
- Tools.Experimental `CodeTransformTools.cs`: TX1a `:488` (reclassify: zero-result), TX2b (split the combined "not found or not
  eligible" message), TX3 `:1102`, `:1133`, `:1184` (3 sites; need a real next call; not text-coupled, section 4).
- Server.Advanced: SV1 is compliant for tool-name misses (no work); SV2 and SV3 are non-miss branches that already name a call.

### 8. Owner question: do tools give fallback orientation hints for not-found?

Quote: "do tools give fallback orientation hints for not-found?" Partly, and unevenly. Every row is now classified: the
table has 189 rows standing for 597 raising sites, of which 161 rows (509 sites) are lookup misses and 28 rows (88 sites)
are non-miss conditions worded or coded as not-found (reclassify, section 6). Across all rows, **83 rows (277 sites) name a
next call** (72 miss rows with 266 sites, 11 non-miss rows with 11 sites); **40 rows (56 sites) prefill request values**
into the suggested call; **22 more rows (182 sites) prefill only a fixed or placeholder argument** (such as `kind: all`,
`mode: symbol`, `action: list` or template placeholders like {file}); and **37 rows (203 sites) list candidates**, of which
6 rows (7 sites) list them only on some paths (closest windows or a near-miss, tagged `Conditional Cand.`). **76 miss rows
(227 sites) give no orientation at all** (no candidates, no next call, no prefill); a further 16 non-miss rows (76 sites) give
none either. The shared file lookup (row C1) accounts for 157 sites of the 277, 182 and 203: without it the counts are 82 rows
(120 sites) naming a next call, 40 rows (56 sites) prefilling request values, 21 rows (25 sites) prefilling only fixed or
placeholder arguments and 36 rows (46 sites) listing candidates, so outside file misses the coverage is thin by site
but not by row: many engines name a request-prefilled `Search` or `GetFileOutline` call without listing candidates (for
example A19, A21, A23 to A25, A27, E-SRF1, E-SRF2, E-SR5, TX2a). The producers that list candidates and name a prefilled next
call are `SyntaxTargetResolver.BuildContainerMissDetailAsync` (TB-T2c, E-SN10; its retry template carries placeholders, not
request values), `BuildMethodNotFoundErrorAsync` (TB-T2b), `MemberNotFoundDetail` (E-MRE6b, E-SN11) and the
`SymbolNavigationEngine` FindReferences family (E-SN3, E-SN5, E-SN7a). Of the producers read, none says "no similar name
exists" and none offers to report the target as non-existent; E-SN3, E-SN4 and E-SN7b say the result is "NOT a confirmed
zero-references (or zero-implementations) result", the closest any message comes to separating a wrong spelling from an
absent target. The counts treat a hint that names a call only on some branches (for example the callable-gated calls in C1
and TB-T9) as naming a next call; they come from the `Cand` / `Next call named` / `Prefill` columns and the `Non-miss` tag of
section 3 (tallied by script over the table), so they move whenever a row is edited.

## Usage

Regenerate the counts with `Search`, one call per project, `includeTests` false (default). The query is a regex, so
escape parentheses and dots. The `fileGlob` must start with the project folder: `**/Engines.Basic/**` matched 0
files; use `RoslynSentinel.Engines.Basic/**/*.cs`.

```
Search(mode: text, query: "new ToolNotFoundException\(", fileGlob: "RoslynSentinel.<Project>/**/*.cs")
Search(mode: text, query: "RoslynSentinel\.Common\.ToolNotFoundException\(", fileGlob: "RoslynSentinel.<Project>/**/*.cs")
Search(mode: text, query: "ToolErrorCode\.NotFound", fileGlob: "RoslynSentinel.<Project>/**/*.cs")
Search(mode: text, query: "FileNotFoundMessage\.Build", fileGlob: "RoslynSentinel.<Project>/**/*.cs")
Search(mode: text, query: "\"FileNotFound\"", ...)      also "\"MethodNotFound\"" and "\"SyntaxRootNotFound\""
Search(mode: text, query: "EngineErrorCode\.SymbolNotResolved", ...)
Search(mode: text, query: "DocumentLookup\.RequireDocument|GetDocumentOrThrow", ...)
Search(mode: text, query: "(?i)not found|was not found|does not exist|no match", fileGlob: "RoslynSentinel.Common/**/*.cs")   also Tools.Basic
Search(mode: text, query: "Refuse\(", fileGlob: "RoslynSentinel.Engines.Basic/**/*.cs")        Refuse-type producers
Search(mode: text, query: "\.Message\.(Contains|StartsWith|EndsWith|IndexOf)|\.Message\s*==|reason\.Contains|Message\.Equals", ...)   string-coupled sweep
```

`<Project>` ranges over `Common`, `Engines.Basic`, `Engines.Advanced`, `Tools.Basic`, `Tools.Advanced`, `Tools.Experimental`,
`Server.Advanced` and `Utilities.PlanStepRunner` (each prefixed `RoslynSentinel.`). Tools.Advanced had 0 `ToolNotFoundException`
sites; `Utilities.PlanStepRunner` has no tool errors.

To update a row after a slice lands: re-read the changed message in source, then change Cand / Next / Prefill / Rule
items. A row is "done" only when the producer has a test through its real entry point (plan Step 5 "Done when"); this
table records the message, not test coverage.

## Gotchas

- **Line numbers drift.** Measured 2026-11-12 at workspaceVersion 9-33; other sessions were editing
  `MemberRefactoringEngine.cs`, `DocumentLookup.cs`, `FileNotFoundMessage.cs` and `WorkspaceReadNavigationImpl.cs`
  during the survey. `MemberRefactoringEngine.cs` sites after ~2431 had shifted +3 between reads. Re-measure before editing.
- **Message text was read from source**, not from a run. A row says what the code builds; whether a given call reaches
  that branch at run time (for example the `isCallable`-gated calls) was not exercised.
- **K policy.** The survey reporters used K loosely, some marking it when the message merely names the kind. The table keeps K
  only where the survey showed another-kind consideration: `BuildMethodNotFoundErrorAsync` (kind-mismatch sentence),
  `MemberNotFoundDetail` rows (same-file members grouped by kind) and E-MRE6c (type-level declaration sentence). Rows whose survey mark
  was K but whose text only names the kind (SyntaxTargetResolver container miss, AdvancedRefactoringEngine 247 / 295, TB-T3a, TB-T3b) carry "-" for K.
- **All rows are read, with a few residual unread bodies.** The two follow-up passes closed every "not read" cell. Still
  unread: `SnippetErrorFormatter` helper bodies (`AmbiguityAdvice`, `BuildLooser`, the AdjacentLine and InvalidDisambiguator
  branches beyond the rows above, the escaped-quote and tool-markup helpers); `DescribeLeakedNewContentMarkup`;
  `SymbolNavigationEngine` scopedResolutionFailure texts at ~`:1908`, `:1942`, `:1994` (previews only); the
  `TraceVariableLifetime` base text (~`:2295`, E-SN8); callers of `TypeHierarchyReport.Error`; and the run-time rendering of
  `ToolCallHint.Call` placeholders. Rows that depend on these (E-SN2, E-SN13, TA7, TB-T1d) say so.
- **Conditional candidates.** Six rows (E-BRE5a, E-BRE5b, E-SN2, E-SN6, E-SN7b, E-SU3) list candidates only when closest
  windows or a near-miss exist. They count as `Cand y` in section 8, so the candidate count is an upper bound for a typo
  that has no near-miss.
- **Sites versus rows.** C1 stands for 157 call sites in one row; a miss raised through `RequireDocument` is not repeated per
  caller. Direct builders (`TB-T1a`, `TB-T2a`) are separate rows because they carry their own wording.
- **Counts include comments** in the `not found` sweeps; `ToolErrorCode.NotFound` hits in Common are mostly mappers, not producers.
- **`Search(mode: text)` tooling.** It matches the query as a regex but prints a literal-only warning when the pattern has
  parentheses; there is no `output_mode`; results page at 200; `**/Engines.Basic/...` matches nothing.
- **`ListAll` has no file or container scope** (parameters `kind`, `projectName`, `includeTests`), so a prefilled "ListAll of the
  named file" cannot be built; `GetFileOutline(filePath)` is the file-scoped call (plan, brief-versus-source corrections).
- **Not a doc of tests.** Passing tests on a helper (`FileNotFoundMessageTests`, `CallableToolHintTests`) say nothing about the
  other 291 + 70 sites; each Step 5 slice defines its own entry-point test (planner), and a reviewer confirms it exercises the
  changed path (not just the shared helper).
- **Spot-check (2026-11-12).** 10 rows were checked against source; 5 were corrected and the others matched.
  Corrected: `AntiPatternEngine.cs:2841` (A19; Cand y changed to n, N and C dropped; `:2915` and `:2852` corrected the same
  way without a re-read, as were `CodeGenerationEngine.cs:770-772`); `PersistentWorkspaceManager.cs:3577-3578` (C4; prefill
  is the fixed `mode: symbol` only, so `part`); `WorkspaceReadNavigationImpl.cs:308-318` (TB-T2c; retry template has
  literal placeholders, so prefill `part`; E-SN10 `SymbolNavigationEngine.cs:2946` and the same-builder row changed the same
  way); `CodeTransformTools.cs:519` (TX2 split into TX2a, prefill `filePath` y, and TX2b for `:552`, `:592`, `:624`, `:649`,
  `:694`, whose per-site wording the later passes read individually; "Possible causes" dropped from the `:519` row); `BasicRefactoringEngine.cs` AnalyzeControlFlow
  throw is at `:2079` (one-line drift, rows now use ~`:2079-2080`, ~`:2122`, ~`:2525`). **Residual risk (before the follow-up passes):** Cand and Prefill
  marks in the groups that were not spot-checked carried the same risk; the follow-up passes re-read most of them, but the
  MemberRefactoringEngine rows (other than `:2538` and `:2547`) were not re-read. Survey reporters marked "Cand y" where a message only
  says "Searched:" or lists a fixed scope (the engine reporter's "genuine not-found" flag, not a candidate list), and marked
  prefill "y" for hints that carry literal placeholders. Treat unchecked `y` marks in Cand and Prefill as upper bounds.
- **Another-session work in flight.** `FindFileCandidates` and the `FileCandidate` overload landed at `5bc6cc8e`; the
  `WorkspaceFileEditImpl`, `WorkspaceReadNavigationImpl` and `DocumentLookup` rows may already read differently.
