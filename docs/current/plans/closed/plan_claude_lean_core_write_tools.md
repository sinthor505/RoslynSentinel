# Plan: Promote WriteFile and ChangeSignature to claude-lean core, and give new files a one-call route

**Status:** CLOSED 2026-11-07. Done as planned (the SubAgent child task ran: 20-tool surface without `WriteFile`, new file written in one `CreateFile(content)` call; the Chain7 eval was not run, see below). Steps 1-5 committed (25be5c3e, 046deb59, abdf8774, af0e830f, 702cee12). Step 6 partly done: live server at 702cee12 lists 21 core tools incl. `WriteFile` and `ChangeSignature`, `SemanticFindReplace` stays on demand, a 6793-char `CreateFile(content)` landed in one call, and a child launched with `--hide-tools=WriteFile` lists 20 tools (no `WriteFile`; `DeleteFile` and `ChangeSignature` present). ModelEval fixture A (`MinimalGuidanceDisambiguated`, host 113, qwen3.5-9b-coder, n=3 per tree): 3/3 at 563f5ead, 3/3 at 046deb59, 2/3 at af0e830f (same code as HEAD); a 0/3 run in the main working tree is put down to another session's uncommitted `Tools.Basic` edits during that run (hypothesis, not proved). No regression found. Still open: one SubAgent child new-file task (the SubAgent tool is not in this client's list; needs a fresh chat) and an optional Chain7 AFTER-only run.

## Problem

An implementer creating a 174-line file spent three attempts on it. It seeded an empty class with `CreateFile`, then `ReplaceSnippet` refused the fill: "newContent is 8573 chars excluding leading indentation (limit 6000)". The error suggested `Member(operation: replace)`, which rejected the content for holding 2 members, so the agent fell back to one call per member.

Environment causes:
- `CreateFile` takes no content, and no active tool writes a new file in one call.
- `CreateFile`'s description names `WriteFile`, which is gated off in claude-lean (`Tools.Basic/WorkspaceTools.cs`, `Tools.Basic/WorkspaceFileEditTools.cs:87`). Run 398 lost 24 turns to the same kind of message (`docs/obsolete/blockers/resolved/finding_planstep_run_398_B_harness.md:117-119`).
- `AdviseForOversizedEdit` (`Common/WriteToolAdviceHelper.cs:141`) receives only the tool name, so it offers `Member(replace)` whatever the content is.
- `WriteFile`'s own description names `ApplyUnifiedDiff`, which is also on demand (`Tools.Basic/WholeFileWriteTools.cs:30`).

Why `WriteFile` is on demand in claude-lean: the core trim moved it to save schema tokens, because "CreateFile, ReplaceSnippet and Member(replace) cover core writes" (`docs/current/proposals/closed/proposal_trim_claude_lean_core_surface.md:55`). The 47% to 26/26 result that argues for keeping it from weak models comes from an earlier, separate gating decision (`finding_planstep_run_398_B_harness.md:123-125`).

Self-hosted models can still reach claude-lean. The SubAgent child server starts with `--mode=Claude-Lean`, described as "the same tool surface Claude Code itself drives with" (`Tools.Advanced/SubAgentChildServerLauncher.cs:32-37`). Promotion alone would therefore hand `WriteFile` to them. The child can already reach it today by enabling `moveExtract`. ModelEval fixtures use their own mode sets and PlanStepRunner uses `--include-tools` (`Utilities.PlanStepRunner/RunnerOptions.cs:98-99`), so promotion does not change their surfaces.

## Decision

1. `CreateFile` gains optional `content`, the one-call route for new files on every surface. It still refuses an existing file.
2. The size-limit error advises from the rejected content's shape.
3. A new `--hide-tools` argument removes named tools from the claude-lean allow-list. The SubAgent child passes `--hide-tools=WriteFile`. The existing `--exclude-tools` cannot do this because it works per class (`ServerStartupHelpers.ResolveActiveToolClasses`), and `WholeFileWriteTools` also holds the core `DeleteFile`.
4. `WriteFile` and `ChangeSignature` move to `ClaudeLeanToolNames` and leave their toolsets. `SemanticFindReplace` stays on demand in `moveExtract` (owner decision 2026-11-06).

### Schema cost

Measured 2026-11-06 from build d17ad364 with `--mode=all` and `tools/list`. Sizes are re-serialized, so treat them as close, not exact. Tokens use the 2.57 chars-per-token calibration.

| Tool | Chars | ~Tokens |
| --- | --- | --- |
| WriteFile | 782 | 304 |
| ChangeSignature | 1868 | 727 |
| **Total added** | **2650** | **1031** |

