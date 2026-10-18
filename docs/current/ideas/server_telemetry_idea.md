# Idea: server-side telemetry for tool-call counts and error rates

Status: **raised, not implemented**. Filed 2026-09-05.

## Context

Every cross-run tool-choice/error-rate finding so far (e.g. the `ReplaceBlockFormatted`
accessibility-cost analysis) has required a bespoke offline aggregation pass over saved
`agent.log`/`transcript.json` files — and those only exist for model-eval batches. Real interactive
sessions have no equivalent saved transcript to mine at all. A dedicated log-parsing script had to
be built specifically because this data didn't exist anywhere live.

## The idea

Add telemetry/metrics to the MCP server itself: live counts of tool calls and error rates per tool,
rather than only being reconstructable after the fact from archived logs. This would surface the
same signal continuously, for any session — not just instrumented eval batches.

## Hook point (confirmed, not yet implemented)

The MCP call-tool filter chain in `RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s
`AddRoslynSentinelToolsBasic` (~line 167-406) is the single place every tool call already passes
through regardless of server flavor, and already has 5 filters in exactly the shape a metrics
filter needs (`filters.AddCallToolFilter(next => new McpRequestHandler<...>(async (context, ct) =>
{ var result = await next(context, ct); ...; return result; }))`).

A metrics filter must be registered **after** the existing "domain-failure → protocol-error sync"
filter, so it counts the corrected `IsError` (including tools that return `Success=false` instead
of throwing), and should count the orientation breaker's own pre-check short-circuit as a failed
call too.

## Open questions

- **Exposure mechanism**: a new read-only tool, or extending `GetWorkspaceHealth`/
  `GetComprehensiveHealthReport`, to surface current counts.
- **Persistence**: whether counts should persist across server restarts, reset per-session, or
  both.

Neither is decided. Revisit both before starting any related work.

## Moved from TODO.md (2026-10-17)

### Feature idea: server-side telemetry/metrics for tool call counts and error rates — open, unactioned

**Found:** 2026-09-05, raised by Andrew while reviewing the `ReplaceBlockFormatted` finding above,
which required an offline post-hoc `Parse-AgentLog.ps1` aggregation pass over archived transcripts
to get per-tool call/error-rate numbers.

**What:** the MCP server itself has no live counters for how often each tool is called, or its
success/error rate — this data currently only exists reconstructible after the fact from
`agent.log`/`transcript.json` files (model-eval only) or not at all (real interactive/production
sessions have no equivalent transcript to mine). A lightweight in-process metrics layer (e.g. a
`ToolCallCount`/`ToolErrorCount` counter per tool name, exposed via a new read-only tool or a
`/metrics`-style endpoint) would give live visibility into tool usage and failure rates across
*any* session — model-eval or a real agent — not just ones that happen to have a saved transcript
to parse afterward.

**Why this matters:** the `ReplaceBlockFormatted` finding above and a separate tool-choice-fidelity
review both needed a bespoke offline analysis pass (grep/parse a log, or hand-review transcripts)
to surface tool-choice and error-rate patterns that live counters would expose immediately and
continuously, for every session, not just archived model-eval batches with saved logs.

**Hook point confirmed 2026-09-05 (not yet implemented):** the MCP call-tool filter chain in
`RoslynSentinel.Server.Basic/ServiceRegistrationExtensionsBasic.cs`'s
`AddRoslynSentinelToolsBasic` (`mcpBuilder.WithRequestFilters(...)`, ~line 167-406) is the single
place every tool call already passes through regardless of server flavor (Advanced reuses Basic's
registration — see `project_advanced_extends_basic`), and already has 5 filters following the
exact shape a metrics filter would need: `filters.AddCallToolFilter(next => new
McpRequestHandler<CallToolRequestParams, CallToolResult>(async (context, ct) => { var result =
await next(context, ct); try { /* logic */ } catch (Exception ex) { Debug.WriteLine(...); }
return result; }))`, with `context.Params?.Name` for the tool name and `context.Server.Services?
.GetService<T>()` already the established way to resolve a shared singleton (used today for
`PersistentWorkspaceManager`/`ILogger<T>`).

**Placement matters**: a metrics filter should be registered *after* the "domain-failure →
protocol-error sync" filter (ends ~line 245) — that's the one that sets `result.IsError = true`
for tools that catch their own exceptions and return `isError: true` instead of throwing (see
`feedback_agent_friendly_error_messages`), so counting before it would undercount real failures
the same way raw exception-based `IsError` detection already does. It should also count the
orientation breaker's own pre-check short-circuit (~line 342-350, which returns before ever
calling `next()`) as a real failed invocation of whatever tool was blocked, not skip it.

**Suggested approach:** a `ConcurrentDictionary<string, ToolCallStats>` (call count, error count,
maybe total duration) singleton, registered in DI and updated inside the new filter exactly as
described above, plus a new read-only tool (or extend `GetWorkspaceHealth`/
`GetComprehensiveHealthReport`) to surface current counts. Whether counts should persist across
server restarts, reset per-session, or both is an open design question — not decided here.
See also `docs/current/reference_parse_agent_log_script.md` and
`docs/current/model_eval_replaceblockformatted_accessibility_cost_2026_09_05.md` for the concrete
analysis gap that prompted this idea.

