# Plan: Persist enabled toolsets per Claude Code tab across server restarts, with McpServerControl restart options

**Status:** IN PROGRESS 2026-11-15. Steps 1-8 done (7b2b901f, 2df3e449, 3398ae4e, 9a247fb0, 0de85bbe, 62edb05f, 0531ece8, 934f415c); left: Step 9 live checks (owner) and Step 10 docs. Supersedes D12 of `plan_tool_experience_journal_fixes.md` ("persist toolset state across a restart").

## Problem

Enabled toolsets live only in the `ToolsetService._enabled` dictionary (`RoslynSentinel.Common/ToolsetService.cs`). Every
server restart starts from the configured `--toolsets` (none in `~/.mcp.json` today). That covers `McpServerControl(StopServer)`,
an idle restart (`IdleRestartMonitor`), `/mcp` reconnect and a tab resume. After an idle restart the client still shows
the old tool list, so the first call to a toolset tool fails with "Unknown tool" and the agent must re-enable the set.
Memory `project_idle_restart_drops_toolsets` records this as an accepted tradeoff. It was accepted only because no
per-tab key existed: each launch gets a random instance id (`roslynsentinel-mcp-launch.ps1`, `$instanceToken`).
`%VSCODE_PID%` is shared by every window.

A per-tab key now exists. The launcher's owner probe (commit `4e06deeb`) logged the following on 2026-11-15:
- Each VS Code Claude tab is its own `claude.exe`, and the launcher is its direct child (`powershell.exe <- claude.exe`).
- `claude.exe` PID + creation time is unique among live tabs. It is stable across a server restart in the same tab
  (PID 22008 and PID 24112 each logged twice).
- `CLAUDE_PID` is not inherited by the MCP process. `CLAUDE_CODE_SESSION_ID` is inherited.
- On a resume, the PID is new and the session id is the same (`534e2b2d` under PIDs 39640, 52604 and 39832).
- `/clear` does not respawn the server, so the server's `CLAUDE_CODE_SESSION_ID` goes stale (`534e2b2d` vs the new
  `1efa4613`).
- Two live servers can therefore report the same session id. The session id cannot be the folder key.

## Decision

Rulings 2026-11-15:
- O1 composing bool (owner).
- O4 accept (owner).
- Run mode Batched, owner away, so Step 9 goes to the morning checklist (owner).
- Review questions, settled by the dispatcher with the recommendation:
  - Q1: Step 5 uses a static `ApplyStopToolsets` seam.
  - Q2: Step 4 resolves the store from DI.
  - Q3: lean-only registration is accepted.

Batches:
- A: Steps 1, 2, 3 (Common, plus `ParseToolsets`).
- L: Step 6 (launcher, disjoint), runs in parallel with A.
- B: Steps 4 and 7 (server startup).
- C: Step 5 (AdminTools), then Step 8.
- D: Step 10 after Step 9.

