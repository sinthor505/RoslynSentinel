# Plan: cut "Unknown parameter 'X' for tool 'Y'" rejections with in-flight rewrites, a benign-ignore list, two capability fixes and better rejection text

**Status:** IN PROGRESS 2026-10-24. Steps 1-4, 5, 5a, 5b, 6-16 built and committed in 892e59fe (the Step 4/5a engine hunks landed earlier inside the other session's commits 51a38b7b and 53a3f207); Step 17 partly done (build, solution tests and live probes ran; 5 remaining failures belong to the concurrent ambiguity-audit work or the environment, the schema budget was raised to 29000); Steps 5c-5g PAUSED; Step 18 (re-measure after restart on the committed binary) open. Earlier history: DRAFT 2026-10-24. Decisions 1-6 applied 2026-10-24; decisions 2 and 5 revised the same day (2: GetMethodSource never picks a match, it returns an ambiguity error; 5: `file_path` is an explicit global alias, the generic comparer is dropped). Third revision the same day: the overload selector is agreed as NONE; `ReadFile(memberName, containerName)` now refuses a container that does not hold the member, sharing one helper with GetMethodSource (Steps 4a, 4b, 5a, 5b). Fourth revision the same day: Step 10 is DECIDED (keep); a dotted container such as `Outer.Inner` matches its last segment (Steps 4a, 5, 5b); the other 8 lenient-filter callers migrate to the strict helpers in Steps 5c-5g (Member replace, Member remove with its post-remove verification, Member view, summary-comment view/remove, summary-comment add), each with a pre-step caller audit and a wrong-container-refused test. Decision 5's note-vs-silent choice stays provisional. Steady ~0.4% friction cost (not a regression); about 77% of the post-alias rejections become mechanically recoverable, the rest need clearer rejection text.

## Problem

Agents (mostly subagents) call RoslynSentinel tools with parameter names the schema does not declare and are
rejected before anything runs. Each rejection costs a full round trip. The rejection is correct (an unknown
argument would otherwise be dropped and the tool would report success on its defaults,
`RoslynSentinel.Server.Basic/ToolArgumentValidator.cs:889-935`), so the defect is that the environment does not
translate or explain the names agents reliably reach for.

### What already exists (verified in source, 2026-10-24)

- Rejection: `ToolArgumentValidator.Validate` (`RoslynSentinel.Server.Basic/ToolArgumentValidator.cs:889`), unknown-name
  branch at lines 900-935. Text: `Unknown parameter(s) 'a' for tool 'T'. [Did you mean 'x' instead of 'a'?] This tool accepts
  only: ... Nothing was executed - ...`. The suggestion (`SuggestClosest`, line 142) is Levenshtein only (tolerance
  `min(3, max(1, len/3))`) and only 39 of 388 rejections carried a "Did you mean". A name already supplied in the same call can be
  suggested (`reason2` -> `reason`).
- Aliasing: `ToolArgumentValidator.ParameterAliases` (line 315) is a **per-tool, compiled-in** dictionary
  `tool -> (alias -> declared name)`, applied by `ApplyParameterAliases` (line 514), which returns early when the tool has no entry
  (`ParameterAliases.TryGetValue(toolName, ...)`, near the top of the body). An alias is skipped when the alias
  is itself declared, the target is not in the emitted schema, or the call already supplies the target. It only renames keys
  (no value change, no drop) and emits `Note: 'x' is not a parameter of T; it was treated as 'y'. Use 'y' in future calls.` (line ~565).
  Rejection still happens if **any** other unknown name remains, so one stray parameter costs the whole call. The `GetMethodSource`
  entry (lines 318-322) maps `memberName` and `symbolName` to `methodName`; it has no `containerName` key, so adding that parameter
  (Step 4) cannot trip the alias guard test. There is no tool-independent alias table today.
- Case normalization: `NormalizeParameterCase` (line 262) renames case-only mismatches silently through `caseInsensitiveLookup`
  (`StringComparer.OrdinalIgnoreCase`, `TryGetSchemaParameters` lines 98-99). It is NOT changed by this plan.
- Filter order (`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs:1116-1170`, `AddArgumentValidationFilter`):
  `NormalizeParameterCase` (case only, silent) -> `NormalizeEnumCase` -> `ApplyParameterAliases` ->
  `ApplyLegacySearchReferencesKind` -> `WrapFlatBatchParameters` -> `CoerceStringToStructured` -> `Validate`. Everything is in
  one `try`; if any step throws the filter falls through to `next` **without** running `Validate` (line ~1166), so a new step must
  never throw. Each pass appends its notes to `aliasNotes` (`aliasNotes is null ? x : [.. aliasNotes, .. x]`); new passes follow the same pattern.
- Notes surface reliably: `AppendAliasNotes` (line ~1085) appends each note as its own trailing `TextContentBlock` after `next`
  returns, so the outer echo filter only stamps block 0. Transcripts since 2026-10-05 contain 47 genuine
  `Note: 'x' is not a parameter of T; it was treated as 'y'` results (Search pattern 13, Git maxCount 12, Git files 6, GetMethodSource
  memberName 5, Search glob 5, GetLargeResult maxItems 4, ...); a note was repeated within one session in 5 of ~35 transcripts, so
  notes teach most of the time but not always. The same notes are appended to a rejection, where "it was treated as" is
  misleading because nothing ran.
- The solution root is reachable in the filter the same way other filters get it:
  `context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot()` (`ServiceRegistrationExtensionsBasic.cs:731`).
- Report script already exists: `scripts/Get-UnknownParameterReport.ps1` (extend it, do not add another). Its regex
  (`Unknown parameter '(?<p>...)' for tool ... This tool accepts only:`, line 128) misses the plural form "Unknown parameters 'a', 'b'"
  (84 of 388 rejections) and the echo-wrapped form `{"toolCall":...,"message":"Unknown parameter ..."}`, so it undercounts.

### Evidence (re-measured, not taken from the brief)

Method: Claude Code transcripts `~\.claude\projects\**\*.jsonl`, `tool_result` with `is_error: true` whose text (or echo `"message"`)
starts `Unknown parameter(s) ... for tool '...'`, de-duplicated by `tool_use_id`, 2026-09-16..2026-10-24. This is stricter than the brief's
scan: reads of the report script's own docstring also match a loose `Unknown parameter` search (17 false hits removed).

- 388 distinct rejections, 276 in subagent transcripts, 84 plural. 246 (63%) occurred **before** the alias table shipped
  (commit b2ba4208, 2026-10-04); the aliased names are now at zero: GetMethodSource memberName 32 -> 0, Member newText 12 -> 0,
  LoadSolution path 7 -> 0, ApplyUnifiedDiff diff 5 -> 0, Git maxCount 8 -> 0, McpServerControl operation 6 -> 0. The brief's
  per-tool table therefore overstates what is still open; the remaining work is the 142 rejections from 2026-10-05 on.
- `SearchSolutionText` (15) is a retired tool: it is deliberately not an `[McpServerTool]`
  (`RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs`, comment above `SearchSolutionTextTool`). Noise.
- Rate: rejections are logged as `ok:false` ~6 ms calls (checked: `.claude/journal` row 2026-10-21T00:41:29 `Build ok:false durationMs 6`
  matches the transcript rejection at 00:41:29), so `.claude/journal/*.calls.jsonl` (`kind: mcp`; starts 2026-10-02) is a valid denominator.

| Week (Sun start) | rejections | mcp calls | rate |
| --- | --- | --- | --- |
| 09-27 | 184 | (no journal) | - |
| 10-04 | 45 | 5455 | 0.82% |
| 10-11 | 37 | 9096 | 0.41% |
| 10-18 (to 10-24) | 68 | 15749 | 0.43% |

- Post-alias window (2026-10-05..10-24): 142 rejections, 102 subagent: implementer 46, implementer-senior 29, test-writer 16,
  explore-haiku 9, other 2.
- Post-alias top names (count): ReadFile limit 15, file_path 14, offset 9, allow_large 2; Search head_limit 11, output_mode 9
  (+outputMode 1), useRegex 5, path 5, type 4, isRegex 2, -n 1; CreateFile content 10; RunTest project 9, projectName 5;
  GetMethodSource containerName 5, includeDocComments 5, maxLines 2; Git path 5; ReplaceSnippet oldSnippet/newSnippet 4 each (alias
  shipped 2026-10-21, after those calls), oldString/newString 2 each; Build fullBuild 3; GetDiagnostics filePath 2;
  GetLargeResult fullResultsId 2; RenameSymbol symbolName/filePath/contextSnippet 2 each. About half (78 of 142) are built-in
  Read/Grep/Write shapes.
- **Separator-style names (data for Decisions item 5).** The 388 rejections carry 499 unknown-name tokens; 43 contain `_` or `-`.
  Only one of them squashes to a declared parameter: `file_path` (15 tokens, all `ReadFile`, all in subagent transcripts, 14 after the
  alias cutover). The other separator names are `head_limit` 14, `output_mode` 11, `allow_large` 2, `-n` 1: none equals a declared name once the
  separator is removed (they need the Step 6-7 rewrite/ignore tables, not a comparer). There is no `start_line`, `end_line`, `max_results`,
  `file_glob`, `member_name` or `file-path` in the whole window. Case-only variants (`filepath`, 3 tokens) are already handled by `NormalizeParameterCase`.
- **GetMethodSource first-match exposure (data for Decisions item 2).** 2,623 `GetMethodSource` calls appear in Claude transcripts since
  2026-10-05 (about 138 per day). Resolving each `filePath` in the current tree and counting declaration lines of `methodName` with a modifier-line
  regex: 2,578 resolvable, 61 (2.4%) name a method declared two or more times in the same file (top names: `LoadSolutionAsync` 9,
  `InvertAssignmentsAsync` 6, `BuildSourceEdits` 4, `SetUp` 3, `ErrorCodeFor` 3). This is an estimate: the regex also counts interface-plus-
  implementation pairs (which Step 4 resolves without an error), files may have changed since the call, and 444 calls matched zero lines (regex
  misses). Read it as "about 2-3% of calls previously returned the first of several matches and would now get a candidate list".
- Real declared parameters (source, not guessed): `GetMethodSource(reason, filePath, methodName)`
  (`WorkspaceReadNavigationTools.cs:32-36`); `ReadFile(reason, filePath, startLine, endLine, memberName, containerName)` and
  `CreateFile(reason, filePath, namespaceName, typeKind, typeName)` (`WorkspaceFileEditTools.cs`); `ReplaceSnippet(reason, action,
  filePath, oldContent, newContent, lineBefore, lineAfter, withinMember, matchLine, batchEdits, validateOnApply, returnDiff)`
  (`WorkspaceFileEditTools.cs:55-76`; `action` is required); `ApplyDiff(reason, changesetFormat, action, changes, filePath, unifiedDiff,
  retryCount, validateOnApply, returnDiff)` (`WholeFileWriteTools.cs:297`); `RunTest(reason, scope, scopeName,
  filter, resultsType, maxDetails, timeoutSeconds, summary, useScratchDir, includeOutput, environment)`,
  `Build(reason, level, scope, scopeName, maxDetails, useScratchDir, includeOutput, includeWarnings)`,
  `GetDiagnostics(reason, scope, scopeName, summarize, maxDetails, topN, verify)` (`WorkspaceBuildTestTools.cs`);
  `LoadSolution(reason, solutionPath, baseRepoDir, forceReload, timeoutSeconds)` (`WorkspaceProjectManagementTools.cs`);
  `Git(reason, operation, count, paths, maxBytes, message, scope, noCommit, amend, mode, repoPath, maxEntries, nameOnly, stat, mainline,
  ref, hunkIds, hunkFingerprint, lineRange, pickaxe, pickaxeRegex, follow)` (`GitTools.cs`);
  `Member(reason, filePath, operation, containerName, namespaceName, memberName, newMemberSource, position, skipPrecheck,
  contextSnippet, lineBefore, lineAfter, dryRun, returnDiff)` (`RefactoringStructuralTools.cs:108-132`);
  `Search(reason, mode, query, fileGlob, maxResults, symbolKind, containingType, containingNamespace, projectName, exactMatch,
  filePath, contextSnippet, lineBefore, lineAfter, includeTests, hasAttribute)` (`WorkspaceReadNavigationTools.cs`);
  `GetLargeResult(reason, resultId, filePath, limit, offset, charLimit)`. `BuildVerifyLevel` = `noBuild, quickBuild, fullBuild,
  fullBuildThrowaway` (`RoslynSentinel.Common/ToolEnums.cs:288`).
- Root causes traced to source for the non-obvious ones:
  - `RunTest project/projectName` (14): the description already says "not a parameter called projectName"
    (`WorkspaceBuildTestTools.cs`, `scopeName` text) and agents still send it. A description hint is demonstrably not enough; the
    environment must translate.
  - `Build fullBuild` (3, as the string "true"): `CLAUDE.md:208` writes the pseudo-call `Build(fullBuild)`, which agents copy as
    `fullBuild: "true"`; the real form is `Build(level: fullBuild)`.
  - `GetMethodSource includeDocComments` (5): doc comments are already in the result (`WorkspaceReadNavigationImpl.cs:245`,
    `method.ToFullString()` keeps leading trivia), so ignoring `true` loses nothing.
  - `GetMethodSource containerName` (5) and the first-match behaviour: `GetMethodSource` takes the first `BaseMethodDeclarationSyntax` of that
    name in the file (`WorkspaceReadNavigationImpl.cs:235`, `FirstOrDefault`) with no signal that others exist. So ignoring the key would be a
    **silent wrong result**, rejecting it leaves the agent no way to name the type, and, independent of this plan, an overloaded or repeated name
    already returns a possibly wrong method without warning. Decision 2 fixes both: a real `containerName` and an ambiguity error instead of a pick.
  - `Search` text mode runs every query as a literal AND a regex (`SearchSolutionTextTool` description; results carry
    `literalResults` and `regexResults`), so `useRegex/isRegex/regex: true` change nothing. In text mode `filePath` is declared but
    unused: `FindIgnoredSearchParameters` (`WorkspaceReadNavigationTools.cs`) lists everything except `fileGlob`/`maxResults` as
    ignored, adds the warning text "Ignored parameter(s) for mode text: filePath." and the search still runs over the whole solution.
    That is a silent widening, so `path` and text-mode `filePath` are translated to `fileGlob` (Steps 8-10) instead of being ignored.
  - `CreateFile content` (10 post-alias, 20 total): `CreateFile` has no content parameter by design; its description already points at
    `WriteFile(operation: CreateFile, content: ...)`. Needs a call-shaped redirect in the rejection, not an alias (different tool).

### Glob semantics the `path` rewrite relies on (verified in source, `WorkspaceReadNavigationImpl.cs`)

- `GlobMatchesFileName` (~1097-1105): the glob is normalized with `Replace('\\','/')`; if it contains `/` the candidate text is the
  document's solution-root-relative path (`filePath.Relative`, also normalized), otherwise the bare file name; the glob becomes `^regex$`,
  case-insensitive.
- `GlobToRegex` (~1116+): `**/` -> `(?:.*/)?` (matches zero or more leading segments), `**` -> `.*`, `*` -> `[^/]*`, `?` -> `[^/]`;
  braces and classes are supported; a bad pattern raises `GlobSyntaxException`, which the search turns into an error.
  So `RoslynSentinel.Tools.Basic/**` matches every file under that folder and `**/Foo.cs` matches `Foo.cs` at any depth including the root.
- `GlobNamesTestProject` (~572): a `/`-containing glob whose first segment names a test project automatically includes test projects, so a
  directory path into a test project needs no `includeTests`.
- The zero-match error for a glob (~1001-1027) already explains scope; no change needed.

## Decision

Keep the rejection for anything that would return a wrong-but-successful result, and remove the round trip everywhere the
meaning is unambiguous. Five layers, each landed as its own slice with the old behaviour intact until the next one:

1. **Data-only aliases** (rename key, same meaning) for the remaining repeat names: per-tool entries (Step 2) plus a new small **global** alias table
   with one measured entry, `file_path` -> `filePath` (Step 3). It uses the standard alias note. There is no generic separator-insensitive matching
   (Decisions item 5).
2. **A small rewrite/ignore engine** (new `ToolArgumentRewrites`) for the cases a plain rename cannot express: value-translating
   rewrites (`offset/limit` -> `startLine/endLine`, `project: X` -> `scope: project, scopeName: X`, `fullBuild: true` ->
   `level: fullBuild`, Search text-mode `path`/`filePath` -> `fileGlob`) and a justified benign-ignore list. Every rule leaves the key
   for `Validate` when its precondition fails.
3. **A capability fix**: `GetMethodSource` gains an optional `containerName` AND stops picking a match for the caller: when more than one method
   still matches (same name in several types, overloads in one type) it returns an `Ambiguous` error listing every candidate (Steps 4-5).
   The same strict helpers (`SyntaxTargetResolver`, Step 4a) make `ReadFile(memberName, containerName)` refuse a container that does not hold the member instead of silently
   falling back to another type's member (Steps 4b, 5a, 5b). The other 8 callers of the lenient `FilterByContainingType` (`Member` view/replace/remove, the post-remove verification and the
   summary-comment methods) move to the same helpers in Steps 5c-5g, one engine method per slice, each with a caller audit and a wrong-container-refused test. A dotted container (`Outer.Inner`) matches its last segment.
