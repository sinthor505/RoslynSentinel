# Per-tool-call correlation id (server logs joined to a specific call, not a timestamp)

## Motivation

`RunId`/`StepId` (this session's Tier 1 work — see `ServerStartupHelpers.cs`'s `ParseRunId`/
`ParseStepId` and the `[Run=... Step=...]` Serilog enrichment) let a server log line be attributed
to the right *run* and *step* without inferring from timestamps. They do not, and were not meant to,
attribute a line to the right *tool call within that step*. Today that join is still timestamp-based:
find the agent.log "calling X" / "X succeeded" pair, read off its wall-clock window, then grep the
server log for lines falling inside it. That already requires clock-sync care across processes, and
it becomes ambiguous the moment two tool calls' windows overlap in the same step.

Confirmed there is nothing to build on: `grep -rn "CorrelationId\|RequestId\|TraceId\|BeginScope\|
PushProperty"` across `RoslynSentinel.Server.Basic`, `RoslynSentinel.Server.Advanced`, and
`RoslynSentinel.Common` returns exactly one hit — `FlushingFileLoggerProvider.BeginScope<TState>` in
`RoslynSentinel.Tests.ModelEval`, and it's a no-op (`=> null`, scopes never propagate into agent.log
either). No request-scoped identifier of any kind reaches a server log line today.

The MCP call loop in `ModelAgentRunner`/PlanStepRunner is sequential — one tool call in flight at a
time — so the ambiguity this proposal removes doesn't bite *today*. It breaks the moment tool calls
run concurrently (parallel steps, a future fan-out inside one step, or just two PlanStepRunner hosts
sharing a log window), and cross-run grepping across the parallel LM Studio hosts (112/113) is
already the painful case in practice even under today's sequential-per-run model, because two runs'
timestamp windows can and do interleave in a merged grep. This proposal is that fix, scoped
narrowly: it is not being built now because Tier 1's RunId/StepId already answers the run/step
question, which was the more urgent gap and covers the common case (one call in flight per step).

## What Tier 1 already makes unnecessary

- Cross-run and cross-step attribution: solved. A server log line's `[Run=... Step=...]` fields are
  now unambiguous without this proposal.
- `ToolCallId` (the model's own tool-call id, persisted onto `AgentToolCallRecord` in this session's
  Tier 1 work) already lets a transcript reader pair a recorded tool call with its result. What it
  does *not* do is reach the server — it's never sent over MCP, so it cannot appear in a server log
  line. This proposal is the piece that would carry an id of that kind (or a new one minted for the
  purpose) across the wire.

## Proposal shape

A per-call id, minted by the runner immediately before each `CallToolAsync`, carried to the server as
protocol-level request metadata (not a tool argument), and opened as a logging scope on the server
for the duration of that one call so every line logged while handling it — including inside shared
engine code several layers below the MCP handler — carries it.

### How the id would travel: MCP request metadata (`_meta`), not a tool parameter

Investigated against the C# SDK clone at `...\repos\csharp-sdk`, checked out at tag `v2.2.0` to match
the installed `ModelContextProtocol`/`ModelContextProtocol.AspNetCore` NuGet version (`2.2.0`) exactly
rather than trusting `main` (HEAD there is a few commits ahead of the tag; none of those commits
touch request params or metadata, but the checkout was done to avoid relying on that judgment call).

Two real options exist:

**Rejected: an extra tool parameter.** Every `[McpServerTool]` method would need a
`callId`/`correlationId` parameter the model has to know to pass, or the server would need to
silently strip such a parameter from the agent-visible schema while still reading it off the wire —
either way it pollutes the tool-facing surface with something the model has no reason to reason
about. This directly conflicts with the repo's own standing direction of optimizing everything about
the tool surface for agent consumption (see e.g. `proposal_tool_error_code_taxonomy.md`'s objection to
an opaque encoding the model has to hold a decoder for). Ruled out.

