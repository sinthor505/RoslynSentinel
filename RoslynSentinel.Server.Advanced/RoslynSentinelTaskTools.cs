using System.Collections.Frozen;

using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Selects which MCP tool calls are eligible to run as MCP Tasks (background execution with a
/// pollable taskId handle) rather than blocking the caller until the tool completes.
/// </summary>
/// <remarks>
/// Shared between <see cref="ServerStdio"/> and <see cref="ServerHttp"/> so the set of task-eligible
/// tools stays in one place. Adding a new long-running tool to task support is a one-line change here.
/// </remarks>
public static class RoslynSentinelTaskTools
{
    /// <summary>Tool names that may run as a task when the calling client declares the tasks capability.</summary>
    public static readonly FrozenSet<string> Names = new[] { "Asyncify", "AsyncifyLoop", "BulkComment" }
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Execution-mode selector for <see cref="McpTasksOptions.ExecutionModeSelector"/>: task-eligible
    /// tools run as <see cref="McpTaskExecutionMode.Optional"/> tasks; everything else stays synchronous.
    /// </summary>
    public static McpTaskExecutionMode SelectExecutionMode(RequestContext<CallToolRequestParams> request) =>
        request.Params?.Name is { } name && Names.Contains(name)
            ? McpTaskExecutionMode.Optional
            : McpTaskExecutionMode.Synchronous;
}