4. **Better rejection text** for what remains: a smarter suggestion plus a per-(tool, parameter) hint table carrying a corrected,
   call-shaped line (cross-tool redirects only name tools that are registered at that moment).
5. **Teaching text where it costs nothing standing**: the dog-fooding hook deny text (shown exactly when an agent reaches for
   Read/Grep), the one `CLAUDE.md` line that taught the wrong shape, and a compact call-shape block in the four implementer-lane agent
   definitions (dispatched, so each dispatch pays about 170 tokens; guarded by a test so it cannot go stale).

The only standing schema cost is one new parameter description on `GetMethodSource` (~290 bytes; the claude-lean schema budget has ~1 KB headroom,
`RoslynSentinel.Tests.Server/ClaudeLeanSchemaBudgetTests.cs`: budget 28500 vs 27435 measured on 2026-10-18; `GetMethodSource` is in the
lean set, `ToolClassRegistry.cs:44`). No other tool description changes.

No new result DTOs: the ambiguity error reuses the existing `ResultError` and `AmbiguousCandidate` (`RoslynSentinel.Common/AmbiguousCandidate.cs`).
The rule tables are `internal`/`private` static members of `RoslynSentinel.Server.Basic` and never cross the wire, so the dto-conventions rules
do not apply; if an implementer introduces a record for a rule it stays `internal sealed record` inside `ToolArgumentRewrites.cs`.

### Benign-ignore list (justification per row; anything else stays rejected)

A key is accepted, removed from the call and reported in a `Note:` only when ignoring it cannot change which results come back.

| Tool | Name | Accepted values | Why ignoring is safe | Why not wider |
| --- | --- | --- | --- | --- |
| Search | `output_mode`, `outputMode` | `content`, `files_with_matches`, `count` | Only presentation; Search always returns the full match list with file/line/preview and `listSummary.byFile` | any other value asks for something Search cannot produce |
| Search | `useRegex`, `isRegex`, `regex` | `true` and mode = text | text mode already evaluates literal and regex in one pass | `false` wants literal-only (we would return extra `regexResults`); non-text modes take a symbol name, a regex flag there signals a misunderstanding |
| Search | `searchMode` | `regex` and mode = text | same as above | `literal` as above |
| Search | `-n` | `true` | every match already has a line number | - |
| Search | `type` | `cs`, `csharp` | text mode scans `.cs` only and symbol modes are C# only | other types (`py`, `md`) ask for files Search cannot search |
| ReadFile | `allow_large` | `true` | no size gate to open; past the threshold the result is a `resultId` the existing response already explains | `false` has no meaning here |
| GetMethodSource | `includeDocComments` | `true` | doc comments are already in the returned source (`WorkspaceReadNavigationImpl.cs:245`) | `false` would want them stripped |

Explicitly NOT ignorable: `contextLines`, `maxLines`, `startLine/endLine` on GetMethodSource, `RunTest.timeout` (units differ:
ms vs `timeoutSeconds`), `head_limit <= 0` (Grep "unlimited" has no Search equivalent). `GetMethodSource.containerName` and
`Search.path/filePath` in text mode are not ignored either: the first becomes a real parameter (Steps 4-5), the second is translated
to `fileGlob` or left for `Validate` (Steps 8-10).

### Rewrite list (value-translating; all skipped if their target is already supplied or not declared)

| Tool | From | To | Notes |
| --- | --- | --- | --- |
| Search | `head_limit: N` (N > 0) | `maxResults: N` | alias; `<= 0` stays rejected with a hint |
| ReadFile | `offset: A`, `limit: B` (ints or numeric strings) | `startLine: A`, `endLine: A+B-1` | `limit` alone -> `endLine: B`; `offset` alone -> `startLine: A`; skipped when `startLine`/`endLine`/`memberName` is present |
| RunTest | `project: X` or `projectName: X` | `scope: "project"`, `scopeName: X` | only when `scope` and `scopeName` are absent |
| GetDiagnostics | `filePath: X` | `scope: "file"`, `scopeName: X` | only when `scope` and `scopeName` are absent |
| Build | `fullBuild: true`/"true" | `level: "fullBuild"` | literal mapping (Decisions item 3); `false` stays rejected |
| Build | `mode: M` where M is a `level` enum member | `level: M` | enum members read from the emitted schema |
| Search (mode text) | `path: P` | `fileGlob: G` | G from the path rules below; `fileGlob` must be absent; unmappable P stays for `Validate` |
| Search (mode text) | `filePath: P` | `fileGlob: G` | same rules; Step 10; unmappable P keeps today's behaviour (ignored with a warning) |

#### Path-to-fileGlob rules (`ToolArgumentRewrites.PathToFileGlob(string value, string? solutionRoot)`, pure, returns the glob or `null`)

Apply in this order; `null` means "cannot map unambiguously" and the key is left alone.

1. Trim; empty -> `null`. Replace `\` with `/`.
2. Any segment equal to `..`, a `~` start, a `[` character, a UNC start (`//`), or a rooted path without a drive letter (starts with `/`
   while the root does not) -> `null`.
3. Absolute path (`X:/...`): normalize `solutionRoot` the same way (backslashes, no trailing `/`); if `solutionRoot` is null or empty -> `null`;
   the value must equal the root (-> `null`: that is the default scope, nothing to restrict) or start with `<root>/` compared
   `OrdinalIgnoreCase`; strip the prefix. Outside the root -> `null`.
4. Strip a leading `./` and any trailing `/`; empty after that -> `null`.
5. Contains any of `*`, `?`, `{`: already a glob; return it as is (it is relative to the root after step 3).
6. Ends with `.cs` (`OrdinalIgnoreCase`): a file; return the relative path as is. A single segment (`Foo.cs`) stays a bare file name,
   which `fileGlob` matches in any folder; the Note says so.
7. Otherwise a directory: return `<relative dir>/**`.

The Note names the exact glob: `Note: 'path' is not a parameter of Search; it was treated as fileGlob: "RoslynSentinel.Tools.Basic/**" (text
mode restricts files with fileGlob). Use fileGlob in future calls.` For a bare file name append `This matches every file named Foo.cs in the solution.`

## Execution rules

- One `RunTest` / `Build` at a time; C# edit dispatches are sequential (CLAUDE.md). Use `ReplaceSnippet` batch / `Member` per the
  gotchas; the new files in Steps 6 and 16 are created with `CreateFile` then `Member(addMember)` as the implementer's tool set allows.
