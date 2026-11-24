# Plan: Toolset changes reach every client without a /mcp reconnect (stdio and HTTP, 2025-11-25 and 2026-07-28)

**Status:** APPROVED 2026-11-23 (design rewritten; owner accepted shared toolset state). Not started. Diagnostics landed
first: 014e4110 (protocol log lines, `McpServerStatus.toolListSubscriptionRequests`), a86b5afd (schema budget).
Supersedes the 2026-10-11 draft of this plan (per-request TryAdd hook, plain `Stateless = true`).
Review fixes applied 2026-11-23 (read-only review against SDK v2.4.0 and source): Step 1 answered from the SDK, Step 6
names the test it flips, Step 4 delivery table reordered, test-impact and Risk corrections; the cross-plan conflict
with `plan_json_toolset_definitions.md` is decided: this plan lands first (Risks). Long-term home of the delivery logic:
[proposal_tool_surface_controller.md](../proposals/proposal_tool_surface_controller.md) (its "Deliver" stage); this plan
is independent of it and can land first.

## Problem

A toolset enabled mid-session must show up in the client's tool list without a `/mcp` reconnect. Evidence, 2026-11-23:

- **stdio, 2025-11-25: works.** Live probes in a VS Code tab and in headless `claude -p` (Claude Code 2.1.329):
  `McpToolsetControl(git, true)` made `GitRemote` appear in ToolSearch at once and callable, for the main session and for
  a general-purpose subagent; disables were picked up too. `McpServerStatus.clientProtocolVersion` = `2025-11-25`.
- **The client probes 2026-07-28 and falls back.** The new ProtocolDiagnostics log (014e4110) of the headless probe:
  `MCP initialize: client requested protocol version 2025-11-25`, `MCP per-request protocol version: 2026-07-28`,
  `MCP server/discover received`, `MCP initialize: negotiated protocol version 2025-11-25`. No `subscriptions/listen`.
  The Claude Code changelog (2.1.292, 2026-10-06) says stdio negotiates 2026-07-28 by default; why it falls back here is
  not known.
- **On 2026-07-28 the SDK stays silent unless the client subscribed.** `McpServerImpl.SendListChangedNotificationAsync`
  (SDK v2.4.0) broadcasts only to initialize-handshake clients; a 2026-07-28 client gets `tools/list_changed` only over
  a `subscriptions/listen` stream that asked for `toolsListChanged`. This fits the unexplained failures of 2026-10-25
  and 2026-11-06 (`blockers/closed/blocking_error_toolset_enable_not_visible_in_client_tool_list.md`,
  `blockers/blocking_error_gitremote_not_callable_after_toolset_enable.md`), which fall after the 2.1.292 switch
  (hypothesis: no server log of those sessions survives).
- **HTTP: a toggle is a silent no-op.** `ServerHttp.cs:91` sets `o.Stateless = true`. In stateless mode the SDK builds
  fresh `McpServerOptions` per request from DI (`StreamableHttpHandler.CreateSessionAsync`, v2.4.0 line 587), so the
  shared `ToolCollection` that `ToolsetService.SetEnabled` mutates is never read; `--toolsets` and every
  `McpToolsetControl` call have no effect. Stateless also cannot push `tools/list_changed` (`McpServerImpl.cs:117`).
- **The SDK hook replaces options for stateful sessions too.** Line 587 builds fresh options whenever
  `HttpServerTransportOptions.ConfigureSessionOptions` is set, so the 2026-10-11 draft's "TryAdd enabled tools into the
  per-request collection" would also cut stateful sessions off the shared collection and its change notifications.

## Decision

1. **One live tool collection for every session.** A `ConfigureSessionOptions` hook sets each session's
   `McpServerOptions.ToolCollection` to the shared singleton `IOptions<McpServerOptions>.Value.ToolCollection`, the
   object `ToolsetService` mutates. Stateful sessions subscribe to its `Changed` event (the SDK unsubscribes on dispose,
   `McpServerImpl.cs:130`), per-request sessions read it live. No accessor or merge in `ToolsetService`.
