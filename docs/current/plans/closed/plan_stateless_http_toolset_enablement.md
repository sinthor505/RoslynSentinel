# Plan: Make runtime toolset enablement work over stateless HTTP, then restore Stateless = true

**Status:** DRAFT 2026-10-11. Not started; Step 1 must confirm the SDK behaviour this plan rests on.

## Problem

Both HTTP hosts run the stateful (legacy, session-based) transport:
`builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = false)` in
`RoslynSentinel.Server.Advanced/ServerHttp.cs:78` and `RoslynSentinel.Server.Basic/ServerHttp.cs:72`.
A live probe of `http://127.0.0.1:5100/mcp` on 2026-10-11 returned an `Mcp-Session-Id` header and an SSE
body for `initialize`. Clients that expect the modern stateless protocol therefore get the legacy one.

The setting was added deliberately in commit `9e88c086` (2026-10-04): in stateless mode,
`McpToolsetControl` reported a set active, but the next call returned "Unknown tool"
(journal `.claude/journal/2026-10-04_a08be84f.md`, line 43).

Mechanism (read from source 2026-10-11; the SDK half is from a local clone newer than the pinned 2.2.0):
- `ToolsetService` (`RoslynSentinel.Common/ToolsetService.cs`) is a DI singleton, registered claude-lean
  only (`Server.Basic/ServiceRegistrationExtensionsBasic.cs:198`). It keeps the on/off state in its own
  `_enabled` dictionary and `SetEnabled` adds/removes tools in the shared
  `IOptions<McpServerOptions>.Value.ToolCollection`.
- In stateless mode the SDK's `StreamableHttpHandler.CreateSessionAsync` builds a fresh
  `McpServerOptions` per request from the startup DI tools, so the shared collection is never read.
  A toggle succeeds and has no effect. A stateful session uses the shared options object, which is why
  `Stateless = false` fixed it.
- Stateless mode cannot send `tools/list_changed` (`McpServerImpl.cs`, `HasStatefulTransport`), so
  clients must re-list; calling a tool by name does not need a re-list.

## Decision

Keep `ToolsetService._enabled` as the single, process-wide source of truth for which on-demand tools
are enabled (the analogue of `activeToolClasses` at startup), and make every freshly built
`McpServerOptions` include the enabled tools. Then set `Stateless = true` on both HTTP hosts.
Stdio and stateful behaviour are unchanged: `SetEnabled` keeps mutating the shared collection.

Consequence to accept: all HTTP clients share one toolset state, so one client's enable changes the
tool surface for every client. This is already true of stateful HTTP today.

## Execution rules

- All C# edits are dispatched to `implementer` (sequentially, one slice at a time); this plan is the brief source.
- `Build` 0 errors before each commit. Test baseline: `docs/current` / memory `reference_known_failing_tests`; report new failures only.
- NUnit 5: `await` every `Assert.ThrowsAsync`/`CatchAsync`.
- Verify the live HTTP behaviour only after `Build(level: fullBuild)` and a server restart (`McpServerControl`); a rebuilt binary does not change the running server.

## Steps

### Step 1 - Verify the SDK assumption against the pinned version
- Files: none edited. Read ModelContextProtocol 2.2.0 (check out the matching tag in the local csharp-sdk clone, memory `reference_csharp_sdk_local_clone.md`).
- Change: confirm, for 2.2.0, that (a) a stateless request builds a fresh `McpServerOptions`/`ToolCollection` from startup DI tools, (b) which hook runs per request and can add tools (`HttpServerTransportOptions.ConfigureSessionOptions` or an `IConfigureOptions<McpServerOptions>` ordered after the SDK's `McpServerOptionsSetup`), (c) whether the per-request `RequestContext.Server.ServerOptions.ToolCollection` is the object `ToolArgumentValidator` reads.
- Done when: a short note appended to this plan under "Risks and open decisions" records the confirmed hook and any difference from the clone. If (a) is false, stop: the cause of "Unknown tool" is elsewhere and this plan is void.

### Step 2 - Failing regression test first
- Files: the existing `McpToolsetControlTests` project (locate with `Search`); one new test.
- Change: host the server over HTTP with `Stateless = true` in-process, call `McpToolsetControl` to enable a set (e.g. `declarations`), then call a tool from that set in a separate request with no session id.
- Done when: the test runs and fails with "Unknown tool" against current code.

### Step 3 - Cache built tools and expose them from ToolsetService
- Files: `RoslynSentinel.Common/ToolsetService.cs`.
- Change: add a read accessor returning the currently enabled `McpServerTool` instances (the lists already held in `_enabled`, copied under `_gate`). No rebuild per request: `SetEnabled` already builds once.
- Done when: Build 0 errors; unit test that the accessor reflects enable and disable.

### Step 4 - Apply enabled tools to every fresh options object
- Files: `Server.Basic/ServiceRegistrationExtensionsBasic.cs` (inside the existing `toolsetControlActive` block, next to the `ToolsetService` registration), plus the hook identified in Step 1.
- Change: register the per-request hook so each new `McpServerOptions.ToolCollection` gets `TryAdd` of the enabled tools. Registered only when `ToolsetService` is. Must not double-add to the shared collection that stdio/stateful already mutate (use `TryAdd`).
- Done when: Build 0 errors; the Step 2 test still fails only because `Stateless` is still false/true as configured, or passes if the test forces stateless.

### Step 5 - Restore Stateless = true
- Files: `Server.Advanced/ServerHttp.cs:78`, `Server.Basic/ServerHttp.cs:72`.
- Change: remove the `o.Stateless = false` override (or set `true` explicitly, with a comment pointing at this plan).
- Done when: Step 2 test passes; Build 0 errors; Tests.Server and Tests.Advanced DI/SubAgent suites show no new failures.

### Step 6 - Live check
- Change: `Build(fullBuild)`, restart the HTTP server, POST `initialize` and confirm no `Mcp-Session-Id` header; enable a toolset and call one of its tools in a later request; call `tools/list` and confirm it appears.
- Done when: all three observations recorded here with the date.

## Out of scope

- Per-client (per-session) toolset isolation over stateless HTTP; it needs a client-supplied key and is a separate design.
- Emitting `tools/list_changed` over `subscriptions/listen` for protocol 2026-07-28 clients.
- Changing stdio behaviour or the `claude-lean` allow-list.

## Risks and open decisions

- Unverified: the per-request rebuild is read from a clone newer than pinned 2.2.0 (Step 1 closes this).
- `ToolArgumentValidator.cs:35` reads `server.ServerOptions.ToolCollection`; with stateless it sees the per-request collection. Step 4 must put dynamic tools there too, otherwise validation is skipped for them (hypothesis, not tested).
- Hook ordering: an options configurator that runs before the SDK's `McpServerOptionsSetup` may be overwritten or interfere with its `?? []` handling.
- Shared state means a client enabling a set silently changes everyone's tool list; decide whether that is acceptable for multi-client HTTP use (open decision for the user).
- Clients without `list_changed` must re-list after enabling; call-by-name works without it.
- Fallback if hook ordering is unworkable: keep `Stateless = false` and document that stateless clients use static modes (`--mode` / `--include-tools`).
