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

public static class LargeResultOffloadFilter
{
    internal static void AddLargeResultOffloadFilter(IMcpRequestFilterBuilder filters)
    {

        // Generic large-result offload backstop (docs/current/references/reference_tool_response_pipeline.md;
        // origin: docs/current/proposals/closed/proposal_centralized_large_result_filter.md): sums the length of text
        // content blocks in the response (no JSON re-serialization) and, above
        // LargeResultHelper.InlineBudgetChars, writes the response text to disk as a ResultWrapperType.Envelope
        // via LargeResultHelper.StoreEnvelopeAsync (the type the marshaller stores, so GetLargeResult pages it with
        // the Envelope reader: inferred path, offset/limit) and replaces the response with a small pointer
        // (resultId) instead of just logging. This is deliberately shape-agnostic: the result marshaller
        // does the offloading, so this only catches text it did not cut (an exception catch-all or refusal body,
        // plain string or content-block returns, non-object JSON, nothing pageable, or a marshaller set to off or
        // passthrough). A result the marshaller fitted has a small response by the time it reaches here and this
        // filter is a no-op for it. A body that is not JSON cannot be stored as an Envelope (StoreEnvelopeAsync
        // parses it); the failure is swallowed below and the body goes out whole, as before.
        // Grep the log for "Large tool result" / "Result marshaller miss" to review offenders without parsing full payloads.
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            async (context, cancellationToken) =>
            {
                // The marshaller raises this when it returns a response over budget on purpose (proposal_sequence_pager.md
                // Decided 4: one unit larger than a page goes out alone). Re-wrapping that under a new resultId would hand the
                // model a second pointer next to the marshaller's own (or replace a result with nothing left to page), so the
                // text passes through; the marshaller has already written the contract violation to the server log.
                using var oversizeSignal = OversizeUnitSignal.Open();
                var result = await next(context, cancellationToken);
                if (oversizeSignal.Raised)
                {
                    return result;
                }

                // No tool-name exemption for GetLargeResult: its Envelope readers (WorkspaceReadNavigationImpl.PageSequence and
                // PageEnvelope) build their hints with PagingHints.ForAlreadyPagedPage, which raises OversizeUnitSignal for the
                // current call while the page is built. That happens in the tool, so it holds in every marshaller mode (mode off
                // registers no marshaller) and after a marshaller exception; a page that one oversize unit leaves over budget is
                // caught by the check above and never re-stored under a new resultId. Pinned by
                // LargeResultOffloadFilterTests.GetLargeResult_OversizeUnitPage_IsNotStoredAgain (mode off) and its ModeOn twin.
                try
                {
                    if (result.Content is null)
                    {
                        return result;
                    }

                    foreach (var block in result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().ToList())
                    {
                        var text = block.Text;
                        if (string.IsNullOrEmpty(text) ||
                            text.Length <= LargeResultHelper.InlineBudgetChars)
                        {
                            continue;
                        }

                        var logger = context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>();
                        // Result marshaller miss detector (plan_wire_result_marshaller.md Step 4, Step 10 slice D1): in mode On a tool
                        // built through SentinelToolFactory (SentinelToolFactory.IsHooked) is marshalled, so a body that still
                        // arrives here over the threshold is a body the marshaller never fitted: an exception catch-all or refusal
                        // body, a result it handed back whole (non-object JSON root, plain string or content-block return, nothing
                        // pageable), or a pager/reserve bug (a fitted result leaves the pointer and echo reserve free). Each is an
                        // error-level signal; the body is still stored below as the backstop. In mode Off or Passthrough the
                        // marshaller is deliberately not cutting, so the original warning is all that is logged.
                        if (ResultMarshallerOptions.Mode == ResultMarshallerMode.On && SentinelToolFactory.IsHooked(context.Params?.Name))
                        {
                            logger?.LogError(
                                "Result marshaller miss: hooked tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdBytes}) the marshaller did not cut; the offload filter is handling it as the backstop. Check ResultPager hints and LargeResultHelper.EchoReserveChars.",
                                context.Params?.Name, text.Length, LargeResultHelper.InlineBudgetChars);
                        }
                        else
                        {
                            logger?.LogWarning(
                                "Large tool result: tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdBytes})",
                                context.Params?.Name, text.Length, LargeResultHelper.InlineBudgetChars);
                        }

                        var workspaceManager = context.Server.Services?.GetService<PersistentWorkspaceManager>();
                        var solutionRoot = workspaceManager?.GetSolutionRoot();
                        var stored = await LargeResultHelper.StoreEnvelopeAsync(text, solutionRoot, cancellationToken);
                        if (!stored.offloaded)
                        {
                            // No solution loaded, or the write failed to qualify -> fail closed to
                            // pass-through rather than blocking the call on a guardrail defect.
                            continue;
                        }

                        // Best-effort size hint so the pointer alone tells the model whether this
                        // is 1 big hit or 300 small ones, instead of forcing a GetLargeResultTool round
                        // trip just to find out. Shape-agnostic by design (this filter runs for
                        // every tool, typed and untyped alike) -> parsed straight from the raw JSON
                        // rather than any tool-specific DTO. itemCount looks for the envelope's own
                        // totalRecords first (set by ForPossiblyLargeDataAsync callers), then falls
                        // back to LargeResultHelper.CountResultItems, which sums every array found
                        // under successData (recursing through nested objects) - covers untyped
                        // tools like FindReferencesTool that set SuccessData directly.
                        // statusMessage is relayed whenever the tool already populated one (e.g.
                        // FindReferencesTool/SearchSolutionTextTool's SummarizeListResult-based summary) so
                        // that hint survives the offload instead of being silently dropped.
                        int? itemCount = null;
                        string? statusMessage = null;
                        System.Text.Json.Nodes.JsonNode? listSummary = null;
                        bool? isError = null;
                        try
                        {
                            var root = System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject();
                            if (root != null)
                            {
                                if (root.TryGetPropertyValue("isError", out var isErrorNode) &&
                                    isErrorNode != null &&
                                    (isErrorNode.GetValueKind() == System.Text.Json.JsonValueKind.True ||
                                     isErrorNode.GetValueKind() == System.Text.Json.JsonValueKind.False))
                                {
                                    isError = isErrorNode.GetValue<bool>();
                                }

                                // listSummary (per-file counts, already capped by SummarizeListResult) is
                                // the only per-file hint once a list tool's statusMessage stops repeating
                                // the file paths, and a raw offload drops it unless it is relayed here.
                                if (root.TryGetPropertyValue("listSummary", out var listSummaryNode) &&
                                    listSummaryNode is System.Text.Json.Nodes.JsonObject)
                                {
                                    listSummary = listSummaryNode;
                                }

                                if (root.TryGetPropertyValue("totalRecords", out var totalRecordsNode) &&
                                    totalRecordsNode != null &&
                                    totalRecordsNode.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                                {
                                    itemCount = totalRecordsNode.GetValue<int>();
                                }
                                else if (root.TryGetPropertyValue("successData", out var successDataNode) && successDataNode != null)
                                {
                                    itemCount = LargeResultHelper.CountResultItems(successDataNode);
                                }

                                if (root.TryGetPropertyValue("statusMessage", out var statusMessageNode) &&
                                    statusMessageNode != null &&
                                    statusMessageNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
                                {
                                    statusMessage = statusMessageNode.GetValue<string>();
                                }
                            }
                        }
                        catch (Exception parseEx)
                        {
                            // Best-effort only - never let a parse quirk in some other tool's shape
                            // block the offload itself, just fall back to no hint.
                            Debug.WriteLine($"Large result offload size-hint parse failed: {parseEx}");
                        }

                        var hint = itemCount is int n ? $" Result contains {n} item(s)." : "";

                        // Preserve the original body's isError onto the protocol-level IsError
                        // flag: this filter overwrites result.Content below, so the IsError-sync
                        // filter (which wraps this one and runs after it returns) would otherwise
                        // inspect this offload envelope instead of the real body and find no
                        // "isError" key, silently leaving IsError at its prior (successful)
                        // value even when the original tool call failed.
                        if (isError == true)
                        {
                            result.IsError = true;
                        }

                        result.Content = [new ModelContextProtocol.Protocol.TextContentBlock
                            {
                                Text = System.Text.Json.JsonSerializer.Serialize(new
                                {
                                    solutionRoot,
                                    offloaded = true,
                                    resultId = stored.resultId,
                                    sizeBytes = text.Length,
                                    sizeChars = text.Length,
                                    itemCount,
                                    isError,
                                    statusMessage,
                                    listSummary,
                                    message = $"Result is {text.Length} bytes (threshold: {LargeResultHelper.InlineBudgetChars}).{hint} Use {RoslynSentinel.Common.LargeResultHints.FetchCall(stored.resultId!)} to page through results."
                                }, RoslynSentinel.Common.SharedJsonOptions.Compact)
                            }];
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Large result offload filter failed: {ex}");
                }

                return result;
            }));
    }
}
