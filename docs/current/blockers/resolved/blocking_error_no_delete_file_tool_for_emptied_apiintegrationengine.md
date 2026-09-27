# Blocker: no MCP tool to delete a file from disk (ApiIntegrationEngine.cs left as empty shell)

**Status:** RESOLVED 2026-09-27 - premise was wrong, see "Correction" below
**Date:** 2026-09-27
**Task:** Group 8 of the engine-reorganization plan (merge ApiIntegrationEngine into ApiAutomationEngine / rename to ApiGenerationEngine)

## What happened

`ApiIntegrationEngine` (`RoslynSentinel.Advanced/ApiIntegrationEngine.cs`) had already been reduced to an
empty class shell (just `_workspaceManager` field + constructor, zero methods) by earlier work this
session, after its one method (`AddValidationToPocoAsync`) was moved to `ApiAutomationEngine` via
`MoveMember`.

For this task I:

1. Confirmed via `GetFileOutline` the class was still an empty shell (field + ctor only).
2. Found all 19 remaining references across 8 files via `SearchSolutionText`.
3. Classified every reference: all of them (a DI registration, a `CodemodTools` ctor
   parameter/field, and several test fields/locals across `ComprehensiveToolTests.cs`,
   `BugFixTests.cs`, `BatteryFifteenTests.cs`, `BatteryNineteenTests.cs`, `BatteryThirtyTests.cs`)
   constructed an `ApiIntegrationEngine` instance that was never subsequently read - confirmed with
   `FindReferences` on each field/local. All were safe to delete outright (no redirect to
   `ApiAutomationEngine` needed, since call sites either already had their own `ApiAutomationEngine`
   usage inline, or had no use of the dead engine at all).
4. Removed all 19 references via `ReplaceSnippet` (field declarations, ctor parameter, DI
   registration line, dead local `var engine = ...` lines). Confirmed via a follow-up
   `SearchSolutionText` that the only remaining hits were the class declaration itself plus two
   cosmetic mentions in a comment/test-class-name in `BatteryFifteenTests.cs`.
5. Called `SafeDeleteUnusedSymbol` on the now fully-unreferenced `ApiIntegrationEngine` class to
   remove it.

## The problem

`SafeDeleteUnusedSymbol` deleted the **symbol** (the `class ApiIntegrationEngine { ... }`
declaration) but did **not** delete the **file**. `ApiIntegrationEngine.cs` still exists on disk,
now containing only:

```csharp
// (using directives / header trivia, unchanged)
namespace RoslynSentinel.Advanced
{
}
```

Confirmed via `GetFileOutline` immediately after the delete: `lineCount: 7`, one symbol
(`namespace RoslynSentinel.Advanced`), zero types.

I searched the full loaded tool surface (`ToolSearch` for "delete file remove file", plus explicit
lookups of `Features`, `Git`) for a way to remove the now-empty file itself:

- `SafeDeleteUnusedSymbol` - deletes a symbol, not a file (this is the root cause: its description
  says "Deletes a symbol only if it has zero usages anywhere in the codebase" - it does not claim
  file-level deletion, and evidently doesn't do it as a side effect when the symbol was the file's
  only type).
- `CreateFile` - create-only, fails if the file already exists; no delete/remove counterpart.
- `Git` (`operation`: status/log/diff/show/stage/add/unstage/commit/revert/reset/branch/checkout/
  push/fetch/pull) - `stage`/`add` can stage a deletion **that already happened on disk**, but
  there is no operation that performs the deletion itself.
- `MoveAllTypesToFiles`, `SyncTypeAndFilename` - reshuffle/rename types-to-files, not applicable to
  removing a file with zero types.

No tool in the currently loaded set (`ListAll`/`ToolSearch` surface as of this session) performs a
plain filesystem delete of a `.cs` file. This matches the task prompt's own anticipation of this
exact gap ("If none exists, that is a tool gap - stop... do not fall back to `rm`").

## What would fix it