**Adopted direction: `CallToolRequestParams.Meta` (`_meta`).** `RequestParams.Meta`
(`ModelContextProtocol.Protocol.RequestParams`, `src/ModelContextProtocol.Core/Protocol/
RequestParams.cs:25-26`) is a `JsonObject? _meta` on the base request type, explicitly documented as
"metadata reserved by MCP for protocol-level metadata" that "implementations must not make
assumptions about." `CallToolRequestParams : RequestParams` (`CallToolRequestParams.cs`) inherits it
directly — it's already there on every tool call, independent of `Arguments`. The SDK already uses
this exact field for its own protocol-level concern: `ProgressToken` is read back off `Meta["progressToken"]`
(`RequestParams.cs:58-77`), and `McpClient.CallToolAsync`'s progress-reporting path
(`Client/McpClient.Methods.cs:994-1029`) builds a `Meta` object merging `RequestOptions.
GetMetaForRequest()` with a progress token — i.e. there is already a precedent, in the same SDK, for
exactly "protocol-level per-call metadata the tool implementation never sees as an argument."

A correlation id would ride the same field, e.g. `_meta["correlationId"]`, populated via a
`RequestOptions` (or a new small helper alongside `GetMetaForRequest()`) passed into `CallToolAsync`.

**Client-side catch, confirmed by reading the actual call site:** `ModelAgentRunner.
ExecuteToolCallAsync` (`RoslynSentinel.Tests.ModelEval\AgentLoop\ModelAgentRunner.cs:380-385`) and
`TranscriptReplayTests.cs:230-234` both currently call the *convenience* overload
`CallToolAsync(toolName, arguments, progress: null, options: null, cancellationToken)`. Adopting
`_meta` means building a real `RequestOptions` there (or switching to the raw
`CallToolAsync(CallToolRequestParams, ...)` overload and setting `Meta` directly) instead of passing
`options: null` — a small, mechanically clear, single-call-site change, not an SDK change.

### Where the server would open the scope

`RoslynSentinel.Server.Basic\ServiceRegistrationExtensionsBasic.cs`'s `AddRoslynSentinelToolsBasic`
(~line 119 onward) already registers several `mcpBuilder.WithRequestFilters(filters =>
filters.AddCallToolFilter(...))` handlers — this is the exact chokepoint
`proposal_tool_error_code_taxonomy.md` names for the (separately proposed) metrics filter, and
`context.Params?.Name`/`context.Params?.Arguments` are already read there by existing filters (e.g.
line 347, 391, 414, 509). Each filter wraps `next(context, cancellationToken)`.

A correlation-scope filter would sit early in that chain and do, in shape:

```
var correlationId = context.Params?.Meta?["correlationId"]?.ToString();
using var _ = correlationId is not null
    ? logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId })
    : null;
return await next(context, cancellationToken);
```

For this to reach the output template the way `RunId`/`StepId` do today, Serilog's
`.Enrich.FromLogContext()` (already configured in `ServerStartupHelpers.cs` per Tier 1) needs the
`ILogger.BeginScope` call to actually flow into Serilog's `LogContext` — worth confirming
experimentally which logger instance the filter receives (the DI `ILogger<T>` wrapping Serilog via
`RegisterSerilogLoggerFactory`, presumably, but not yet traced end-to-end) before assuming
`BeginScope` here is equivalent to `Serilog.Context.LogContext.PushProperty`. Because the scope wraps
`next(...)` for the whole call, everything logged during tool execution — including shared engine
code several layers below the MCP handler, which never sees `context` at all — inherits it for free,
which is the entire point relative to threading an explicit parameter through every engine method.

### Output template

Same mechanism as `RunId`/`StepId`: add `[Call={CorrelationId}]` (or fold into the existing
`[Run={RunId} Step={StepId}]` bracket as a third field) to `ServerStartupHelpers.LogOutputTemplate`.
Falls back to a placeholder (`-`) when the property isn't in scope, exactly like `RunId ?? "-"` today,
so lines logged outside any call (startup, shutdown, crash handler) are unaffected.

## Alternatives considered, not pursued

### Request id (`JsonRpcRequest.Id`) as the correlation key