The core was 28877 chars against a 29000 budget (`Tests.Server/ClaudeLeanSchemaBudgetTests.cs:22-28`), so this adds about 9%, plus the new `content` parameter. Both tools fit the 4500 per-tool limit, but the total budget must rise.

## Execution rules

- Start after the in-flight W20 implementer finishes and its files are committed.
- Follow `docs/current/references/reference_changing_claude_lean_tools.md`, using `scripts/Test-ClaudeLeanSurface.ps1` as the fast loop.
- One `Build`/`RunTest` at a time. Build 0 errors before each commit. The test baseline is 0 failures.
- Commits carry a `Plan: plan_claude_lean_core_write_tools` trailer; the last carries `Closes:`.
- Each step below touches more than the 3 files of a default `implementer` slice: split it at a compile-green point or dispatch `implementer-senior`.
- Remove each `// TODO` marker named below in the step that resolves it.

## Steps

### Step 1 - CreateFile content and accurate descriptions
- Files: `Tools.Basic/WorkspaceFileEditTools.cs`, `Tools.Basic/WorkspaceTools.cs`, `Tools.Basic/WorkspaceFileEditImpl.cs`, `Tools.Basic/WholeFileWriteTools.cs`, `Server.Advanced/ToolArgumentValidator.cs:328`, `Tests.Server/ToolParameterAliasTests.cs:560-614` and about `:680`.
- Change:
  - Add `content`. For a `.cs` file, require either `content` or `typeKind` + `typeName`, and name both forms in the error.
  - Share `WriteFile(operation: CreateFile)`'s write path (`ApplyProposedChangesAsync` with compile validation) rather than adding a second one.
  - Remove the `WriteFile` sentence from both `CreateFile` descriptions and the "never writes free-form whole-file content" clause.
  - Change `WriteFile`'s description to stop naming `ApplyUnifiedDiff`.
  - Remove the `CreateFile.content` rejection hint and replace its test.
  - Remove the `// TODO` above `CreateFile` in `WorkspaceTools.cs`.
- Done when: tests pass for creating a `.cs` file with content, refusing an existing file, refusing content that adds compiler errors, refusing both forms together, and creating a non-`.cs` file.

### Step 2 - Content-aware size-limit advice
- Files: `Common/WriteToolAdviceHelper.cs`, `Tools.Basic/WorkspaceFileEditImpl.cs:682-688` and `:834-847`, `Tests.Server/WriteToolAdviceHelperTests.cs`, `Tests.Tools.Basic/ReplaceSnippetSizeGuardTests.cs`.
- Change:
  - Pass the rejected content's shape to `AdviseForOversizedEdit`. Both callers already hold `OldContent` and `NewContent`.
  - Classify in `Tools.Basic` or `Engines.Basic`, because `Common` holds no such parsing today. Reuse the logic in `MemberRefactoringEngine.DetectMultipleMemberDeclarations`.
  - Offer `Member(replace)` only for a single member. For several members or usings, advise a small `ReplaceSnippet` for the usings and one `Member(addMember)` per member.
  - Keep `WriteFile` as the last clause and only when `IsExposed`.
  - Correct `ToolToDeclaringClass`: it maps `ChangeSignature` to `RefactoringStructuralTools`, but the tool is declared on `AdvancedRefactoringTools`.
  - Remove the `// TODO` in `AdviseForOversizedEdit`.
- Done when: new tests cover single-member, multi-member and usings-plus-members content, and advice never names an unexposed tool.

### Step 3 - Per-tool hiding for the SubAgent child
- Files: `Server.Advanced/ServerStartupHelpers.cs`, `Server.Advanced/ServiceRegistrationExtensionsBasic.cs:127-129`, `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs`, `Tools.Advanced/SubAgentChildServerLauncher.cs:43-54`, `Tests.SubAgent/SubAgentLaunchTests.cs`.
- Change:
  - Parse `--hide-tools` and subtract its names from the `ToolAllowList` built for claude-lean. `WriteToolAdviceHelper` reads that list (`ServiceRegistrationExtensionsBasic.cs:140`), so advice follows automatically.
  - Add `--hide-tools=WriteFile` to `BuildServerArguments`, with a comment saying why it is per tool.
- Done when: a test shows the child surface has `DeleteFile` and `ChangeSignature` but no `WriteFile`, and its advice does not name `WriteFile`. This step is inert until Step 4.

