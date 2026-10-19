# Trim the claude-lean core surface from about 16.4k to about 9k tokens

**Status:** IN PROGRESS 2026-10-18. Slices 1-5 and 6a built (Git aliases `target`/`commitHash` -> `ref`, `files` -> `paths` via validator aliases; Git 7186 -> 6507 chars). Remaining: 6b `GitRemote` class (registered in every mode with Git, on-demand set `git` in claude-lean; not on the breaker allow-list), 7 `TypedMember` holding only `addTypedMember` and its 9 params (registered in every mode with Member, on-demand `declarations` in claude-lean; `addTopLevelType` stays in Member), 8 re-measure. Decisions made by the user 2026-10-18.

## Motivation

The claude-lean core (Advanced: 24 tools) costs about 16.4k tokens of schema every session. Measured
2026-10-18 by starting the Advanced Debug exe with `--mode=claude-lean` over stdio and calling
`tools/list` (`scripts/Measure-ToolSchemaCost.ps1`): 42,221 chars. Calibration: 2.57 chars per token
(`Git` = 7205 chars = the ~2800 tokens the client reports).

| Tool | Chars | ~Tokens | Calls in 7 days (main session) |
| --- | --- | --- | --- |
| Git | 7205 | 2804 | 642 (470) |
| Search | 4753 | 1849 | 2652 (138) |
| RunTest | 3374 | 1313 | 622 (88) |
| ReplaceSnippet | 3306 | 1286 | 684 (5) |
| Member | 3270 | 1272 | 366 (1) |
| Build | 2331 | 907 | 290 (33) |
| SubAgent | 1950 | 759 | 17 (17) |
| ExternalFileDrift | 1787 | 695 | 44 (12) |
| McpToolsetControl | 1474 | 574 | 12 (9) |
| RenameSymbol | 1226 | 477 | 12 (0) |
| LoadSolution | 1223 | 476 | 36 (22) |
| GetLargeResult | 1176 | 458 | 260 (12) |
| InspectSymbol | 1164 | 453 | 42 (1) |
| McpServerStatus | 1103 | 429 | 77 (12) |
| GetDiagnostics | 1100 | 428 | 13 (1) |
| UsingDirective | 1047 | 407 | 11 (0) |
| ReadFile | 937 | 365 | 3188 (120) |
| CreateFile | 883 | 344 | 21 (0) |
| WriteFile | 801 | 312 | 94 (1) |
| McpServerControl | 681 | 265 | 11 (11) |
| GetMethodSource | 413 | 161 | 578 (32) |
| GetFileOutline | 356 | 139 | 380 (10) |
| DeleteFile | 336 | 131 | 5 (0) |
| UndoLastApply | 325 | 126 | 2 (0) |

Usage source: 10,189 MCP calls in 38 `.claude/journal/*.calls.jsonl` files from the last 7 days.
Caveats: most calls are subagents on the same server; the log records neither the server mode nor the
`Git` operation, so per-operation usage is unknown. Shared text cost: `reason` appears in 24 tools
(2616 chars), `useScratchDir` in 2 (1030), `contextSnippet` in 3 (851), `lineBefore`/`lineAfter` in 4
(1047). The on-demand sets saw 22 calls (MoveMember 11, ApplyUnifiedDiff 5, MoveType 4, others 1).

## Proposal

Estimates, not measured on a modified build. Target about 9k tokens (-45%).

### A. Core list: 24 tools to 19 (-2.36k tokens)
Move to on-demand toolsets (via `ToolsetCatalog`):

- SubAgent (Advanced only) to a new set `modelEval`.
- GetDiagnostics and InspectSymbol to `projectAdmin` (Build quickBuild and Search mode symbol cover the core need).
- UsingDirective to `declarations`.
- WriteFile to `moveExtract` (beside ApplyDiff); CreateFile, ReplaceSnippet and Member(replace) cover core writes.

Keep ExternalFileDrift and McpServerControl in core: the CLAUDE.md recovery paths name them.

### B. Git split (-1500 tokens)
- `Git` (core): status, log, diff, show, hunks, stage, add, unstage, commit, revert, reset, abort.
- `GitRemote` (new on-demand set `git`): branch, checkout, push, fetch, pull, tag, stash, worktree, with their 13 params (`branchName`, `startPoint`, `createBranch`, `deleteBranch`, `remoteName`, `setUpstream`, `rebase`, `action`, `tagName`, `stashIndex`, `includeUntracked`, `worktreePath`, `discardUncommittedChanges`).
- Fold alias params: `target` into `ref`, `commitHash` into `ref`, `files` into `paths`.
- State each operation's requirements once; shorten `operation`, `action`, `ref`, `scope` text.
- Move `pickaxe`, `pickaxeRegex`, `follow` out of core or cut.