2. **`HttpServerSessionMode.StatefulForInitializeClients`** replaces `Stateless = true`: initialize-handshake clients
   (2025-11-25, what Claude Code ends up on) get a session and the SDK's `tools/list_changed`; 2026-07-28 clients stay
   stateless and see the change on their next `tools/list` or call.
3. **Direct notification for an unsubscribed 2026-07-28 client on a push-capable transport (stdio).** After a real
   change, `McpToolsetControl` sends `notifications/tools/list_changed` through `McpServer.SendNotificationAsync` when the
   request's protocol version is 2026-07-28 or later, the host transport is stdio, and
   `ToolsetService.ToolListSubscriptionRequests` is 0. This deviates from SEP-2575 (a server must not send notification
   types the client did not subscribe to) on purpose, with a code comment; the restored-start filter
   (`AddRestoredToolsetListChangedMessageFilter`) already sends the same way.
4. **Shared state accepted (owner, 2026-11-23):** all HTTP clients of one server share one toolset state; one client's
   enable changes every client's tool list.
5. **Status text states the delivery path as fact** (sent by the SDK, sent directly, or not deliverable on this
   connection, with the option to re-list), replacing the unconditional `/mcp` sentence.

## Execution rules

- Dispatch per the `dispatch-implementer` skill (tier file `.claude/implementer-tiers.json`, depth tier D1 at writing).
- `Build` 0 errors before each commit; tests run by the dispatcher (`scripts/Invoke-WorktreeTests.ps1 -WithRatchets`),
  baseline 0 failures. The claude-lean schema budget (`ClaudeLeanSchemaBudgetTests`, 48400) had 13 chars of headroom
  after a86b5afd: any description text added to a claude-lean tool must be paid for elsewhere or the budget raised with
  a reason in the commit message.
- NUnit 5: `await` every `Assert.ThrowsAsync`/`CatchAsync`.
- Live checks only after commit and a server restart (`McpServerControl(StopServer)`) or `LaunchTestInstance`.

## Steps

Order: Step 1 (done) and Step 3 add no wire text; Steps 2, 4, 5 and 6 are behaviour-changing.

### Step 1 - Spike: the raw HTTP shape of a 2026-07-28 request (DONE 2026-11-23, answered in review)
- Files: none edited.
- Depends on: none.
- Effect: none.
- Answer (SDK v2.4.0, `StreamableHttpHandler.cs` unless noted): a session-less 2026-07-28 POST to
  `StatefulForInitializeClients` is served statelessly (`:477-478`) only if it carries all of:
  - header `MCP-Protocol-Version: 2026-07-28`; without it a non-initialize request gets 400 (`:486-495`);
  - header `Mcp-Method: <method>` (`ValidateMcpHeaders`, `:1019-1042`);
  - header `Mcp-Name: <tool>` on `tools/call` (`:1061-1107`);
  - a body `params` object whose `_meta["io.modelcontextprotocol/protocolVersion"]` equals the header (`:956-978`, `:939`)
    and whose `_meta["io.modelcontextprotocol/clientCapabilities"]` is a JSON object (`:786-796`); keys in
    `MetaKeys.cs:16`, `:45`.
  Today's `tools/list` snippet sends no `params` at all, so it would get 400 on 2026-07-28.

### Step 2 - Shared tool collection and session mode on the HTTP host
- Files: `RoslynSentinel.Server.Advanced/McpHttpTransportSetup.cs` (new), `RoslynSentinel.Server.Advanced/ServerHttp.cs`.
- Depends on: none.
- Effect: behaviour-changing for HTTP only: initialize-handshake clients get `Mcp-Session-Id` and `tools/list_changed`;
  toggles and `--toolsets` take effect for every request. stdio is untouched (it never calls this helper).
