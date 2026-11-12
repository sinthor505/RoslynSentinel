# Reference: inventory of every not-found error producer and what orientation each gives

**Status:** CURRENT 2026-11-12. Five read-only survey passes plus two follow-up read passes that closed every "not read" row (all rows now read, a few helper bodies excepted, see Gotchas); message text was read from source; line numbers measured 2026-11-12 at workspaceVersion 9-33 while other sessions were editing `MemberRefactoringEngine.cs`, `DocumentLookup.cs`, `FileNotFoundMessage.cs` and `WorkspaceReadNavigationImpl.cs`, so re-measure before editing.

**Rows updated so far:** batch D (Steps 5a, 5b, 5d) and batch E (Steps 4d, 5c-prep to 5c-C, 5e-1 to 5e-6, and the 5e breaker note
in section 3 "Not tool not-found errors") carry an `Updated <hash> (<slice>)` note in the Note column, with the previous text kept
after `Before:`; the file:line cells of those rows are still the survey's lines, which batch E moved by up to ~+135 lines in
`MemberRefactoringEngine.cs` (use the enclosing method, not the number). Rows with a `Left (<hash>)` note stayed non-lookup and
got a `// TODO` in source (checked by search for C6f, E-MRE7b, E-MRE8b, E-PS2 to E-PS5, E-SU3, E-CSW, E-IMP3, TB-T5b, TA7); rows
with no such note are unchanged since the survey. Still open after batch E: TB-T3b, TB-T2d / TB-T2e / TB-T2g, the reclassify-only
rows (E-BRE5c, E-BRE11, E-MRE10, E-INV2, E-TS3, E-TIL1, E-SU2), and Engines.Advanced, Tools.Experimental and GitImpl (plan Decision
9: out of plan scope). Section 8's counts were recomputed from the table after batch E.

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

Provenance and limits: the survey was read-only and ran no tests, so nothing in the survey columns says a message is covered by a
test; the `tests:` lists in `Updated` notes are the class names the commits report (see Gotchas, "Batch E update"). Every row has been read; the few helper bodies and base texts that were not opened are listed in Gotchas
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
| C2 | Project miss in `GetDocuments` | `DocumentLookup.cs:423` (call `:413`) | y | n | n | S, N, W | Sites 1. Updated 06f8a86e (5e-6): `ProjectNotFoundDetail.Build` (now `DocumentLookup.cs:443`): count fact, 'Did you mean X? Retry with scopeName: X' per similar project, or 'No loaded project has a similar name', then the loaded project names similar-first (cap 10, '(+N more)'). The retry is a parameter, not a tool call. Tests: QualifyCrefsProjectMissTests. Before: "Project '{key}' was not found in the loaded solution. Projects: {names}." All project names, unranked. |
| C3 | docCommentId shape check (SymbolNotResolved) | `SymbolWireResolver.cs` (`NotADocCommentIdText`) | n | y | part | N (example only), W, X | Sites 1. Re-read in the batch E update (the move predates it; hash not traced): the shape check now lives in `SymbolWireResolver.cs` (`NotADocCommentIdText`, reached from `ResolveAsync`); its text ends 'Find the symbol with {Search(mode: symbol)} and use the ID it returns', built with `ToolCallHint.Call(Search, mode: symbol)`, so it does name a call (fixed argument only). This closes the open question in section 5 (plan seed row 5 was right). The same text is reused by `DescribeUnresolvedIdAsync` (E-CONSTR, E-NFR, E-NAE2, E-SRE1). Before: "is not a documentation-comment ID ... for example 'T:MyApp.Orders.OrderService'". The survey reads the `Search(mode: symbol)` mention as an example, not a call; the plan seed row 5 says it names the call (see section 5). Re-verify. |
| C4 | docCommentId no longer resolves (SymbolNotResolved) | `SymbolWireResolver.cs` (`BuildNotFoundTextAsync`, `ResolveAsync`); forwarded by `PersistentWorkspaceManager.cs:3554-3562` | y | y | y | S, N, W, X | Sites 1. Updated e2fdf9b0 (4d), on top of the earlier Step 4c rewrite (hash not traced; the row below still described the pre-4c text): the unresolved id goes through `SymbolWireResolver.BuildNotFoundTextAsync` to `SymbolNotFoundDetail.BuildAsync` over the named project (or every non-test project with a scope fact when the name matches none): 'Did you mean one of these?' with name, kind, container and file per candidate, then 'If none of these is the symbol you meant, it does not exist in this solution.'; with no similar name, the no-similar-name fact plus one orientation call (GetFileOutline or ListAll). 4d: each candidate also states its exact docCommentId and carries the calling tool's own call with only the id corrected (`retryCall` delegate: RenameSymbol (callable-gated, a null result falls back to the Search call), DeleteUnusedSymbol, PreviewRenameImpact); `PersistentWorkspaceManager.cs` only forwards the delegate. Hints still name tools unconditionally where no ActiveToolSurface is in scope (TODO isCallable in `SymbolWireResolver`). Tests: DocCommentIdNotFoundCandidateTests, SymbolNotFoundDetailTests. Before: Prefill is a fixed argument only: the hint is `ToolCallHint.Call("Search", ("mode","symbol"))`, no query (spot-check). (Surfaced by RenameSymbol `RefactoringSignatureImpl.cs:117`, DeleteUnusedSymbol `WorkspaceProjectManagementImpl.cs:918`, PreviewRenameImpact `DiscoveryEngine.cs:542` rewrap). "Symbol '{id}' no longer resolves - may have been renamed, moved, or removed. Re-run Search(mode: symbol)." The id is echoed only. |
| C5a | Solution path resolve | `PersistentWorkspaceManager.cs:427` | y | n | n | S, W | Sites 1. Updated 06f8a86e (5e-6 lists it; the option sentence itself landed with d5667196, `PersistentWorkspaceManager.cs`): the Tried list is followed by 'LoadSolution accepts an absolute path, or a baseRepoDir to resolve the relative path against ...' as an option; `// TODO` to name ListWorkspaceSolutions once Common has an isCallable gate. Tests: SolutionPathMissTests (LoadSolution). Before: "Could not resolve relative solution path ... Tried: ..." The tried-paths list. |
| C5b | LoadSolution load failure under NotFound code | `PersistentWorkspaceManager.cs:915`, `:983` | n | n | n | - | Sites 2. Non-miss. "Solution '{path}' failed to load: ..." A load failure, not a lookup miss (misclassified). |
| C6a | Snippet match: empty snippet | `SnippetMatching/SnippetErrorFormatter.cs:122` | n | n | n | S, W | Sites 1. Updated 06f8a86e (5e-6): '<label> is empty or whitespace only, so there is nothing to look for and nothing was searched. No candidates exist: the file's windows are scored against the text of <label>, and it has none.' (facts only). Tests: SnippetMissFactsAndOptionsTests (ReplaceSnippet), SnippetErrorFormatterTests assertions rewritten. Before: "nothing was searched. No candidates exist". |
| C6b | Snippet match: no candidate | `SnippetErrorFormatter.cs:169` (BuildNoMatch) | n | n | n | W | Sites 1. Updated 06f8a86e (5e-6): the order 're-read the file and check the file path' is replaced by the fact 'No candidates: no window of the file reaches a similarity of 0.xx to your <label>, so there is no close text to show. The text does not appear in the file that was searched.' (or, when literal escape sequences explain it, 'Unescaping its literal escape sequences gives text that matches or nearly matches the file.'). Transcription diagnoses (escaped quotes, literal escapes, tool markup) still precede it. Tests: SnippetMissFactsAndOptionsTests (ReplaceSnippet). Before: "re-read the file and check the file path" (prose only). |
| C6c | Snippet match: closest windows | `SnippetErrorFormatter.cs:180` | y | n | n | W | Sites 1. Updated 06f8a86e (5e-6): 'copy verbatim' removed; the list is introduced as 'Closest windows in the file, best first (the file's own text, as written in the file):' with line and score per window. Tests: SnippetMissFactsAndOptionsTests (ReplaceSnippet), SnippetErrorFormatterTests. Before: Window line and score; "copy verbatim". |
| C6d | Snippet match: selector mismatch | `SnippetErrorFormatter.cs:333` (BuildSelectorMismatch) | y | n | y | S, C, W | Sites 1. Updated 06f8a86e (5e-6): '<label> was found (N match(es)), but not at the <selectors> you supplied; nothing was located.' then 'Candidates in file order (the file's own text, with the matchLine/withinMember values that select each):'; no ordering verb. Tests: SnippetMissFactsAndOptionsTests (ReplaceSnippet). Before: "was found (N), but not at the selector you supplied"; per-candidate matchLine / withinMember; prefills the selector values. |
| C6e | Snippet match: adjacent-line mismatch | `SnippetErrorFormatter.cs:355` (BuildAdjacentLineMismatch) | y | n | y | S, N, W | Sites 1. Updated 06f8a86e (5e-6): '<label> was found (N match(es)), but not next to the <selectors> you supplied; nothing was located. lineBefore/lineAfter must be the single real line immediately before/after the match.' then the candidates in file order with the values that select each. Tests: SnippetMissFactsAndOptionsTests (ReplaceSnippet), ContextHelperLineAfterTests assertions rewritten. Before: lineBefore / lineAfter per candidate. |
| C6f | Snippet match: invalid disambiguator | `SnippetErrorFormatter.cs:468`, `:474`, `:479` (BuildInvalidDisambiguator) | n | n | n | - | Sites 3. Non-miss. InvalidArgument-type text under the NotFound code (misclassified). Left (06f8a86e, 5e-6): non-miss (an invalid selector is InvalidArgument but is returned as ToolNotFoundException), `// TODO C6f` added at `BuildInvalidDisambiguator`. `BuildLooser`, `AmbiguityAdvice` and the escaped-quote, literal-escape and tool-markup diagnoses still order the caller; a `// TODO` there marks them for a later slice. |