- **Key.** The tab key is `claude-<pid>-<creationUtc:yyyyMMddHHmmss>`.
- **Folder.** The launcher creates `<root>\tabstate\<repoHash>-<tabKey>\` and passes `--tab-state-dir=<that>` to the
  server. `<root>` is `<instancesRoot>` in the prod layout and `bin-vscode` in the dev layout. `tabstate\` is a sibling
  of `instances\`, so the instance sweep never touches it. Build DLLs stay in the random-token folders, unchanged.
- **State file.** The server keeps `toolsets.json` in that folder. It holds the enabled set names, the server's
  `CLAUDE_CODE_SESSION_ID` and the save time. It is rewritten on every real enable/disable through a new
  `ToolsetService.EnabledToolSetsChanged` event.
- **Startup.** If the state file exists it replaces `--toolsets`. If it is absent, the server copies the newest
  sibling state whose session id equals its own `CLAUDE_CODE_SESSION_ID` (resume carry-over; copy, never share). If
  neither exists, the server uses `--toolsets`.
- **Client refresh.** After a restored start the server sends one `notifications/tools/list_changed` once the client
  has initialized. The client keeps its pre-restart list across a respawn (`ToolsetControlTools.cs:79`, verified live
  in Step 9), so without this notification a changed set would stay invisible.
- **McpServerControl(StopServer).** Two options compose (owner ruling O1, 2026-11-15). The next process loads
  `(keepToolsets ? current sets : configured --toolsets) union toolsets`:
  - `toolsets`: the existing param, added for LaunchTestInstance by plan D7/Step 14. For StopServer it names sets to
    ADD to the base.
  - `keepToolsets` (new, `bool`, default `true`): picks the base. `false` = the configured `--toolsets` (none today),
    so `keepToolsets: false, toolsets: "git"` is exactly `git` today. `false` with no `toolsets` clears the state file
    instead of saving the configured list, so a later `~/.mcp.json` change still applies (O4).
  - Removing one set: `McpToolsetControl(enabled: false)` before the stop; every switch is saved immediately.
  The state file is written inside the confirmed-stop callback, never on a refused call.
- **Durable by default.** Keeping the current sets needs no option. The idle restart and `/mcp` reconnect keep them too.
- **Out of the key path.** A server started without the launcher (test instances, ModelEval, HTTP fallback) gets no
  `--tab-state-dir`. It behaves exactly as today, and StopServer says the options had no effect.

## Execution rules

- `plan_tool_experience_journal_fixes.md` Step 14 (`string? toolsets` on `McpServerControlTool`) and
  `ToolsetService.ToolListChangedRaised` are committed (verified 2026-11-15). Run `Git status` before each batch anyway;
  other sessions work in this tree.
- `TabToolsetStateStore` is registered only where `ToolsetService` is (claude-lean with `ToolsetControlTools` active).
  Other modes have no toolsets, so they get no tab state. This is intended.
- `Build` 0 errors per slice. Tests are named per step for the dispatcher. The baseline is 0 failures, so any red test
  is new.
- Server source changes need `Build(level: fullBuild)` and a server restart before Step 9.
- Text-neutral slices first: Steps 1-4 and 6 change nothing visible until the launcher passes the flag (Step 6) and a
  state file exists.

## Steps

### Step 1 - Contract skeleton (Common)
- Done: 7b2b901f
- Files: `RoslynSentinel.Common/TabToolsetState.cs` (new), `RoslynSentinel.Common/ToolsetCatalog.cs`, `RoslynSentinel.Common/ServerInstancePaths.cs`
- Depends on: none
- Effect: text-neutral (new members, no callers).
- Change:
  - **New file**, one `CreateFile` with `NEW-DTO` in the reason:
    - `public sealed record TabToolsetState(IReadOnlyList<string> EnabledToolSets, string? SessionId, DateTime SavedUtc);`
    - `public sealed class TabToolsetStateStore`:
      - `public const string FileName = "toolsets.json";`
      - `public TabToolsetStateStore(string tabStateDir, string? sessionId, ILogger? logger = null)`
      - `public string TabStateDir { get; }`
      - `public string? SessionId { get; }`
      - `public IReadOnlyList<ToolSetName> ConfiguredToolSets { get; set; } = Array.Empty<ToolSetName>();` (the
        `--toolsets` list, set at startup in Step 4, read by StopServer in Step 5; a real auto-property, not a stub)
      - `public bool RestoredAtStartup { get; set; }` (set by Step 4 when a saved state was applied, read by Step 7;
        a real auto-property)
      - `public TabToolsetState? Load()`: own file, else the session-id seed. Never throws; returns null on any IO or parse problem.
      - `public void Save(IReadOnlyList<ToolSetName> enabled)`: temp file + `File.Move(overwrite: true)`. Never throws.
      - `public void Clear()`
    - Bodies `throw new NotImplementedException(); // TODO implement (Step 2)`.
  - **`ToolsetCatalog`**: add `public static bool TryParseSetName(string name, out ToolSetName set)` (new), stub
    `// TODO implement (Step 3)`. It must not be confused with the existing `public static ToolSetName? FindSet(string toolName)`,
    which maps a tool name to its set.
  - **`ServerInstancePaths`**: add `public const string TabStateDirFlag = "--tab-state-dir";` and
    `public string? TabStateDir { get; }`. Parse it in `public static ServerInstancePaths Resolve(string[] args, string binaryDirectory)`
    with the existing private `GetArgValue`. Extend the private ctor `ServerInstancePaths(string workingDir, bool isExplicit, string stopMarkerPath)`
    with `string? tabStateDir`. The only caller is `Resolve`, so this is a real implementation, not a stub. `Resolve`
    has two return paths (explicit `--working-dir` and the binary-dir fallback); parse `--tab-state-dir` for both.
  - **DTO justification**:
    ```
    NEW-DTO: TabToolsetState (Item, Common.Toolsets)
    Evaluated: ActiveToolSurface - startup surface, has no toolsets and is not persisted; ToolTelemetrySnapshot - per-tool counters; LedgerSnapshot - edit ledger
    Wire impact: none (on-disk file only)
    ```
