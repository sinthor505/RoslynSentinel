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