#### Tools.Basic

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| TB-T1a | File miss in WorkspaceFileEditImpl (ReadFile via `BuildFileNotFoundError`, ReplaceSnippet single, ReplaceSnippetBatch) | `WorkspaceFileEditImpl.cs:544-545` (also `:695`), `:1229` (`lookup.ToResultError`), `:1551` (`lookup.Describe()`) | y | y | part | S, N, W, X | Sites 4. Same text as C1. |
| TB-T1b | ReadFileWithoutSolution | `WorkspaceFileEditImpl.cs:557` | n | y | n | S, W, X | Sites 1. Updated cebd67dd (5e-5): ''F' does not exist at 'ABS' (only that exact path was checked: no solution is loaded, so no search by file name or similar name ran).' then `SolutionNotLoadedMessage` (the LoadSolution call). Tests: WorkspaceLookupMissTests. Before: "'{file}' does not exist at '{absolutePath}'" plus `SolutionNotLoadedMessage` (`SolutionLoadState.cs:75-151`: "No solution is loaded. Call LoadSolution with a .sln, .slnx, or .csproj path."; the LoadCancelledAfterTimeout variant prefills `timeoutSeconds`). |
| TB-T1c | ReplaceSnippet on an empty file (`DescribeEmptyFile`) | `WorkspaceFileEditImpl.cs:1175` (helper `:927-945`) | n | y | part | S, X | Sites 1. Non-miss. "'{relativePath}' is empty, so there is no text for oldContent to match." Names `UndoLastApply` / `CreateFile` / `WriteFile(operation: ReplaceFile)`, callable-gated. Not a lookup miss. |
| TB-T1d | Rethrow with rejected-origin and rewrite notes | `WorkspaceFileEditImpl.cs:1311` (rejected-origin note; `:1278` is a comment), `:1316` (rewrite note), `:1614-1621` (batch per-edit catch; TODO at `:1614`) | n | n | n | S (file:line only, rewrite note) | Sites 3. Appends the rejected-origin note ("This oldContent comes from your rejected edit's newContent ... read it again before editing", no call) and the `RewrittenLineMemory.Explain` note (`RewrittenLineMemory.cs:103-135`: "F:N was rewritten by S earlier: it now reads 'X'." up to 3, then "Re-read the file or use the new text."; prose, no call). The batch catch appends both to "batchEdits[i] (F): " plus the base message, which comes from `ResolveEditMatch` to `SnippetErrorFormatter.Build` (the C6 family). `:1598` is a separate InvalidArgument branch (`DescribeLeakedNewContentMarkup`, not a not-found; helper body not read). |
| TB-T2a | File miss in ReadFile / GetMethodSource / GetFileOutline (`BuildFileNotFoundError`) | `WorkspaceReadNavigationImpl.cs` ~74-80 (callers ~259/264, 386/391; moved at v33) | y | y | part | C, N, W, X | Sites 1 (4 callers). Same text as C1 through a duplicate builder. The plan expects S; the survey marked C instead (not reconciled). |
| TB-T2b | GetMethodSource: method not in file (`BuildMethodNotFoundErrorAsync`, code `MethodNotFound`) | `WorkspaceReadNavigationImpl.cs:87-186` | y | y | part | S (tests included), N, K, W, X | Sites 1. "Method or constructor '{name}' not found in '{file}'", qualified-name hint, kind mismatch ("exists as a property/field/event/local function, not a method; read it with Member(operation: view, memberName)"), top-5 solution declarations ("declared in"), `Search(mode: method, query: name, exactMatch: false)` retry. Retry args: GetMethodSource template n; Search method y; Member view y. The best message in Tools.Basic. |
| TB-T2c | GetMethodSource: container miss | `WorkspaceReadNavigationImpl.cs:308-318` to `SyntaxTargetResolver.BuildContainerMissDetailAsync` (`SyntaxTargetResolver.cs:837`, core ~263-349, `BuildContainerNotFoundMessageAsync` 151-200) | y | y | part | S, N, C, W, X | Sites 1. Prefill is template placeholders only ({file}, {member}, {container}); request values are not inserted (spot-check). Whether the ReadFile line-range hint carries request values was not re-read. "Type 'C' declares no method or constructor named 'M' (searched the whole file; type arguments optional, case ignored)"; declared-in list (max 10, with lines); `GetFileOutline` when none; retry template max 3; ReadFile line range. Survey marked K; not carried (kind named in the text, no other-kind lookup shown). |
| TB-T2d | SyntaxRootNotFound | `WorkspaceReadNavigationImpl.cs:274`, `:401` | n | n | n | - | Sites 2. Non-miss. Not a `ToolErrorCode` value; an internal read failure. |
| TB-T2e | PageEnvelope: no pageable node | `WorkspaceReadNavigationImpl.cs:1769` | n | n | n | S | Sites 1. |
| TB-T2f | PageEnvelope: path miss | `WorkspaceReadNavigationImpl.cs:1781-1782` | y | y | y | S, N, W, X | Sites 1. Updated cebd67dd (5e-5): `DescribeMissingPagePath`: 'Path 'p' was not found in the stored result (N pageable path(s) searched; paths use property names separated by dots and [i] array indexes ...)', then pageable paths ranked by last segment ('Did you mean X? Retry with <LargeResultHints fetch call for X>') or 'No pageable path has a name similar to p' plus the fetch call for the first pageable path, then 'Pageable paths: ...'. Tests: LargeResultMissTests. Before: "Path '{p}' was not found in the stored result. Pageable paths: {list}" plus `LargeResultHints.FetchCall`. |
| TB-T2g | Stored result from older server | `WorkspaceReadNavigationImpl.cs:1886` | n | n | n | - | Sites 1. "repeat the original call" (prose). |
| TB-T2h | GetLargeResult: file not found | `WorkspaceReadNavigationImpl.cs:1952` | n | y | y | S, W, X | Sites 1. Updated cebd67dd (5e-5): `DescribeLargeResultMiss`: 'No stored result matches resultId R.' or 'filePath F is not a stored result: <which checks failed: outside the largeresults directory; name does not match largeresult_*.json; no file at that path>.' or 'Neither resultId nor filePath was given.', then 'The largeresults directory ... holds N stored result(s).' and 'Most recent resultId: R; <prefilled fetch call> pages it.' (one most-recent id, no similarity ranking). Tests: LargeResultMissTests. Before: "Result file not found. Supply a valid resultId or filePath ..." (`:1943` is SolutionNotLoaded, not NotFound.) |
| TB-T3a | Search(mode: symbol) / LocateSymbol | `SymbolNavigationImpl.cs:55-80` (message `:75`; hidden-test note `:62-70`) | n | y | n | S (test projects), N (advice only), W, X | Sites 1. "Symbol 'X' not found in the solution (project: P). Try exactMatch=false ... verify the symbol name and symbolKind, or call ListAll". The `exactMatch=false` advice is substring-only and cannot match a typo. Survey marked K; not carried (kind named). |
| TB-T3b | InspectSymbol: unresolved | `SymbolNavigationImpl.cs:131` | n | y | n | N, W | Sites 1. Generic unresolved text; names `GetMethodSource` / `GetFileOutline` in prose. Survey also marked K; not carried. Not updated in batch E: `SymbolNavigationImpl.cs` InspectSymbol unresolved (now the `// TODO error-policy` sites near `:160`, `:184`); a peer session owned the file during the batch. Still open (section 7). |
| TB-T4 | RenameSymbol: missing parameter list (`MissingParameterListResolver`) | `RefactoringSignatureImpl.cs:85-111` (`:117` maps SymbolNotResolved to NotFound = C4) | y | y | y | N (parameter list), X | Sites 1. Overload ids; the retry is the same tool with the full id. |
| TB-T5a | ListSolutionItems: project miss | `WorkspaceProjectManagementImpl.cs:90` | y | n | n | S, N, W | Sites 1. Updated cebd67dd (5e-5): 'ListSolutionItems(kind: files): ' plus `ProjectNotFoundDetail.Build` (count fact, ranked 'Retry with projectName: X' or the no-similar-name fact, loaded list). Tests: WorkspaceLookupMissTests. Before: "Project '{projectName}' not found." |
| TB-T5b | ListWorkspaceSolutions: directory miss | `WorkspaceProjectManagementImpl.cs:208` | n | n | n | - | Sites 1. Non-miss. "Directory not found" under code InvalidArgument (misclassified). Left (cebd67dd, 5e-5): non-miss; the text now states what exists at the path ('workspacePath P is not an existing directory (nothing exists at that path / P is a file)'), still coded InvalidArgument; `// TODO` added at the site (the cause is a missing directory, NotFound). Tests: WorkspaceLookupMissTests. |
| TB-T5c | LoadSolution failure catch | `WorkspaceProjectManagementImpl.cs:765` | n | y | n | X | Sites 1. NotFound adds `ToolCallHint.Next` `ListWorkspaceSolutions` (no args); otherwise "Check the server log..." plus `LoadSolution(forceReload: true)`. (`:918` DeleteUnusedSymbol is the C4 mapper.) |
| TB-T6a | ExternalFileDrift Acknowledge: names match no tracked change | `AdminTools.cs:159`, `:164` | y | y | part | S, N, W, X | Sites 2. Updated cebd67dd (5e-5): `UnmatchedDriftNamesText`: 'Nothing cleared: 'names' match no tracked change (names and relative paths are compared ignoring case; N change(s) tracked).', per unmatched name 'Did you mean X? Retry with files: X' (top 3, file-name similarity) or 'No tracked change has a name similar to n', then 'Tracked changes: ...' and ExternalFileDrift(operation: List) 'lists the full paths' (fixed argument; the retry is a parameter). The blank-names branch (:164) keeps its own text. Tests: ExternalFileDriftMissTests. Before: "Nothing cleared: '{names}' match no tracked change. Tracked changes: ... Call ExternalFileDrift(operation: List)". Names echoed at `:159` only. |
| TB-T6b | Ledger op: no ledger open | `AdminTools.cs:273` | n | n | n | S | Sites 1. "Nothing changed: no ledger is open." |
| TB-T6c | Ledger op: unknown entry ids | `AdminTools.cs:294` | y | y | part | S, N, W, X | Sites 1. Updated cebd67dd (5e-5): `UnknownLedgerIdsText`: 'Nothing changed: unknown entry id(s) ... (ids are compared ignoring case; the open ledger holds N entries).', per id 'Did you mean X? Retry with entryIds: X' or 'No ledger entry has an id similar to i', then 'Unresolved ids: ... <list call> shows the details.' (the list call text is a variable, as before). Tests: ExternalFileDriftMissTests. Before: "unknown entry id(s) ... Call {listCall}"; ids echoed; the call text is a variable. |
| TB-T7a | ProjectDoc: DocReadResult mapping fallback | `DocumentationTools.cs:15-30` | n | y | part | S (docType), X | Sites 1. Re-read cebd67dd (5e-5): the `Message` default (Document not found) at `DocReadResult` is unchanged; the live text it stands in for is now TB-T7d at `ToToolResult`. Tests: ProjectDocMissTests (TB-T7d). Before: The `Message` default at `:27` is only a fallback; the live text is `:601` (TB-T7d), used only when Error is null and Found is false (`:482`, migration-state.yaml). Other ProjectDoc misses (`:237`, `:263`, `:292`, `:308`) carry their own text. Zero-result: the file is absent. Prefill is the fixed `action: list` only. |
| TB-T7b | ProjectDoc: path-qualified miss | `DocumentationTools.cs:239-242` | y | y | y | S, N, W, X | Sites 1. Updated cebd67dd (5e-5): ''F' was not found under docType='D', nor at that path relative to the docs root. Names containing a directory separator are treated as explicit relative paths, so no basename fallback was attempted, and a same-named file in a different directory is not substituted.' then `DocMissFacts`: similar doc names ranked (the docType's own files first), each with a prefilled `ProjectDoc(action: read, docType, name)` retry, or 'No file under docs/ has a name similar to s (N file(s) searched)', then `ProjectDoc(action: list)` 'lists every file under docs/'. Tests: ProjectDocMissTests. Before: "'{filename}' was not found under docType ... no basename fallback ... Call ProjectDoc(action: "list")". Prefill is the fixed `action: list` only; the name is echoed. |
| TB-T7c | ProjectDoc: stem miss | `DocumentationTools.cs:308` | y | y | y | S, N, W, X | Sites 1. Updated cebd67dd (5e-5): 'No file matching 'stem' (with or without extension) was found under docType='D', or anywhere else under docs/.' then `DocMissFacts` (as TB-T7b). The Ambiguous branch still says 'Did you mean'. Tests: ProjectDocMissTests. Before: "No file matching '{stem}' (with or without extension) was found under docType=...". The Ambiguous branch returns "Did you mean". Prefill is the fixed `action: list` only; the name is echoed. |
| TB-T7d | ProjectDoc: document miss | `DocumentationTools.cs:601` | n | y | part | S, W, X | Sites 1. Updated cebd67dd (5e-5): ''name' (docType D) matched no document. ProjectDoc(action: list) lists the available files.' (a fact plus the call as an option; the old 'Use ProjectDoc(action: list)' order is gone). Tests: ProjectDocMissTests. Before: "Document not found: '{name}' (docType ...). Use ProjectDoc(action: "list")". Prefill is the fixed `action: list` only; the name is echoed. |
| TB-T8 | DeleteFile: path does not exist | `WholeFileWriteTools.cs:139` | y | y | y | S, N, W, X | Sites 1. Updated cebd67dd (5e-5): `DeleteFileMissTextAsync`: 'DeleteFile: 'P' does not exist on disk.' (or 'is a directory, not a file', or, with no solution loaded, 'no solution is loaded, so 'p' was not resolved to a path and no file was checked.'), 'Searched N loaded project(s) by exact path, bare file name and similar file name; path comparison ignores case.', then existing similar files each as 'Did you mean 'X'? Retry with DeleteFile(filePath: X)' or 'No existing file in the solution has a similar name.', then Search(mode: text) as an option when callable (query not prefilled). `// TODO` added: with no solution loaded the code is NotFound but the cause is SolutionNotLoaded. Tests: DeleteFileMissTests. Before: "'{path}' does not exist." plus a `Search(mode: "text")` hint; the pattern is not prefilled. |
| TB-T9 | Git: path / target not found (separate family) | `GitImpl.cs` sites 771-773, 815-817, 899-901, 1017, 1057-1058, 1404, 1422-1423, 1498-1500, 1883, 1897-1898, 3025-3026, 3043-3044, 3240 (codes `GitErrorCodes` 57, 71, mapped to NotFound at `:92`) | n | y | n | X (TargetNotFound only) | Sites 13. Hint `:115` names `GitRemote(operation: "tag")` for TargetNotFound; `:103` PathNotFound hint says to use repo-relative paths (prose). |

#### Engines.Basic - MemberRefactoringEngine.cs

Line numbers as of v12; sites after ~2431 had shifted +3 by the time of the later reads.

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| E-MRE1a | ConstructorParameter: class lookup | `MemberRefactoringEngine.cs:176`, `:441` | y | y | y | S, N, K, W, X | Sites 2. Updated d13ed771 (5c-A): `MemberRefactoringNotFound.Class`: 'Class 'C' was not found in 'F'.' with ranked class names ('Did you mean X? Retry with className: X.') or the no-similar-name fact plus a prefilled `GetFileOutline(filePath)`; with no class declared 'No class is declared in F' plus the outline call; plus the other-kind fact when the name is declared as a struct, interface, enum or record. Tests: MemberRefactoringMissClassEnumTests. Before: "Cannot edit: class not found." |
| E-MRE1b | ConstructorParameter: snippet not in a constructor | `:462` | y | y | y | S, C, W, X | Sites 1. Updated d13ed771 (5c-A): `SnippetOutsideConstructors`: 'contextSnippet is not inside any constructor of 'C' that declares parameter 'p'.' plus the start lines of the constructors that declare it and a prefilled `ReadFile(filePath, startLine)` for the first. Lists locations, not similar names. Tests: MemberRefactoringMissClassEnumTests. Before: "contextSnippet is not inside any constructor with parameter...". |
| E-MRE1c | ConstructorParameter: parameter miss | `:471` | y | y | y | S, N, C, W, X | Sites 1. Updated d13ed771 (5c-A): `ConstructorParameter`: 'Constructor parameter 'p' was not found in the constructors of 'C'.' with ranked parameter names ('Retry with paramName: X') or 'No constructor parameter in the constructors of C has a similar name', the declared list, the does-not-exist fact and a prefilled `GetFileOutline(filePath)`. Tests: MemberRefactoringMissClassEnumTests. Before: "Constructor parameter 'p' not found on 'C'." |
| E-MRE1d | GetConstructorParameters: class miss | `:575` | y | y | y | S, N, K, W, X | Sites 1. Updated d13ed771 (5c-A): the `Class` factory text (as E-MRE1a) followed by the kept 'Searched class, interface, struct, record and enum declarations matching this name and the contextSnippet/lineBefore/lineAfter filter.' The old 'Next: GetFileOutline' sentence is replaced by the factory's ranked retry or prefilled outline option. Tests: MemberRefactoringMissClassEnumTests. Before: "Class 'C' was not found in 'F'. Searched: class, interface, struct, record and enum declarations ... Next: GetFileOutline(filePath)". |
| E-MRE2a | ModifyEnum: enum miss | `:641` | y | y | y | S, N, K, W, X | Sites 1. Updated d13ed771 (5c-A): `MemberRefactoringNotFound.Enum`: 'Enum 'E' was not found in 'F'.' with ranked enum names ('Retry with enumName: X') or the no-similar-name fact plus a prefilled `GetFileOutline`; plus the other-kind fact when the name is declared as a class, struct, interface or record. Tests: MemberRefactoringMissClassEnumTests. Before: "Cannot edit: enum 'E' not found." |
| E-MRE2b | ModifyEnum: member miss | `:823`, `:847` | y | y | y | S, N, C, W, X | Sites 2. Updated d13ed771 (5c-A): `EnumMember`: 'Enum member 'M' was not found in enum 'E'.' with ranked member names of that enum ('Retry with memberName: X') or 'No enum member in enum E has a similar name', the declared members, the does-not-exist fact and a prefilled `GetFileOutline`. Tests: MemberRefactoringMissClassEnumTests. Before: "Cannot edit: member 'M' not found in enum 'E'." Names the enum. |
| E-MRE2c | AddEnumMember: insert anchor | `:789`, `:799` (`BuildInsertAnchorNotFoundMessage`, kind enum) | y | y | y | S, N, C, W, X | Sites 2. Updated 1df84f16 / fe4c572c: anchors ranked (similar first, cap 25), "Did you mean ... Retry with <param>" per candidate (top 3), the no-similar-name fact otherwise; `Member(operation: view)` with `containerName`. Facts and options only. |
| E-MRE3a | MethodSignature view | `:1165` | y | y | y | S, N, W, X | Sites 1. Updated 699e760d (5c-B1): the `Method` factory text (ranked method names with 'Retry with methodName: X', or the no-similar-name fact plus a prefilled `GetFileOutline`), then the kept 'Searched: method declarations matching this name and the contextSnippet/lineBefore/lineAfter filter.' and a prefilled `Search(mode: text, query: M)` stated as 'finds the name as text' (a fact; the old 'Next:' wording is gone). Tests: MemberRefactoringMissMethodTypeTests, CodeTransformMissMethodTypeTests (which class drives which site is not verified). Before: "Method 'M' was not found in 'F'. Searched: method declarations ... Next: Search(mode: text, query: M) or GetFileOutline(filePath)". |
| E-MRE3b | MethodSignature add / remove | `:1214`, `:1276` | y | y | y | S, N, W, X | Sites 2. Updated 699e760d (5c-B1): the `Method` factory (candidates or no-similar-name fact plus prefilled `GetFileOutline`, as E-MRE3a without the Search sentence). Tests: MemberRefactoringMissMethodTypeTests, CodeTransformMissMethodTypeTests. Before: "Cannot edit: method 'M' not found." Names the kind only. (`:1282` "has no parameters" is not a lookup miss.) |
| E-MRE4a | ChangeAccessibility, AddModifier, RemoveModifier, AddAttribute, ReplaceAttribute, RemoveAttribute | `:1594`, `:1647`, `:1698`, `:1780`, `:1851`, `:1917` | y | y | y | S, N, W, X | Sites 6. Updated 6efd5648 (5c-B2): `MemberRefactoringNotFound.Target`: 'Declaration 'T' was not found in 'F'.' with ranked names drawn from every member, enum member and type of the file ('Retry with targetName: X') or the no-similar-name fact plus a prefilled `GetFileOutline`; the exception keeps missing-target kind 'member'. Tests: MemberRefactoringMissTargetTests. Before: Six identical "Cannot edit: target not found." |
| E-MRE4b | ReplaceAttribute: attribute miss | `:1859` | y | y | y | S, N, C, W, X | Sites 1. Updated 6efd5648 (5c-B2): `MemberRefactoringNotFound.Attribute`: 'Attribute 'A' was not found in 'target'.' with ranked attribute names carried by the located target ('Retry with existingAttribute: X', the tool's parameter, not the engine's oldAttributeName) or the no-similar-name fact, the declared list, the does-not-exist fact and a prefilled `GetFileOutline`. `// TODO` added at RemoveAttribute: an attribute the target lacks removes nothing yet returns Modified. Tests: MemberRefactoringMissTargetTests. Before: "attribute 'A' not found on target." |
| E-MRE4c | Batch edit failures (`BatchEditFailure` NotFound) | `:929` (ApplyModifierBatch), `:1433` (ApplyAttributeBatch), `:5512` (ApplyBaseTypeBatch) | y | y | y | S, N, W, X | Sites 3. Updated 6efd5648 (5c-B2): `BatchMessage`: 'batchEdits[i] (name): Declaration 'name' was not found in 'F'.' (the base-type batch says Type) with ranked names ('Retry with batchEdits[i].targetName: X'; typeName for the base-type batch) or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests: MemberRefactoringMissTargetTests. Before: "batchEdits[i] (name): target not found." and "type not found." |
| E-MRE4d | Non-throw result messages | `:1713`, `:5464` | y | n | n | S, N, C, W | Sites 2. Updated 6efd5648 (5c-B2): result messages, not throws (the outcome stays AlreadyInTargetState): `ModifierMessage` ('Modifier 'm' was not found in 'target'.' with ranked modifiers and 'Retry with modifier: X', or the no-similar-name fact and the declared modifiers) and `BaseTypeMessage` (same shape over the type's base list, 'Retry with baseTypeName: X'). No file path is passed, so no `GetFileOutline` call. `// TODO` added at RemoveBaseType: a base list naming nothing matching falls through to Modified with unchanged text. Tests: MemberRefactoringMissTargetTests (engine-direct for the near-miss modifier). Before: "// Cannot edit: modifier not found.", "// Cannot edit: base type not found." |
| E-MRE5a | Container not found (InsertMemberAfter, InsertMemberBefore, AddMember) via `SyntaxTargetResolver.BuildContainerNotFoundMessageAsync` (`SyntaxTargetResolver.cs:118-200`) | `:1952`, `:2012`, `:2452` | y | n | n | S, N, C, W | Sites 3. Updated 580e2a55 / fe4c572c (5a-2a): types ranked with `NameCandidates` (similar first, "Did you mean 'X'? Retry with containerName: 'X'." per candidate, cap 3); with none "No type declared in this file has a similar name" plus a prefilled `GetFileOutline(filePath)` when the path is known; up to 3 other files declaring the name. "Pass one of those as containerName" stays (parameter option). Tool-level tests in `MemberInsertAnchorTests` (typo type, unknown type). `ComposeFailure` now keeps kind, name, snippet and candidates. |
| E-MRE5b | Insert anchor not found, container kind | `:2091` (`ResolveInsertAnchorIndex`; builder `:2055-2067`) | y | y | y | S, N, C, W, X | Sites 1. Updated 1df84f16 / fe4c572c (5a-2b): same as E-MRE2c; tool-level tests `Member_AddWithTypoAnchor_...` and `...NoSimilarMember...` in `MemberInsertAnchorTests`. |
| E-MRE5c | SortMembers: container miss | `:2225` | y | y | y | S, N, W, X | Sites 1. Updated 699e760d (5c-B1): `MemberRefactoringNotFound.Type` with retry parameter className (a commented deviation: the only tool reaching it, the sort_members transform, names its parameter className): 'Type 'T' was not found in 'F'.' with ranked type names or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests: MemberRefactoringMissMethodTypeTests, CodeTransformMissMethodTypeTests. Before: "Cannot edit: container not found." |
| E-MRE6a | AddRemoveParams: method miss | `:2285` | y | y | y | S, N, W, X | Sites 1. Updated 699e760d (5c-B1): the `Method` factory (as E-MRE3b); the former `ToolCallHint.Next(GetFileOutline)` sentence is replaced by the factory's ranked retry or its prefilled outline option. Tests: MemberRefactoringMissMethodTypeTests, CodeTransformMissMethodTypeTests. Before: "Method 'M' not found." plus `ToolCallHint.Next(GetFileOutline)` with filePath. |
| E-MRE6b | ReplaceMember (`:2338`) and RemoveMember (`:2670`) via `MemberNotFoundDetail.BuildAsync(includeHead: true)` | `:2338`, `:2670` (`MemberNotFoundDetail.cs:41-117`, similarity `:196-252`) | y | y | y | S, N, C, K, W, X | Sites 2. Top 3 similar, same-file members grouped by kind, other-file declarations; `ReadFile` / `GetFileOutline` (and `ReadFile(memberName)` for another file); prefills filePath and memberName. Updated e7446193 / fe4c572c (5a-1): a prefilled `ReadFile(filePath, memberName[, containerName])` per candidate; with no candidate "No member of <scope> has a similar name" and the does-not-exist fact. Tests in `MemberNotFoundDetailTests` (shared member read). |
| E-MRE6c | RemoveMember: type-level declaration | `:2664` | n | y | n | K, X | Sites 1. Updated 4a754d4f (5c-C): policy phrases stripped ('as a last resort' after the ReplaceSnippet and ApplyDiff options; 'then confirm the solution still compiles' after the direct-edit sentence); the callable-gated option calls (ModifyEnum, DeleteUnusedSymbol, ReplaceSnippet, ApplyDiff) stay. Only the changed fragments were read, not the whole message. Tests: MemberRefactoringMissMoveTests. Before: "'M' is a type-level declaration (kinds) that Member(remove) could not delete here..." Names `ModifyEnum` / `DeleteUnusedSymbol` / `ReplaceSnippet` / `ApplyDiff`, callable-gated. |
| E-MRE7a | AddTopLevelType: namespace miss | `:2538` | y | y | y | S, N, W, X | Sites 1. Updated 699e760d (5c-B1): `MemberRefactoringNotFound.Namespace`: 'Namespace 'N' was not found in 'F'.' with ranked namespace names ('Retry with namespaceName: X') or the no-similar-name fact plus a prefilled `GetFileOutline` (with no namespace declared, 'No namespace is declared in F' plus the outline call), then 'Namespaces declared in F: ...' (cap 15). Tests: MemberRefactoringMissMethodTypeTests, CodeTransformMissMethodTypeTests. Before: "Namespace 'N' not found. Available: ..." All namespaces, unranked. |
| E-MRE7b | AddTopLevelType: ambiguous namespace | `:2547` | y | n | n | - | Sites 1. Non-miss (ambiguous, coded NotFound: code mismatch). "File has N namespaces (...); pass namespaceName to disambiguate." Candidates unranked; the instruction is a parameter, not a call. Left (cd7bfcbe, 5c-prep): non-miss (several namespaces and no namespaceName is an ambiguous target coded NotFound); `// TODO` added at the throw (code it Ambiguous or InvalidArgument; a tool-level branch on the code may depend on it). |
| E-MRE8a | MoveMember: hand-rolled file / syntax-root / class lookups (bypass `DocumentLookup`, no bare-file-name rule) | `:2980` ("File 'F' not found."), `:2986` ("Failed to get syntax root"), `:2992` ("Class 'C' not found in 'F'.") | y | y | y | S, N, K, W, X | Sites 3. Updated cd7bfcbe (5c-prep, file), d13ed771 (5c-A, class), 4a754d4f (5c-C, syntax root): the file lookup is now `DocumentLookup.RequireDocument` (the C1 text: candidates, bare-file-name rule); the syntax-root site is `Unavailable('syntax root', F)`: 'The syntax root of F is not available: the file is a document of the loaded solution but did not produce one.' plus a prefilled `ReadFile(filePath)`; the class lookup is the `Class` factory (as E-MRE1a). `// TODO` added near the base-class check: a targetFilePath that matches no document yields an empty search, so a misspelt targetFilePath reads as 'no existing class' and a new class is synthesized (not reported as a miss). Tests: MoveMemberFileMissTests and MemberRefactoringNotFoundTests (file lookup, factories), MemberRefactoringMissClassEnumTests (class), MemberRefactoringMissMoveTests (syntax root). |
| E-MRE8b | MoveMember: not an existing base class | `:3140` | n | n | n | C | Sites 1. Non-miss. "Cannot move instance member(s) ... not an existing base class" (prose fix; not a lookup miss). Left (cd7bfcbe, 5c-prep): non-miss (a refusal coded NotFound: the target exists and is not a base class); `// TODO` added (prose fix only; the MemberRefactoringNotFound factories do not apply). |
| E-MRE8c | MoveMembersToBaseType and target resolution | `:3596` (external assembly), `:3602` ("Base class source file not found."), `:3608` ("Base class source document not found at 'F'."), `:3614` (syntax root), `:3620` ("Base class 'B' not found in 'F'."), `:3718` ("Could not resolve target class 'T' to rewrite call sites against it.") | y | y | y | S, N, W, X | Sites 6. Updated d13ed771 (5c-A) and 4a754d4f (5c-C). Conditional Cand. (only the base-class lookup lists names). `:3620` base class lookup is `Class(baseType.Name, baseRoot, baseFile, targetClassName)` (ranked names or the no-similar-name fact plus prefilled `GetFileOutline`); `:3596` external assembly is `Unresolved`: 'Base class 'B' is declared in an external assembly, so it has no source in the loaded solution to move members into.' plus a prefilled `GetFileOutline` of the source file; `:3602` no source path and `:3608` document not in the solution are `Unresolved` facts only (no file path, so no call); `:3614` syntax root is `Unavailable` plus a prefilled `ReadFile(baseFile)`; `:3718` target class is an `Unresolved` fact ('did not resolve to a symbol, so the call sites of the moved members cannot be rewritten against it') plus a prefilled `GetFileOutline` of the target document. Tests: MemberRefactoringMissClassEnumTests, MemberRefactoringMissMoveTests. |
| E-MRE8d | MoveInstanceMembers: qualified-ctor class miss | `:4142`, `:4200` | y | y | y | S, N, K, W, X | Sites 2. Updated d13ed771 (5c-A): the `Class` factory (as E-MRE1a): ranked class names or the no-similar-name fact plus prefilled `GetFileOutline`, and the other-kind fact. Tests: MemberRefactoringMissClassEnumTests. Before: "Class 'C' was not found in the file." (qualified `RoslynSentinel.Common.ToolNotFoundException(`). |
| E-MRE8e | PreviewInstanceMoveCallSites | `:4950` ("File 'F' not found."), `:4956` (syntax root), `:4962` ("Class 'C' not found in 'F'."), `:4971` (semantic model) | y | y | y | S, N, K, W, X | Sites 4. Updated cd7bfcbe (5c-prep, file: `DocumentLookup.RequireDocument`, the C1 text), d13ed771 (5c-A, class: the `Class` factory), 4a754d4f (5c-C, syntax root and semantic model: `Unavailable` plus a prefilled `ReadFile(filePath)`). Tests: MoveMemberFileMissTests (file), MemberRefactoringMissClassEnumTests (class), MemberRefactoringMissMoveTests (syntax root, semantic model). |
| E-MRE9 | ConvertMethodToProperty, MakeMethodStatic, AddBaseType, RemoveBaseType | `:4536`, `:4556` ("Method not found."), `:5408`, `:5455` ("Type not found.") | y | y | y | S, N, W, X | Sites 4. Updated 699e760d (5c-B1): the two method sites use the `Method` factory and the two type sites the `Type` factory: ranked names or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests: MemberRefactoringMissMethodTypeTests, CodeTransformMissMethodTypeTests (which class drives which site is not verified). |
| E-MRE10 | Syntax-root group (ToolInvalidArgumentException with "syntax root not found" wording; not NotFound) | `:207`, `:330`, `:429`, `:914`, `:1203`, `:1265`, `:1408`, `:1582`, `:1636`, `:1687`, `:1890`, `:5395`, `:5442`, `:5496` | n | n | n | - | Sites 14. Non-miss. Wording says not found; code is InvalidArgument. Left: non-miss (InvalidArgument wording); unchanged by batch E, no `// TODO` added. |
| E-MSEL | MoveMember members (`MemberSelection.SelectMembers`) | `MemberSelection.cs:287`, `:295` | y | y | part | S, N, W, X | Sites 2. Updated 12a0cc97 / fe4c572c (5a-3): found list ranked (similar first), "Did you mean 'X' for '<entry>'?" per missing entry with one prefilled retry (`MoveMember(memberNames: [...])`), with no candidate "No member of class 'C' has a similar name". `// TODO` x2: no `filePath` reaches `SelectMembers`, so no `GetFileOutline` prefill. Tests: `MoveMemberNotFoundTests` (MoveMember tool). |

The 30 `RequireDocument` sites in MemberRefactoringEngine are part of C1.

#### Engines.Basic - other A-M files

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| E-BRE1 | ChangeSignature (a `Refuse()`, not a throw) | `BasicRefactoringEngine.cs:251` | y | y | part | S, N, W, X | Sites 1. Updated 9c5fbc1c / fe4c572c (5b-1): original sentence kept, plus `DeclarationNotFoundDetail.Build` (ranked method and constructor names, "Retry with methodName: 'X'", or the no-similar-name fact and a prefilled `GetFileOutline`). Tool-level tests in `BasicRefactoringNotFoundTests`. (`:248` primary-constructor refusal is not a lookup miss.) |
| E-BRE2 | MoveAllTypesToFilesInProject: project miss | `:1452` | y | n | n | S, N, W | Sites 1. Updated 2f60a0a2 (5d-3a): `ProjectNotFoundDetail.Build` (count fact, "Did you mean ... Retry with projectName", or the no-similar-name fact, loaded project list similar-first, cap 10). Tool-level tests (`ProjectNameMissTests`, MoveAllTypesToFilesTool). |
| E-BRE3 | ExtractInterface: class miss | `:1500` | y | y | part | S, N, W, X | Sites 1. Updated 9c5fbc1c (5b-1): ranked class names or the no-similar-name fact plus prefilled `GetFileOutline`. Tool-level tests via `ExtractMembersTool(newType: interface)` in `ExtractInterfaceNotFoundTests` (0e556f10). |
| E-BRE4 | Indexer miss | `:1751` | n | y | y | S, W, X | Sites 1. Updated 9c5fbc1c (5b-1): "No indexer is declared in 'F'." plus a prefilled `GetFileOutline`. An indexer has no name, so no candidate case. ConvertIndexerToMethod has no tool: the test (0e556f10) is engine-direct. |
| E-BRE5a | Snippet lookup (extract local / constant): no match | `:1795` | y | n | n | W | Sites 1. Updated 06f8a86e (5e-6): Conditional Cand. The 'Context snippet not found.' fallback is reworded to 'contextSnippet matched no place in the file.'; the normal text still comes from `SnippetLookup.TryResolve` then `SnippetErrorFormatter.Build` (the C6 family, now facts and options). Observed in source, not changed: every TryResolve failure at this site throws ToolInvalidArgumentException, so a no-match here carries InvalidArgument, not NotFound. No listed test names this ExtractConstant site (`ExtractLocalVariableSnippetMissTests` is named for the local-variable site, E-BRE5b); a gap for the planner and reviewer, not a defect in the change. Before: Conditional Cand (candidates only when closest windows exist). Text comes from `SnippetLookup.TryResolve` then `SnippetErrorFormatter.Build` (the C6 family), not `ContextHelper`; the "?? Context snippet not found." fallback at `:1795` is unreachable. Ambiguous snippet outcomes under `TryResolve` are non-miss and coded InvalidArgument at these sites. |
| E-BRE5b | Snippet lookup (extract local / constant): no match with advice | `:1873` | y | y | y | S, W, X | Sites 1. Updated 06f8a86e (5e-6): Conditional Cand. The order-phrased advice ('Re-check the snippet against GetMethodSource/GetFileOutline output ... to disambiguate.') is replaced by the options 'GetFileOutline(filePath: F) shows what the file declares; lineBefore/lineAfter (the single line immediately before/after) narrow a match to one place.' (filePath prefilled); same InvalidArgument code as E-BRE5a. Tests: ExtractLocalVariableSnippetMissTests (by name). Before: (candidates were conditional) Same text as E-BRE5a plus " Re-check the snippet against GetMethodSource/GetFileOutline output, or add lineBefore/lineAfter ... to disambiguate." (tools in prose, no args). |
| E-BRE5c | Extract local / constant: ineligible or internal conditions | `:1802`, `:1808`, `:1898`, `:1905`, `:1912`, `:1918`, `:1986` | n | n | n | - | Sites 7. Non-miss (ineligible or internal; TargetIneligible). "literal not found", "containing type not found", "contextSnippet was found at line N, but it does not align to a single whole extractable expression", "containing method / statement / block not found", "statement index not found". Not updated (reclassify-only, still open). |
| E-BRE6 | AnalyzeControlFlow, AnalyzeDataFlow, GetSummaryComment ("Searched:" literals) | ~`:2079-2080` (spot-check: AnalyzeControlFlow throw is at `:2079`), ~`:2122`, ~`:2525` | y | y | y | S, N, W, X (updated ee2edf36, 5b-2: ranked names appended via `DeclarationNotFoundDetail.Build(includeOutlineCall: false)`; tool-level tests in `BasicRefactoringNameMissTests` through `ScanTools.AnalyzeMethodTool` and `SummaryCommentTool`) | Sites 3. "Method 'M' not found in 'F'. Searched: method declarations in the file by name. Next: GetFileOutline / Search(mode method)"; `:2525` "Target 'T' not found in 'F'. Searched: symbols named 'T' in the file. Next: Search(mode symbol) or GetFileOutline". Prefills filePath and query. No N. |
| E-BRE7 | Using directive miss | `:2277` | y | y | part | S, N, W, X | Sites 1. Updated 9e213c6b (5b-3): sentence kept plus ranked using names ("Retry with namespaceName: 'X'") or the no-similar-name fact, or "No using directive is declared in 'F'" plus `GetFileOutline`. Tool-level tests through `UsingDirectiveTool`. |
| E-BRE8 | Add / RemoveSummaryComment | `:2336`, `:2481` | y | y | part | S, N, W, X | Sites 2. Updated 9e213c6b (5b-3): sentence kept plus ranked declaration names (shared `DeclarationNamesIn`) or the no-similar-name fact with `GetFileOutline`. Tool-level tests through `SummaryCommentTool` add and remove. |
| E-BRE9a | WrapInTryCatch: no block / statements in line range | `:2567`, `:2573` | n | y | y | S, W, X | Sites 2. Updated a35cee2a (5e-4): 'No block body of 'F', which has N line(s), contains lines A-B. A constructor header, a field or an attribute list is not inside a block body. To see the line range of each member, call GetFileOutline(filePath)' and 'No statement of the block body that contains lines A-B of 'F' (which has N line(s)) overlaps that range. ...': the file's line count is stated and the outline call is prefilled. Tests: WrapRangeTryCatchMissAdvancedTests. Before: "Could not find a suitable block ... use GetFileOutline to find the lines." and "Could not find any statements in the specified range ... use GetFileOutline"; no arguments. |
| E-BRE9b | WrapInTryCatch: snippet variants | `:2642`, `:2659` | n | y | y | S, W, X | Sites 2. Updated a35cee2a (5e-4, read in source; the brief names only the line-range sites): 'The snippet matched at line N of 'F', which has M line(s), and no block body contains that line. ...' and '... inside a block body, but no statement of that block contains it. ...', each with a prefilled `GetFileOutline(filePath)`. Tests: WrapRangeTryCatchMissAdvancedTests (named for the line-range sites; whether it drives the snippet variants is not verified). Before: "... use GetFileOutline". |
| E-BRE10 | UpdateXmlDocsFromSignature | `:2786` | y | y | part | S, N, W, X | Sites 1. Updated ee2edf36 (5b-2): "Method 'M' not found in 'F'." plus ranked method names or the no-similar-name fact with `GetFileOutline`. Test is engine-direct: its only host tool (`CodeTransformTools.ApplyMethodCodeTransform`, Tools.Experimental) has no test factory. |
| E-BRE11 | Syntax-root group (InvalidArgument) | `:2199`, `:2269`, `:2325`, `:2470`, `:2514`, `:2553`, `:2624` | n | n | n | - | Sites 7. Non-miss. Not-found wording under InvalidArgument. Not updated (reclassify-only, still open). |
| E-DISC1 | DiscoveryEngine.FindProject | `DiscoveryEngine.cs:238` | y | n | n | S, N, W | Sites 1. Updated 187c9cdd (5e-1): `SolutionStructureEngine.ProjectNotFound` (`ProjectNotFoundDetail.Build`: count fact, ranked retry or the no-similar-name fact, loaded list). The phrase 'not found in solution' that `ScanTools` matched on (section 4) is gone from this site. Tests: DiscoveryMissTests, CodeTransformMissFileClassTests (per-row mapping not verified). Before: "Project 'P' not found in solution." (the phrase `ScanTools` matches on; section 4). |
| E-DISC2 | DiscoveryEngine file misses | `:400`, `:557` | y | y | part | S, N, C, W, X | Sites 2. Updated 187c9cdd (5e-1): both sites now `DocumentLookup.RequireDocument` (the C1 text). Tests: DiscoveryMissTests (per-row mapping not verified). Before: "File not found: {f}". |
| E-DISC3 | DiscoveryEngine.RequireSingle | `:402` | y | y | y | S, N, W, X | Sites 1. Updated 187c9cdd (5e-1): `MemberRefactoringNotFound.Declaration('type', C, 'containerName', type names of the file, F)`: ranked names ('Retry with containerName: X') or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests: DiscoveryMissTests (per-row mapping not verified). Before: "Type 'C' not found." |
| E-DISC4 | PreviewRenameImpact resolver rewrap | `:542-543` | n | y | n | S, X | Sites 1. Updated e2fdf9b0 (4d): `PreviewRenameImpactAsync` takes an optional `retryCall` and forwards it to the resolver, so the C4 text it rewraps now carries each candidate's exact docCommentId and a prefilled PreviewRenameImpact retry; the rewrap itself (and the raw `InvalidOperationException` for other codes) is unchanged. Tests: DocCommentIdNotFoundCandidateTests, SymbolNotFoundDetailTests. Before: SymbolNotResolved becomes `ToolNotFound(message)` with the C4 text; any other code becomes a raw `InvalidOperationException` (leak, section 6). |
| E-DISC5 | DiscoveryEngine: project miss | `:609` | y | n | n | S, N, W | Sites 1. Updated 187c9cdd (5e-1): `SolutionStructureEngine.ProjectNotFound` (as E-DISC1). Tests: DiscoveryMissTests (per-row mapping not verified). Before: "Project 'P' not found in the loaded solution." |
| E-DIAG | DiagnosticEngine | `DiagnosticEngine.cs:38` ("File not found: {f}"), `:53` ("Project not found.") | y | y | part | S, N, C, W, X | Sites 2. Updated 7c19972f (5e-2): the file miss is now `DocumentLookup.RequireDocument` (the C1 text); the project miss is `SolutionStructureEngine.ProjectNotFound` (ranked retry or the no-similar-name fact, loaded list). Tests: DiagnosticMissTests. |
| E-CONSTR | ConstructionReplacer | `ConstructionReplacer.cs:46`, `:84` | y | y | y | S, N, W, X | Sites 2. Updated 30bf3efd (5e-3): ''p: docCommentId 'ID' resolves to no symbol in any project of the loaded solution.' plus `SymbolWireResolver.DescribeUnresolvedIdAsync`: ranked similar symbols each with its exact docCommentId and the calling tool's own SemanticFindReplace call with the id corrected (a preview, so the retry writes nothing), or the no-similar-name fact plus one orientation call; an id with no kind prefix gets the doc-comment-id shape fact (C3). Tests: SemanticReplaceUnresolvedIdTests. Before: "symbol: no symbol found for docCommentId '{id}'. {SymbolHint}"; `:84` is the factory variant. Names `Search(mode symbol)` / constructor hint. |
| E-INV1 | InventoryEngine: file not in solution | `InventoryEngine.cs:36`, `:64` | y | y | part | S, N, W, X | Sites 2. Updated 7c19972f (5e-2): `FileMiss`: the shared file-miss text (`lookup.Describe()`: candidates or the no-similar-name fact with an orientation option) followed by 'Disk and load facts: existsOnDisk=B, projectsLoaded=N. A project that failed to load leaves its files out of the solution.' The phrase 'File not found in solution' is gone from these sites (section 4). Tests: InventoryMissAdvancedTests. Before: "File not found in solution: {p} (existsOnDisk, projectsLoaded=N). The owning project may have failed to load..." (the phrase `ScanTools` may match on; section 4). |
| E-INV2 | InventoryEngine: syntax root | `:43`, `:71` | n | n | n | - | Sites 2. Non-miss. Raw `InvalidOperationException` "Syntax root not found." (not a ToolException). |
| E-IMP1 | ImpactAnalyzer: document | `ImpactAnalyzer.cs:39` (no code, surfaced as NotFound), `:201` (ToolNotFound) | y | y | part | S, N, W, X | Sites 2. Updated 7c19972f (5e-2): `:39` uses `documentLookup.Describe()` (the C1 text) in the returned `Error`, with a `// TODO` that this lookup miss carries no ErrorCode (it reads as Exception); `:201` is `DocumentLookup.RequireDocument` (C1). Tests: ImpactMissAdvancedTests. Before: "Document not found: {f}". |
| E-IMP2 | ImpactAnalyzer: no symbol at position | `:73`, `:226` | n | y | y | S, W, X | Sites 2. Updated 7c19972f (5e-2): `:73` 'No symbol was found for the snippet matched at line N of 'F': the snippet matched there, but its innermost node (K) has no declared or referenced symbol. To see what the file declares, call GetFileOutline(filePath). A snippet on a declaration, or one narrowed with lineBefore/lineAfter, resolves to a symbol.' (coded NotFound); `:226` `MemberRefactoringNotFound.Unresolved`: 'No symbol was found at line L, column C of F: the node there (K) has no declared or referenced symbol.' plus a prefilled `GetFileOutline`. Tests: ImpactMissAdvancedTests. Before: "No symbol found at the specified position ..." (reached by InspectSymbol blastRadius `SymbolNavigationImpl.cs:154`). |
| E-IMP3 | ImpactAnalyzer: symbol not declared | `:79` | n | n | n | N (advice only), W | Sites 1. Updated 7c19972f (5e-2): text now states the cause ('... is not declared in this solution: the snippet resolved to a referenced (metadata) symbol such as a library type or constructor, which has no source references to report. A snippet on the declared name, or one narrowed with lineBefore/lineAfter, resolves to a source symbol.', coded NotFound); still no candidates or call. Left: `// TODO` added to offer ranked candidates (SymbolNotFoundDetail.FindCandidatesAsync over the solution's source symbols). Tests: ImpactMissAdvancedTests. Before: "Not found: '{sym}' is not declared in this solution ... Pass the declared name with lineBefore/lineAfter" (advice, no call). |
| E-IMP4 | ImpactAnalyzer.GetDataFlow | `:153` (was `:152`) | y | y | part | S, N, W, X | Sites 1. Updated 7c19972f (5e-2): `DocumentLookup.RequireDocument` (the C1 text). Tests: ImpactMissAdvancedTests. Before: "Document not found: {filePath}". |
| E-MSA | MsToolAugmentEngine | `MsToolAugmentEngine.cs:122` ("Field 'f' not found in file."), `:1261` ("Type 't' not found in file."), `:1474` ("No method named '{contextSnippet}' found in file."), `:1876` ("File not found: {f}") | y | y | y | S, N, W, X | Sites 4. Updated a35cee2a (5e-4): `:122` field -> `DeclarationMessage('field', f, 'fieldName', declared fields, F)`; `:1261` type -> `DeclarationMessage('type', t, 'typeName', ...)`; `:1474` method -> `DeclarationMessage('method', <snippet>, 'contextSnippet', method names, F)` plus 'With extractEntireBody: true, contextSnippet is read as the name of the method whose body is extracted.' (the wrong-name-slot finding of section 6 is now stated, not removed); `:1876` file -> `FileNotFoundMessage.Build(...)` with `DocumentLookup.FindFileCandidates` / `FindDeclaringTypeFiles` (a directory fact, same-name and similar files, or the no-similar-name fact; with no solution loaded it states 0 projects), `// TODO isCallable`. Each ranked text gives 'Retry with <param>: X' or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests (engine-direct): MsToolAugmentEncapsulateFieldMissTests (`:122`), MsToolAugmentGenerateToStringMissTests (`:1261`), MsToolAugmentExtractMethodMissTests (`:1474`); no listed test names the `:1876` file-miss site. Before: At `:1474` the name slot holds the snippet (section 6). |
| E-CSW | ChangeSignatureWrapper | `ChangeSignatureWrapper.cs:30` | n | y | part | X | Sites 1. "could not resolve 'M' to a method or constructor symbol." then "Retry with" `ChangeSignature(oldSignature: "remove", ...)` (literal arguments). Left (30bf3efd, 5e-3): `// TODO` added (the declared method names are not in scope where the symbol is missing, so no ranked candidates). |
| E-NAE1 | NamedArgumentsEngine: project | `NamedArgumentsEngine.cs:144` | y | y | n | S, N, W | Sites 1. Updated a35cee2a (5e-4): `ProjectNotFoundDetail.Build(scopeName, loaded projects, 'scopeName')`: count fact, ranked 'Retry with scopeName: X' or the no-similar-name fact, loaded list (a retry parameter, no tool call; coded NotFound). Tests: NamedArgumentsMissTests. Before: "No project named ..." plus the available list. |
| E-NAE2 | NamedArgumentsEngine: file / symbol | `:164`, `:194` | y | y | y | S, N, W, X | Sites 2. Updated a35cee2a (5e-4): the file miss is `lookup.Describe()` (the C1 text, `// TODO isCallable`); a document with no file path gets its own fact ('resolved to a document that has no file path, so no document of the loaded solution has that path'); the symbol miss is 'targetSymbol: docCommentId 'ID' resolves to no symbol in any project of the loaded solution.' plus `DescribeUnresolvedIdAsync` with a prefilled NamedArguments retry (scope, scopeName, targetSymbol corrected, mode preview). Tests: NamedArgumentsMissTests. Before: "File ... is not part of the loaded solution."; "No symbol found for targetSymbol docCommentId". |
| E-NFR | NullForgivingRemover | `NullForgivingRemover.cs:42` | y | y | y | S, N, W, X | Sites 1. Updated 30bf3efd (5e-3): 'symbol: docCommentId 'ID' resolves to no symbol in any project of the loaded solution.' plus `DescribeUnresolvedIdAsync` (ranked candidates with exact ids and the calling tool's own retry call, or the no-similar-name fact plus an orientation call). Tests: SemanticReplaceUnresolvedIdTests. Before: "no symbol found for docCommentId 'X' in any project of the loaded solution ... (from Search(mode: symbol) or Search(mode: property))"; two Search calls, `mode` literal. |
| E-SRE1 | SemanticReplaceEngine: symbol | `SemanticReplaceEngine.cs:64` | y | y | y | S, N, W, X | Sites 1. Updated 30bf3efd (5e-3): 'docCommentId 'ID' resolves to no symbol in any project of the loaded solution.' plus `DescribeUnresolvedIdAsync`; the candidate retry is a prefilled SemanticFindReplace(operation: invertBoolean, symbol, newName, mode: preview) only when newName was given, otherwise the neutral Search call. Tests: SemanticReplaceUnresolvedIdTests. Before: "No symbol found for docCommentId". |
| E-SRE2 | SemanticReplaceEngine: document | `:1256` | y | y | part | S, N, W, X | Sites 1. Updated 30bf3efd (5e-3): 'A reference to the symbol sits in 'F', which is not a document of the loaded solution, so the edit cannot be planned there.' plus `lookup.Describe()` (the C1 text); the code stays TargetIneligible (commented: the file is not a document of the solution). Tests: SemanticReplaceUnresolvedIdTests (by name; whether it drives this document site is not verified). Before: "Document not found for file" under TargetIneligible (code and message disagree). |
| E-TCT | TestCategoryTaggingEngine | `TestCategoryTaggingEngine.cs:155` | y | y | n | S, N, W | Sites 1. Updated a35cee2a (5e-4): `ProjectNotFoundDetail.Build(entry, every C# project, 'testScope')` (ranked 'Retry with testScope: X' or the no-similar-name fact, loaded list). Tests: TestCategoryTaggingMissTests. Before: "testScope names no project called ..." plus a list. |
| E-TIL1 | TestInstanceLauncher: precondition | `TestInstanceLauncher.cs:347` | n | n | n | - | Sites 1. Non-miss. No server dll / apphost; a precondition, misclassified. |
| E-TIL2 | TestInstanceLauncher: pid not tracked | `:423` | n | y | y | S, X | Sites 1. Updated a35cee2a (5e-4): 'No test instance with pid N is tracked by this server.' plus 'Tracked pids: ...' (or 'No test instance is tracked at all.') and McpServerControl(operation: ListTestInstances) 'lists the running ones.' (a pid is exact, so no similarity ranking; the tracked pids are listed as a fact). Tests: TestInstanceStopMissTests. Before: Names `McpServerControl` ListTestInstances. |
| E-TIL3 | TestInstanceLauncher: build skipped | `:535` | n | y | n | X (prose) | Sites 1. "build Skip but no output"; names Build / LaunchTestInstance in prose. |
| E-TRE | TestRunEngine | `TestRunEngine.cs:136` | y | y | n | S, N, W | Sites 1. Updated a35cee2a (5e-4): `ProjectNotFoundDetail.Build(scopeName, loaded projects, 'scopeName')` inside the EngineError (ranked retry or the no-similar-name fact, loaded list); the code stays EngineError InvalidInput. Tests: RunTestMissTests. Before: "Project ... was not found in the loaded solution." EngineError InvalidInput, not ToolNotFound. |

Not producers (verification strings, hints or warnings that contain the words): `ChangeSignatureCallBindingCheck.cs:172`,
`MoveMemberCallBindingCheck.cs:85,158,159`, `MovePositionMap.cs:569`, `CompilerErrorLookupHelper.cs:387`,
`DocTrailerEngine.cs:772`.

#### Engines.Basic - N-Z files

| # | Tool/op | file:line | Cand | Next call named | Prefill | Rule items | Note |
| --- | --- | --- | --- | --- | --- | --- | --- |
| E-SN1 | SymbolNavigationEngine: method miss | `SymbolNavigationEngine.cs:1224`, `:1357` | y | y | y | S, N, W, X | Sites 2. Updated 187c9cdd (5e-1): `MemberRefactoringNotFound.Method(methodName, root, filePath)`: 'Method 'M' was not found in 'F'.' with ranked method names ('Retry with methodName: X') or the no-similar-name fact plus a prefilled `GetFileOutline`. The separate 'Method 'M' in 'F' has no resolvable symbol. Next: GetFileOutline ...' throws that follow it in both methods are not in this table and were not changed. Tests: SymbolNavigationMissTests, SymbolNavigationMissAdvancedTests. Before: "Method 'M' not found in 'F'. Ensure the method name exactly matches (case-sensitive). Next: Search(mode: method, query: M) or GetFileOutline(filePath: F)". |
| E-SN2 | Snippet no match, wrapped with a FindReferences next call | `:1569` (kind: callers), `:1925` (kind: implementations) | y | y | y | W (inherited), X | Sites 2. Conditional Cand (the C6 closest windows, when any). Snippet NoMatch text plus " Next: FindReferences(symbolName, kind: callers, filePath, contextSnippet: <decl line>)" (empty when the file holds no declaration); the declaration line is derived. Both catch only `ToolNotFoundException`, so ambiguous snippet outcomes propagate as `ToolAmbiguousMatchException`. Hypothesis: `FindReferences` declares contextSnippet schema-required (`SymbolRelationshipTools.cs:70`) while `FormatFindReferencesNextCall` (~`:2692`) omits it for type targets, so the rendered call may carry a placeholder. |
| E-SN3 | Snippet did not resolve | `:1575-1590` | y | y | y | S, X | Sites 1. "contextSnippet did not resolve to a symbol in 'F'. This is NOT a confirmed zero-references result ... WAS found declared at: line N" (up to 3, name-only candidates) plus a `FindReferences` call. |
| E-SN4 | symbolName not declared in filePath | `:1619-1622` | y | y | y | S, N, W, X | Sites 1. Updated 187c9cdd (5e-1): `DeclarationMessage('symbol', X, 'symbolName', names declared in the file, F)`: ranked names ('Retry with symbolName: X') or the no-similar-name fact plus a prefilled `GetFileOutline`, then 'The lookup never ran, so this is not a zero-references result. Omitting filePath resolves symbolName by name across the solution.' Tests: SymbolNavigationMissTests, SymbolNavigationMissAdvancedTests. Before: "symbolName 'X' was not found declared in 'F'. This is NOT a confirmed zero-references result ... Verify the name against GetFileOutline(filePath: F)". |
| E-SN5 | contextSnippet no match | `:1710-1716` | y | y | y | S, W, K, X | Sites 1. Up to 10 ranked candidates plus a count of the rest; `FindReferences` for the best candidate with the verbatim line; a type hint (K). |
| E-SN6 | Unresolved anywhere | `:1744-1749` | y | y | y | S, N, W, K, X | Sites 1. Updated 187c9cdd (5e-1): after 'symbolName X could not be resolved anywhere in the solution ... The lookup never ran, so this is not a zero-references result.' the detail is `DescribeSymbolMissInSolutionAsync` (`SymbolNotFoundDetail.BuildAsync` over every project): ranked candidates (name, kind, container, file) each with a prefilled `FindReferences(symbolName, kind: callers, filePath)` call, or the no-similar-name fact plus one orientation call; `DescribeNearMissCandidatesAsync` is `[Obsolete]`. Candidates are no longer conditional. Tests: SymbolNavigationMissTests. Before: (candidates were conditional) "symbolName X could not be resolved anywhere in the solution ... Verify via Search(mode: symbol)/GetFileOutline ... Did you mean: ...?" Candidate only when a near-miss exists; the generic call has a fixed mode, the near-miss call is request-derived. |
| E-SN7a | FindImplementations: snippet did not resolve | `:2068-2080` | y | y | y | S, W, K, X | Sites 1. "contextSnippet did not resolve ... candidate location(s)" plus `FindReferences(kind: implementations)`. |
| E-SN7b | FindImplementations: unresolved anywhere | `:2137-2145` | y | y | y | S, N, W, K, X | Sites 1. Updated 187c9cdd (5e-1): the same `DescribeSymbolMissInSolutionAsync` detail with kind: implementations (a prefilled `FindReferences(kind: implementations)` per candidate), after 'The lookup never ran, so this is not a zero-implementations result.'; a scoped-resolution failure that already names candidates skips the detail. Candidates are no longer conditional. Tests: SymbolNavigationMissTests. Before: (candidates were conditional) "could not be resolved anywhere in the solution ... NOT a confirmed zero-implementations result ... Verify via Search(mode: symbol)/GetFileOutline"; candidate only when a near-miss exists. |
| E-SN8 | VariableLifetimeReport | `:2295` | y | n | n | S, N, C, W | Sites 1. Updated 187c9cdd (5e-1): `DescribeVariableMiss`: 'Variable 'v' was not found near line N in 'F' (searched variable declarations and parameters on that line, then variable declarations within 5 lines of it).' plus ranked names from the member containing the line, each with its declaration line ('Retry with variableName: X. It is declared at line L; lineNumber: L selects it.'), or the no-similar-name fact with the declared names (cap 15). No call here: the calling tool appends its own GetFileOutline (TA7). Tests: SymbolNavigationMissTests (by name). Before: NotFound. |
| E-SN9 | Container not found | `:2869` | y | y | y | S, N, W, X | Sites 1. Updated 187c9cdd (5e-1): `DeclarationMessage('container', C, 'containerName', type names of the file, F)` (ranked 'Retry with containerName: X' or the no-similar-name fact plus a prefilled `GetFileOutline`), the kept 'Searched: class, interface, struct, record and enum declarations matching this name and the contextSnippet/lineBefore/lineAfter filter.', and `Search(mode: text, query: C)` stated as 'finds the name as text'. Tests: SymbolNavigationMissTests, SymbolNavigationMissAdvancedTests. Before: "Container 'C' was not found in 'F'. Searched: class, interface, struct, record and enum declarations matching this name ... Next: GetFileOutline(filePath: F)"; filePath and query prefilled. |
| E-SN10 | Container miss via `BuildContainerMissDetailAsync` | `:2946` | y | y | part | S, N, C, W, X | Sites 1. Same builder and text as TB-T2c, so the same placeholder-only prefill caveat. |
| E-SN11 | Member miss via `MemberNotFoundDetail.BuildAsync` | `:2966` | y | y | y | S, N, C, K, W, X | Sites 1. Member read behind `ReadFile(memberName)` / `Member(view)`; no "no similar name" sentence. |
| E-SN12 | Bare member miss | `:2976` | n | n | n | - | Sites 1. "Member 'M' not found". |
| E-SN13 | TypeHierarchyReport.Error (returned, not thrown) | `:3129` | y | y | y | S, N, W, X | Sites 1. Updated 187c9cdd (5e-1): `SymbolNotFoundDetail.BuildAsync` over the searched projects (kind type): ranked candidates each with a prefilled `GetTypeInfo(typeName)` call (type candidates only), or the no-similar-name fact plus one orientation call; returned as `TypeHierarchyReport.Error`. The caller `SymbolNavigationImpl.GetTypeInfo` (read now) maps `hierarchy.Error` to code InvalidArgument; a `// TODO` there says it is a lookup miss. Tests: SymbolNavigationMissTests. Before: "Type 'T' not found in solution." Callers of `TypeHierarchyReport.Error` were not read. |
| E-PS1 | ProjectStructureEngine: file misses | `ProjectStructureEngine.cs:23`, `:543` | y | y | part | S, N, C, W, X | Sites 2. Updated 187c9cdd (5e-1): both sites now `DocumentLookup.RequireDocument` (the C1 text). Tests: ProjectStructureMissTests, ProjectStructureMissAdvancedTests. Before: "File not found: {f}". |
| E-PS2 | Outside project directory | `:30` | n | y | n | X | Sites 1. Non-miss (precondition). "File 'F' is outside the project directory ... Next: ListAll to confirm the owning project"; no args. Left (187c9cdd, 5e-1): non-miss, precondition (the file resolved but lies outside the project directory); `// TODO` added (reclassify as ToolTargetIneligibleException). |
| E-PS3 | No namespace declaration | `:68` | n | y | n | S, X | Sites 1. Non-miss (zero-result). "No namespace declaration found in 'F' ... Next: ReadFile"; no args. Left (187c9cdd, 5e-1): non-miss, zero-result (the file declares no namespace); `// TODO` added (reclassify as ToolTargetIneligibleException). |
| E-PS4 | Project directory not found | `:77` | n | y | n | X | Sites 1. Non-miss (precondition). "Project directory not found for 'F'. Next: ListAll"; no args. Left (187c9cdd, 5e-1): non-miss, precondition (the project has no directory on disk); `// TODO` added (reclassify as ToolTargetIneligibleException). |
| E-PS5 | Project / class / solution-path misses | `:385`, `:545`, `:1063`, `:1515`, `:1540`, `:1797`, `:1798`, `:1886` | y | y | y | S, N, W, X | Sites 8. Updated 187c9cdd (5e-1): Conditional Cand. The project misses (`:385`, `:1063`, `:1515`, `:1540`, `:1886`) now use `SolutionStructureEngine.ProjectNotFound` (`ProjectNotFoundDetail.Build`: count fact, ranked retry or the no-similar-name fact, loaded list; the helper is declared in `ProjectStructureEngine.cs` on class SolutionStructureEngine); the class miss (`:545`) is `MemberRefactoringNotFound.Class` (ranked names or no-similar-name fact plus prefilled `GetFileOutline`); the two 'Solution path not found.' throws (`:1797`, `:1798`) are Left: server-state preconditions, not name lookups, `// TODO` added. Tests: ProjectStructureMissTests, ProjectStructureMissAdvancedTests. Before: `:385` "Project 'P' not found in the solution.", `:545` "Class not found.", `:1063` "Project ... not found in the loaded solution.", `:1515`/`:1540` "Project ... not found.", `:1797`/`:1798` "Solution path not found.", `:1886` "Source project not found." (`:62`, `:142` are `RequireDocument` = C1.) |
| E-SR1 | StructuralRefinementEngine: top-level type miss | `StructuralRefinementEngine.cs:55` | y | y | y | S, N, W, X | Sites 1. Updated 7c19972f (5e-2): `DeclarationMessage('top-level type', T, 'targetTypeName', top-level type names, F)` (ranked 'Retry with targetTypeName: X' or the no-similar-name fact plus a prefilled `GetFileOutline`), then 'Top-level types declared in F: ...' and, when T exists only nested, 'T is declared in F only as a nested type; this tool syncs the file name with a top-level type.' Tests: StructuralRefinementMissAdvancedTests. Before: "No top-level type named 'T' found in 'F'. Top-level types present: ... Next: Search(mode: class, query: T)"; all top-level types, unranked; the query is a request value, the mode is fixed. |
| E-SR2 | No type declaration | `:65` | n | y | n | S, X | Sites 1. Non-miss (zero-result). "No type declaration found in 'F'. Next: ReadFile" with no args. Left (7c19972f, 5e-2): non-miss (zero-result); the text still ends 'Next: ReadFile ...' with no arguments; no `// TODO` at the site. |
| E-SR3 | File not in loaded solution | `:109`, `:168`, `:349` | y | y | part | S, N, C, W, X | Sites 3. Updated 7c19972f (5e-2): the three sites now use `DocumentLookup.RequireDocument` (the C1 text). Tests: StructuralRefinementMissAdvancedTests. Before: "File 'F' is not in the loaded solution. Next: ListAll"; no args. |
| E-SR4 | No symbol at line/column | `:122` | n | y | y | S, W, X | Sites 1. Updated 7c19972f (5e-2): `MemberRefactoringNotFound.Unresolved`: 'No symbol was found at line L, column C of F: the node there (K) has no declared or referenced symbol.' plus a prefilled `GetFileOutline(filePath)`. Tests: StructuralRefinementMissAdvancedTests. Before: "Next: ReadFile". |
| E-SR5 | No symbol named (branch A of the block now read at ~`:211`) | `:175` | y | y | y | S, N, W, X | Sites 1. Updated 7c19972f (5e-2): with no candidate declaration, `MemberRefactoringNotFound.Declaration('symbol', X, 'symbolName', every declared name of the file, F)`: ranked 'Retry with symbolName: X' or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests: StructuralRefinementMissAdvancedTests. Before: "No symbol named 'X' found in 'F'. Next: Search(mode: symbol, query: X)". Line drifted: read again at `:211`. |
| E-SR6 | Declarations do not match contextSnippet (branch B, read at ~`:211`) | `:183` | y | y | y | S, W, X | Sites 1. Updated 7c19972f (5e-2): ''X' has N declaration(s) in 'F', starting at line L1, L2, and contextSnippet matched none of them. ReadFile(filePath, startLine: first) reads the first one.' (start lines listed, a prefilled `ReadFile`). Tests: StructuralRefinementMissAdvancedTests. Before: "'X' has N declaration(s) in 'F', and contextSnippet did not match any of them. Provide a contextSnippet that is a verbatim substring ...". |
| E-SR7 | No symbol resolved at matched location | `:191`, `:219` | n | y | y | S, W, X | Sites 2. Updated 7c19972f (5e-2): `Unresolved`: 'No symbol could be resolved for 'X' at the matched location of 'F': the matched node (K) is not a declaration that resolves to a symbol.' plus a prefilled `GetFileOutline`. Only one such `Unresolved` site was found in the current source (the row counted two); the second site is unverified. Tests: StructuralRefinementMissAdvancedTests. Before: Names `ReadFile`. |
| E-SR8 | Single-candidate branch (RequireSingle) | `:198` (was `:211`) | y | y | y | X | Sites 1. Non-miss (ambiguous, coded ToolAmbiguousMatch). "matches N declarations ... Next: retry ... DeleteUnusedSymbol(filePath: F, symbolName: X, contextSnippet: <line>)"; lists all N. `// TODO align SyntaxTargetResolver.ResolveBySnippetOrThrow`. Left (7c19972f, 5e-2): non-miss (ambiguous, coded ToolAmbiguousMatch); `RequireSingle` already passes a retryCall (a prefilled DeleteUnusedSymbol with the candidate's first line); the `// TODO align SyntaxTargetResolver.ResolveBySnippetOrThrow` is unchanged. |
| E-SR9 | Declaration not found | `:381` | n | y | y | S, W, X | Sites 1. Updated 7c19972f (5e-2): 'Symbol 'S' has no declaration in source, so no declaration of it exists in 'F'.' or 'No declaration of symbol 'S' was found in 'F'; it is declared in P1, P2.' (a fact), then 'To locate it, call Search(mode: symbol, query: S).' (query prefilled). Tests: StructuralRefinementMissAdvancedTests. Before: "Declaration of symbol 'S' not found in 'F'. Next: Search mode symbol"; query prefilled. |
| E-SRF1 | SemanticRefactoringEngine: class miss | `SemanticRefactoringEngine.cs:184` | y | y | y | S, N, K, W, X | Sites 1. Updated 7c19972f (5e-2): the `Class` factory (as E-MRE1a): ranked class names or the no-similar-name fact plus prefilled `GetFileOutline`, and the other-kind fact. Tests: SemanticRefactoringMissAdvancedTests. Before: "Class 'C' was not found in 'F'. Searched: class declarations in this file. Next: Search(mode: class, query: C)". |
| E-SRF2 | Property miss | `:189` | y | y | y | S, N, C, W, X | Sites 1. Updated 7c19972f (5e-2): `DeclarationMessage('property', P, 'propertyName', properties declared directly in class C, F)` plus 'Only the properties declared directly in class 'C' were searched.': ranked 'Retry with propertyName: X' or the no-similar-name fact plus a prefilled `GetFileOutline`. Tests: SemanticRefactoringMissAdvancedTests. Before: "Property 'P' was not found in class 'C'. Next: Search(mode: property, query: P)". |
| E-SRF3 | No statements in line range | `:225` | n | y | y | S, W, X | Sites 1. Updated 7c19972f (5e-2): 'No statements were found in lines A-B of 'F', which has N line(s): no statement inside a block body has its span within that range. A constructor header, a field or an attribute list is not a statement of a block body. To see the line range of each member, call GetFileOutline(filePath).' (prefilled). `// TODO` added: a line range outside the file throws ArgumentOutOfRangeException before reaching this text. Tests: SemanticRefactoringMissAdvancedTests. Before: Line-range miss. "No statements found in lines A-B of 'F'. Searched: statements inside a block body whose span falls within that line range ... GetFileOutline"; no args. |
| E-SRF4 | SnippetNodeKindError.Describe | `:269` | n | y | n | W, X | Sites 1. Non-miss (matched, wrong node kind; coded NotFound: code mismatch). "contextSnippet matched at line N, but no statement is there; the innermost node is K." plus Searched and `GetFileOutline`; no args. Left (7c19972f, 5e-2): non-miss (matched, wrong node kind; coded NotFound); unchanged. |
| E-SU1 | SyntaxUpgradeEngine: lookup misses | `SyntaxUpgradeEngine.cs:155`, `:161`, `:405`, `:632` | y | y | y | S, N, W, X | Sites 4. Updated 30bf3efd (5e-3): Conditional Cand. `:155` and `:632` method misses use the `Method` factory and `:405` the `Class` factory (ranked names or the no-similar-name fact plus prefilled `GetFileOutline`); `:161` is 'No switch statement is declared in method 'M' of 'F', so there is nothing to convert. GetMethodSource(filePath, methodName) shows the method body.' (a fact plus a prefilled call, no candidates). Tests: SyntaxUpgradeEngineMissTests. Before: "Method not found.", "Switch statement not found.", "Class 'C' not found in file.", "Method 'M' not found in file." |
| E-SU2 | Zero-result messages | `:43`, `:373`, `:597`, `:710` | n | n | n | - | Sites 4. Non-miss. "No if-throw guard clause patterns found", no backing-field pairs, no block-form namespace, no replacements found. Zero-result, not a lookup miss. |
| E-SU3 | Passthrough of `SnippetLookup.TryResolve` text | `:121` | y | n | n | W | Sites 1. Conditional Cand. Wraps every `SnippetLookup` outcome verbatim (C6 text) in `ToolNotFoundException`. NoMatch is a miss (candidates only with closest windows); ambiguous outcomes are non-miss and coded NotFound (code mismatch). Left (30bf3efd, 5e-3): `// TODO` added at the wrap (the snippet family wraps ambiguous and no-match outcomes alike as NotFound with SnippetLookup's text; the snippet-sweep slice owns the site). |
| E-TS1 | ThreadSafetyEngine: method miss | `ThreadSafetyEngine.cs:35` | y | y | y | S, N, W, X | Sites 1. Updated 30bf3efd (5e-3): the `Method` factory (ranked names or the no-similar-name fact plus prefilled `GetFileOutline`); the same factory now also serves the ConvertLockToSemaphoreSlim method miss, split out of the E-TS2 site. Tests: ThreadSafetyEngineMissTests. Before: "Method 'M' not found." plus Next `GetFileOutline`. |
| E-TS2 | Containing type / body / type misses | `:46`, `:120`, `:123` | n | y | y | S, W, X | Sites 3. Updated 30bf3efd (5e-3), the row's Sites count not recomputed: 'Method 'M' in 'F' has no enclosing class, struct, interface or record declaration, so there is no type to add a lock field to (or a semaphore field to). GetFileOutline(filePath) shows where the method sits.' (two sites); the old 'Method or body not found' site is split into a `Method` factory miss (E-TS1) and 'Method 'M' in 'F' has no block body (it is abstract, extern or expression-bodied), so it holds no lock statement to convert. GetMethodSource(filePath, methodName) shows the method as declared.' Tests: ThreadSafetyEngineMissTests. Before: "Containing type not found for method", "Method or body not found: ... in file:", "Type not found for method". |
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
| TA6 | ScanTools: project miss rethrow | `ScanTools.cs:1096-1101` | y | n | n | S, N, W | Sites 1. Updated 187c9cdd (5e-1): `ScanTools` no longer matches on message text: it catches `ToolNotFoundException` by type when persistBaseline is false and returns NotFound with 'GetPublicApiSurface failed: <engine message>', so the engine's ranked project text (E-DISC1) reaches the agent unchanged. Section 4 item 1 is closed. Tests: DiscoveryMissTests (by name; whether it drives the ScanTools path is not verified). Before: Catches `ToolNotFoundException` / `ArgumentException` when `ex.Message.Contains("not found in solution")`, rethrows NotFound "Project '...' not found in solution." (text-coupled; section 4). |
| TA7 | IntelligenceTools: NotFound hint on TraceVariableLifetime | `IntelligenceTools.cs:258` | n | y | y | X | Sites 1. Appends " Call GetFileOutline(filePath: "<raw filePath>") on the file to confirm the enclosing method's line range, then retry with a lineNumber inside it." The base text comes from `SymbolNavigationEngine` ~`:2295` (E-SN8, not read here). Left (187c9cdd, 5e-1): `// TODO` added at the hint (it is phrased as an instruction, 'then retry with a lineNumber inside it'; reword as a fact). The base text it follows now comes from `DescribeVariableMiss` (E-SN8, ranked variable names), which this row's own columns do not describe. |
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
`HaltInfo.cs:26` (breaker text; the tripped message of `OrientationCircuitBreaker.StateMessage` was reworded in 2b7113c7 (5e) to facts and
options with no "You MUST": with a callable listing tool it says "<ListAll and/or ListSolutionItems call> lists what exists.", with none
"No listing tool is callable on this surface." and names no call; the follow-on "GetFileOutline and ReadFile are also available once you have
a real path from that list" sentence is unchanged; tests: CallableToolHintTests; not a lookup producer, so no row), `PersistentWorkspaceManager.cs:399` ("baseRepoDir does not exist on this host",
an `ArgumentException`), `WorkspaceReadNavigationImpl.cs:1262`, `:1267`, `:1279` (zero-hit text-search warnings,
not errors; `:1279` names `includeTests: true`), `SymbolRelationshipImpl.cs:142` (a scope note about a
"trustworthy 'not found anywhere' signal", only when includeTests and no project), `PlanStepFile.cs:47`
(`Utilities.PlanStepRunner`, "Plan directory not found").

### 4. String-coupled consumers

Text-coupled (a message change can break these silently):

1. **Closed in 187c9cdd (5e-1).** `RoslynSentinel.Tools.Advanced/ScanTools.cs` (the GetPublicApiSurface tool, was `:1096`) filtered on
   `ex.Message.Contains("not found in solution")` and rethrew NotFound at `:1101`. It now catches `ToolNotFoundException` by type
   (only when persistBaseline is false, the DiscoveryEngine path) and returns NotFound with "GetPublicApiSurface failed: <engine
   message>", so a wording change in `DiscoveryEngine.cs` (now `ProjectNotFoundDetail`, E-DISC1) or the old `InventoryEngine.cs`
   phrase ("File not found in solution", gone since 7c19972f) cannot break it silently. Kept for history: the survey's
   candidate sites for the old phrase were `DiscoveryEngine.cs:238`, `InventoryEngine.cs:36`, `:64`.
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
- Tools.Experimental `CodeTransformTools.cs:787` (by code); Tools.Advanced `IntelligenceTools.cs:258` (by result code); Tools.Advanced
  `ScanTools.cs` GetPublicApiSurface catch (by exception type since 187c9cdd, item 1 above).
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
- Row 5: the plan says the shape check names `Search(mode: symbol)`; the survey read `PersistentWorkspaceManager.cs:3564-3565`
  as an example id only (`for example 'T:MyApp.Orders.OrderService'`). Resolved in the batch E update: the check now lives in
  `SymbolWireResolver.NotADocCommentIdText` and does name `Search(mode: symbol)` (see C3). Row 4's resolver message was at `:3577-3578`
  (plan `:3571-3581`); it is now `SymbolWireResolver.BuildNotFoundTextAsync` (see C4).
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
- **Unreachable fallback (reworded in 06f8a86e):** the `"?? Context snippet not found."` fallback at `BasicRefactoringEngine.cs:1795`
  is now `"contextSnippet matched no place in the file."` and, as before, is reached only when `SnippetLookup.TryResolve` fails with no
  text (not re-traced). Observed in batch E: every `TryResolve` failure at `:1795` and `:1873` throws `ToolInvalidArgumentException`,
  so a snippet no-match there carries InvalidArgument, not NotFound (E-BRE5a, E-BRE5b); not changed.
- **Wrong name slot (stated, not removed, in a35cee2a):** `MsToolAugmentEngine.cs:1474` still passes the snippet in the name slot of
  `DeclarationMessage`; the text now adds that with extractEntireBody contextSnippet is read as the method name (E-MSA).
- **Plan seed row 5 conflict:** resolved, see section 5 (the shape-check message does name `Search(mode: symbol)`).
- **Codes found while updating rows in batch E (all with a `// TODO` in source):** `SymbolNavigationImpl.GetTypeInfo` maps the type-miss
  text `hierarchy.Error` (E-SN13) to InvalidArgument; `ImpactAnalyzer` `:39` returns the file-miss text with no ErrorCode (E-IMP1);
  `DeleteFileMissTextAsync` returns NotFound when no solution is loaded, where the cause is SolutionNotLoaded (TB-T8);
  `ListWorkspaceSolutions` codes a missing directory InvalidArgument (TB-T5b).

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

Status after batch E (2026-11-12): an item marked **DONE <hash>** below has an `Updated` note in section 3 (the message was
changed and a test named in the commit drives the tool; this table does not say the test exercises every site, section 3 notes
the gaps it found). Unmarked items are still open; **LEFT** means the site stayed a non-lookup with a `// TODO` in source.

**Engines.Basic - MemberRefactoringEngine.cs**

- Lookup, **DONE**: E-MRE1b (d13ed771); E-MRE6a (699e760d); E-MRE4c (6efd5648); E-MRE4d (6efd5648, two TODOs mark silent no-op paths);
  E-MRE8c (d13ed771, 4a754d4f); E-MRE8d (d13ed771); E-MRE8a syntax root and E-MRE8e syntax root and semantic model (4a754d4f);
  E-MRE8a and E-MRE8e file lookups moved onto `DocumentLookup.RequireDocument` (cd7bfcbe, which also added the factories).
- Near standard, **DONE**: E-MRE1d (d13ed771), E-MRE3a (699e760d), E-MRE6c (4a754d4f, policy phrases only).
- Reclassify, **LEFT** with `// TODO` (cd7bfcbe): E-MRE7b (ambiguous, coded NotFound), E-MRE8b (prose fix). Open, no TODO: E-MRE10 (14
  syntax-root sites, InvalidArgument wording).

**Engines.Basic - BasicRefactoringEngine.cs**

- Lookup, **DONE**: E-BRE5a/b (06f8a86e: fallbacks and advice reworded; the C6 family itself is C6a-C6e), E-BRE9a/b (a35cee2a).
- Reclassify, open: E-BRE5c `:1802`, `:1808`, `:1898`, `:1905`, `:1912`, `:1918`, `:1986` (7 sites, ineligible or internal), E-BRE11 (7 syntax-root sites).

**Engines.Basic - other files**

- SymbolNavigationEngine: **DONE 187c9cdd** E-SN1, E-SN4, E-SN6, E-SN7b, E-SN8, E-SN9, E-SN13. Unchanged by design: E-SN2, E-SN3, E-SN5,
  E-SN7a, E-SN10, E-SN11, E-SN12 (the ambiguous-outcome and possible-placeholder findings in section 6 stay open).
- ProjectStructureEngine: **DONE 187c9cdd** E-PS1, E-PS5 (project and class misses); **LEFT** with `// TODO`: E-PS2, E-PS3, E-PS4, and the two
  'Solution path not found.' sites of E-PS5.
- StructuralRefinementEngine: **DONE 7c19972f** E-SR1, E-SR3 to E-SR7, E-SR9 (E-SR7's second site is unverified); **LEFT**: E-SR2, E-SR8
  (non-miss; E-SR8 keeps its `// TODO align SyntaxTargetResolver.ResolveBySnippetOrThrow`).
- SemanticRefactoringEngine: **DONE 7c19972f** E-SRF1 to E-SRF3; **LEFT** E-SRF4 (non-miss).
- SyntaxUpgradeEngine: **DONE 30bf3efd** E-SU1; **LEFT** E-SU3 (`// TODO`, snippet-sweep slice); open: E-SU2 (4 zero-result sites).
- ThreadSafetyEngine: **DONE 30bf3efd** E-TS1, E-TS2; open: E-TS3 (zero-result).
- **DONE** in 187c9cdd: DiscoveryEngine E-DISC1 to E-DISC3, E-DISC5 (E-DISC4 only forwards the 4d delegate, e2fdf9b0); in 7c19972f: DiagnosticEngine
  E-DIAG, InventoryEngine E-INV1, ImpactAnalyzer E-IMP1 to E-IMP4 (E-IMP3 text now states the cause, TODO for candidates; E-IMP1 TODO for the
  missing ErrorCode); in 30bf3efd: ConstructionReplacer E-CONSTR, NullForgivingRemover E-NFR, SemanticReplaceEngine E-SRE1, E-SRE2; in a35cee2a:
  MsToolAugmentEngine E-MSA, NamedArgumentsEngine E-NAE1, E-NAE2, TestCategoryTaggingEngine E-TCT, TestRunEngine E-TRE, TestInstanceLauncher
  E-TIL2 (Stop). **LEFT** ChangeSignatureWrapper E-CSW (`// TODO`). Open: InventoryEngine E-INV2 (raw `InvalidOperationException`), TestInstanceLauncher
  E-TIL1 (reclassify) and E-TIL3, `SyntaxTargetResolver` E-STR (also fix the `ComposeFailure` payload drop).

**Engines.Advanced (136 `ToolNotFoundException` sites plus result producers; slice by file group; all open after batch E, plan Decision 9: out of plan scope)**

- File misses: A1, A2 (27 sites); they should route to `DocumentLookup` / `RequireDocument` (C1) as the 157 call sites already do.
- Class / method / member lookups: A4 AdvancedStructural (21), A5 AsyncOptimization (11), A6 LogicOptimization (8),
  A8 CodeGeneration (8), A9 Modernization (7), A10 Testing (3), A11 DependencyInjection (2), A12 (5), A13 OutParam (2),
  A14 AsyncBatch (5), A3 (2).
- Project misses: A7 AntiPattern (10 sites) plus A5 `:2480`, `:3728` and A12 `:297`, `:340` share the `ProjectLookup.FindProject` template, so one helper change covers them.
- Silent miss: A15 `AsyncBatchEngine.cs:1383` (add an error).
- Reclassify only: A16 (15 sites), A17 (13), A18 (4).
- Already near standard (check for a "no similar name" sentence only): A19, A20, A21, A23 to A27; A22 needs a next call.

**Tools.Basic**

- **DONE** in 06f8a86e: `SnippetErrorFormatter` C6a to C6e; **LEFT** C6f (`// TODO`, reclassify). **DONE** in cebd67dd: `WorkspaceFileEditImpl`
  TB-T1b; `WorkspaceReadNavigationImpl` TB-T2f, TB-T2h; `WorkspaceProjectManagementImpl` TB-T5a (**LEFT** TB-T5b, `// TODO`); AdminTools TB-T6a,
  TB-T6c; DocumentationTools TB-T7a to TB-T7d (TB-T7a unchanged, its live text is TB-T7d); WholeFileWriteTools TB-T8.
- Open: `SymbolNavigationImpl.cs` TB-T3b (InspectSymbol unresolved; a peer session owned the file during batch E); TB-T1c, TB-T1d (non-miss
  and note-append rows); `WorkspaceReadNavigationImpl` TB-T2d, TB-T2e, TB-T2g (non-miss, left); TB-T5c; AdminTools TB-T6b; GitImpl TB-T9
  (13 sites, its own family; plan Decision 9: out of plan scope). TB-T2b and TB-T2c were already compliant.

**Common**

- **DONE**: `DocumentLookup.cs` C2 (06f8a86e, ranked project candidates); C3 and C4 (C4's ranked text predates batch E, its docCommentId and
  retry-call part is e2fdf9b0); `PersistentWorkspaceManager.cs` C5a (06f8a86e lists it; the option sentence landed with d5667196).
  Open: C5b (reclassify).

**Tools.Advanced / Tools.Experimental / Server.Advanced**

- **DONE** TA6 (187c9cdd: passthrough by exception type). **LEFT** TA7 (`// TODO`: reword the hint as a fact). Open: TA2 (non-miss: reclassify),
  TA3 (named in 5d row 22 but no change specified), TA8 (reclassify). Tools.Experimental and GitImpl are out of plan scope (Decision 9).
- Tools.Experimental `CodeTransformTools.cs`: TX1a `:488` (reclassify: zero-result), TX2b (split the combined "not found or not
  eligible" message), TX3 `:1102`, `:1133`, `:1184` (3 sites; need a real next call; not text-coupled, section 4).
- Server.Advanced: SV1 is compliant for tool-name misses (no work); SV2 and SV3 are non-miss branches that already name a call.

### 8. Owner question: do tools give fallback orientation hints for not-found?

Quote: "do tools give fallback orientation hints for not-found?" At survey time: partly, and unevenly. Every row is
classified: the table has 189 rows standing for 597 raising sites, of which 161 rows (509 sites) are lookup misses and 28 rows
(88 sites) are non-miss conditions worded or coded as not-found (reclassify, section 6).

**Counts recomputed after batch E** (a script over the table's Cand / Next / Prefill columns and the `Non-miss` tag; the previous
figures in this paragraph, 83 rows naming a call and 37 rows listing candidates, came from the survey: the same script over the table
as it stood before the batch E edits gave 92 and 46, so they had not been refreshed after batch D's row edits). Across all rows, **130 rows (373 sites) name a next call**
(119 miss rows with 362 sites, 11 non-miss rows with 11 sites); **83 rows (144 sites) prefill request values** into the suggested
call; **32 more rows (202 sites) prefill only a fixed or placeholder argument** (such as `kind: all`, `mode: symbol`, `action: list` or
template placeholders like {file}); and **101 rows (319 sites) list candidates**, of which 7 rows (23 sites) list them only on some
paths (closest windows, a near-miss, or one site of several; tagged `Conditional Cand.`: E-BRE5a, E-BRE5b, E-SN2, E-SU3, E-MRE8c, E-PS5,
E-SU1). **26 miss rows (128 sites) give no orientation at all** (no candidates, no next call, no prefill): 16 Engines.Advanced rows (A1, A4 to
A15, A18, A22, A26; out of plan scope), TA3, TX3, the fact-only snippet texts C6a and C6b, TB-T1d, TB-T2e,
TB-T2g, TB-T6b, E-IMP3 (the text states the cause; `// TODO` for candidates) and E-SN12; a further 16 non-miss rows (76 sites) give none either. The shared
file lookup (row C1) accounts for 157 sites of the 373, 202 and 319: without it the counts are 129 rows (216 sites) naming a next
call, 83 rows (144 sites) prefilling request values, 31 rows (45 sites) prefilling only fixed or placeholder arguments and 100 rows
(162 sites) listing candidates. A row's `y` is an upper bound where a note says `Conditional Cand.` or lists several sites with
different text (E-MRE8c, E-PS5, E-SU1).

What the producers say now: the shared builders `DeclarationNotFoundDetail` and `MemberRefactoringNotFound` (E-MRE rows, E-SN1, E-SN4,
E-SN9, E-SR1, E-SR5, E-SRF1, E-SRF2, E-DISC3, E-MSA, E-TS1, E-SU1), `ProjectNotFoundDetail` (C2, E-PS5, E-DISC1, E-DISC5, E-DIAG, E-NAE1,
E-TCT, E-TRE, TB-T5a), `SymbolNotFoundDetail` (C4, E-SN6, E-SN7b, E-SN13, E-CONSTR, E-NFR, E-SRE1, E-NAE2) and `NameCandidates` callers
(TB-T2f, TB-T6a, TB-T6c, TB-T7b, TB-T7c, E-SN8; TB-T8 ranks through `DocumentLookup.FindFileCandidates`) rank similar names with a prefilled retry, or state that no name is similar
(and, in `DeclarationNotFoundDetail`, `SymbolNotFoundDetail` and `ScopedMessage`, that the target "does not exist" in that scope) with a
prefilled orientation call. This answers the survey's "none says no similar name exists" finding for the updated rows; the unupdated
rows (Engines.Advanced A1 to A27, Tools.Experimental, TB-T9 and the open rows in section 7) still say neither. The producers that
already listed candidates and named a prefilled call at survey time were `SyntaxTargetResolver.BuildContainerMissDetailAsync` (TB-T2c,
E-SN10; its retry template carries placeholders, not request values), `BuildMethodNotFoundErrorAsync` (TB-T2b), `MemberNotFoundDetail`
(E-MRE6b, E-SN11) and the `SymbolNavigationEngine` FindReferences family (E-SN3, E-SN5, E-SN7a); E-SN3 and E-SN7b-style text ("NOT a
confirmed zero-references result") separated a wrong spelling from an absent target only by saying the lookup never ran. Engines.Advanced
engines such as A19, A21, A23 to A25, A27 and TX2a still name a request-prefilled `Search` or `GetFileOutline` call without listing
candidates. The counts treat a hint that names a call only on some branches (for example the callable-gated calls in C1 and TB-T9) as
naming a next call.

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
  during the survey. `MemberRefactoringEngine.cs` sites after ~2431 had shifted +3 between reads. Re-measure before editing. Batch E then
  moved the engine's sites again (the namespace miss `:2538` is at ~`:2675-2685`; base-type sites at ~`:5535-5600`), and `SymbolNavigationEngine.cs`
  (E-SN1 `:1224` is now ~`:1218`), `ProjectStructureEngine.cs` and `DocumentLookup.cs` (C2 now `:443`) moved too; the section 3 file:line
  cells were not refreshed except C3 and C4.
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
  `TraceVariableLifetime` base text (read in the batch E update as `DescribeVariableMiss`, E-SN8); the sole caller of
  `TypeHierarchyReport.Error` (`SymbolNavigationImpl.GetTypeInfo`, read in the batch E update, E-SN13); and the run-time rendering of
  `ToolCallHint.Call` placeholders. Rows that depend on these (E-SN2, TA7, TB-T1d) say so.
- **Conditional candidates.** Seven rows (E-BRE5a, E-BRE5b, E-SN2, E-SU3, E-MRE8c, E-PS5, E-SU1) list candidates only when closest
  windows or a near-miss exist, or at one site of several. (E-SN6 and E-SN7b stopped being conditional in 187c9cdd.) They count as
  `Cand y` in section 8, so the candidate count is an upper bound for a typo that has no near-miss.
- **Batch E update: provenance and limits.** Message text of the batch E rows was re-read from current source (Search and ReadFile,
  2026-11-12, workspaceVersion 401-432, while peers were editing). The text of `MemberRefactoringEngine.cs` call sites and of
  E-MRE6c was taken from the batch E commit diffs and `MemberRefactoringNotFound.cs`, not by re-reading every engine line, because
  the engine had moved by up to ~135 lines. Test class names in the `Updated` notes are the ones the commits report; this update did
  not open the tests, so a note's `tests:` list says which classes exist for the commit, not which site each exercises, and
  where a commit names several classes the per-site mapping is marked unverified. Coverage gaps found while reading are stated in the
  row (E-BRE5a: no listed test for the ExtractConstant site; E-MSA: none for the `:1876` file-miss site). Under the role boundaries
  these are planner (what the test must cover) and reviewer (whether it does) gaps; a green run says nothing about them.
  Row `Sites` counts were not recomputed (E-TS2 gained a throw by splitting one; E-SR7 has one verified site of two). Not verified
  at all: E-SR7's second site; whether `DiscoveryMissTests` / `CodeTransformMissFileClassTests` drive the ScanTools path (TA6).
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