### C. Description trims (no schema shape change)
- Search (-700): tool description 1322 to about 400 chars; `query` 469 to about 200; `fileGlob` 372 to about 120; drop `contextSnippet` from Search.
- RunTest (-580): description 1374 to about 350; `useScratchDir` to one sentence; drop duplicated `filter` text.
- Build (-350): `useScratchDir` (462) to one sentence; trim description.
- ReplaceSnippet (-300): one shared `lineBefore`/`lineAfter` text with "see lineBefore" for the pair; short item descriptions inside `batchEdits`.
- Member (-450): move addTypedMember and addTopLevelType (and `typedKind`, `typedName`, `typedType`, `accessibility`, `hasSetter`, `isInit`, `isReadonly`, `isStatic`, `initializer`, `namespaceName`) to a new `TypedMember` tool in `declarations`.
- `reason` (-590): description from 99 to about 40 chars, keeping a minimum-length and has-a-space rule (an empty description made models omit the field).
- ExternalFileDrift (-420): 1787 to about 700 chars. McpToolsetControl, LoadSolution, GetLargeResult (-300 together).

### Where the text lives (verified 2026-10-18)

claude-lean's `ClaudeLeanToolClasses` (`ToolClassRegistry.cs:60`) hosts the class `WorkspaceTools`
(`WorkspaceTools.cs`), which declares LoadSolution (:122), ReplaceSnippet (:145), GetDiagnostics (:469),
Build (:488) and RunTest (:503). `WorkspaceFileEditTools.cs:55`, `WorkspaceBuildTestTools.cs:15/35/50`
and `WorkspaceProjectManagementTools.cs:42` repeat the same tool names with copy-pasted text for the
split-out modes. A description trim must be applied to both copies or the modes drift; the sync test does
not pin these. Other locations: Git `GitTools.cs:23` (impl `GitImpl.cs`, enum `ToolEnums.cs:8`); Search
`WorkspaceReadNavigationTools.cs:114`; Member `RefactoringStructuralTools.cs:109` (enum `ToolEnums.cs:232`);
ExternalFileDrift `AdminTools.cs:19`; McpToolsetControl `ToolsetControlTools.cs:22`; GetLargeResult
`WorkspaceReadNavigationTools.cs:94`; shared text `ToolParams.cs` (`Reason` :106, `ContextSnippet` :42,
`LineBefore` :44, `LineAfter` :47, `UseScratchDir` :5); `McpToolSchemaPatcher.cs:372-433` overrides
ReplaceSnippet's `lineBefore`/`lineAfter` with `ToolParams.ReplaceSnippetAnchorLine`.

### Slice plan (sequential, one implementer at a time; Build between slices)

1. Description-only trims in shared text: `ToolParams.Reason`, `UseScratchDir`, `ReplaceSnippetAnchorLine`; update
   `MergedToolDescriptionSyncTests` only if it pins the old wording; lower budget constants last.
2. Search: description, `query`, `fileGlob`; remove `contextSnippet` from Search (and its handling).
3. RunTest + Build descriptions and `includeOutput` text, in both copies (`WorkspaceTools.cs` and `WorkspaceBuildTestTools.cs`).
4. ExternalFileDrift, McpToolsetControl, LoadSolution (both copies), GetLargeResult descriptions.
5. Core list change (A): `ClaudeLeanToolNames`, `ToolsetCatalog` sets (`modelEval` new), `McpToolsetControl` description,
   `ClaudeLeanModeTests.CoreTools`, `BasicCoreTools` exclusions; regenerate docs.
6. Git trim: alias folding and text, then `GitRemote` class (three registration steps) and move the 13 params.
7. Member split: `TypedMember` class in `declarations`, three registration steps.
8. Re-measure with `scripts/Measure-ToolSchemaCost.ps1`, set the budget constants, regenerate docs, run `Test-ClaudeLeanSurface.ps1`.

## Alternatives considered, not pursued

- **Split Search into Search and a declaration-listing tool.** The 18 enum values cost little; the cost is prose. Trimming gets the saving without renaming a tool that CLAUDE.md and prompts name.
- **Drop `reason` from read-only tools.** A product decision about journaling, not a token fix (see `proposal_reduce_tool_schema_token_cost.md`).
- **Three-way Git split (read / write / remote).** Same total cost as two; only the per-tool size drops.

## Open questions

- Does any weak-model eval depend on `GetDiagnostics`, `InspectSymbol`, `UsingDirective` or `WriteFile` being core? Run ModelEval before and after.
- Which `Git` operations do agents actually use? The call log does not record the operation (hypothesis: branch/tag/stash/worktree are rare).
- Does Claude Code refresh the tool list on `list_changed`? Unverified (see the earlier proposal); enabled tools are callable by name regardless.

## Cost / risk

- Touches `ToolClassRegistry.ClaudeLeanToolNames`, `ToolsetCatalog`, the `McpToolsetControl` description, `ClaudeLeanModeTests.CoreTools`, `ClaudeLeanSchemaBudgetTests` constants, `MergedToolDescriptionSyncTests`, and `docs/generated/architecture_tools.md`. Procedure: `reference_changing_claude_lean_tools.md`.
- Two new tool classes (`GitRemote`, `TypedMember`) need the three registration steps in CLAUDE.md and a fresh chat session plus server restart.
- Tools moved on-demand cost a round trip and a model must know to enable the set.
- Shortened descriptions can raise weak-model failure rates; verify with ModelEval before and after.

## Related

- `docs/current/proposals/proposal_reduce_tool_schema_token_cost.md`
- `docs/current/references/reference_changing_claude_lean_tools.md`
- `scripts/Measure-ToolSchemaCost.ps1`