- Done when: `Build` 0 errors.
- Lane: `implementer` D1 (3 files).

### Step 2 - TabToolsetStateStore implementation
- Done: 2df3e449
- Files: `RoslynSentinel.Common/TabToolsetState.cs`
- Depends on: 1
- Effect: text-neutral.
- Change:
  - **`Load`**:
    - Read `<dir>\toolsets.json` (System.Text.Json, camelCase).
    - If the file is absent and `SessionId` is non-empty, scan the sibling directories of `TabStateDir`'s parent for
      `toolsets.json` with an equal `sessionId`. Take the newest `savedUtc`, copy its file into the own folder and
      return it.
    - Unknown set names in the file are kept as strings; the caller reports them.
  - **`Save`**: writes `{enabledToolSets, sessionId, savedUtc}`, creating the directory if missing.
  - **`Clear`**: deletes the file, ignoring a missing one.
  - IO errors are logged at warning level and swallowed. A state file must never stop the server (pattern:
    `ToolTelemetryFlusher`).
- Done when: `Build` 0 errors.
- Lane: `implementer` D1.
- Tests (test-writer, new file `RoslynSentinel.Tests.Server/TabToolsetStateStoreTests.cs`):
  - `SaveThenLoad_RoundTrips`
  - `Load_NoFile_SeedsFromNewestSiblingWithSameSession`
  - `Load_NoFile_IgnoresSiblingWithOtherSession`
  - `Load_CorruptFile_ReturnsNull`
  - `Clear_MissingFile_DoesNotThrow`

### Step 3 - Set-name parser and change event
- Done: 3398ae4e, test 9a247fb0
- Files: `RoslynSentinel.Common/ToolsetCatalog.cs`, `RoslynSentinel.Common/ToolsetService.cs`, `RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs`
- Depends on: 1
- Effect: text-neutral (same parsing, new event with no subscriber).
- Change:
  - **`TryParseSetName`**: move the normalization from `ServerStartupHelpers.ParseToolsets`
    (`name.Replace("-", "").Replace("_", "")`, then `Enum.TryParse<ToolSetName>(..., ignoreCase: true)` + `Enum.IsDefined`)
    into it. `ParseToolsets` calls it.
  - **`ToolsetService`**: add `public event Action<IReadOnlyList<ToolSetName>>? EnabledToolSetsChanged;` (new). Raise it
    from `public ToolsetChange SetEnabled(ToolSetName toolSet, bool enabled)` outside `_gate`, only when `_enabled`
    actually changed, passing `EnabledToolSets`. `SetEnabled` builds and returns its `ToolsetChange` inside the lock:
    capture the result and a `changed` flag in locals, leave the lock, raise, then return.
- Done when: `Build` 0 errors.
- Lane: `implementer` D1 (3 files).
- Tests (test-writer, `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`):
  - `EnabledToolSetsChanged_RaisedOnceOnRealChange_NotOnIdempotentCall`
  - The existing `ParseToolsets_*` tests stay green.

