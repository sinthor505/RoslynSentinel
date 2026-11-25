using System.Diagnostics;
using System.Threading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;
using System.Text.Json.Nodes;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Call-tool filters for the breakers that refuse tool calls once a session is halted.
/// Public because Server.Advanced has no InternalsVisibleTo for its tests (the repo's existing pattern).
/// </summary>
public static class BreakerFilters
{
    public static void AddUnrecoverableBreakerFilter(IMcpRequestFilterBuilder filters)
    {

        // Unrecoverable breaker: a server-integrity fault (currently a failed operation-blob
        // write, meaning a change landed on disk with no undo record) halts the session for
        // good. See IUnrecoverableBreaker for why this is not IManualCircuitBreaker and has no
        // reset. Registered as its own filter rather than folded into the orientation-breaker
        // one above so neither can mask the other's message.
        //
        // The authoritative enforcement is in PersistentWorkspaceManager.ApplyProposedChangesAsync
        // -> the write chokepoint, which no mutating tool can bypass. This filter exists so the
        // refusal also arrives as a protocol-level IsError carrying the specific diagnostic,
        // rather than only as a per-tool error, and so tools that would do expensive analysis
        // before their first write fail fast.
        //
        // Allowlist rather than a list of mutating tools: a deny-list would silently omit any
        // tool added later, which is the same forgotten-call-site mode that produced this
        // defect. Anything not in the allow-list is refused, so the safe default is "refused".
        // The allow-list is defined by [UnrecoverableBreaker(Allowed)] attributes on tool methods.
        // These are the tools an operator or agent needs to read the state and stop cleanly.
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                try
                {
                    IUnrecoverableBreaker? breaker = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                    var toolName = context.Params?.Name;

                    if (breaker is not null && breaker.IsTripped() &&
                        !UnrecoverableBreakerPolicy.IsAllowed(toolName))
                    {
                        return new ModelContextProtocol.Protocol.CallToolResult
                        {
                            Content = [new ModelContextProtocol.Protocol.TextContentBlock
                                {
                                    Text = breaker.StateMessage()
                                        ?? "The server recorded an unrecoverable integrity failure. This session cannot continue. Stop and report to the user/operator."
                                }],
                            IsError = true,
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Unrecoverable breaker pre-check failed: {ex}");
                }

                return await next(context, cancellationToken);
            }));
    }
}