Every JSON-RPC request already carries a `RequestId` (string or int,
`src/ModelContextProtocol.Core/Protocol/RequestId.cs`), reachable server-side via
`RequestContext.JsonRpcRequest.Id`. It needs no client-side change at all — it already exists on the
wire. Rejected as the primary key because it's the *client library's* request numbering, not something
RoslynSentinel controls the shape or readability of (typically a bare monotonic int), and PlanStepRunner
doesn't currently read or log it on the agent side, so there'd be nothing in `transcript.json` or
agent.log to join it against — defeating the purpose. Worth revisiting only as a supplementary,
zero-cost field to log alongside a real correlation id, not as a replacement for one.

### Encoding the call id inside `RunId`/`StepId` instead of a new field

Considered folding a call counter into the existing `Step=` value (e.g. `Step=03-fix-types#7`).
Rejected: it overloads a field whose contract other tooling may come to depend on as "the step name,"
and it doesn't compose if a future need wants run/step/call queried independently (e.g. "every call
for this step across all runs"). A separate field is one more bracket in the template, not a reason to
conflate two identities that happen to currently correlate 1:1 in the sequential loop.

## Open questions (not verified, not asserted)

- **Whether `ILogger.BeginScope` in this SDK's DI pipeline actually reaches Serilog's
  `LogContext`.** Traced the filter chokepoint and the enrichment config independently but not the
  connection between them end-to-end; this needs a small experiment (log a marker property inside a
  `BeginScope` at the filter and confirm it appears in the file output) before relying on it.
- **Exact `RequestOptions`/`GetMetaForRequest()` shape for attaching arbitrary metadata** beyond the
  progress-token case the SDK already uses it for — confirmed the extension point exists and is used
  today, not yet confirmed the cleanest way to add a second key alongside a progress token if both are
  ever in play on the same call.
- **Id format**: a short human-typable token (mirroring this session's `RunId`/`StepId` choice of
  reusing an existing readable identifier over a GUID) vs. a GUID/short hash minted fresh per call.
  Per-call ids are far higher-volume than per-run ids, so brevity matters less for hand-typing and more
  for not needing a registry — a `Guid.NewGuid().ToString("N")[..8]`-style short random token, generated
  client-side per call, is the likely answer but not decided here.
- **Whether this also needs plumbing into `transcript.json`** (a third id alongside the existing
  `RunId` and `ToolCallId`) so the join is discoverable from the agent side without re-deriving it, or
  whether `ToolCallId` itself should simply become the value sent as `_meta["correlationId"]` — the
  latter would mean no new id concept at all, just carrying the id that already exists
  (`LmStudioAgentClient.cs:483`, persisted onto `AgentToolCallRecord` in this session's Tier 1 work)
  one hop further, over the wire instead of stopping at the transcript. Not decided; this doc treats
  them as possibly the same field rather than assuming a new one is needed.

## Cost / risk

- Touches: one client call site (`ModelAgentRunner.ExecuteToolCallAsync`, and `TranscriptReplayTests.cs`
  if it should also correlate), one new server-side request filter registration (additive, alongside
  the existing filters in `AddRoslynSentinelToolsBasic`), the shared `LogOutputTemplate` constant.
  Nothing in tool implementations changes.
- Additive on the wire: `_meta` is optional and ignored by any client/server that doesn't set or read
  it, so a server built with this change still serves clients that don't send a correlation id (falls
  back to `-` in the log, same as `RunId`/`StepId` do for a server started without `--run-id`).
- Main risk is the unverified `BeginScope`→Serilog connection above: if it doesn't flow automatically,
  the fallback is explicit `Serilog.Context.LogContext.PushProperty` in the filter instead of
  `ILogger.BeginScope`, which is a one-line change to the filter body, not a design change.

## Status

Design proposal only — not yet implemented. Depends on nothing further from Tier 1 (RunId/StepId are
complete); the SDK-side mechanism (`_meta`) is confirmed to exist and already used for an analogous
purpose (progress tokens), but the Serilog scope-propagation detail above is not yet verified
experimentally.