- Change:
  - New `public static class McpHttpTransportSetup` with `public static void Configure(HttpServerTransportOptions options)`:
    sets `options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients` and
    `options.ConfigureSessionOptions = (context, sessionOptions, _) => { sessionOptions.ToolCollection =
    context.RequestServices.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection; return Task.CompletedTask; }`.
    Doc comment: why (SDK builds fresh options per request and, with this hook set, per stateful session; sharing the
    collection keeps `ToolsetService` changes and `Changed`-driven notifications live), link this plan.
  - `ServerHttp.cs:91`: `.WithHttpTransport(o => o.Stateless = true)` becomes `.WithHttpTransport(McpHttpTransportSetup.Configure)`.
- Done when: `Build` 0 errors. Lane: `implementer` D1 (2 files, ~3 edits).

### Step 3 - HTTP regression tests
- Files: `RoslynSentinel.Tests.Server/HttpToolsetEnablementTests.cs` (new).
- Depends on: 2. Pairs with: Step 4 (disjoint projects' files).
- Effect: text-neutral.
- Change: an in-process `WebApplication` on a loopback ephemeral port, configured like `ServerHttp` (claude-lean mode,
  `AddRoslynSentinelHostOptions(..., HostTransport.Http, ...)` - without it `AddRoslynSentinelEnginesBasic` TryAdds the
  default `Transport = Stdio` (`EngineServiceCollectionExtensionsBasic.cs:32`) and Step 4's "none" branch goes untested -
  `AddRoslynSentinelToolsAdvanced`, `WithHttpTransport(McpHttpTransportSetup.Configure)`, `MapMcp("/mcp")`), client
  `HttpClientTransport` (the first HTTP MCP client in the repo). Tests:
  - `LegacyClient_GetsSessionAndListChanged_AfterEnable`: client `ProtocolVersion = "2025-11-25"`; enable `declarations`;
    one `tools/list_changed` arrives; `ListToolsAsync` contains `TypedMember`.
  - `PerRequestClient_SeesEnabledToolsOnNextList`: client on the SDK default (2026-07-28); enable `declarations`, then a
    separate `ListToolsAsync` contains `TypedMember`; disable, and it is gone.
  - `StartupToolsets_AppearForPerRequestClient`: host started with `--toolsets=declarations`
    (`ServerStartupHelpers.ApplyStartupToolsets`); first `ListToolsAsync` contains `TypedMember`.
  Reuse fixture helpers from `McpToolsetControlTests.LiveServer` where they fit; do not change that class.
- Done when: the dispatcher's run shows the three tests green on Step 2's commit (and all three red on a86b5afd, if run
  there: under `Stateless` the legacy client gets no `list_changed` either). Lane: `test-writer` (new fixture design: escalate to `test-writer-senior` at once if ASP.NET Core
  hosting from Tests.Server does not compile).

### Step 4 - Direct notification for unsubscribed 2026-07-28 clients, and delivery-path status text
- Files: `RoslynSentinel.Tools.Basic/ToolsetControlTools.cs`, `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`.
- Depends on: none (uses `ToolsetService.ToolListSubscriptionRequests`, landed in 014e4110).
- Effect: behaviour-changing: a 2026-07-28 stdio client without a tool-list subscription receives one
  `tools/list_changed` per real change; `StatusMessage` text changes.
- Change:
  - `ToolsetControlTools` constructor (currently `ToolsetService` only; built by DI, no direct `new` in source or tests)
    also takes `SentinelHostOptions hostOptions` (`RoslynSentinel.Common/SentinelHostOptions.cs:38`).
  - `McpToolsetControlTool` becomes `async Task<SentinelCallToolResult<object>>` and gains a trailing
    `RequestContext<CallToolRequestParams>? requestContext = null` (same pattern as `ServerStatusTools.cs:63`). Current
    signature: `public SentinelCallToolResult<object> McpToolsetControlTool(ToolCallReason reason, ToolSetName toolSet,
    bool enabled, CancellationToken cancellationToken = default)`. No direct callers in source or tests (calls go through MCP).
  - After a change with added+removed > 0: `version = requestContext?.JsonRpcRequest.Context?.ProtocolVersion ??
    requestContext?.Server.NegotiatedProtocolVersion`; `perRequest = version is not null &&
    string.CompareOrdinal(version, "2026-07-28") >= 0`. Delivery path, evaluated in this order (transport first: on
    HTTP per-request `HasStatefulTransport` is false and the SDK does not fan out, `McpServerImpl.cs:2103`):
    - not `perRequest`: "sdk" (the SDK broadcast already went out on a stateful transport).
    - `perRequest` and `hostOptions.Transport == HostTransport.Http`: "none" (a per-request HTTP exchange cannot carry an
      unsolicited notification), whatever the subscription counter says.
    - `perRequest`, stdio, `ToolListSubscriptionRequests > 0`: "sdk" (the SDK fans out to the subscription).
    - `perRequest`, stdio, `ToolListSubscriptionRequests == 0`: send `NotificationMethods.ToolListChangedNotification`
      via `requestContext.Server.SendNotificationAsync` (declared on `McpSession`, `McpSession.Methods.cs:98`); "direct"
      on success, "none" if it throws (catch and log; never fail the tool, never report "Sent" for a failed send).
      Comment the SEP-2575 deviation and why.
    - Known limit, state it in a code comment: `ToolListSubscriptionRequests` only increments
      (`RecordToolListSubscriptionRequest`), so after a client's subscription stream closes neither path notifies.
  - Replace the unconditional sentence ("A server started by the launcher saves ... needs a /mcp reconnect ...") with
    facts per path, roughly: sdk/direct - "Sent notifications/tools/list_changed; a client that does not re-list on it
    still sees the change after a /mcp reconnect."; none - "This connection cannot receive notifications/tools/list_changed;
    tools/list shows the change." Keep the tab-state persistence sentence. No policy wording
    (`reference_tool_error_text_facts_options_policy.md`). Status text is not schema text (no budget impact).
  - Tests in `McpToolsetControlTests`: `Lean_ClientOn20260728_GetsListChangedOnlyOverASubscription` (line 557) asserts
    no notification without a subscription; split it into `Lean_ClientOn20260728_WithoutSubscription_GetsOneDirectListChanged`
    and `Lean_ClientOn20260728_WithSubscription_GetsExactlyOneListChanged` (no duplicate from the direct path; it must
    open the subscription before the first toggle, unlike the current test at its l.563, or it sees a direct send).
    Update the doc comments that name the old test (`McpToolsetControlTests.cs` ~:330 and :406), and rename or re-scope
    `Enable_StatusMessage_TellsNonRefreshingClientsToReconnect` (:610): it pins `Does.Contain("/mcp")`, which holds only
    for the sdk/direct text.
- Done when: `Build` 0 errors; dispatcher runs `McpToolsetControlTests`. Lane: `implementer` D1 (2 files).

### Step 5 - LaunchTestInstance snippets work against the session mode
- Files: `RoslynSentinel.Engines.Basic/TestInstanceLauncher.cs`, `RoslynSentinel.Tests.Tools.Basic/TestInstanceLauncherTests.cs`.
- Depends on: 1, 2.
- Effect: behaviour-changing: `BuildHowToCall` (line 735) snippets carry the request shape from Step 1, so a session-less
  `tools/list`/`tools/call` is served statelessly instead of refused for a missing session. `InitializeBody` (line 297,
  readiness probe, protocol `2025-06-18`) keeps working: it now opens a session, which is harmless.
- Change: add the Step 1 headers (`MCP-Protocol-Version`, `Mcp-Method`, and `Mcp-Name` on the call snippet) and body
  changes (a `params` object with the two `_meta` keys) to both snippets; single-quoted PowerShell keys keep the existing
  "no double quotes or backslashes" test rule satisfiable. Update the `BuildHowToCall` doc comment ("stateless Streamable
  HTTP") and `BuildHowToCall_CarriesUrlBothAcceptValuesContentTypeSseRecipeAndStopHint` (TestInstanceLauncherTests.cs:267).
- Done when: `Build` 0 errors; dispatcher runs `TestInstanceLauncherTests`. Lane: `implementer` D1.

### Step 6 - list_changed after every launcher start (closes TODO.md case D)
- Files: `RoslynSentinel.Server.Advanced/ServiceRegistrationExtensionsBasic.cs`, `RoslynSentinel.Tests.Server/McpToolsetControlTests.cs`.
- Depends on: 4 (same test file).
- Effect: behaviour-changing: after `StopServer(keepToolsets: false)` the client drops the removed tools.
- Change: in `AddRestoredToolsetListChangedMessageFilter`, replace the `RestoredAtStartup != true` condition with "a
  `TabToolsetStateStore` is registered" (launcher start; verify it is absent for non-launcher starts), resolve the
  `// TODO Send after every launcher-started ...` marker, and rename the method to `AddLauncherStartListChangedMessageFilter`
  (one caller, line 94). Flip `PlainStart_SendsNone` (McpToolsetControlTests.cs ~:535-555: registers a
  `TabToolsetStateStore` with `RestoredAtStartup = false` and asserts zero `list_changed`; under the new condition it
  gets one) into a launcher-start case that expects exactly one, and keep a non-launcher start (no store) asserting none.
  Update `RestoredStart_SendsOneToolListChangedAfterInitialized` if its wording assumes restore. Remove the duplicate
  READY item at `docs/current/TODO.md:43` when this lands.
  - Known limit, state it in a code comment: the filter fires on the `initialized` notification, which the SDK rejects on
    2026-07-28 (`ValidateNotificationBoundary`, `McpServerImpl.cs:435-443`), so a stdio client that stays on 2026-07-28
    gets no post-start `list_changed` from this filter.
- Done when: `Build` 0 errors; dispatcher runs `McpToolsetControlTests` including the flipped `PlainStart_*` case.
  Lane: `implementer` D1.

### Step 7 - Guidance and records
- Files: `CLAUDE.md` ("Gated tools" paragraph), `docs/current/blockers/blocking_error_gitremote_not_callable_after_toolset_enable.md`
  (close with the 2026-11-23 probe evidence, move to `closed/`), `docs/current/TODO.md` (case D line at :43; the
  plan's label at :39 still says "DRAFT, reopened" - update it to this plan's state). INBOX.md has no toolset or
  `list_changed` lines today, so it needs no change unless one is added meanwhile.
- Depends on: 4, 6.
- Effect: none (docs).
- Change: CLAUDE.md: after `McpToolsetControl`, the tool appears through ToolSearch; if it does not, report
  `McpServerStatus` `clientProtocolVersion` and `toolListSubscriptionRequests`, then `/mcp` reconnect. Note that custom
  subagents with an explicit `tools:` list never see toolset tools. CLAUDE.md is already over its 15 KB budget (15,791
  bytes on 2026-11-23): the edit must not grow it net; trim or move text to a reference doc to pay for it.
- Done when: CLAUDE.md byte size is not larger than before the step (`scripts/Test-GuidanceHygiene.ps1` only prints
  sizes; it does not fail on them). Lane: main session.

### Step 8 - LIVE verification
- Files: none.
- Depends on: 2-7 committed; restart.
- Effect: none.
- Change and outcome classes (record each with the date here):
  - **A. stdio tab, enable then ToolSearch:** `visible` | `visible-after-reconnect` | `not-visible`; record
    `clientProtocolVersion` and `toolListSubscriptionRequests`.
  - **B. HTTP legacy initialize (LaunchTestInstance):** `session-id-present` | `absent`.
  - **C. HTTP per-request request (Step 1 shape):** `served-stateless` | `refused`.
  - **D. HTTP enable, then `tools/list` in a later request:** `listed` | `missing`.
  - **E. howToCall snippets as returned by LaunchTestInstance:** `work` | `fail`.
  - **F. stdio on 2026-07-28 (only if a client can be made to stay on it, e.g. an SDK client):** `direct-received` |
    `subscribed-received` | `none`.
  - **G. A real Claude Code client over HTTP:** `session` (it used initialize and gets pushes) | `per-request-no-push`
    (it stayed on 2026-07-28; see the Decision 2 Risk).
- Done when: A-E and G recorded (F if reachable).

## Out of scope

- Per-client toolset isolation over HTTP (needs a client key; shared state accepted).
- A custom `SubscriptionsListenHandler` for stateless 2026-07-28 HTTP clients.
- Why Claude Code falls back to 2025-11-25 after `server/discover`; the counter and log lines from 014e4110 make a later
  investigation possible (capturing the `server/discover` response is the next diagnostic if needed).
- Changing the `claude-lean` allow-list or stdio transport setup.

## Risks and open decisions

- **Cross-plan conflict: DECIDED (owner, 2026-11-23): this plan lands first; the JSON toolsets plan dropped its HTTP
  fold and now depends on this plan's Step 2.** Background: `plan_json_toolset_definitions.md` recorded an owner ruling of the same
  day that HTTP folds groups at startup and registers no `McpToolsetControl`/`ToolsetService` (its Decision bullet
  "HTTP folds groups at startup", a step stating "On HTTP the server has no `ToolsetService`", live case D), and its
  "Out of scope" describes this plan as the old 2026-10-11 design. A note at the top of that plan now flags this. This plan keeps
  runtime toolsets live over HTTP. Both also edit `ServerHttp.cs`, `ServiceRegistrationExtensionsBasic.cs` and
  `ToolsetControlTools.cs`. Today `ToolsetService` is registered on HTTP in claude-lean
  (`ServiceRegistrationExtensionsBasic.cs:414-420`), so this plan's assumptions hold now. Recommendation (2026-11-23):
  this plan lands first and the JSON plan drops "HTTP folds"; `proposal_tool_surface_controller.md` also assumes the
  shared live collection.
- **Decision 2 may miss the target client over HTTP.** `StatefulForInitializeClients` serves a 2026-07-28 request
  statelessly (`StreamableHttpHandler.cs:477-478`), so a client that probes 2026-07-28 first gets no session and no
  push. `HttpServerSessionMode.Stateful` refuses 2026-07-28 instead, so a dual-path client falls back to `initialize`
  (`:469-474`). The fallback evidence in Problem is stdio only. Live case G decides; if it records
  `per-request-no-push`, switch to `Stateful`.
- **Step 1 answered:** the request shape needs headers and a `params._meta` body (Step 1), so Step 5's snippets grow
  by both; the fallback, if that proves impractical, is snippets that initialize first and replay `Mcp-Session-Id`.
- **Direct notification acceptance:** whether Claude Code acts on an unsolicited `tools/list_changed` while on
  2026-07-28 is unverified (case F). The send is harmless if ignored.
- **ASP.NET Core in Tests.Server (Step 3):** Tests.Server already references Server.Advanced, which carries
  `ModelContextProtocol.AspNetCore`, so the framework reference probably flows transitively (hypothesis, unbuilt); a
  `FrameworkReference` to `Microsoft.AspNetCore.App` in the test `.csproj` is in Step 3's scope if it does not.
- **"Sent" on the stateful-HTTP sdk path** is true only if the client holds a GET SSE stream; the SDK fires
  `SendListChangedNotificationAsync` without confirming delivery (`McpServerImpl.cs:128`).
- **SDK coupling:** sharing the collection relies on `ConfigureSessionOptions` running after `McpServerOptionsSetup`
  (it does in v2.4.0: the factory runs first, the hook second, `StreamableHttpHandler.cs:588-602`). Re-check on SDK upgrades.
- **Other HTTP clients:** none exist. AgentLoop, PlanStepRunner and SubAgent use `StdioClientTransport`
  (`SubAgentChildServerLauncher.cs:130`, PlanStepRunner `Program.cs:196`); no `HttpClientTransport` appears anywhere in
  the repo, tests included. Step 3 adds the first, so the HTTP change affects only external clients and
  `LaunchTestInstance` snippets.