A dedicated `DeleteFile` MCP tool (mirroring `CreateFile`'s shape: `filePath` + `reason`), or an
optional `deleteFileIfEmptied: true` flag on `SafeDeleteUnusedSymbol` for the case where the
deleted symbol was the file's sole top-level type/namespace content.

## Correction: the premise was wrong - the tool already existed, just mode-gated out

The search that produced "no tool in the currently loaded set... performs a plain filesystem delete"
above was accurate for *this session's active tool set*, but the conclusion ("no tool exists") was not
checked against the full solution source - only against what was visible in this session. A
`DeleteFile` tool already existed at `RoslynSentinel.Server.Basic/WholeFileWriteTools.cs:111`,
predating this session entirely. It was invisible here because `WholeFileWriteTools` was not in the
`claude` mode's active tool-class list at the time (it's gated alongside `AdminTools`).

I built a second, duplicate `DeleteFile` (see
`blocking_error_deletefile_tool_not_loaded_stale_server_binary.md` for the full follow-on incident)
instead of finding the existing one - a plain `SearchSolutionText(pattern: "Name = \"DeleteFile\"")`
across the whole solution (not filtered to any one mode's active classes) would have found it
immediately. **Fix applied:** the user added `WholeFileWriteTools` to the `claude` mode's active
classes; my duplicate additions were reverted (see the other doc). No new tool was actually needed.

## Current repo state (uncommitted, in-flight edit finished cleanly)

- `RoslynSentinel.Advanced/ApiIntegrationEngine.cs` - emptied to a bare namespace block (needs
  physical deletion once the tool gap is closed).
- `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsAdvanced.cs` - redundant
  `services.AddSingleton<ApiIntegrationEngine>();` registration removed.
- `RoslynSentinel.Server.Advanced/CodemodTools.cs` - dead `_apiIntegrationEngine` field, ctor
  parameter, and assignment removed.
- `RoslynSentinel.Tests.Advanced/ComprehensiveToolTests.cs` - dead `_apiIntegrationEngine` field
  and assignment removed.
- `RoslynSentinel.Tests.Advanced/BugFixTests.cs` - three dead `var engine = new
  ApiIntegrationEngine(...)` locals removed from the three `BUG_67_*` tests (each already called
  `AddValidationToPocoAsync` via its own inline `new ApiAutomationEngine(...)`, so nothing
  redirected, just deleted).
- `RoslynSentinel.Tests.Battery/BatteryFifteenTests.cs` - dead `_engine` field and `SetUp`
  assignment removed from `ApiIntegrationEngineTests` (its three real test methods already used
  their own inline `new ApiAutomationEngine(_mgr)` and never touched `_engine`). Two cosmetic
  leftovers remain: a comment on line 5 mentioning "ApiIntegrationEngine" and the test class name
  `ApiIntegrationEngineTests` itself - neither references the deleted type, both are just stale
  naming, deferred until the file-delete + rename step can be done in one pass.
- `RoslynSentinel.Tests.Battery/BatteryNineteenTests.cs` - dead `_apiIntegrationEngine` field and
  `Setup` assignment removed.
- `RoslynSentinel.Tests.Battery/BatteryThirtyTests.cs` - dead `_apiEngine` field and `Setup`
  assignment removed.

All of the above edits were validated in-line by `ReplaceSnippet`'s default recompile-and-reject
behavior (each succeeded with `validationResult.success: true`, zero diagnostics) so the solution
should currently build clean except for the emptied file, which itself compiles fine (an empty
namespace is legal).

**Not yet done (blocked on this gap):** physically deleting `ApiIntegrationEngine.cs`, renaming
`ApiAutomationEngine` -> `ApiGenerationEngine`, updating the two cosmetic `BatteryFifteenTests.cs`
mentions, Build, RunTest, and the commit. Stopping here per the dog-fooding failure doctrine
(tool gap, not a routing-around candidate) rather than guessing at an unsupported deletion path.