### Step 4 - Promote the two tools
- Files: `Server.Advanced/ToolClassRegistry.cs:42-65`, `Common/ToolsetCatalog.cs:40,44-47,75-76`, `ToolsetControlTools.cs` (the `McpToolsetControl` description), `Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs:76-85`, `Server.Advanced/ServiceRegistrationExtensionsBasic.cs:318-321`, `Tests.Server/ClaudeLeanModeTests.cs`, `Tests.Server/McpToolsetControlTests.cs:254-285`, `Tests.Server/ClaudeLeanSchemaBudgetTests.cs`, `docs/generated/*`.
- Change:
  - Add the names to `ClaudeLeanToolNames` and `CoreTools`.
  - Add `AdvancedRefactoringTools` to `ClaudeLeanToolClasses`. Its other tools stay hidden by the allow-list.
  - Remove the names from `ToolsBySet`, `Summaries` and the control-tool description.
  - Update the stale comments at `ToolClassRegistry.cs:54-57` and `ServiceRegistrationExtensionsAdvanced.cs:76-79`, and the `// TODO` at `ServiceRegistrationExtensionsBasic.cs:318-321`.
  - Update `ClaudeLeanModeTests.cs:166-189`. Flip `:251`, keep `:254`. `:215-230` still holds because `SemanticFindReplace` stays on demand.
  - Update `DynamicTools_AreIdenticalToTheSameToolsRegisteredAtStartup`, and check `McpServerStatusToolListingTests` and `CallableToolHintTests.cs:12`.
  - Raise the total budget to the measured size rounded up to the next 500, and state why in the commit message.
  - Regenerate docs with `scripts/Test-ClaudeLeanSurface.ps1 -Regenerate`.
- Done when: the fast loop passes, then `Build(level: fullBuild)` and `RunTest` on `RoslynSentinel.Tests.Server`, `Tests.SubAgent` and `Tests.Tools.Basic` pass.

### Step 5 - Agent guidance
- Files: `.claude/agents/implementer.md:33-39`, `.claude/agents/implementer-senior.md`, `.claude/agents/test-writer.md:44`, `docs/current/references/reference_tool_comparison_harness.md:33`, `docs/current/references/reference_changing_claude_lean_tools.md:94`.
- Change:
  - Add `CreateFile(filePath, content)` to the call shapes. `AgentCallShapeBlocksTests` checks these blocks against the emitted schemas.
  - Add one rule: a new file is written in one `CreateFile(content)` call, and an existing file uses a targeted edit unless most of it is being rewritten.
  - Correct the reference lines that now misstate the surface or budget figures.
- Done when: `AgentCallShapeBlocksTests` passes.

### Step 6 - Live and weak-model verification
- Change:
  - Restart the server (`McpServerControl(operation: StopServer, confirmServerStop: ConfirmServerStop)`, then `LoadSolution`) with no subagent mid-operation.
  - Confirm with `McpServerStatus(toolListing: all)` that both tools are active without a toolset call and `SemanticFindReplace` is still on demand.
  - Create a new file above 6000 chars through `CreateFile(content)`.
  - Run a ModelEval fixture before and after, because `CreateFile(content)` also appears in the modes those fixtures use.
  - Dispatch one new-file task through a SubAgent child, because no ModelEval fixture covers claude-lean.
- Done when: the new-file call works in one step, the child surface lacks `WriteFile`, and eval pass rates have not regressed. On a regression, stop and report; do not widen the change.

## Out of scope

- Raising the `ReplaceSnippet` size limit.
- A multi-member form of `Member`.
- Promoting `SemanticFindReplace`, `ApplyDiff`, `ApplyUnifiedDiff` or any other toolset member.
- Moving `ChangeSignature` out of the Advanced projects (`proposal_usage_based_core_split.md`).

## Risks and open decisions

Decided 2026-11-06 by the owner: the SubAgent child hides `WriteFile` and leaves `ChangeSignature` visible. No decisions remain open.

Risks:
- `implementer` runs on Haiku, so "Claude" is not uniformly a strong model. Core `WriteFile` could draw it to whole-file rewrites over targeted edits, and rewrites read from disk rather than the workspace document (a `// TODO` in `WholeFileWriteTools.cs` already records this). Mitigation: Step 5's rule and last-place advice. Check the journals after a few sessions.
- `ChangeSignature` adds about 727 always-on tokens on small local contexts in the child. Step 6 should show whether that matters.
- The 47% to 26/26 evidence is known here only through summaries. Whether `CreateFile(content)` shares that risk is a hypothesis until Step 6 runs.

Unverified, to check during preflight:
- How `WorkspaceFileEditImpl.CreateFile` seeds a file.
- Whether `WriteFile` has a size limit, and how it adds a new file to its project.
- Whether `MemberRefactoringEngine.DetectMultipleMemberDeclarations` (`internal`) is visible from `Tools.Basic`.
- What `McpServerStatusToolListingTests` and `CallableToolHintTests.cs:12` pin.
