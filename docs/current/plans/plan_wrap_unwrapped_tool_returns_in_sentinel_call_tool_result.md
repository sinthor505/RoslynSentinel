# Plan: Move the 9 unwrapped [McpServerTool] methods to SentinelCallToolResult<T>

**Status:** DRAFT 2026-10-17. Not started; another session is implementing. Do not begin until the user says so.

## Problem
Of 141 `[McpServerTool]` methods in the Tools.* projects, 9 do not return `SentinelCallToolResult<...>`
(declared at `RoslynSentinel.Common/SentinelCallToolResult.cs:293` one-arg, `:150` two-arg). They return
raw strings, raw DTOs or `object`, so errors are unstructured prose or a parallel per-DTO contract, and
agents (especially weak models) cannot rely on `IsError` / `StatusMessage`. Sibling methods in the same
files (e.g. `GenerationTools.cs:240`, `QualityTools.cs:90`) already use `SentinelCallToolResult<object>`.

Found by `Search(mode: attribute, query: McpServerTool)` (141 rows) cross-checked with a text search on
return types, then an `explore-haiku` read of the 9 bodies. Line numbers below are approximate and were
taken while another session had uncommitted edits in the tree; re-check before editing.

| # | Tool | File | Returns today |
| --- | --- | --- | --- |
| 1 | McpServerStatus | Tools.Basic/ServerStatusTools.cs:25 | `object`, but body already returns `SentinelCallToolResult<McpServerStatusResult>` |
| 2 | GenerateClassesFromJson | Tools.Advanced/GenerationTools.cs:78 | `object` boxing raw `GenerationResult`; errors via `ToolErrorMapper.ToErrorMessage` string |
| 3 | GenerateHttpClient | GenerationTools.cs:98 | `Task<string>` (JSON summary on success, prose on miss) |
| 4 | GenerateDefaultConfigJson | GenerationTools.cs:142 | `Task<string>` (raw text on success, prose on miss) |
| 5 | InterpolateStringSafe | GenerationTools.cs:191 | `Task<string>` (same as #3) |
| 6 | DescribeAdvancedToolOptions | Tools.Advanced/QualityTools.cs:50 | sync `ToolOptionsResult`; in-band `Error` (`NoFurtherDocumentation`) |
| 7 | ProjectDoc | Tools.Basic/DocumentationTools.cs:374 | `object` holding `DocReadResult` / `DocWriteResult` / `DocListResult` |
| 8 | ExternalFileDrift | Tools.Basic/AdminTools.cs:18 | sync `string`, every branch |
| 9 | McpServerControl | AdminTools.cs:160 | sync `string` from `ControlServer(...)` |

No non-test callers for any of them (MCP entry points only). Test-project callers were NOT checked.

## Decision
Migrate #2-#9 to `SentinelCallToolResult<T>` (one-arg form unless a step says otherwise) and tighten #1's
declared return type. One tool per slice so each stays compile-green and inside the implementer tier
limits (`.claude/implementer-tiers.json`). Order is lowest wire-risk first. No migration-first
`[Obsolete]` wrappers: these are protocol entry points with no in-repo callers, so the return type is
changed in place.

## Execution rules
- Dispatch per CLAUDE.md: `implementer` for C#, `test-writer` for tests; main session reviews and commits.
  Sequential dispatch only. Load `dispatch-implementer` before the first dispatch.
- Before each slice: `InspectSymbol(aspect: blastRadius)` on the tool method to measure test-project
  call sites (not measured yet), and put the count in the brief's `Call sites:`.
- Build 0 errors after each slice; one `Build`/`RunTest` at a time; compare tests against the
  known-failing baseline (memory `reference_known_failing_tests`).
- New method bodies: errors go through the wrapper's error path, never raw exception text or paths
  in `ResultError`.
- Check the three-part tool registration is untouched (return-type change only; no registry edits).
- Commit with `Git` scope `listed`, explicit file list, one commit per slice or per file.

## Steps

### Step 0 - Preflight reads (explore-haiku, read-only)
- Files: AdminTools.cs, DocumentationTools.cs, tests referencing the 9 tools
- Change: none. Read `AcknowledgeDrift` and `ControlServer` bodies; list test-project call sites and
  tests that assert on the current string/DTO shapes of all 9 tools; confirm the working tree is clean
  of the other session's edits in the files below (or wait for its commit).
- Done when: a short report lists, per tool, the tests that must change.

### Step 1 - McpServerStatus: tighten declared return type
- Files: Tools.Basic/ServerStatusTools.cs
- Change: `object` -> `SentinelCallToolResult<McpServerStatusResult>` (sync). Check the nested records
  `McpServerStatusToolSurface` / `McpServerStatusDeclaredTool` are unaffected.
- Done when: Build 0 errors; existing McpServerStatus tests pass.

### Step 2 - GenerationTools: GenerateClassesFromJson
- Files: Tools.Advanced/GenerationTools.cs
- Change: return `SentinelCallToolResult<object>` (match siblings, e.g. line 240) or `<GenerationResult>`
  if the sibling convention allows; success carries the DTO, failures carry a `ResultError` instead of
  the `ToErrorMessage` string.
- Done when: Build 0 errors; generation tests updated and passing.

### Step 3 - GenerationTools: GenerateHttpClient
- Files: Tools.Advanced/GenerationTools.cs
- Change: `Task<string>` -> `Task<SentinelCallToolResult<object>>`; "not found" prose paths become
  structured errors; success carries the summary payload (no more `ToJsonSummary()` string).
- Done when: Build 0 errors; tests updated and passing.

### Step 4 - GenerationTools: GenerateDefaultConfigJson
- Files: Tools.Advanced/GenerationTools.cs
- Change: as Step 3. Success payload is currently raw `result.UpdatedText`; wrap it (decide field name
  in the brief, consistent with sibling generation results).
- Done when: Build 0 errors; tests updated and passing.

### Step 5 - GenerationTools: InterpolateStringSafe
- Files: Tools.Advanced/GenerationTools.cs
- Change: as Step 3.
- Done when: Build 0 errors; tests updated and passing.

### Step 6 - QualityTools: DescribeAdvancedToolOptions (needs decision)
- Files: Tools.Advanced/QualityTools.cs, `ToolOptionsResult` in Common/SentinelCallToolResult.cs:447
- Change: wrap in the sync-compatible form. Open decision: is `NoFurtherDocumentation` a success with a
  message or an `IsError`? Its own doc says it is not an invalid-tool error.
- Done when: decision recorded in this plan; Build 0 errors; tests updated and passing.

### Step 7 - DocumentationTools: ProjectDoc
- Files: Tools.Basic/DocumentationTools.cs (DocReadResult :10, DocWriteResult :48, DocListResult :75)
- Change: return `SentinelCallToolResult<object>` (or per-operation typed payload); fold the `Found`/`Error`
  and `IsError`/`Error` parallel contracts into the wrapper's error path. Wire shape changes:
  fields move under `SuccessData`.
- Done when: Build 0 errors; ProjectDoc tests updated; any agent prompt or doc that parses the old
  shape is found (Step 0) and updated.

### Step 8 - AdminTools: ExternalFileDrift and McpServerControl
- Files: Tools.Basic/AdminTools.cs
- Change: `string` -> `SentinelCallToolResult<...>` for both. Keep `UnrecoverableBreaker` attribute on
  ExternalFileDrift unchanged (recovery path for a halted session). Refusals ("nothing changed",
  "Unknown operation", missing confirm param) become `IsError` results. Do this slice last and
  restart/verify with `McpServerControl` afterwards, since it governs recovery and shutdown.
- Done when: Build 0 errors; admin tests pass; live check that `StopServer` without the confirm
  param still refuses and `ExternalFileDrift(List)` still works after a server restart.

## Out of scope
- The one-arg vs two-arg (`<T, ResultError>`) wrapper split across the other ~130 tools.
- Renaming tools, changing parameters, or touching registry/DI wiring.
- Changing the content of the generated code or documents.

## Risks and open decisions
- Wire-shape changes for #6 and #7 may break consumers (agent prompts, PlanStepRunner/ModelEval
  assertions, docs). Step 0 must find them; not yet searched.
- Test-project call sites for all 9 tools are unmeasured.
- Step 6 `NoFurtherDocumentation` semantics undecided.
- `AcknowledgeDrift` and `ControlServer` bodies unread, so Step 8 scope is a guess.
- The other session's uncommitted edits (MemberRefactoringEngine.cs, AdvancedRefactoringTools.cs, etc.)
  do not overlap these files per `git status` at session start, but re-check before dispatching.
- The "SentinelCallToolResponse" name in the original request does not exist; this plan assumes
  `SentinelCallToolResult<T>` was meant.