### Step 4 - Startup restore and save wiring
- Done: 62edb05f
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Server.Advanced/ServerStartupHelpers.cs`
- Depends on: 2, 3
- Effect: behaviour-changing only with `--tab-state-dir` (none until Step 6). A restored server loads the saved sets
  instead of `--toolsets`.
- Change:
  - **Registration**: in `RegisterRoslynSentinelToolsBasic`, beside the `services.AddSingleton(sp => new ToolsetService(`
    block (~line 277), register `TabToolsetStateStore` as a singleton when `ServerInstancePaths.Current.TabStateDir` is
    non-null. Construct it with `Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID")`.
  - **`ApplyStartupToolsets`** (`public static IReadOnlyList<string> ApplyStartupToolsets(IServiceProvider services, string[] args)`):
    - **Early return**: today it returns early when `--toolsets` is empty (`if (sets.Count == 0 && unknown.Count == 0)`,
      ~line 151). Move that check below the store load. Otherwise a state file with no `--toolsets` restores nothing
      and the save subscription never attaches.
    - Resolve the store with `services.GetService<TabToolsetStateStore>()`, not `ServerInstancePaths.Current`. Tests
      register the store directly; `Current` is process-static and races under parallel tests.
    - Set `store.ConfiguredToolSets` to the `ParseToolsets(args)` sets. If `Load()` returns a state, use its parsed sets
      (via `TryParseSetName`) instead of `ParseToolsets(args)`, and set `store.RestoredAtStartup = true`.
    - Add a warning per unknown saved name, and one line naming the source (`toolsets restored from tab state: git, declarations`).
    - Then subscribe `toolsetService.EnabledToolSetsChanged += store.Save`.
    - If `ToolsetService` is null (not claude-lean), only warn.
    - Keep "never throws".
    - Both call sites (`ServerStdio.cs:~140`, `ServerHttp.cs:103`) stay as they are.
- Done when: `Build` 0 errors.
- Lane: `implementer` D1 (2 files).
- Tests (test-writer, new file `RoslynSentinel.Tests.Server/StartupToolsetRestoreTests.cs`):
  - `StateFile_ReplacesToolsetsArg`
  - `NoStateFile_UsesToolsetsArg`
  - `EnableAfterStartup_WritesStateFile`
  - `UnknownSavedName_WarnsAndSkips`

### Step 5 - McpServerControl(StopServer) toolset options
- Done: 934f415c (budget 45200 -> 45500, owner approved)
- Files: `RoslynSentinel.Tools.Basic/AdminTools.cs`
- Depends on: 3, 4
- Effect: behaviour-changing. There is one new optional param, `keepToolsets`. `toolsets` gains a StopServer meaning.
  The StopServer response names the toolsets the respawn will load.
- Change:
  - **Constructor**: append the optional params
    `TabToolsetStateStore? tabToolsetState = null, ToolsetService? toolsetService = null` to
    `public AdminTools(IWorkspaceManager workspaceManager, SentinelHostOptions? hostOptions = null, TestInstanceLauncher? testInstances = null, ActiveToolSurface? activeToolSurface = null, ResponseGatedProcessExit? gatedExit = null)`.
    They are trailing optionals, so no caller changes.
  - **Signature**: on `McpServerControlTool`, add `[Description("StopServer: true = the restarted server keeps the current toolsets; false = starts from the configured --toolsets. toolsets is added to either.")] bool keepToolsets = true` after `toolsets`.
  - **Descriptions**: change the `toolsets` description to
    `"ToolSetName values, comma-separated, e.g. git,moveExtract. LaunchTestInstance: the child's toolsets. StopServer: added to the toolsets the restarted server loads."`
  - **StopServer branch** (before `ControlServerResult`):
    - Validate `toolsets` with `ToolsetCatalog.TryParseSetName`. On an unknown name, return `InvalidArgument` listing
      the unknown names and `Enum.GetNames<ToolSetName>()`.
  - **Test seam** (new): `public static string ApplyStopToolsets(TabToolsetStateStore? store, ToolsetService? toolsetService, bool keepToolsets, IReadOnlyList<ToolSetName> added)`.
    It performs the state write and returns the response line below. It is static so tests can call it directly: the
    confirmed-stop path through `McpServerControlTool` without `requestContext`/`gatedExit` falls to
    `ProcessExitScheduler.Schedule()`, which calls `Environment.Exit(0)` and would kill the test host.
    - `keepToolsets` false and no `added`: `Clear()`.
    - Otherwise: `Save(base union added)`, distinct, base order first.
      - Base for `true`: `toolsetService.EnabledToolSets`, or empty when `toolsetService` is null.
      - Base for `false`: `store.ConfiguredToolSets`.
  - **Inside the `scheduleExit` lambda** (runs only on a confirmed stop, synchronously inside the static
    `ControlServerResult`): call `ApplyStopToolsets` and capture its line in a local. After `ControlServerResult`
    returns success, append the line to the message in `McpServerControlTool`. `ControlServerResult` is static and
    cannot see the store, so the line cannot be added there.
  - **Response text** (the line `ApplyStopToolsets` returns):
    - When the store is non-null: `Toolsets after restart: <list, or "configured --toolsets" after Clear>.`
    - When the store is null and either option deviates from the default: the fact
      `toolsets/keepToolsets had no effect: this server has no --tab-state-dir (not started by the MCP launcher).`
  - Facts only, no policy words.
- Done when: `Build` 0 errors.
- Lane: `implementer` D1 (1 file, about 8 edits including the seam).
- Tests (test-writer, `RoslynSentinel.Tests.Tools.Basic/AdminToolsTests.cs`). Confirmed-stop cases call
  `ApplyStopToolsets` directly. Only the refusal case goes through `McpServerControlTool`, because a refusal never
  reaches the exit.
  - `StopServer_WithToolsets_AddsToCurrentSets`
  - `StopServer_Default_SavesCurrentSets`
  - `StopServer_KeepToolsetsFalse_ClearsState`
  - `StopServer_KeepFalseWithToolsets_IsConfiguredPlusList`
  - `StopServer_UnknownToolset_InvalidArgumentListsValidNames`
  - `StopServer_WithoutConfirmation_DoesNotTouchState`
  - `StopServer_NoTabStateDir_SaysOptionsHadNoEffect`
  - Dispatcher also runs `ClaudeLeanSchemaBudgetTests` and `TestInstanceLauncherTests`.

### Step 6 - Launcher: tab key, --tab-state-dir, tabstate sweep
- Done: 0de85bbe
- Files: `scripts/roslynsentinel-mcp-launch.ps1`
- Depends on: none. It can run beside Steps 1-4 (disjoint files). Server-side use needs Step 4.
- Effect: behaviour-changing. Servers launched from a Claude tab get `--tab-state-dir`.
- Change:
  - **Owner probe**: turn the probe block (`# Diagnostic only (not used for keying yet)`, ~line 366) into the source of
    `$tabKey = "claude-$pid-$($created.ToUniversalTime().ToString('yyyyMMddHHmmss'))"`. Keep its log line and change
    the comment.
  - **Folder and flag**:
    - `$tabStateRoot` is `Join-Path $prodRoot 'tabstate'` in prod (defined beside `$telemetryRoot`, ~line 228) and
      `Join-Path $binVscodeRoot 'tabstate'` in dev.
    - `$tabStateDir` is `Join-Path $tabStateRoot "$repoHash-$tabKey"`. Create it.
    - Add `"--tab-state-dir=$tabStateDir"` to the server args next to `--working-dir` (~line 938).
      - `$extraLaunchArgs` is filled only `if ($prodMode)` (~938-942), so add a dev-layout branch that appends the flag.
      - `$binVscodeRoot` exists only in the non-prod branch (~241). The dev sweep (~432) filters on
        `^[0-9a-f]{8}-[0-9a-f]{8}$`, so `bin-vscode\tabstate` is safe from it.
    - Without a `claude.exe` ancestor, pass no flag.
  - **Sweep**: in the sweep region, remove `tabstate\*` folders older than 7 days by `LastWriteTime` whose
    `claude-<pid>-<stamp>` owner is not a live `claude.exe` with that creation time. Reuse `$procById` from the probe.
    Keep the 7 days so a resumed tab can still seed from a closed tab's folder.
  - Windows PowerShell 5.1 syntax only.
- Done when: the script parses (`[System.Management.Automation.Language.Parser]::ParseFile`, 0 errors). The probe and
  key block, extracted to a temp script with a stub `Write-LaunchLog` and run under `powershell.exe` from the agent's
  shell (whose ancestor is a `claude.exe`), prints a non-empty `$tabStateDir`.
- Lane: `implementer` D1 (1 file).

### Step 7 - list_changed after a restored start
- Done: 0531ece8
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs` (or a new handler file in `Server.Advanced`)
- Depends on: 4
- Effect: behaviour-changing. After a start that restored at least one set, the client receives one
  `notifications/tools/list_changed` after `notifications/initialized` and re-lists tools.
- Change:
  - Add an incoming message filter. ModelContextProtocol 2.3.0 is used, and the review confirmed this API in the package
    XML docs:
    `mcpBuilder.WithMessageFilters(f => f.AddIncomingFilter(next => async (ctx, ct) => { await next(ctx, ct); ... }))`.
    The pattern to copy is `AddResponseGatedExitMessageFilter`, which uses `AddOutgoingFilter` (~line 99). Place the
    new filter in the documented filter order (~line 123).
  - When `ctx.JsonRpcMessage` is a `JsonRpcNotification` with `Method == NotificationMethods.InitializedNotification`,
    the process has not sent yet, and `ctx.Services.GetService<TabToolsetStateStore>()?.RestoredAtStartup == true`:
    after `next`, `await ctx.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct)`.
    Guard it with an `Interlocked` flag so it sends once per process.
  - Log one line when it is sent, so Step 9 can observe it in the server log.
- Done when: `Build` 0 errors.
- Lane: `implementer` D1 (API named). Escalate to `implementer-senior` if the filter signature does not compile as
  quoted.
- Tests (test-writer, `RoslynSentinel.Tests.Server/McpToolsetControlTests.LiveServer`):
  - `RestoredStart_SendsOneToolListChangedAfterInitialized`
  - `PlainStart_SendsNone`

### Step 8 - Stub sweep
- Done: no stubs left; fullBuildThrowaway 0 errors; fullBuild owed (peer ModelEval runs held the lock)
- Files: Step 1-7 files
- Depends on: 2, 3, 4, 5, 7
- Effect: text-neutral.
- Change: `Search(mode: text, query: "NotImplementedException|TODO implement \\(Step")` over the solution returns no
  match in the Step 1-7 files.
- Done when: zero matches; `Build(level: fullBuild)` 0 errors.
- Lane: dispatcher.

### Step 9 - LIVE verification
- Files: none
- Depends on: 5, 6, 7, and a server restart on the new build
- Effect: none.
- Change: run each case in a real VS Code Claude tab and record one outcome class per case:
  - **A. Default restart keeps sets.** Enable `git`, then `StopServer`. On the next call, `Git(log)` works with no
    re-enable. Outcomes: `kept` | `lost`.
  - **B. `/mcp` reconnect keeps sets.** Outcomes: `kept` | `lost`.
  - **C. `StopServer(toolsets: "declarations")`.** Outcomes: `visible-without-reconnect` (Step 7 works) |
    `visible-after-reconnect` (limit: the client ignores list_changed on a fresh connection) | `not-loaded`.
  - **D. `keepToolsets: false`.** Outcomes: `config-only` | `kept`.
  - **E. Close the tab and resume the conversation.** Outcomes: `seeded` | `fresh`.
  - **F. A second concurrent tab is unaffected by A-D.** Outcomes: `isolated` | `leaked`.
  - **G. Idle restart.** Covered by A (same respawn path). Note it only if one happens during the run.
- Done when: every case has an outcome class recorded in this plan's Status. A non-ideal class gets a `// TODO` or a
  follow-up in `docs/current/TODO.md`.
- Lane: dispatcher (main session; needs the owner for `/mcp` and resume).

### Step 10 - Docs and guidance text
- Files: `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`, `CLAUDE.md`, `docs/current/references/reference_live_server_freshness.md`, `docs/current/plans/plan_tool_experience_journal_fixes.md`, `docs/current/findings/finding_tool_experience_journal_rollup_pain_points.md`
- Depends on: 9 (wording follows the observed outcomes)
- Effect: behaviour-changing (tool status text).
- Change:
  - **`ToolsetControlTools.cs:79`**: rewrite the status `summary` sentence that says a reconnect resets enabled
    toolsets. The new fact is that enabled toolsets persist per tab across restarts and reconnects when the server was
    started by the launcher.
  - **`CLAUDE.md` "Gated tools"**: drop "(the reconnect resets toolsets ...)" and "then enable again" if case B is
    `kept`.
  - **`reference_live_server_freshness.md` ~lines 14 and 17**: update to match.
  - **D12**: in `plan_tool_experience_journal_fixes.md` (lines ~67 and ~644) and the finding's D12 row (~line 85),
    point to this plan.
  - **Memory**: move `project_idle_restart_drops_toolsets` to MEMORY_CLOSED.
- Done when: `Build` 0 errors; `scripts/Test-GuidanceHygiene.ps1` passes. The dispatcher runs `McpToolsetControlTests`.
- Lane: main session (docs) + `implementer` D1 (the `.cs` string).

## Out of scope

- Persisting anything else per tab: the loaded solution path, undo history, the operating mode. The folder makes them
  possible later.
- Keying by `CLAUDE_CODE_SESSION_ID`. It is rejected because of the `/clear` staleness and live-collision evidence above.
- The shim route (`roslynsentinel-mcp-launch-shim.ps1`). The parent walk already tolerates the extra hop. It is not
  wired in `~/.mcp.json` today, so it is not tested here.
- A `McpServerStatus` field showing the tab state dir and restore source. That file is under another session's edit;
  this is a follow-up.
- Test instances (`LaunchTestInstance`) and HTTP/ModelEval servers. They get no tab state by design.

## Risks and open decisions

- **O1 - SETTLED 2026-11-15 (owner):** the composing bool. Result = `(keepToolsets ? current : configured) union toolsets`,
  with no ignored combination. Adding a set is one call. The enum form (`restartToolsets: keep | configured | listed`)
  was rejected.
- **O2 - Does the client re-list on `list_changed` right after a re-initialize?** Unverified. Step 9 case C decides it.
  If the outcome is `visible-after-reconnect`, the `toolsets` option still works for the next reconnect and for
  restored tools that the client already knew; record it as a limit.
- **O3 - SDK hook for `initialized`.** The review named the API (incoming message filter, `SendNotificationAsync`).
  It has not been compiled yet; Step 7 escalates if the quoted signature fails.
- **O4 - Restore precedence.** The state file replaces `--toolsets`, so a set the owner later adds to `~/.mcp.json`
  does not appear in a tab that already has state until `keepToolsets: false` or a new tab.
  - **SETTLED 2026-11-15 (owner): accept.** The state file replaces `--toolsets`. The union alternative was rejected
    because it makes disabling a configured set impossible to persist.
- **O5 - PID reuse.** The creation time in the key makes a reused PID a different key. A crashed tab's folder lingers
  until the 7-day sweep.
- **O6 - Concurrent edits.** Resolved: the other plan's Step 14 and the `ToolsetService` edits are committed. Other
  sessions still share the tree, so run `Git status` before each batch.
- **O7 - Schema budget.** `keepToolsets` plus the longer `toolsets` description count against `ClaudeLeanSchemaBudgetTests`
  (`AdvancedTotalBudget = 45000`, per-tool cap 5000). Step 5's dispatcher run catches an overrun. If it fails, trim
  the descriptions before raising the budget.