- Test baseline is 0 failures (`docs/known-failing-tests.Solution.txt`); any failure is new.
- New code that runs in `AddArgumentValidationFilter` must catch its own exceptions and return `null` (see "What already
  exists": a throw skips `Validate`).
- Test project for new server-filter tests: `RoslynSentinel.Tests.Server`. Unit tests are pure (no server); end-to-end tests extend the existing
  in-process harness in `ToolParameterAliasTests.cs` (`SetUp`, `ActiveModes = { "Workspace" }`). If a tool used in a test is not
  registered under `ActiveModes`, look the mode up in `RoslynSentinel.Server.Basic/ToolClassRegistry.cs` and add it to the set.
  `GetMethodSource` tests live in `RoslynSentinel.Tests.Tools.Basic/GetMethodSourceTests.cs`.
- Slice order and dependencies: 1, 2, 3 independent (3 needs only the alias machinery). The container work runs in this order, each step compile-green on its own:
  4a (shared strict helpers) -> 4b (`ToolNotFoundException` candidates) -> 4 (GetMethodSource, calls 4a) -> 5 (its tests) -> 5a (ReadFile refusal, needs 4a and 4b) -> 5b (its tests)
  -> 5c (Member replace) -> 5d (Member remove and verification) -> 5e (Member view; needs 5a's flag) -> 5f (summary-comment view/remove) -> 5g (summary-comment add).
  4 is also before 14; 5a is before 14 (the call-shape prose names the ReadFile refusal); 5g is before 17. **Steps 5c-5g are PAUSED (decided 2026-10-24)** until the first-match ambiguity plan (`plan_first_match_ambiguity_audit.md`) finishes its work on `MemberRefactoringEngine`, `SnippetSymbolLocator`, `SyntaxTargetResolver`, `SymbolNavigationEngine`, `BasicRefactoringEngine` and `AdvancedStructuralEngine`; then re-audit 5c-5g against its result and either resume or fold them into it. Step 17 does not wait for them. 5c-5g do not touch 6-16, but never run in parallel with them (C# edit dispatches are sequential). 6 -> 7 -> 8 -> 9 -> 10
  (6 defines the `Apply` signature including `solutionRoot`; 9 wires it; 10 is DECIDED, keep, and nothing depends on it). 11 before 12. 12 after 4 and 9. 13 any time after 9 and 4.
  14 -> 15 -> 16 (call-shape blocks, then the guard test over them). 17 last.
- ASCII-only punctuation in code comments, strings and docs.

## Steps

### Step 1 - Make the report script reproduce the baseline and the rate
- Files: `scripts/Get-UnknownParameterReport.ps1`
- Change: (a) parse each matching line with `ConvertFrom-Json`; keep only `tool_result` items with `is_error: true` whose text, or the
  echo `"message"` property, matches `^Unknown parameters? ('x'(, 'y')*) for tool 'T'\.`; take every name from the quoted list (plural
  form) instead of one; keep de-dup by `tool_use_id`. (b) New switches: `-Until <date>`; `-Weekly` (Sunday-start week table: rejections,
  mcp calls from `.claude/journal/*.calls.jsonl` rows with `kind = mcp`, rate, subagent share); `-ByAgentType` (for rejections found
  in `subagents\agent-*.jsonl`, read `agentType` from the sibling `agent-*.meta.json`). (c) Keep the existing suggestion heuristic and
  the current-schema re-evaluation, but report the pre/post alias split by taking a new `-Cutover <date>` (default 2026-10-05) and
  printing counts on each side. (d) New switch `-NoteRepeats`: count `Note: '<x>' is not a parameter of <T>; it was treated as` results
  (and the new `fileGlob:` / `filePath` note shapes from Steps 8-10) per session and report how many sessions saw the same note more than
  once (the repeat metric for Decisions item 1). (e) New column `SquashesToDeclared` in the name table: true when the unknown name, with `_` and `-`
  removed and compared case-insensitively, equals a parameter the tool declares today (the check that keeps the global alias table of Step 3 honest:
  a new snake_case habit shows up here before it costs much). (f) Header comment: state the baseline numbers from this plan.
- Done when: `.\scripts\Get-UnknownParameterReport.ps1 -Since 2026-09-16 -Until 2026-10-25 -Weekly -ByAgentType -NoSchema` prints 388
  distinct rejections (plus any made after 2026-10-24 21:30 UTC), 276 from subagents, 142 at/after the cutover, and the weekly rates in
  the table above (within one rejection per week); `-NoteRepeats` prints a per-note repeat count (about 5 sessions of ~35 for the
  existing alias notes); `SquashesToDeclared` is true only for `file_path` (15 tokens) in the baseline window.
- Out of scope: changing the suggestion heuristics, anything under `.claude/journal` writers.

### Step 2 - Data-only alias additions
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs` (the `ParameterAliases` initializer, lines 315-411),
  `RoslynSentinel.Tests.Server/ToolParameterAliasTests.cs`
- Change: add entries (each target verified above; the existing guard test
  `EveryAliasEntry_ForAnActiveTool_PointsAtADeclaredParameter_AndIsNotItselfDeclared` fails if a target is not declared):
  - `ReplaceSnippet`: `oldString` -> `oldContent`, `newString` -> `newContent`
  - `Git`: `path` -> `paths`, `filePath` -> `paths`, `filePaths` -> `paths`, `limit` -> `count`, `commitish` -> `ref`
  - `GetLargeResult`: `fullResultsId` -> `resultId`
  - `Member`: `memberCode` -> `newMemberSource`
  Call sites of `ParameterAliases`: `ApplyParameterAliases` (same file, line 514) and tests (`ToolParameterAliasTests.cs`
  lines 247-274); no signature change.
- Done when: `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Server, filter: ToolParameterAliasTests)` passes, including new
  tests `ReplaceSnippetStringAliases_AreInTheAliasTable`, `GitPathAlias_IsRenamedToPaths_AndNoteNamesPaths` (end to end: `Git(operation: status)`
  is not suitable; use `operation: log, path: <seeded file>, limit: "2"` and assert no "Unknown parameter", a note
  `treated as 'paths'` and a note `treated as 'count'`; this also proves a numeric string binds to `count`).
- Out of scope: GetMethodSource `memberName` (already aliased since b2ba4208), RunTest `timeout`.

### Step 3 - Explicit global alias `file_path` -> `filePath` (tool-independent, noted)
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Tests.Server/ToolParameterAliasTests.cs`
- Why a global table and not a per-tool entry: `file_path` is not declared by any tool; it is the built-in Read/Edit/Write habit and can arrive on any tool that
  takes `filePath` (ReadFile today: 15 of 15 occurrences; GetMethodSource, GetFileOutline, ReplaceSnippet, Member, ApplyDiff, CreateFile are the same shape).
  A per-tool entry would need ~10 rows kept in sync; skipping tools without `filePath` is already how aliases behave.
- Change (one `ReplaceSnippet` batch, definition first):
  1. Beside `ParameterAliases` (line 315) add
     `public static readonly IReadOnlyDictionary<string, string> GlobalParameterAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["file_path"] = "filePath" };`
     with a comment: only names measured in real rejections, only when the target means the same on every tool that declares it; a per-tool entry in
     `ParameterAliases` for the same key wins; add a row only after `Get-UnknownParameterReport.ps1`'s `SquashesToDeclared` column flags the name.
  2. `ApplyParameterAliases` (line 514): replace the early return `if (!ParameterAliases.TryGetValue(toolName, out var aliases)) return null;` with
     `ParameterAliases.TryGetValue(toolName, out var aliases);` (aliases may be null) and, in the key loop, resolve the canonical name as
     `(aliases is not null && aliases.TryGetValue(key, out var canonical)) || GlobalParameterAliases.TryGetValue(key, out canonical)`. Every existing skip rule is
     unchanged and now covers the global table too: skip when the key is itself declared, when the target is not declared by this tool, or when the call already
     supplies the target. The note is the standard one: `Note: 'file_path' is not a parameter of ReadFile; it was treated as 'filePath'. Use 'filePath' in future calls.`
     Update the XML summary of `ApplyParameterAliases` to mention the global table.
  3. Nothing changes in `TryGetSchemaParameters`, `NormalizeParameterCase`, or the filter: the notes already flow through the existing `aliasNotes` pattern.
- Call sites: `ParameterAliases` and `ApplyParameterAliases` as in Step 2 (same file, tests at `ToolParameterAliasTests.cs` lines 247-274); the new member has no other readers.
- Done when: `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Server, filter: ToolParameterAliasTests)` passes, including new tests
  `FilePathSnakeCase_OnReadFile_IsRenamedWithAStandardNote` (end to end: `ReadFile` with `file_path` runs on the seeded file, a note says `treated as 'filePath'`),
  `FilePathSnakeCase_OnAToolWithoutFilePath_IsStillRejected` (for example `RunTest` with `file_path`: target not declared, so it stays unknown),
  `FilePathSnakeCase_WithFilePathAlsoSupplied_IsLeftForValidate`, `GlobalAliasKey_IsNeverDeclaredByAnyActiveTool` (guard: for every active tool, no global alias key is
  a declared parameter), and the existing `EveryAliasEntry_*` guard still passes.
- Out of scope: `NormalizeParameterCase` and the case-insensitive comparer stay as they are (case-only renames remain silent); other snake_case names.

### Step 4a - Shared strict-container helpers in SyntaxTargetResolver (definitions only; nothing calls them yet)
- Files: `RoslynSentinel.Engines.Basic/SyntaxTargetResolver.cs`, `RoslynSentinel.Tests.Advanced/GenericContainerNameTests.cs`
- Why first: Step 4 (GetMethodSource), Step 5a (ReadFile) and Steps 5c-5g (Member and summary-comment tools) all need "match the container strictly" and "explain a container miss". One definition, many callers, no duplicated wording.
  `SyntaxTargetResolver` is a `public static class` (line 11); `SyntaxNodeCandidate` and `CandidateKind` are public (lines 555, 566); `BuildDeclarationCandidates` is `internal static` (line 420), reachable from new members of the same class.
- Why not change `FilterByContainingType` (line 85): it has 8 callers outside Steps 4 and 5a (migrated one engine method at a time in Steps 5c-5g, listed in Step 5a) and stays byte-for-byte until the last caller has moved.
- Change (add after `BuildContainerNotFoundMessage`, line ~126; one `Member(addMember)` per member; `using` for `RoslynSentinel.Common` is already present because the class returns `AmbiguousCandidate` at line 420):
  1. `private static string LastSegment(string name)`: trim; the text after the last '.' that is outside angle brackets (scan from the end, tracking '<' and '>' depth); the whole trimmed name when there is no such dot; the empty string for a trailing dot.
  2. `public static bool ContainingTypeMatches(string? actualTypeName, string requestedContainer)`: false when `actualTypeName` is null or `LastSegment(requestedContainer)` is empty; otherwise
     `NormalizeTypeName(actualTypeName).Equals(NormalizeTypeName(LastSegment(requestedContainer)), StringComparison.OrdinalIgnoreCase)`. Case-insensitive and arity-tolerant (`Box`, `Box<T>`, ``Box`1``).
     **DECIDED 2026-10-24:** a dotted request (`Outer.Inner`, `Ns.Type`, `Outer<T>.Inner<U>`) is compared by its LAST segment, so `Outer.Inner` matches a type named `Inner`. `NormalizeTypeName` is applied to the last segment only, because it cuts at the
     first '<' (line 70 region) and would wrongly truncate `Outer<T>.Inner<U>` to `Outer`. The qualifier is NOT verified: `Wrong.Inner` also matches `Inner` (a candidate's `ContainingTypeName` is a bare identifier, so verifying the qualifier would need the whole enclosing chain; if two nested types
     are both named `Inner` both match and the callers already report that as an ambiguity). The rule stays strict: it never falls back to a type whose last segment differs, and a top-level declaration (null `ContainingTypeName`) matches no container.
  3. `public static List<SyntaxNodeCandidate> FilterByContainingTypeStrict(List<SyntaxNodeCandidate> candidates, string? containerName)`: null or whitespace -> `candidates` unchanged; otherwise only candidates with
     `ContainingTypeMatches(c.ContainingTypeName, containerName)`. Never falls back; returns an empty list even when a single candidate exists elsewhere.
  4. `public static (string Message, IReadOnlyList<AmbiguousCandidate> Candidates) BuildContainerMissDetail(SyntaxNode root, SourceText sourceText, string memberName, string containerName, string filePath, string retryCallTemplate, bool methodsOnly, bool includeTypeKinds = false)`.
     - `retryCallTemplate` is the whole call the caller would retry, with the placeholders `{file}`, `{member}`, `{container}` (each may appear once), for example `ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')` or
       `GetMethodSource(filePath: '{file}', methodName: '{member}', containerName: '{container}')`. It must contain one `, <name>: '{container}'` argument; for a top-level declaration the helper removes that argument (regex `, \w+: '\{container\}'`) because there is no container to name.
     - `listed` = `PreferNonInterfaceMember(ResolveCandidates(root, sourceText, memberName)` then, unless `includeTypeKinds`, `.Where(c => c.Kind is not (Class or Interface or Struct or Record or Enum))`, `.ToList())`, further reduced to `Method`/`Constructor` kinds when `methodsOnly`.
       `includeTypeKinds: true` is for tools that may target a type declaration (`Member(remove)`, the summary-comment tools).
     - Message, in this order, single spaces between sentences. (a) When no `BaseTypeDeclarationSyntax` in `root` satisfies `ContainingTypeMatches(t.Identifier.Text, containerName)`: `BuildContainerNotFoundMessage(root, containerName)`; otherwise
       `Type '<C>' declares no <method or constructor|member|declaration> named '<m>' (searched the whole file '<filePath>'; type arguments optional, case ignored).` (`method or constructor` when `methodsOnly`, `declaration` when `includeTypeKinds`, else `member`.)
       (b) If `listed.Count > 0`: `'<m>' is declared in: 1. <Type>.<Preview> - lines <S>-<E>; 2. ... (+K more).` (at most 10 entries, `Preview` is the first line of the declaration, at most 80 chars; a candidate whose `ContainingTypeName` is null prints `(top level) <Preview>`);
       else `No type in this file declares '<m>'; list the file's members with GetFileOutline(filePath: '<filePath>').` (c) If `listed.Count > 0`: for each distinct `ContainingTypeName` that has exactly one listed candidate (at most 3):
       `Retry <the template, expanded>.` with `{container}` = that type (a type with overloads gets no container retry). A single top-level candidate gets `Retry <the template without its container argument>; the declaration is top level, so omit the container.`
       Then always one: `To read exact lines: ReadFile(filePath: '<filePath>', startLine: <S1>, endLine: <E1>).` using the first listed candidate's real range.
     - Candidates = `BuildDeclarationCandidates(listed, sourceText, filePath)` (cap 10, `Counted` true). Never throws for a bad `containerName`.
- Call sites: none yet (definitions). `FilterByContainingType` and its callers are untouched.
- Done when: `Build` has 0 errors and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Advanced, filter: SyntaxTargetResolverTests)` passes with new tests in that fixture (`GenericContainerNameTests.cs:82`):
  `ContainingTypeMatches_GenericAndCaseSpellings_AllMatch`, `ContainingTypeMatches_NullActual_IsFalse`, `FilterByContainingTypeStrict_SingleCandidateInOtherType_ReturnsEmpty` (the same input through the lenient `FilterByContainingType` still returns it: pins both),
  `FilterByContainingTypeStrict_NullContainer_ReturnsInput`,
  `ContainingTypeMatches_DottedContainer_MatchesByLastSegment` (`Outer.Inner`, `Ns.Type`, `Outer<T>.Inner<U>`, `Outer.Inner<U>` against actual `Inner`/`Type`),
  `ContainingTypeMatches_DottedContainerWithADifferentLastSegment_IsFalse` (`Outer.Other` and `Inner.` against `Inner`),
  `ContainingTypeMatches_DottedContainer_DoesNotVerifyTheQualifier` (`Wrong.Inner` matches `Inner`; this test documents the accepted looseness),
  `FilterByContainingTypeStrict_DottedContainerWithNoMatchingLastSegment_ReturnsEmpty`,
  `BuildContainerMissDetail_ContainerAbsent_ListsTypesAndWhereTheMemberLives`, `BuildContainerMissDetail_ContainerPresentWithoutMember_NamesTheTypesThatDeclareIt_WithRetryPerTypeAndReadFileRange`,
  `BuildContainerMissDetail_MemberDeclaredNowhere_PointsAtGetFileOutline`, `BuildContainerMissDetail_MethodsOnly_IgnoresAPropertyOfTheSameName`, `BuildContainerMissDetail_OverloadsInOneType_OffersNoContainerRetryForThatType`,
  `BuildContainerMissDetail_CandidateLinesMatchTheMessage`, `BuildContainerMissDetail_RetryTemplate_IsExpandedWithFileMemberAndContainer`,
  `BuildContainerMissDetail_TopLevelDeclaration_SaysOmitTheContainerAndDropsTheContainerArgument`, `BuildContainerMissDetail_IncludeTypeKinds_ListsATypeNamedLikeTheMember`.
- Out of scope: any caller of the new members; changing `NormalizeTypeName` or `FilterByContainingType`.

### Step 4b - ToolNotFoundException can carry structured candidates
- Files: `RoslynSentinel.Common/ToolException.cs`
- Why: `ToolNotFoundException.StructuredDetail` holds only a snippet today (`ToolException.cs:86`); the ReadFile refusal needs the candidate list on the wire the same way `ToolAmbiguousMatchException` does (`ToolException.cs:122-137`).
  `ToolErrorMapper.ToResultError` already forwards any exception's `StructuredDetail` (`ToolException.cs:254-267`), so the mapper does not change.
- Change: add `IReadOnlyList<AmbiguousCandidate>? candidates = null` as the LAST parameter of `ToolNotFoundException(string message, string? missingTargetKind = null, string? missingTargetName = null, SnippetMatching.SnippetFailureDetail? snippet = null)` (line 69),
  a get-only `Candidates` property, and change `StructuredDetail` (line 86) to: the candidates through `ToolErrorMapper.ToStructuredDetail(Candidates)`, then the snippet when set; null when neither (the same shape as `ToolAmbiguousMatchException.StructuredDetail`).
  Existing calls pass at most four arguments, so every `new ToolNotFoundException(` compiles unchanged and behaves the same when `candidates` is null. First run `Search(mode: references)` on `StructuredDetail` and confirm no consumer casts a NotFound detail
  to `SnippetFailureDetail`.
- Done when: `Build` has 0 errors. The behavioural proof is Step 5b's `StructuredDetail` assertion.
- Out of scope: other exception types.

### Step 4 - GetMethodSource: optional `containerName` and an ambiguity error instead of a first-match pick (one edit)
- Files: `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationImpl.cs`, `RoslynSentinel.Tools.Basic/WorkspaceReadNavigationTools.cs`,
  `RoslynSentinel.Tools.Basic/WorkspaceTools.cs`
- Why these three together: the parameter goes before `CancellationToken`, and both existing callers pass `cancellationToken` positionally
  as the 4th argument (`WorkspaceReadNavigationTools.cs:36`, `WorkspaceTools.cs:570`), which would silently stop compiling (a token
  bound to a `string?`). Use one `ReplaceSnippet` batch, definition first (`Impl`), then the two callers.
- Resolution rule (a strict filter on the syntax `GetMethodSource` already walks, deliberately NOT a reuse of `SymbolNavigationEngine.GetMemberSourceAsync`,
  `SymbolNavigationEngine.cs:2653-2734`, whose lenient `SyntaxTargetResolver.FilterByContainingType` (`SyntaxTargetResolver.cs:85-93`) returns all candidates when only one exists or none match):
  use the strict helpers added in Step 4a - `SyntaxTargetResolver.ContainingTypeMatches` for the type filter and `SyntaxTargetResolver.BuildContainerMissDetail` for the container-miss error.
  The same helpers serve `ReadFile` in Step 5a, so the wording and candidate lists are defined once. `Tools.Basic` already references `Engines.Basic`; add `using RoslynSentinel.Engines.Basic;` if absent.
- Change:
  1. `WorkspaceReadNavigationImpl.GetMethodSource` (line 202): signature becomes
     `(ToolCallReason reason, string filePath, string methodName, string? containerName = null, CancellationToken cancellationToken = default)`.
     Build the candidate list in this order. (a) `all = root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>()`. (b) When `containerName` is not null or whitespace, keep
     only nodes whose nearest enclosing `TypeDeclarationSyntax` (class, struct, record, interface) satisfies
     `SyntaxTargetResolver.ContainingTypeMatches(t.Identifier.Text, containerName)` (Step 4a). (c) Name match, two passes as today (line 235): `OrdinalIgnoreCase`
     candidates are used only when the ordinal pass found none. (d) Interface rule: if both interface-declared members (parent is `InterfaceDeclarationSyntax`) and others
     remain, drop the interface ones (mirrors `SyntaxTargetResolver.PreferNonInterfaceMember`, which works on a different candidate type). (e) Exactly one left -> return it exactly as
     today (same envelope, signature, attributes, large-result offload). More than one -> the ambiguity error below; the first match is never returned. None -> `BuildMethodNotFoundErrorAsync`
     when `containerName` is absent (unchanged), or the container not-found error below when present.
  2. Ambiguity error: `internal static ResultError BuildAmbiguousMethodError(IReadOnlyList<BaseMethodDeclarationSyntax> matches, string methodName, string? containerName, string requestedFilePath)`
     placed beside `BuildMethodNotFoundErrorAsync` (line 100). It returns
     `new ResultError(ToolErrorCode.Ambiguous, message, null, ToolErrorMapper.ToStructuredDetail(candidates)?.ToList())` (`ToolErrorCode.Ambiguous` = "Ambiguous", `SentinelCallToolResult.cs:84`;
     `ToStructuredDetail` is public in `ToolException.cs:273`). One `AmbiguousCandidate` (`AmbiguousCandidate.cs`) per match, capped at 10: `FilePath` = `requestedFilePath`, `Line` = the declaration's start line
     (`GetLocation().GetLineSpan()`, the same numbers the success envelope reports as `ReturnedFromLine`/`ReturnedToLine`, lines 255-258), `Kind` = `"method"` or `"constructor"`,
     `Signature` = the existing `BuildSignature(method)` (line 1360: modifiers, return type, name, full parameter list with types), `ContainingType` = nearest type name,
     `ContextSnippet` = the trimmed first line of the declaration, `LineBefore` = null, `Counted` = true. Message template (ladder style, states what was searched and the next call):
     `Method or constructor '<m>' is declared <N> times in '<file>' (searched the whole file<, narrowed to type '<C>'>). GetMethodSource never picks one for you. Candidates: 1. <Type>.<signature> - lines <S>-<E>; 2. ... (+K more). To choose a type, retry GetMethodSource(filePath: '<file>', methodName: '<m>', containerName: '<Type>'). To choose between overloads, read the exact lines: ReadFile(filePath: '<file>', startLine: <S1>, endLine: <E1>).`
     The `containerName` retry sentence is included only for types that have exactly one candidate (name them, at most 3); when every candidate is in one type (overloads) only the `ReadFile` sentence
     appears. The `ReadFile` sentence always uses the first candidate's real range as the example.
  3. Container miss (when `containerName` is given and no method matches): call
     `var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(root, sourceText, methodName, containerName, requestedFilePath, retryCallTemplate: "GetMethodSource(filePath: '{file}', methodName: '{member}', containerName: '{container}')", methodsOnly: true)`
     (Step 4a; `sourceText` from `document.GetTextAsync`, the document is already open for the envelope line numbers) and return
     `new ResultError("MethodNotFound", message, null, ToolErrorMapper.ToStructuredDetail(candidates)?.ToList())`. No container wording is written in `WorkspaceReadNavigationImpl`: the shared message
     covers a container absent from the file (lists the declared types), a container present without the method (names the types that declare it, with signature and line range) and the retry calls.
  4. `WorkspaceReadNavigationTools.GetMethodSourceTool` (line 32): add `[Consumes(DataTag.SymbolName, required: false)][Description("Optional. Narrows the search to the type that declares the method (generic arguments optional: 'Box' or 'Box<T>'; 'Outer.Inner' means Inner). A name that is still declared more than once returns an Ambiguous error listing every candidate; the first match is never returned.")] string? containerName = null`
     before `cancellationToken`, and forward it. The tool-level `[Description]` is unchanged. Optional on purpose: CLAUDE.md asks that a parameter added to stop a known model failure be mandatory unless a
     concrete usage needs the fallback, and here the fallback is every existing call (unique method names; all 11 existing fixture calls pass no container).
  5. `WorkspaceTools.GetMethodSourceTool` (line 568, a non-tool delegator): add `string? containerName = null` before the token and forward it.
- **Recommendation on picking one overload (decision for the human, not assumed): add no selector parameter now.** Reasons: the retry the error names, `ReadFile(filePath, startLine, endLine)` with the
  candidate's own line range, already works for every case (verified: `ReadFile` has `startLine`/`endLine` inclusive, 1-based, `WorkspaceFileEditTools.cs:44-45`) and returns the same text; a `parameterTypes` string is
  hard for a weak model to write correctly and a `startLine`/`lineNumber` parameter would duplicate ReadFile and add a second meaning of `startLine` that the hint table currently redirects to ReadFile (Step 12). The cost
  is that an overload pick loses the structured `signature`/`attributes` envelope. Revisit with data: if `GetMethodSource` `Ambiguous` results with `containerName` supplied (same-type overloads) are followed by a
  second `GetMethodSource` instead of `ReadFile`, add an optional `startLine`-style selector in its own plan.
- **Caller audit for the behaviour change (searched 2026-10-24 with `Search`/`Grep` over C#, docs, hooks, scripts, agent files and prompts; the fix per caller):**

| Caller | Evidence | Relies on first match? | Fix |
| --- | --- | --- | --- |
| `RoslynSentinel.Tests.Tools.Basic/GetMethodSourceTests.cs` (11 tool calls) | fixtures use `Bar`, `WithCtor` (one constructor), `Qux`, `Size`, `Foo.Bar`, `SomeClass`, `bar` (case fallback finds only `Bar`), `Huge`, `NoSuchMethod` | No, every name is unique in its file | None; Step 5 adds the new cases |
| `RoslynSentinel.Tests.Server/RelativePathInputAuditTests.cs:156` | seeds `AuditTarget` once | No | None |
| `ClaudeLeanModeTests.cs`, `McpToolsetControlTests.cs`, `ToolClassRegistry.cs:44` | tool name only | No | None |
| `WorkspaceReadNavigationTools.cs:36`, `WorkspaceTools.cs:570` | only in-repo C# callers of `Impl.GetMethodSource` | No (forwarders) | Updated in the same edit (Step 4) |
| Error and prompt text that tells a model to call `GetMethodSource` (`AppliedChangeSummary.cs:112`, `DriftMessages.cs:9`, `HaltInfo.cs:23`, `AgentSystemPrompts.cs:59`, `ToolParams.cs:71`, `BuildMethodNotFoundErrorAsync` "Retry GetMethodSource with filePath ...") | text only, no method argument | No | None; the new ambiguity message is self-contained |
| Model-eval and PlanStepRunner prompts | no C# prompt in `Tests.ModelEval`/`Utilities.PlanStepRunner` names a method for `GetMethodSource` | No | None |
| `docs/current/plans/plan-9b-model-test-step1.md` steps 3, 4, 9 | `ConvertAbstractClassToInterfaceAsync` (one declaration in the current tree), `ReplaceNodeFormattedAsync` (not in this tree; that plan targets the separate `RoslynSentinel-AgentTesting` clone) | No | None (historic plan); if re-run and a name is ambiguous, the new error lists the candidates |
| `.claude/hooks/enforce-dogfood.ps1` (lines 30, 33, 188, 205, 301, 597), `.claude/hooks/friction-cases.Tests.ps1:72`, `CLAUDE.md:91,106,124`, `Samples/ContosoOrders/SCENARIOS.md` (241, 376, 479) | mentions only | No | Step 13 adds the `containerName` and ambiguity wording to the hook line; others unchanged |
| `.claude/agents/*.md` | no current mention of `GetMethodSource` | No | Steps 14-15 add the call shape and the ambiguity retry line |
| Real agent calls | transcripts since 2026-10-05: about 2.4% of 2,578 resolvable calls name a method declared 2+ times in the file (estimate, see Evidence) | Yes, silently | Intended: those calls now get a candidate list and one retry |

- Call sites (searched with `Search(mode: references)`): `WorkspaceReadNavigationImpl.GetMethodSource` is called only from `WorkspaceReadNavigationTools.cs:36`; `WorkspaceReadNavigationTools.GetMethodSourceTool` only from `WorkspaceTools.cs:570`;
  `WorkspaceTools.GetMethodSourceTool` is called by `GetMethodSourceTests.cs` (all positional `(reason, path, name)`, unaffected).
- Done when: `Build` has 0 errors and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Tools.Basic, filter: GetMethodSourceTests)` still passes
  (unique names behave exactly as before). The new behavioural tests are Step 5, which lands immediately after.
- Out of scope: a selector parameter for same-type overloads (agreed: none, see Decisions item 2); changing `ReadFile` (Step 5a).

### Step 5 - GetMethodSource ambiguity and `containerName` tests
- Files: `RoslynSentinel.Tests.Tools.Basic/GetMethodSourceTests.cs`
- Change: add tests in the existing fixture; each builds its own solution like `GetMethodSource_ConstructorRequestedByClassName_ReturnsConstructorSourceAsync`
  (line 81) does, calling `_tools.GetMethodSourceTool(reason: "test message", path, name, containerName: ...)`. Error assertions use `result.ErrorData` (`ErrorCode`, `Message`, `StructuredDetail`) as in lines 112-175:
  - `GetMethodSource_SingleMatch_IsUnchangedAsync`: the existing happy-path tests stay as the proof; add one case with the name also present only in a different file to show a single in-file match still returns.
  - `GetMethodSource_SameNameInTwoTypes_WithoutContainerName_ReturnsAmbiguousListingBothAsync`: `class A { int Run(int x) => 1; } class B { int Run(string s) => 2; }`; `ErrorCode` is `Ambiguous`, message contains `A.`, `B.`,
    both signatures, `lines`, `containerName: 'B'` and `ReadFile(`; `StructuredDetail` has 2 entries with `ContainingType` `A` and `B` and distinct `Line`; `IsError` is true and no source is returned.
  - `GetMethodSource_SameNameInTwoTypes_WithContainerName_ReturnsTheRightOneAsync`: same file, `containerName: "B"` returns source containing `=> 2`; `"A"` returns `=> 1`.
  - `GetMethodSource_OverloadsInOneType_ReturnsAmbiguousListingEverySignatureAsync`: `class A { void Run(int x){} void Run(string s){} }`; message lists `Run(int x)` and `Run(string s)`, offers only the `ReadFile` retry (no `containerName` sentence), and stays `Ambiguous` even with `containerName: "A"`.
  - `GetMethodSource_AmbiguityRetryHint_ReadFileRange_ReturnsTheNamedOverloadAsync`: take the first candidate's `Line` and end line from the message/`StructuredDetail`, call `_tools.ReadFileTool(reason: "test message", path, startLine: <S>, endLine: <E>)` (`WorkspaceTools.cs:576`; the fixture's `WorkspaceTools` builds its own file-edit delegate, line 65) and assert the returned text is exactly that overload: proves the retry the error names works.
  - `GetMethodSource_TwoConstructors_ByClassName_ReturnsAmbiguousAsync` (previously the first constructor was returned).
  - `GetMethodSource_InterfaceDeclarationAndImplementationInOneFile_ReturnsTheImplementationAsync`.
  - `GetMethodSource_CaseVariantNames_OrdinalMatchWinsWithoutAmbiguityAsync`: `Run` and `run` both exist, `Run` requested -> returns `Run` (case fallback only when the ordinal pass is empty).
  - `GetMethodSource_GenericContainerSpellings_AllResolveAsync`: `class Box<T> { T Get() => default!; }` resolves with `"Box"` and `"Box<T>"`.
  - `GetMethodSource_ContainerDeclaresNoSuchMethod_ListsTheTypesThatDoAsync`: `class A { void Run(){} } class B { void Other(){} }`, container `B`: code `MethodNotFound`, message contains `declares no method`, `'Run'`, `A.` with its line range and `containerName: 'A'`; `StructuredDetail` has one candidate with `ContainingType` `A`.
  - `GetMethodSource_ContainerNotInFile_ListsDeclaredTypesAsync`: container `Nope`: message contains `No type named 'Nope'`, `Types declared here` and, because `Run` exists in `A`, `is declared in`.
  - `GetMethodSource_ConstructorWithContainerName_ReturnsTheConstructorAsync`: `methodName: "A"`, `containerName: "A"`, one constructor.
  - `GetMethodSource_DottedContainer_ResolvesByLastSegmentAsync`: `class Outer { class Inner { void Run(){} } } class Other { void Run(){} }`, `containerName: "Outer.Inner"` returns the nested type's `Run` (not `Other`'s, which is excluded by the filter); `containerName: "Inner"` returns the same.
  - `GetMethodSource_WrongDottedContainer_RefusesAsync`: same file, `containerName: "Outer.Missing"`: code `MethodNotFound`, message contains `Types declared here` and `Inner`, no source returned; `StructuredDetail` lists both `Run` declarations (`Inner` and `Other`) because the member exists elsewhere.
- Done when: `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Tools.Basic, filter: GetMethodSourceTests)` passes including the new tests.

### Step 5a - ReadFile refuses a container that does not hold the member
- Files: `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs`, `RoslynSentinel.Tools.Basic/WorkspaceFileEditImpl.cs`, `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`
- Why: `ReadFile(filePath, memberName, containerName)` goes `WorkspaceFileEditImpl.ReadFile` (line 318) -> `SymbolNavigationEngine.GetMemberSourceAsync` (line 2653), which narrows with the lenient `FilterByContainingType` (line 2672):
  a `containerName` that matches nothing, or that is given when only one candidate exists, is silently ignored and another type's member is returned (a silent wrong result for a read). `Member(operation: view, memberName, containerName)` calls the same
  engine method (`RefactoringStructuralImpl.cs:571`) and changes in Step 5e, hence an opt-in flag until then.
- Why these three together: the new parameter goes before `CancellationToken` (CLAUDE.md), and both callers pass the token positionally, so they must change in the same edit. Use one `ReplaceSnippet` batch, definition first.
- Change:
  1. `SymbolNavigationEngine.GetMemberSourceAsync`: add `bool requireContainerMatch = false` after `lineAfter`, before `cancellationToken`. Replace the single line
     `memberCandidates = SyntaxTargetResolver.FilterByContainingType(memberCandidates, containerName);` (line 2672) with: when `requireContainerMatch` and `containerName` is not null or whitespace,
     `var narrowed = SyntaxTargetResolver.FilterByContainingTypeStrict(memberCandidates, containerName);` and, if `narrowed.Count == 0` and (`memberCandidates.Count > 0` or `allNameCandidates.Count == 0`), call
     `var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(root, sourceText, memberName, containerName, document.FilePath ?? string.Empty, "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false);` and
     `throw new ToolNotFoundException(message, "member", memberName, candidates: candidates);` (Step 4b); otherwise `memberCandidates = narrowed`. With the flag false keep the existing line exactly. A type-level name
     (`allNameCandidates.Count > 0`, `memberCandidates.Count == 0`) still reaches the existing `ToolTargetIneligibleException`; overloads inside the matching container still reach the existing `ToolAmbiguousMatchException`.
     Update the XML summary: with `requireContainerMatch` a `containerName` that matches no candidate is refused (NotFound, listing the types that declare the member) instead of ignored.
  2. `WorkspaceFileEditImpl.ReadFile` (line 318): the call becomes `GetMemberSourceAsync(filePathResolved, memberName, containerName, null, null, null, true, cancellationToken)`. The existing `catch` already maps the exception with
     `ToolErrorMapper.ToResultError(ex, _workspaceManager, "ReadFile")` to code `NotFound` with the candidates as `StructuredDetail`; no other change.
  3. `RefactoringStructuralImpl.Member` (line 571): pass `false` for the new parameter (`..., lineAfter, false, cancellationToken)`) with the comment `// TODO Member(view) keeps the lenient container fallback; Step 5e of plan_unknown_parameter_rejection_reduction flips the flag to true and removes this`.
- Call sites (`Search(mode: callers)` on `GetMemberSourceAsync`, 12): `WorkspaceFileEditImpl.cs:318` (passes `true`), `RefactoringStructuralImpl.cs:571` (passes `false`), and 10 calls in `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs`
  (lines 237, 271, 290, 307, 325, 370, 978, 1009, 1027, 1045) that pass at most `contextSnippet:` by name, no container and no token: unaffected.
- **Caller audit for the ReadFile change (searched 2026-10-24: `Search` over C#, `Grep` over `.claude` and `docs/current`, transcripts):**

| Caller | Evidence | Relies on the fallback? | Fix |
| --- | --- | --- | --- |
| `RoslynSentinel.Tests.Tools.Basic/ReadFileTests.cs:~378` `ReadFile_MemberNameWithContainerName_ReturnsMethodFromNamedContainerAsync` | container `Second` holds `Value` | No | None; must stay green |
| `WorkspaceFileEditTools.cs:53`, `WorkspaceTools.cs:584` | forwarders of `containerName` | No | None |
| `RefactoringStructuralImpl.cs:571` (`Member` view) | same engine method | Possibly (lenient today) | Passes `false` until Step 5e flips it to `true` |
| `MemberRefactoringTests.cs` (10 engine calls) | none passes a container | No | None; Step 5b adds a characterization test |
| `.claude/agents`, hooks, `CLAUDE.md`, `docs/current`, model-eval and PlanStepRunner prompts | no text passes `containerName` to `ReadFile` (the only mentions are the Step 14-15 call-shape blocks this plan adds) | No | Step 14 prose names the refusal |
| Real calls | 8,574 ReadFile calls since 2026-10-05 (parsed tool_use blocks, de-duplicated by id): 58 with `memberName`, 10 of those with `containerName` | Unknown (not replayed) | Measured after rollout |

- **Other callers of the lenient `FilterByContainingType` (8 in total, found with `Search(mode: callers)`). DECIDED 2026-10-24: all migrate, one engine method per slice, in Steps 5c-5g (this step leaves them unchanged):**

| Caller | Tool surface | Migrated in |
| --- | --- | --- |
| `MemberRefactoringEngine.ReplaceMemberAsync` :2241 | `Member(operation: replace)` (write: a wrong container edits a member in the wrong type; highest priority) | Step 5c |
| `MemberRefactoringEngine.RemoveMemberAsync` :2470 and :2503 (type-deletion candidates) | `Member(operation: remove)` (write: a wrong container deletes in the wrong type) | Step 5d |
| `MemberRefactoringEngine.IsDeclarationStillPresentAsync` :2582 | post-remove verification (must use the same rule as Remove) | Step 5d |
| `SymbolNavigationEngine.GetMemberSourceAsync` :2672 via `RefactoringStructuralImpl.cs:571` | `Member(operation: view)` | Step 5e (flips this step's `false` to `true`) |
| `BasicRefactoringEngine.RemoveSummaryCommentAsync` :1753, `GetSummaryCommentAsync` :1801 | summary-comment tools (`containingTypeName`) | Step 5f |
| `BasicRefactoringEngine.AddSummaryCommentCoreAsync` :1607 | summary-comment add; also called by `CommentingEngine.CommentFileAsync` | Step 5g |

  Each of Steps 5c-5g starts with a mandatory caller audit (search tests, docs, agent files and prompts for callers that pass a container and rely on the fallback) and carries the planner's audit table. One known fallback reliance exists
  already: `MemberRefactoringTests.ReplaceMember_WholeClassOverSameNamedConstructor_ThrowsInvalidArgument` passes the namespace `"N"` as `containerName` (Step 5c fixes the test, not the behaviour).
- Done when: `Build` has 0 errors; `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: MemberRefactoringTests)` and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Tools.Basic, filter: ReadFileTests)` still pass
  (the new behavioural tests land in Step 5b).
- Out of scope: `Member` view/replace/remove and the summary-comment tools (Steps 5c-5g), an overload selector, a log-level change for the refusal.

### Step 5b - ReadFile container refusal tests
- Files: `RoslynSentinel.Tests.Tools.Basic/ReadFileTests.cs`, `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs`
- Change: in `ReadFileTests` use `UseDocumentSource(...)` (line 349) and `_tools.ReadFileTool(reason: "test message", _documentPath, memberName: ..., containerName: ...)` as the existing `ReadFile_MemberNameWithContainerName_...` test does. Error assertions read `result.ErrorData`
  (`ErrorCode`, `Message`, `StructuredDetail`):
  - `ReadFile_ContainerHoldsNoSuchMember_SingleCandidateElsewhere_RefusesWithTheTypeThatDeclaresItAsync`: `class First { int Value() => 1; } class Second { int Other() => 2; }`, member `Value`, container `Second`: `IsError`, `ErrorCode` is `NotFound` (`ToolErrorCode.NotFound`),
    `SuccessData` is null, message contains `Second`, `declares no member named 'Value'`, `First.`, `lines`, `ReadFile(filePath:` and `containerName: 'First'`; `StructuredDetail` has exactly one `AmbiguousCandidate` with `ContainingType` `First` and the 1-based declaration `Line`.
    (This is the case the old filter answered silently, because only one candidate existed.)
  - `ReadFile_RefusalRetryHints_BothWorkAsync`: from that refusal, `ReadFile(memberName: "Value", containerName: "First")` returns a member whose source contains `=> 1`, and `ReadFile(startLine: S, endLine: E)` with the `lines S-E` from the message returns text containing `Value()`.
  - `ReadFile_MemberInTwoOtherTypes_RefusalListsBothWithARetryEachAsync`: `First`, `Third` declare `Value`, container `Second` (declares `Other`): both listed with distinct lines, a `containerName` retry for each.
  - `ReadFile_ContainerNotInFile_RefusesAndListsDeclaredTypesAsync`: container `Nope`: message contains `No type named 'Nope'` and `Types declared here`.
  - `ReadFile_MemberDeclaredNowhere_WithContainer_PointsAtGetFileOutlineAsync`: member `Missing`, container `First`: message contains `No type in this file declares 'Missing'` and `GetFileOutline`; no `Exception` and no `   at ` text.
  - `ReadFile_GenericContainerSpellings_BothResolveAsync`: `class Box<T> { T Get() => default!; } class Other { int Get() => 1; }`: container `Box` and container `Box<T>` both return Box's `Get` (before this change `Box<T>` fell back to an arbitrary candidate).
  - `ReadFile_ContainerNameCaseDiffers_ResolvesAsync`: container `second` returns `Second.Value`.
  - `ReadFile_OverloadsInsideTheNamedContainer_StillAmbiguousAsync`: `class A { void Run(int x){} void Run(string s){} }`, container `A`: `ErrorCode` is `Ambiguous` (existing behaviour, unchanged).
  - `ReadFile_DottedContainer_ResolvesByLastSegmentAsync`: `class Outer { class Inner { int Value() => 1; } } class Other { int Value() => 2; }`, container `Outer.Inner` returns the member whose source contains `=> 1`; container `Inner` returns the same.
  - `ReadFile_WrongDottedContainer_RefusesAsync`: same file, container `Outer.Missing`: `IsError`, `ErrorCode` `NotFound`, message contains `Types declared here`, `StructuredDetail` lists both `Value` declarations; nothing else is returned.
  In `MemberRefactoringTests` add one characterization test `GetMemberSource_WrongContainerWithoutStrictFlag_StillFallsBack` (copy the setup of `GetMemberSource_Method_ReturnsFullBody`, line 271; call the engine with a container that does not hold the member and the default flag; it still returns the
  member) with a comment that it pins `Member(view)` as unchanged and is replaced by `GetMemberSource_WrongContainerWithStrictFlag_RefusesAsync` in Step 5e.
- Done when: `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Tools.Basic, filter: ReadFileTests)` and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: MemberRefactoringTests)` pass including the new tests.

### Step 5c - Member(replace) refuses a container that does not hold the member
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs`, `RoslynSentinel.Tests.Tools.Basic/MemberOutcomeFollowsCauseTests.cs`
- Why: `MemberRefactoringEngine.ReplaceMemberAsync` narrows candidates with the lenient `FilterByContainingType` (`memberCandidates = SyntaxTargetResolver.FilterByContainingType(memberCandidates, containerName);`, about line 2241). A `containerName` that matches no
  candidate, or is given when only one candidate exists, is silently ignored, so `Member(replace)` rewrites that member in the WRONG type: a silent wrong write. Exposure: of 222 `Member(operation: replace)` calls in transcripts since 2026-10-05, 121 passed
  `containerName` (none dotted, none with a generic spelling); whether any named a wrong container was not replayed. Background: the fallback was added on purpose (resolved blockers `docs/current/blockers/resolved/blocking_error_member_replace_ignores_containername_scoping.md`
  and `docs/current/blockers/resolved/blocking_error_member_replace_containername_regression_setup_overload.md`: "falls back to the unfiltered set if narrowing would zero out all candidates"); the implementer reads both first.
- **Before editing (mandatory): search for callers that rely on the fallback and list them in the step report.** Use `Search(mode: text)` for `ReplaceMemberAsync` and `containerName` over `RoslynSentinel.Tests*`, `Grep` over `.claude`, `docs/current`, `docs/testing`, `Samples`, `scripts`
  and the model-eval and PlanStepRunner prompts for `Member(replace` with `containerName`. Planner audit (2026-10-24), to be re-checked, not trusted:

| Caller | Evidence | Relies on the fallback? | Fix |
| --- | --- | --- | --- |
| `MemberRefactoringTests.cs:2118` `ReplaceMember_WithContainerName_ScopesToRequestedContainerOnly` | container `SecondAccuracyTests` is a real type holding `Setup` | No | None |
| `MemberRefactoringTests.cs:2784` `ReplaceMember_WholeClassOverSameNamedConstructor_ThrowsInvalidArgument` | `SetSource("namespace N { public class Foo { public Foo() { } } }")` with `containerName: "N"`: `N` is a NAMESPACE, matches no type, one candidate | **Yes** | Change the argument to `containerName: "Foo"` (the constructor's type) in this step; the asserted `declares a type` message is unchanged |
| `RefactoringStructuralImpl.cs:619` (`Member` replace) | passes `containerName` straight through | No | None |
| Other tests with `containerName: "<literal>"` | all are `addMember` / `TypedMember` / `ReadFile` | No | None |
| `.claude/agents`, hooks, `CLAUDE.md`, `docs/testing/**` step prompts, `Samples` | the `Member(add, containerName: ...)` prompts are addMember (already strict via `BuildContainerNotFoundMessage`); none passes a container to `Member(replace)` | No | None |

- Change: in `ReplaceMemberAsync` replace the lenient line with: when `containerName` is not null or whitespace, `var narrowed = SyntaxTargetResolver.FilterByContainingTypeStrict(memberCandidates, containerName);` and, if `narrowed.Count == 0`,
  `var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(root, sourceText, memberName, containerName, document.FilePath ?? string.Empty, "Member(filePath: '{file}', operation: replace, memberName: '{member}', containerName: '{container}', newMemberSource: ...)", methodsOnly: false);`
  then `throw new ToolNotFoundException(message, "member", memberName, candidates: candidates);` (thrown before any text is produced, so nothing is written); otherwise `memberCandidates = narrowed`. With no container the line behaves as today. The tool (`RefactoringStructuralImpl.Member`) already maps
  engine exceptions to `ResultError` with the structured candidates; no tool change.
- Done when: `Build` 0 errors and these pass. In `MemberRefactoringTests`: `ReplaceMember_ContainerHoldsNoSuchMember_SingleCandidateElsewhere_RefusesAsync` (throws `ToolNotFoundException`, message names the type that declares it with its line range and a `Member(` retry),
  `ReplaceMember_GenericContainerSpellings_BothResolveAsync`, `ReplaceMember_ContainerNameCaseDiffers_ResolvesAsync`, `ReplaceMember_DottedContainer_ResolvesByLastSegmentAsync` (`Outer.Inner`), `ReplaceMember_WrongDottedContainer_RefusesAsync` (`Outer.Missing`), the existing
  :2118 test unchanged, and the edited :2784 test. In `MemberOutcomeFollowsCauseTests` one tool-level test `Member_ReplaceWithWrongContainer_RefusesAndWritesNothingAsync` (copy the setup and the "writes nothing" assertions of `Member_AddMemberAfterUnparsableSource_ReturnsInvalidArgumentAndWritesNothingAsync`,
  line ~187): `ErrorCode` `NotFound`, one structured candidate, document text unchanged. Run `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: MemberRefactoringTests)` and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Tools.Basic, filter: MemberOutcomeFollowsCauseTests)`.
- Out of scope: Member remove (Step 5d), the enum-member replace route, other callers.

### Step 5d - Member(remove) and its post-remove verification refuse a container that does not hold the member
- Files: `RoslynSentinel.Engines.Basic/MemberRefactoringEngine.cs`, `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs`, `RoslynSentinel.Tests.Tools.Basic/MemberOutcomeFollowsCauseTests.cs`
- Why: `RemoveMemberAsync` uses the lenient filter twice (member candidates, about line 2470; type-deletion candidates, about line 2503) and `IsDeclarationStillPresentAsync` once (about line 2582). Today a wrong `containerName` deletes the only same-named member, or
  (through `StructuralRefinementEngine.SafeDeleteSymbolAsync`) a same-named TYPE, in the wrong place; the verification then compares against a different candidate set than the removal used. Exposure: of 257 `Member(operation: remove)` calls since 2026-10-05, 225 passed `containerName`
  (none dotted, none generic); wrong ones not replayed. The read-only resolved blocker `docs/current/blockers/resolved/blocking_error_member_remove_false_not_found.md` concerns this verification path; read it first.
- **Before editing (mandatory): search for callers that rely on the fallback and list them in the step report** (`Search(mode: text)` for `RemoveMemberAsync`, `IsDeclarationStillPresentAsync` and `containerName` over `RoslynSentinel.Tests*`; `Grep` over `.claude`, `docs/current`, `docs/testing`, `Samples`, `scripts`,
  model-eval and PlanStepRunner prompts for `Member(remove` with `containerName`). Planner audit (2026-10-24), to be re-checked:

| Caller | Evidence | Relies on the fallback? | Fix |
| --- | --- | --- | --- |
| `MemberRefactoringTests.cs:2158` `RemoveMember_WithContainerName_ScopesToRequestedContainerOnly` | container `SecondAccuracyTests` is a real type | No | None |
| `MemberRefactoringTests.cs` lines 551, 618, 2068, 2592, 2804, 2886, 2914 (`RemoveMemberAsync`) and 2933, 2936 (`IsDeclarationStillPresentAsync`) | no container passed | No | None |
| `RefactoringStructuralImpl.cs:689` and `:699` (`Member` remove and its verification) | pass the same `containerName` to both | No | None; both now agree on the rule |
| Tool-level tests removing with a container | none found | No | None |
| Docs, agent files, hooks, prompts | none passes a container to `Member(remove)` | No | None |

- Change: (1) in `RemoveMemberAsync`, right after `allNameCandidates` is computed: when `containerName` is not null or whitespace and `FilterByContainingTypeStrict(allNameCandidates, containerName).Count == 0`, build
  `BuildContainerMissDetail(root, sourceText, memberName, containerName, document.FilePath ?? string.Empty, "Member(filePath: '{file}', operation: remove, memberName: '{member}', containerName: '{container}')", methodsOnly: false, includeTypeKinds: true)` and
  `throw new ToolNotFoundException(message, "member", memberName, candidates: candidates);` (before the usage count and before any text is built, so nothing is deleted). (2) Replace the two lenient calls (`memberCandidates = ...` and the `FilterByContainingType(deletableTypeCandidates, containerName)` argument) with
  `FilterByContainingTypeStrict`; the type-level paths (`SafeDeleteSymbolAsync`, the "type-level declaration" refusal) are otherwise unchanged. A top-level type has no containing type, so `Member(remove, memberName: <TopLevelType>, containerName: <namespace or any name>)` is now refused; the message says
  the declaration is top-level and to omit `containerName` (Step 4a). (3) `IsDeclarationStillPresentAsync`: `var filtered = SyntaxTargetResolver.FilterByContainingTypeStrict(allCandidates, containerName); if (filtered.Count == 0) return false;` before the existing resolve, so a member
  removed from the named container reports "gone" even if a same-named member remains in another type.
- Done when: `Build` 0 errors and these pass. In `MemberRefactoringTests`: `RemoveMember_ContainerHoldsNoSuchMember_SingleCandidateElsewhere_RefusesAndDeletesNothingAsync`, `RemoveMember_TypeNameWithWrongContainer_RefusesAndDeletesNothingAsync` (an unreferenced `class Foo {}` with container `Nope`: before this step
  the type was deleted; now `ToolNotFoundException` and the document text is unchanged), `RemoveMember_TopLevelTypeWithNamespaceContainer_RefusedWithOmitContainerHintAsync`, `RemoveMember_DottedContainer_RemovesFromTheNamedTypeAsync`, `RemoveMember_WrongDottedContainer_RefusesAsync`,
  `RemoveMember_GenericContainerSpelling_ResolvesAsync`, `IsDeclarationStillPresent_SameNameInOtherType_ReportsGoneForTheNamedContainerAsync`, `IsDeclarationStillPresent_ContainerWithoutTheMember_ReturnsFalseAsync`, and the existing :2158 test unchanged. In `MemberOutcomeFollowsCauseTests`
  `Member_RemoveWithWrongContainer_RefusesAndWritesNothingAsync` (same pattern as Step 5c). Run `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: MemberRefactoringTests)` and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Tools.Basic, filter: MemberOutcomeFollowsCauseTests)`.
- Out of scope: the usage-count precheck, `skipPrecheck`, `SafeDeleteSymbolAsync` itself.

### Step 5e - Member(view) refuses a non-matching container (flip the Step 5a flag)
- Files: `RoslynSentinel.Tools.Basic/RefactoringStructuralImpl.cs`, `RoslynSentinel.Engines.Basic/SymbolNavigationEngine.cs`, `RoslynSentinel.Tests.Basic/MemberRefactoringTests.cs`
- Why: after Step 5a only `Member(operation: view, memberName, containerName)` still reaches `GetMemberSourceAsync` with the lenient rule (`RefactoringStructuralImpl.cs:571` passes `false`). Exposure: of 20 `Member(view)` calls since 2026-10-05, 9 passed `containerName` and 5 passed both `memberName` and `containerName`.
- **Before editing (mandatory): search for callers that rely on the fallback and list them in the step report** (`Search(mode: text)` for `operation: MemberAction.view`/`MemberAction.view` with `containerName` over `RoslynSentinel.Tests*`; `Grep` over `.claude`, `docs/current`, `docs/testing`, `Samples`, prompts for `Member(view` with `containerName`).
  Planner audit: no test, doc, agent file or prompt passes a container to `Member(view)` with a `memberName`; the 10 engine tests of `GetMemberSourceAsync` pass none; the container-only view (`GetContainerMembersAsync`) is a different path and is not changed.
- Change: (1) `RefactoringStructuralImpl.Member` (line 571): pass `true` instead of `false` and delete the `// TODO Member(view) keeps the lenient container fallback...` comment added in Step 5a. (2) `SymbolNavigationEngine.GetMemberSourceAsync`: every production caller now passes `true`; leave
  `// TODO remove requireContainerMatch and the lenient branch: no production caller passes false` next to the parameter (the parameter stays, its removal is a later cleanup slice because it is positional before `CancellationToken`). (3) Delete the Step 5b characterization test
  `GetMemberSource_WrongContainerWithoutStrictFlag_StillFallsBack` (its job is done) and add `GetMemberSource_WrongContainerWithStrictFlag_RefusesAsync`.
- Done when: `Build` 0 errors and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: MemberRefactoringTests)` passes including a tool-level or engine test `MemberView_WrongContainer_RefusesWithCandidatesAsync` (NotFound, candidate list, `Member(` retry in the text) and `MemberView_RightContainer_ReturnsTheMemberAsync`;
  `ReadFileTests` still pass.
- Out of scope: removing the flag; `GetContainerMembersAsync`.

### Step 5f - SummaryComment view and remove refuse a container that does not hold the target
- Files: `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs`, `RoslynSentinel.Tests.Basic/BasicRefactoringTests.cs`
- Why: `GetSummaryCommentAsync` and `RemoveSummaryCommentAsync` call the lenient filter (`candidates = SyntaxTargetResolver.FilterByContainingType(candidates, containingTypeName);`, about lines 1801 and 1753). `SummaryComment(operation: remove)` with a wrong `containingTypeName` strips the doc comment from the
  wrong declaration. Exposure: no `SummaryComment` call appears in transcripts since 2026-10-05, so this is correctness, not observed friction.
- **Before editing (mandatory): search for callers that rely on the fallback and list them in the step report** (`Search(mode: text)` for `GetSummaryCommentAsync`, `RemoveSummaryCommentAsync`, `containingTypeName`; `Grep` over `.claude`, `docs/current`, `docs/testing`, `Samples`, prompts for `SummaryComment` with `containingTypeName`). Planner audit:
  the 4 engine tests of these two methods (`BasicRefactoringTests.cs` lines 527, 531, 570, 590) pass no container; no test, doc, agent file or prompt passes `containingTypeName`; the tool (`RefactoringExtractionDocsImpl.SummaryComment`, line ~156) forwards it unchanged.
- Change: in both methods, after the `candidates` list is built (it includes type declarations, so use `includeTypeKinds: true`): when `containingTypeName` is not null or whitespace and `FilterByContainingTypeStrict(candidates, containingTypeName).Count == 0`, call
  `BuildContainerMissDetail(root, sourceText, targetName, containingTypeName, document.FilePath ?? string.Empty, "SummaryComment(filePath: '{file}', operation: <view|remove>, targetName: '{member}', containingTypeName: '{container}')", methodsOnly: false, includeTypeKinds: true)` (write `view` in the Get method and `remove` in the Remove method)
  and throw `new ToolNotFoundException(message, "declaration", targetName, candidates: candidates)`; otherwise `candidates = FilterByContainingTypeStrict(candidates, containingTypeName)`. Nothing is edited on a refusal.
- Done when: `Build` 0 errors and `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: BasicRefactoringTests)` passes including `GetSummaryComment_WrongContainer_RefusesAsync`, `RemoveSummaryComment_WrongContainer_RefusesAndStripsNothingAsync` (document text unchanged), `GetSummaryComment_RightContainer_ReturnsTheSummaryAsync`,
  `RemoveSummaryComment_DottedContainer_ResolvesByLastSegmentAsync`, `GetSummaryComment_TopLevelTypeWithAContainerName_RefusedWithOmitContainerHintAsync`.
- Out of scope: the add path (Step 5g).

### Step 5g - SummaryComment add (and BulkComment) refuse a container that does not hold the target
- Files: `RoslynSentinel.Engines.Basic/BasicRefactoringEngine.cs`, `RoslynSentinel.Tests.Basic/BasicRefactoringTests.cs`, `RoslynSentinel.Engines.Advanced/CommentingEngine.cs` (only if the audit below finds its container spelling wrong)
- Why: `AddSummaryCommentCoreAsync` (lenient call about line 1607) has a second production caller besides the tool: `CommentingEngine.CommentFileAsync` (`RoslynSentinel.Engines.Advanced/CommentingEngine.cs:337`) passes `site.ContainingTypeName`
  for every member it comments, with a per-item `catch (ToolException)` that records a failure. A strict filter turns any mismatch in that engine-computed value into a per-member failure instead of a comment on a possibly wrong member.
- **Before editing (mandatory): search for callers that rely on the fallback and list them in the step report.** Required checks: (a) `Search(mode: text)` for `AddSummaryCommentCoreAsync` and `AddSummaryCommentAsync` over all projects (planner found: the tool `RefactoringExtractionDocsImpl.cs:187`, `CommentingEngine.cs:337`,
  `WriteChokepointGuardrailTests.AddSummaryComment_KeepsTheFilesLineEndingAsync` which passes a context but must be checked for a container, 20 calls in `BasicRefactoringTests.cs` that pass none); (b) read how `MemberSite.ContainingTypeName` is computed in `CommentingEngine.FindStaleMembersAsync` (`memberContainingType`, about line 250)
  and confirm it is the nearest enclosing type's `Identifier.Text` (the same expression `FindEquivalentMemberByName` uses at line ~738); if it is anything else (qualified, generic-decorated) fix the computation in this step or the strict filter would refuse it; (c) `Grep` docs/agents/prompts for `BulkComment`/`SummaryComment` with a container.
- Change: same pattern as Step 5f in `AddSummaryCommentCoreAsync` with the retry template `SummaryComment(filePath: '{file}', operation: add, targetName: '{member}', containingTypeName: '{container}', summaryText: ...)`.
- Done when: `Build` 0 errors; `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Basic, filter: BasicRefactoringTests)` passes including `AddSummaryComment_WrongContainer_RefusesAndEditsNothingAsync`, `AddSummaryComment_RightContainer_AddsToTheNamedTypesMemberAsync`
  (same method name in two types, container picks one), `AddSummaryComment_DottedContainer_ResolvesByLastSegmentAsync`; and the commenting engine tests that exist (`Search(mode: text)` for `CommentFileAsync` over `RoslynSentinel.Tests*`; run them) still pass.
- Out of scope: any other change to `CommentingEngine`.
### Step 6 - Rewrite engine, part A: helpers and the benign-ignore table (not yet wired)
- Files: `RoslynSentinel.Server.Basic/ToolArgumentRewrites.cs` (new), `RoslynSentinel.Tests.Server/ToolArgumentRewritesTests.cs` (new)
- Change: `public static class ToolArgumentRewrites` with
  `public static IReadOnlyList<string>? Apply(string? toolName, IDictionary<string, JsonElement>? arguments, IReadOnlySet<string> declared, IReadOnlyDictionary<string, JsonElement> properties, string? solutionRoot)`
  (pure; `declared` and `properties` are `schema.Value.All` and `schema.Value.Properties` from `TryGetSchemaParameters`, `solutionRoot` is passed by
  Step 9; the parameter exists from the start so Steps 8 and 10 add no signature change). Private helpers: `IsTrue(JsonElement)` (JSON true or string
  "true", case-insensitive), `TryGetInt(JsonElement, out int)` (number or numeric string), `EnumMembers(properties, name)` (reads `enum` array like
  `NormalizeEnumCase`, lines 423-498), `Rename`/`Remove` that mutate `arguments`. Implement only the ignore table from "Decision" (rows: Search
  output_mode/outputMode, useRegex/isRegex/regex, searchMode, -n, type; ReadFile allow_large; GetMethodSource includeDocComments) as a data table
  `(Tool, Name, Predicate, Reason)`; one note per removed key:
  `Note: 'output_mode' is not a parameter of Search and was ignored (<Reason>). Omit it in future calls.` Every rule is a no-op when the
  name is itself declared (declared wins) and when the predicate fails (key stays for `Validate`). The mode condition reads
  `arguments["mode"]` as a case-insensitive string.
- Done when: `ToolArgumentRewritesTests` passes: `OutputMode_Content_IsRemovedWithNote`, `OutputMode_UnknownValue_IsLeftForValidate`,
  `UseRegex_True_TextMode_IsRemoved`, `UseRegex_True_SymbolMode_IsLeftForValidate`, `UseRegex_False_IsLeftForValidate`,
  `Type_Cs_IsRemoved`, `Type_Py_IsLeftForValidate`, `DeclaredNameWins_IsNotRemoved`, `IncludeDocCommentsTrue_OnGetMethodSource_IsRemoved`,
  `OtherTool_IsUntouched`. `Build` has 0 errors (nothing calls `Apply` yet).
- Out of scope: wiring, structural rewrites.

### Step 7 - Rewrite engine, part B: value-translating rewrites
- Files: `RoslynSentinel.Server.Basic/ToolArgumentRewrites.cs`, `RoslynSentinel.Tests.Server/ToolArgumentRewritesTests.cs`
- Change: inside `Apply`, after the ignore table, dispatch by tool name to small private methods, one per row of the "Rewrite list" except the two Search path
  rows: `RewriteSearchHeadLimit`, `RewriteReadFileRange`, `RewriteRunTestProject`, `RewriteGetDiagnosticsFile`, `RewriteBuildLevel`. Each: bail
  out (leave keys) if a target is already in `arguments`, a target is not in `declared`, or a value does not parse; for the enum targets
  confirm the member exists in `EnumMembers(properties, "scope"/"level")`; write numbers as JSON numbers
  (`JsonSerializer.SerializeToElement(int)`); emit one note per call: `Note: 'projectName' is not a parameter of RunTest; it was treated
  as scope: "project", scopeName: "X". Use RunTest(scope: "project", scopeName: "X") in future calls.`
- Done when: new tests pass: `Search_HeadLimit5_BecomesMaxResults5`, `Search_HeadLimitZero_IsLeftForValidate`,
  `ReadFile_OffsetLimit_BecomesStartEndLine` (offset "1659", limit "20" -> 1659..1678), `ReadFile_LimitOnly_BecomesEndLine`,
  `ReadFile_OffsetLimitWithStartLine_IsLeftForValidate`, `RunTest_ProjectName_BecomesScopeProject`, `RunTest_ScopeAlreadySupplied_IsLeftForValidate`,
  `GetDiagnostics_FilePath_BecomesScopeFile`, `Build_FullBuildTrue_BecomesLevelFullBuild`, `Build_FullBuildFalse_IsLeftForValidate`,
  `Build_ModeFullBuild_BecomesLevel`.

### Step 8 - Rewrite engine, part C: Search text-mode `path` -> `fileGlob`
- Files: `RoslynSentinel.Server.Basic/ToolArgumentRewrites.cs`, `RoslynSentinel.Tests.Server/ToolArgumentRewritesTests.cs`
- Change: add `public static string? PathToFileGlob(string value, string? solutionRoot)` implementing exactly the "Path-to-fileGlob rules" above (steps 1-7;
  pure string work, no file-system access, no exceptions: wrap the body in `try/catch` returning `null`). Add `RewriteSearchPath` called from `Apply`
  for tool `Search`: acts only when `arguments["mode"]` equals `text` (case-insensitive), `path` is present as a JSON string, `fileGlob` is declared and
  absent, and `path` is not itself declared; on a non-null glob it removes `path`, sets `fileGlob`, and returns the Note from the rules section (with the
  bare-file-name sentence when the glob contains no `/` and no wildcard). In every other case `path` stays for `Validate`.
- Done when: new tests pass. Table test `PathToFileGlob_Rules` (root `C:\repo\Sol`): `C:\repo\Sol\Proj.Tools\Impl` -> `Proj.Tools/Impl/**`;
  `C:\repo\Sol\Proj.Tools\Foo.cs` -> `Proj.Tools/Foo.cs`; `c:\REPO\sol\Proj` -> `Proj/**` (case-insensitive root); `Proj.Tools\Impl\` -> `Proj.Tools/Impl/**`;
  `./Proj/Foo.cs` -> `Proj/Foo.cs`; `Foo.cs` -> `Foo.cs`; `**/Proj/**/*.cs` -> unchanged; `C:\repo\Sol\Proj\*.cs` -> `Proj/*.cs`; `C:\elsewhere\X` -> `null`;
  `C:\repo\Sol` -> `null`; `..\X` -> `null`; `/etc/x` -> `null`; `\\server\share\x` -> `null`; `""` -> `null`; any absolute path with `solutionRoot: null` -> `null`.
  Rewrite tests: `SearchText_AbsoluteDirectoryPath_BecomesFileGlobWithNote` (note contains the exact glob), `SearchText_PathAndFileGlobBothSupplied_LeavesPathForValidate`,
  `SearchSymbolMode_Path_IsLeftForValidate`, `SearchText_PathOutsideRoot_IsLeftForValidate`, `SearchText_BareFileName_NoteWarnsItMatchesAnyFolder`.

### Step 9 - Wire the engine into the filter and fix the rejection-note wording
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`,
  `RoslynSentinel.Tests.Server/ToolParameterAliasTests.cs`
- Change: (1) In `ToolArgumentValidator` add
  `public static IReadOnlyList<string>? ApplyArgumentRewrites(McpServer? server, string? toolName, IDictionary<string, JsonElement>? arguments, string? solutionRoot)`:
  `TryGetSchemaParameters`, then `ToolArgumentRewrites.Apply(toolName, arguments, schema.All, schema.Properties, solutionRoot)`, whole body in
  `try/catch` returning `null` on any exception. (2) In `AddArgumentValidationFilter` (`ServiceRegistrationExtensionsBasic.cs`) compute
  `var solutionRoot = context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot();` (the pattern at line 731, inside the `try`), call
  `ApplyArgumentRewrites` after the legacy-referencesKind pass and before the wrap pass, and add the result with the same 3-line accumulate-notes block the
  neighbours use (`if (rewriteNotes is not null) aliasNotes = aliasNotes is null ? rewriteNotes : [.. aliasNotes, .. rewriteNotes];`). (3) In the rejection
  branch (`if (validationError is not null)`), when `aliasNotes` is non-empty add one extra trailing text block before the notes: `The notes below describe corrections already
  made to your call. The call did not run because of the unknown parameter(s) above; resend it with the corrected names.` (placed with `AppendAliasNotes`, one edit).
- Call sites: the filter is the only caller (`AddArgumentValidationFilter`, line 1116; registered once at line 105).
- Done when: new end-to-end tests pass in `ToolParameterAliasTests`: `Search_GrepShapedCall_RunsAndNotesEveryTranslation`
  (`pattern`, `output_mode: "content"`, `head_limit: "5"`, `type: "cs"`, `mode: "text"` -> success, contains the seeded `Caller`, notes name
  `query`, `maxResults`, and the ignored keys), `ReadFile_ReadShapedCall_ReturnsTheRequestedLines` (`file_path`, `offset: "1"`, `limit: "2"`; also asserts the Step 3 note
  `treated as 'filePath'` alongside the offset/limit note), `Search_TextPath_AbsoluteDirectory_RestrictsToThatDirectoryAndNotesTheGlob` (seed the same token in two
  folders; `path: <absolute seeded dir>`; only that folder's hits come back and a note contains the exact glob), `GetMethodSource_ContainerName_RunsWithoutARejection`
  (end to end; proves the new parameter is in the emitted schema), `RejectionWithAppliedRenames_SaysTheCallDidNotRun` (`Search` with `pattern` plus an
  unknown `bogusParam`: rejected, text contains "did not run").

### Step 10 - Search text-mode `filePath` -> `fileGlob`
- **Status: DECIDED 2026-10-24, keep as a normal step.** No other step depends on it; it stays separate only so it can be reverted alone.
- Files: `RoslynSentinel.Server.Basic/ToolArgumentRewrites.cs`, `RoslynSentinel.Tests.Server/ToolArgumentRewritesTests.cs`
- Why: today `filePath` in text mode is declared, so `Validate` accepts it, `FindIgnoredSearchParameters` only appends the warning "Ignored parameter(s) for
  mode text: filePath." (no `Note:` block), and the search runs over the whole solution. An agent that meant "search this file" receives hits from everywhere
  with a small warning: a silent widening. The same mapping as `path` is unambiguous (a text search has no other use for `filePath`).
- Change: in `RewriteSearchPath` (or a sibling `RewriteSearchFilePath`) handle `filePath` when mode is `text`, `fileGlob` is absent (after the `path` rule ran),
  and `PathToFileGlob` returns a glob: remove `filePath`, set `fileGlob`, emit `Note: 'filePath' only pins symbol resolution and is ignored by Search in text mode; it was
  treated as fileGlob: "<glob>". Use fileGlob to restrict a text search.` When the helper returns `null`, leave the call unchanged (today's behaviour: ignored with a warning).
- Done when: new tests pass: `SearchText_FilePath_BecomesFileGlobWithNote`, `SearchText_FilePathUnmappable_IsLeftUnchanged`, `SearchSymbolMode_FilePath_IsUntouched`
  (symbol modes keep `filePath` as the resolution pin), `SearchText_PathAndFilePath_PathWinsAndFilePathIsLeft`.

### Step 11 - Rejection suggestions: alias-aware, supplied-aware, token-aware
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Tests.Server/ToolParameterAliasTests.cs`
- Change: replace the single call `SuggestClosest(name, declared)` at line 923 with a new private
  `SuggestForUnknown(string toolName, string unknown, IEnumerable<string> declared, ICollection<string> supplied)` that tries in
  order: (1) `ParameterAliases[toolName][unknown]`, then `GlobalParameterAliases[unknown]`, when the target is declared; if the target is already in `supplied`, return the text
  `'<unknown>' means '<target>', which this call already passes; remove '<unknown>'.`; (2) the existing `SuggestClosest` over
  declared names **not** in `supplied` (fixes `reason2` -> `reason`); (3) camelCase/underscore token overlap with a strictly unique best
  candidate (ignore stop tokens `name`, `value`, `id`); (4) substring containment either way for names >= 4 chars. `SuggestClosest`
  itself keeps its signature (also used for enum values at line ~1031).
- Done when: new tests pass: `RejectionNamesTheAliasTarget_WhenTargetAlreadySupplied` (existing test
  `DeclaredParameterAlreadyPresent_AliasIsNotRenamed_AndIsRejectedAsUnknown` gains the new assertion; add a `file_path` plus `filePath` variant for the global table), `RejectionDoesNotSuggestAnAlreadySuppliedName`
  (`Member` with `reason2`), `RejectionSuggestsByTokenOverlap` (`RunTest` with `testFilterExpressionX` -> `filter`).

### Step 12 - Rejection hints: per-(tool, parameter) text with a corrected call
- Files: `RoslynSentinel.Server.Basic/ToolArgumentValidator.cs`, `RoslynSentinel.Tests.Server/ToolParameterAliasTests.cs`
- Change: add `private static readonly Dictionary<string, Func<UnknownParameterContext, string>> UnknownParameterHints` keyed
  `"Tool.param"` (case-insensitive), where `UnknownParameterContext` is a private nested `readonly record struct` carrying the supplied
  scalar values (rendered, truncated to 40 chars) and `Func<string, bool> IsToolRegistered` (backed by
  `server.ServerOptions.ToolCollection`, the same lookup `TryGetSchemaParameters` uses at lines 41-52, evaluated at reject time so
  on-demand toolsets are respected). `Validate` appends the hint after the suggestion, before "This tool accepts only". Entries
  (hint text is a short sentence plus a call line using the caller's own values where supplied):
  - `CreateFile.content`: `CreateFile only creates a seeded file and takes no content.` then, if `IsToolRegistered("WriteFile")`,
    `To create it with its full content use WriteFile(operation: CreateFile, filePath: <filePath>, content: ...).`, else
    `Create it with CreateFile, then add members with Member(operation: addMember, containerName: <typeName>, newMemberSource: ...).`
  - `GetMethodSource.maxLines`, `.startLine`, `.endLine`: `For a line range use ReadFile(filePath: <filePath>, startLine: ..., endLine: ...).`
    (No `GetMethodSource.containerName` entry: it is a declared parameter after Step 4.)
  - `Search.path`, `.filePath` (reached only when the rewrite could not map the value): `The value could not be turned into a fileGlob (it must be a file or
    folder inside the solution, without '..'). In text mode restrict files with fileGlob ('Foo.cs', or 'MyProject/**').`
  - `Search.filePathFilter`, `.pathFilter`: `In text mode restrict files with fileGlob (bare filename 'Foo.cs', or 'MyProject/**'); filePath only pins symbol
    resolution.`
  - `Search.projectFilter`: `Use projectName for symbol/attribute/declaration modes; in text mode use fileGlob 'MyProject/**'.`
  - `Search.contextLines`, `Search.type` (non-cs): `Search returns one-line previews and searches C# only. For surrounding lines use
    ReadFile(filePath: ..., startLine: ..., endLine: ...).`
  - `RunTest.timeout`: `Use timeoutSeconds (seconds, not milliseconds).`
  - `Build.fullBuild` (value false): `Build(level: fullBuild) writes the shared bin/; the default level fullBuildThrowaway builds without it.`
  - `RenameSymbol.symbolName`, `.filePath`, `.contextSnippet`: `RenameSymbol targets a symbol by docCommentId. Get it with
    Search(mode: symbol, query: <name>) and pass docCommentId, projectName, newName.`
  Hints are only for names the rewrite/ignore layers did not already handle; a name that is both in a hint and handled is handled.
- Done when: new tests pass: `CreateFileContent_Rejection_RedirectsToACallThatExists` (asserts the redirect names `WriteFile` only if
  `_client.ListToolsAsync` contains it, else names `Member`), `SearchTextPathUnmappable_Rejection_ShowsTheFileGlobHint`,
  `RejectionWithoutAHint_IsUnchanged` (the existing `UnlistedUnknownParameter_IsStillRejected` still passes).

### Step 13 - Teaching text where it costs nothing standing
- Files: `.claude/hooks/enforce-dogfood.ps1`, `CLAUDE.md`
- Change: (1) Hook, Read deny text (lines 196-212): under `ReadFile - whole file or startLine/endLine` add one line
  `Read(file_path, offset, limit) -> ReadFile(filePath: ..., startLine: offset, endLine: offset+limit-1); one method: GetMethodSource(filePath, methodName[, containerName]); a name declared more than once returns a candidate list, never a guess.`
  (2) Hook, Grep deny text (lines 286-307): after the four `Search(...)` lines add
  `Grep options: path/glob -> fileGlob ('Foo.cs' or 'MyProject/**'); head_limit -> maxResults; output_mode, -n, -i, type are not needed (text mode
  is case-insensitive and always returns file, line and preview).` (3) `CLAUDE.md:208`: change `Build(fullBuild)` to `Build(level: fullBuild)`
  (+7 bytes; no other CLAUDE.md edit). No line is added to the Grep -> Search table row: `CLAUDE.md` is 15,658 bytes today against the
  15,360-byte (15 KB) budget, so no addition fits; the hook text carries the translation instead.
- Done when: the hook parses (`[System.Management.Automation.Language.Parser]::ParseFile('.claude/hooks/enforce-dogfood.ps1', [ref]$null, [ref]$e)`
  leaves `$e` empty), `Select-String` finds both new lines, and `.\scripts\Test-GuidanceHygiene.ps1` runs; record its SIZE line for
  `CLAUDE.md` (expected 15,665 bytes, i.e. still over budget by the same ~300 bytes it was over before; that overage is pre-existing and not
  fixed here, see Risks).
- Out of scope: any Search or ReadFile `[Description]` change (no standing-cost additions; revisit only if Step 18 shows residual).

### Step 14 - Call-shape block in `implementer.md` and `implementer-senior.md`
- Files: `.claude/agents/implementer.md`, `.claude/agents/implementer-senior.md`
- Change: add one section `## Call shapes` per file, placed after the dog-fooding bullet list, wrapped in the markers `<!-- call-shapes:start -->` and
  `<!-- call-shapes:end -->` (the Step 16 test reads between them). Positive wording only; it names only tools that file already names or that every
  lane is told to use (`Build`, `RunTest`). Parameter names below were read from source on 2026-10-24 (see "Real declared parameters"); keep to about 170 tokens:

  ```
  ## Call shapes
  <!-- call-shapes:start -->
  Parameter names are exact; an extra name rejects the whole call.
  Call every tool by its full name from your tool list, with the mcp__ prefix (for example mcp__root_roslyn_sentinel_advanced_stdio__Search); the bare name "Search" is a different, unavailable tool and fails.
  ReadFile(filePath, startLine, endLine) | ReadFile(filePath, memberName, containerName)
  GetMethodSource(filePath, methodName, containerName) | GetFileOutline(filePath)
  A name declared more than once, or a containerName that does not hold it, returns a candidate list; retry with containerName or ReadFile(filePath, startLine, endLine).
  Search(mode: text, query, fileGlob, maxResults) | Search(mode: symbol, query, projectName)
  ReplaceSnippet(action: apply, filePath, oldContent, newContent)
  Member(filePath, operation: addMember, containerName, newMemberSource) | Member(filePath, operation: replace, memberName, newMemberSource)
  Build() builds without touching the shared bin/ | RunTest(scope: project, scopeName, filter)
  <!-- call-shapes:end -->
  ```
  (The prose line about the candidate list contains `ReadFile(filePath, startLine, endLine)`, which is also a real call shape, so the Step 16 test checks it too.)
- Done when: both files contain the block between the markers and their frontmatter is unchanged (`Select-String '^name:|^tools:|^model:'` returns the same
  three lines as before). The Step 16 test is the machine check.

### Step 15 - Call-shape block in `implementer-sweep.md` and `test-writer.md`
- Files: `.claude/agents/implementer-sweep.md`, `.claude/agents/test-writer.md`
- Change: same markers and heading, and the same "Call every tool by its full name ... mcp__ prefix" line as Step 14 in both files (the Step 16 regex ignores it: it has no `Name(` shape except the parenthesis-free example). `implementer-sweep.md`: only tools whose signatures were verified from source, so the block is
  `ReadFile(filePath, startLine, endLine)`, `GetMethodSource(filePath, methodName, containerName)` with the same candidate-list sentence,
  `Search(mode: text, query, fileGlob, maxResults)`, `Build()`, `RunTest(scope: project, scopeName, filter)`. `test-writer.md`: the Step 14 block plus
  `CreateFile(filePath, namespaceName, typeKind, typeName)` then `Member(filePath, operation: addMember, containerName, newMemberSource)` for a new test file (the file
  already lists `CreateFile`; `CreateFile` takes no content, so the second call adds the first test).
- Done when: both files contain the block between the markers and their frontmatter is unchanged.
- Out of scope: `explore-haiku.md` (9 post-alias rejections) and `test-writer-senior.md`; revisit with Step 18's per-agent split.

### Step 16 - Guard test: every documented call shape exists in the emitted schema
- Files: `RoslynSentinel.Tests.Server/AgentCallShapeBlocksTests.cs` (new)
- Change: one fixture reusing the in-process harness pattern of `ToolParameterAliasTests.cs` (`ActiveModes` must cover the tools in the blocks: look up `Search`,
  `ReadFile`, `GetMethodSource`, `GetFileOutline`, `ReplaceSnippet`, `Member`, `Build`, `RunTest`, `CreateFile` in `ToolClassRegistry.cs` and add their modes; a missing tool
  fails with a message naming the mode to add). Locate the repo root by walking up from `TestContext.CurrentContext.TestDirectory` to the directory holding
  `RoslynSentinel.slnx`; read the four `.claude/agents/*.md` files from Steps 14-15; extract text between the two markers; for each match of `\b([A-Z][A-Za-z]+)\(([^)]*)\)` split the
  argument text on `,`, take the identifier before `:` (or the whole token), drop a trailing `?`, and assert the name is a property of the tool's emitted input schema
  (`tool.ProtocolTool.InputSchema`, as `TryGetSchemaParameters` reads it). Also assert each file has exactly one marker pair.
- Done when: `RunTest(scope: project, scopeName: RoslynSentinel.Tests.Server, filter: AgentCallShapeBlocksTests)` passes; to prove it bites, change one name in a block locally
  (for example `oldContent` -> `oldString`), see the test name the file, tool and parameter, then revert.
- Note: this is the staleness guard. Without it the blocks drift silently whenever a tool parameter is renamed; with it a rename fails a test that names the agent file to fix.

### Step 17 - Verification
- Files: none
- Change: `Build` (fullBuild, 0 errors, no new warnings vs `docs/known-build-warnings.Solution.txt`); `RunTest` solution scope and
  compare with `docs/known-failing-tests.Solution.txt` (baseline 0 failures; report only new failures; includes
  `ClaudeLeanSchemaBudgetTests`, which must still pass with the new `GetMethodSource.containerName` description); then
  `McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)` and `LoadSolution` so tools run the new binary (`isServerBinaryStale` must read false).
- Done when: the three results above are clean, and manual probes on the restarted server succeed with notes:
  `Search(mode: text, pattern: "ToolArgumentValidator", output_mode: content, head_limit: 3)` returns matches plus notes;
  `Search(mode: text, query: "ToolArgumentValidator", path: "<absolute path of RoslynSentinel.Server.Basic>")` returns matches only from that folder with a note naming the glob;
  `ReadFile(file_path: "<a file>", startLine: 1, endLine: 5)` returns lines with a note `treated as 'filePath'`;
  `GetMethodSource(filePath: <a file with two same-named methods>, methodName: ...)` returns an `Ambiguous` error listing both, and the same call with `containerName` returns the named type's method.
  `ReadFile(filePath: <a file with two types, one declaring M>, memberName: M, containerName: <the other type>)` returns NotFound naming the type that declares M with its line range, and the retry it names
  (`containerName: <that type>`) returns the member.
  Wrong-container probes (Steps 5c-5g), on a scratch file with two types `A` (declares `M`) and `B`: `Member(operation: replace, memberName: M, containerName: B, newMemberSource: ...)` returns NotFound naming `A` with its line range and `Git(operation: diff)` shows no change;
  `Member(operation: remove, memberName: M, containerName: B)` is refused the same way and `M` is still in `A`; `Member(operation: view, memberName: M, containerName: B)` is refused; `GetMethodSource(methodName: M, containerName: 'Outer.Inner')` resolves a method of a nested type `Inner`.

### Step 18 (not a code step) - Re-measure
See Measurement.

## Measurement

Baseline (re-derived 2026-10-24, strict method above): 388 distinct rejections since 2026-09-16, 142 since the alias table
(2026-10-05), weekly rate 0.82% / 0.41% / 0.43% of logged mcp calls for the three weeks with a journal denominator.
Estimated coverage of this plan on the 142 (revised after decisions 2 and 4): ~110 calls (77%) have all unknown names rescued by Steps 2-10
(the earlier ~100, plus GetMethodSource `containerName` 5 now a working parameter and Search `path` 5 now a `fileGlob` rewrite), ~14 (10%) get a call-shaped
hint but are still rejected (CreateFile content 10, GetMethodSource maxLines 2, two others), ~18 (13%) are one-off names with no mechanical fix.
Projected residual rate: 32 of 142 at the steady 0.43% is about 0.10% of mcp calls.

Cost side of decision 2 (not a rejection, so tracked separately): about 2-3% of `GetMethodSource` calls (about 3 per day at the current ~138 per day) were silently
answered with the first of several matches and will now return an `Ambiguous` error and need one retry. That is the intended price of never returning a possibly wrong method.
Cost side of the ReadFile refusal (Steps 5a-5b): a `ReadFile(memberName, containerName)` call whose container does not hold the member used to return another type's member (or the only
candidate) silently; it now gets one refusal and needs one retry. The size is unknown: of 8,574 ReadFile calls since 2026-10-05, 58 passed `memberName` and 10 of those also passed `containerName`
(parsed tool_use blocks, de-duplicated by id); whether any named a wrong container was not replayed.
Cost side of the Member and summary-comment migration (Steps 5c-5g). Exposure baselines (transcripts since 2026-10-05, parsed tool_use blocks, de-duplicated by id): `Member(operation: replace)` 121 of 222 calls passed `containerName`;
`Member(operation: remove)` 225 of 257; `Member(operation: view)` 9 of 20 (5 with `memberName`); `addMember` is unaffected (already strict); dotted or generic-spelled containers 0 of 355 across replace, remove and view; `SummaryComment` 0 calls. Whether any
of those named a wrong container was not replayed, so the number of calls whose result changes is unknown. The behaviour change that matters most: a `Member(remove)` with a type name and a container that does not hold it used to delete the type; it is now refused.

Target, measured over the first 14 days after Step 17's restart and at least 10,000 mcp calls:
- Rejection rate <= 0.2% of mcp calls (halve the 0.42% steady state); stretch 0.12% (the 14 hint-only calls plus the one-offs are the floor).
- Zero rejections whose unknown names are all in the rescued tables (any hit is a bug in Steps 2-10; open a finding).
- Share of rejections that carry a suggestion or a hint >= 70% (today 39 of 388 carry a suggestion: 10%).
- Note repeats (`-NoteRepeats`, Step 1): the share of sessions that see the same note twice stays at or below today's 5 of ~35 (about 14%). A rise is the signal to
  revisit Decisions item 1 (reject once per session) and item 5 (a silent `file_path` alias would then be wrong; the note is what teaches).
- `file_path` notes per 1,000 mcp calls (cost side of Decisions item 5): record it; it is the only new standing per-call output. Also record `SquashesToDeclared` hits other than `file_path`
  (the trigger for adding a row to the global alias table, or for reopening the generic comparer if several appear).
- `GetMethodSource` outcomes: share of calls returning `Ambiguous`; of those, how many are followed by `GetMethodSource` with `containerName` vs `ReadFile` with a line range vs a repeat of the same
  call (a repeat means the error text failed); and whether `Ambiguous` with `containerName` supplied (same-type overloads) recurs often enough to justify an overload selector (Decisions item 2).
- Container refusals, per surface (Steps 5a-5g; baseline above: ReadFile 10 of 58 memberName calls passed `containerName`; Member replace 121 of 222; remove 225 of 257; view 9 of 20). After rollout, from transcripts with the same one-off scan as the baseline, count results whose message
  contains `declares no member named`, `declares no method or constructor named`, `declares no declaration named` or `No type named`, separately for ReadFile, GetMethodSource, Member replace, Member remove, Member view and SummaryComment, per 1,000 calls of that surface that passed a container,
  and classify what follows each: a retry with the named container or a `ReadFile` line range (the hint worked), the same call again (the text failed), or a fall back to Read/Grep/Edit on the file (a gap). A rising repeat share is the trigger to move the retry call to the front of the message.
  For the write surfaces (replace, remove) also record refusals that were previously silent edits of another type: any refusal whose message names a different type than the requested container is a prevented wrong write, the benefit side of this change.
  Dotted containers (`Outer.Inner`, `Ns.Type`) now resolve by their last segment and are not refused; record how many calls pass one (baseline 0 of 355), and any call whose qualifier was wrong yet matched (the accepted looseness of Step 4a).

Re-measure: `.\scripts\Get-UnknownParameterReport.ps1 -Since <restart date> -Weekly -ByAgentType -NoSchema -NoteRepeats`; compare against the
table in this plan. Confounders to record with the reading: server binary staleness (rejections from sessions still on the old process
count against the old behaviour; `isServerBinaryStale` is true on the live server at plan time), and the number of subagent dispatches in the window.

## Out of scope

- A parameter that selects one overload inside one type for `GetMethodSource` (recommendation in Step 4: not now; the error names a `ReadFile` line range instead).
- Deleting the then-unused lenient `FilterByContainingType` and the `requireContainerMatch` flag: Steps 5c-5g move all 8 callers to the strict helpers; the removal is a later cleanup slice (`// TODO` markers are left in Steps 5a and 5e).
- A generic separator-insensitive parameter-name comparer (dropped; see Decisions item 5).
- Scalar-string coercion (`"true"`, `"50"` sent for bool/int): `Git count: "6"` is exercised in Step 2's test only to confirm the binder
  accepts it; a general coercion pass is a separate plan if that test fails.
- The duplicate `["filePath"]` initializer in `ParameterHints` (`ToolArgumentValidator.cs:207-208`), the legacy attributed duplicates in
  `WorkspaceTools.cs`, and CLAUDE.md being over its size budget.
- Retired-tool rejections (`SearchSolutionText`) and the `McpServerControl` operation rejections (all before 2026-10-05).
- Call-shape blocks for `explore-haiku.md`, `test-writer-senior.md` and the orchestrator-class agents.

## Decisions (resolved 2026-10-24) and remaining risks

1. **Silent-drop vs round trip: accept-and-note. DECIDED.** No per-session "reject once" state. The notes are the teaching; the repeat rate is measured
   (Step 1 `-NoteRepeats`, Measurement) and a rise reopens this.
2. **GetMethodSource: optional `containerName` AND never pick a match; ReadFile refuses a non-matching container. DECIDED, revised twice** (Steps 4a, 4b, 4, 5, 5a, 5b, 5c-5g).
   - When more than one method still matches after the optional type filter (same name in several types, or overloads in one type), the tool returns an `Ambiguous` error (existing
     `ToolErrorCode.Ambiguous` and `AmbiguousCandidate`, structured and in the message) listing every candidate with containing type, full signature with parameter types, and start-end lines.
   - The retry the error names always works: `GetMethodSource(..., containerName: <Type>)` for types with one candidate, and `ReadFile(filePath, startLine, endLine)` with the candidate's own range (verified: ReadFile
     takes inclusive 1-based `startLine`/`endLine`) for overloads in one type. A test proves the ReadFile range returns the named overload.
   - Two traced facts shaped the design. (a) `SyntaxTargetResolver.FilterByContainingType` (`SyntaxTargetResolver.cs:85-93`) falls back to all candidates when the container does not match or only one candidate exists,
     and it compares with plain `==` without `NormalizeTypeName`, so a `Box<T>` request never narrows. Step 4a therefore adds strict helpers beside it and leaves the lenient filter itself byte-for-byte unchanged (its 8 callers move one engine method at a time in Steps 5a and 5c-5g).
     GetMethodSource (Step 4) and ReadFile (Step 5a) both use the strict helpers and the one shared miss message. (b) The GetMethodSource parameter is optional because every
     existing call lacks it.
   - **Recommendation, not assumed: no overload selector parameter now** (reasons in Step 4); revisit if the measured same-type-overload ambiguity recurs.
   - **Cost to accept:** about 2-3% of `GetMethodSource` calls (estimate, Evidence) change from a silent first-match result to an error plus one retry. Callers that relied on first-match were audited in Step 4: none in code, tests, docs, hooks,
     scripts, agent files or prompts depends on it; only live agent calls do.
   - **Overload selector: DECIDED, none** (user agreed). The measurable revisit trigger stays in Measurement (same-type-overload `Ambiguous` followed by a second GetMethodSource instead of ReadFile).
   - **`ReadFile(memberName, containerName)` with a non-matching container: DECIDED, explicit refusal** (Steps 4a, 4b, 5a, 5b). Today `WorkspaceFileEditImpl.ReadFile` (line 318) reaches `GetMemberSourceAsync` (line 2672), whose lenient filter
     ignores the container and returns another type's member. After Step 5a it returns NotFound with the types that actually declare the member (containing type, signature, start-end lines, as structured `AmbiguousCandidate` detail and in the text) and a retry
     call that works (`containerName: <Type>` for a type with one candidate, or `ReadFile(filePath, startLine, endLine)`); the message and candidate list come from the same `BuildContainerMissDetail` that GetMethodSource uses. A test proves both retries.
     The refusal is opt-in per call (`requireContainerMatch`), ReadFile passes `true` from Step 5a and Member(view) from Step 5e.
   - **Migrating the other 8 lenient-filter callers: DECIDED 2026-10-24, scheduled in Steps 5c-5g** (found with `Search(mode: callers)`; table in Step 5a): `Member` replace (5c), `Member` remove with its post-remove verification (5d), `Member` view (5e, flips the Step 5a flag and leaves a
     flag-removal `// TODO`), summary-comment view/remove (5f), summary-comment add (5g). Each is its own compile-green slice that starts with a caller audit (the planner's findings are in the step) and ends with a test proving a wrong container is refused and nothing is edited or deleted. The write paths
     (replace, remove) come first because a wrong container there edits or deletes in the wrong type.
   - **Dotted container names: DECIDED 2026-10-24, last-segment match.** `ContainingTypeMatches` (Step 4a) falls back to the LAST segment of a dotted request when the full name does not match, so `Outer.Inner` matches a type named `Inner`. It is still strict (never matches a type whose name differs, never falls back to an
     unrelated type) and does not verify the qualifier. Measured demand is 0 of 355 container-bearing Member calls, so this is cheap insurance for tool results that print qualified names, not a response to observed failures.
3. **`Build fullBuild: true` -> `level: fullBuild`: literal mapping. DECIDED.** It writes the shared `bin/`; the Step 13 CLAUDE.md fix removes the main source of the habit,
   and the Step 14-15 blocks teach `Build()` (throwaway) to the subagent lanes.
4. **Search text-mode `path`: rewrite to `fileGlob`. DECIDED** (Steps 8-9). Glob semantics verified in source (section above): `**` after a folder matches everything beneath it,
   `**/` matches zero or more leading segments, the match is against the solution-root-relative path. The rewrite is lexical only; anything that cannot be mapped unambiguously
   is left for `Validate`. **Text-mode `filePath`: rewrite to `fileGlob`. DECIDED** (Step 10): today it is a warning plus a whole-solution search; the same mapping is unambiguous and
   removes that silent widening. The user confirmed keeping Step 10; it stays a separate step only so it can be reverted alone.
5. **`file_path` -> `filePath`: explicit global alias, noted with the standard alias note; the generic separator-insensitive comparer is DROPPED. DECIDED, revised** (Step 3; note-vs-silent stays **provisional**).
   - Recommendation and why: keep one explicit row, drop the comparer. Measured: of 499 unknown-name tokens in the 388 rejections, 43 contain `_` or `-`, and exactly one name (`file_path`, 15 tokens, all `ReadFile`, all subagents) squashes to a
     declared parameter; `head_limit` (14), `output_mode` (11), `allow_large` (2) and `-n` (1) do not equal any declared name without their separator and need the rewrite/ignore tables regardless; there is no `start_line`, `max_results`, `file_glob`, `member_name` or `file-path`
     in the window. The comparer would add a nested class, change two lines in the schema cache that case normalization depends on, widen collision exclusions for every tool, and turn any future separator-only coincidence into a silent rename, for zero measured benefit beyond the one row. The
     alias table is auditable, shows up in the standard note, is guarded by the existing alias tests, and a new snake_case habit is caught by the report script's `SquashesToDeclared` column (Step 1) and added as one line.
   - Side effect removed: the earlier version of Step 3 had to change `NormalizeParameterCase` to return notes and replace the filter's note accumulation. None of that is needed now; Step 3 touches one file and its test.
   - Note vs silent: the alias emits the standard note, about 25 tokens on each call that sends `file_path`. Reason: `file_path` is not declared by any tool; it is the habit from built-in Read/Edit/Write, so the note is the only place the habit is corrected. The user may
     flip this to silent by special-casing the note in `ApplyParameterAliases` (one condition); the measurement records the per-1,000-calls cost so the call can be made on data.
6. **Call-shape blocks in the agent definitions: included now. DECIDED** (Steps 14-16), with the staleness guard test (Step 16). Cost: about 170 tokens on each dispatch of four lanes; 91 of the
   102 post-alias subagent rejections came from implementer 46, implementer-senior 29 and test-writer 16.

Remaining risks and unverified items:

7. **Unverified.** (a) That a numeric string binds to an `int` parameter through the SDK binder (Step 2 test proves or disproves it). (b) That
   `WriteFile` and `ReadFile`/`Search`/`RunTest`/`Build` are all registered under mode `Workspace` in the test harness (implementer checks
   `ToolClassRegistry.cs`; Step 16 needs the modes of nine tools). (c) Rejected calls are logged `ok:false`: confirmed for one sample, not for every tool. (d) The live server binary is stale
   (`isServerBinaryStale: true` at plan time); the 2026-10-21 `ReplaceSnippet` alias and 2026-10-18 Git aliases only help sessions started
   after a restart, so the post-alias rejection counts for those names may shrink without any of this plan's code. (e) Nested types and partial classes in Step 4: a method in a nested type belongs to the nested type for `containerName`;
   a partial class spread over files is searched per file, as `GetMethodSource` already works per file. (f) The 2.4% first-match exposure is a regex estimate over current files, not a replay of the original calls.
8. **Size of `ToolArgumentValidator.cs`** is 1,082 lines; Steps 2, 3, 11 and 12 all edit it, which is why the engine lives in a new file and the steps are sequential.
9. **Schema budget.** `GetMethodSource.containerName` adds ~290 bytes to the claude-lean schema; headroom is ~1 KB at the 2026-10-18 measurement. Step 17 runs
   `ClaudeLeanSchemaBudgetTests`; if it fails, shorten the description rather than raising the budget.
10. **A global alias applies to every tool that declares the target.** `file_path` -> `filePath` is safe because `filePath` means "the file" everywhere it is declared; the guard test `GlobalAliasKey_IsNeverDeclaredByAnyActiveTool`
    and the existing skip rules (target not declared, target already supplied) keep it from inventing a parameter. Any future global row needs the same same-meaning-on-every-tool check, stated in the code comment from Step 3.
11. **Container strictness (Steps 5a-5g).** (a) A dotted `containerName` such as `Outer.Inner` or `Ns.Type` matches by its last segment (DECIDED); the qualifier is not verified, so `Wrong.Inner` matches `Inner`, and two nested types both named `Inner` both match (callers report the ambiguity). (b) The compare is case-insensitive and arity-tolerant, which is looser
    than the old `==` in one way (`box` matches `Box`) and stricter in the other (no silent fallback). (c) Until Step 5e lands, `ReadFile` and `Member(view)` disagree about a wrong container; the characterization test in Step 5b makes that explicit and Step 5e deletes it. (d) `WorkspaceFileEditImpl.ReadFile` logs every thrown ToolException at error level in its catch,
    so a refusal adds an error log line; not changed here. (e) Behaviour change on write paths: a `Member(remove)` naming a type with a container that does not hold it used to delete the type (through `SafeDeleteSymbolAsync`) and is now refused (Step 5d); a `Member(replace)` with a wrong container used to rewrite a same-named member elsewhere and is now refused (Step 5c).
    Callers that relied on the fallback were audited in each step; the one found is a test that passes a namespace as the container (fixed in Step 5c). Unaudited: live agent calls and any prompt outside this repo.
12. **`ToolNotFoundException` gains an optional last constructor parameter (Step 4b)** in `Common`. No caller changes, but code that reads NotFound `StructuredDetail` will now also see candidate objects, not only a snippet detail. Unverified:
    Step 4b has the implementer run `Search(mode: references)` on `StructuredDetail` to confirm no consumer casts a NotFound detail to `SnippetFailureDetail`.
13. **A namespace name is not a container.** `Member(replace/remove, containerName: "N")` where `N` is the namespace (as in the existing test at `MemberRefactoringTests.cs:2784`) matched nothing before and was rescued by the fallback; it is now refused with the declared type list (`BuildContainerNotFoundMessage`). A top-level type has no containing
    type, so `Member(remove, memberName: <TopLevelType>, containerName: <anything>)` is refused with "the declaration is top level, omit containerName". Agents that habitually pass the namespace will see one refusal per call until they drop it; the measurement bullet counts these.
14. **Unverified: how `CommentingEngine` spells the container (Step 5g).** `MemberSite.ContainingTypeName` (`CommentingEngine.cs:55`, set at about :255 from `memberContainingType`) is passed to `AddSummaryCommentCoreAsync` for every member `CommentFileAsync` comments (:337-338), with a per-item `catch (ToolException)`. If it is not the nearest enclosing type's bare identifier,
    the strict filter would turn those comments into per-item failures. Step 5g makes the implementer read the derivation and fix it before changing the filter.
15. **`requireContainerMatch` is temporary (Step 5e).** After 5e no production caller passes `false`; the flag and the lenient `FilterByContainingType` stay only to keep each step compile-green. Removing them is a later cleanup (see Out of scope).
