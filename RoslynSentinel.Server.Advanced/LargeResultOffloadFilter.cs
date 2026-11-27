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
    /// <summary>Registers the generic large-result offload backstop; the logic is in <see cref="InvokeAsync"/>.</summary>
    internal static void AddLargeResultOffloadFilter(IMcpRequestFilterBuilder filters)
    {
        filters.AddCallToolFilter(next => new ModelContextProtocol.Server.McpRequestHandler<
            ModelContextProtocol.Protocol.CallToolRequestParams,
            ModelContextProtocol.Protocol.CallToolResult>(
            (context, cancellationToken) => InvokeAsync(
                context.Params?.Name,
                () => context.Server.Services?.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot(),
                context.Server.Services?.GetService<ILogger<PersistentWorkspaceManager>>(),
                () => next(context, cancellationToken),
                cancellationToken)));
    }

    /// <summary>
    /// Generic large-result offload backstop (docs/current/references/reference_tool_response_pipeline.md;
    /// origin: docs/current/proposals/closed/proposal_centralized_large_result_filter.md): sums the length of text
    /// content blocks in the response (no JSON re-serialization) and, above the offload threshold, writes the response
    /// text to disk as a ResultWrapperType.Envelope via LargeResultHelper.StoreEnvelopeAsync (the type the marshaller
    /// stores, so GetLargeResult pages it with the Envelope reader: inferred path, offset/limit) and replaces the
    /// response with a small OffloadPointerResult (resultId) instead of just logging.
    /// <para>
    /// The threshold is the one the marshaller recorded on InlineBudgetSignal for this call; with no signal value it
    /// is the Default class threshold, or the Whole class threshold when the body is an error-only failure.
    /// </para>
    /// <para>
    /// This is deliberately shape-agnostic: the result marshaller does the offloading, so this only catches text it did
    /// not cut (an exception catch-all or refusal body, plain string or content-block returns, non-object JSON, nothing
    /// pageable, or a marshaller set to off or passthrough). A result the marshaller fitted has a small response by the
    /// time it reaches here and this filter is a no-op for it. A body that is not JSON cannot be stored as an Envelope
    /// (StoreEnvelopeAsync parses it); the failure is swallowed and the body goes out whole, as before. Grep the log for
    /// "Large tool result" / "Result marshaller miss" to review offenders without parsing full payloads.
    /// </para>
    /// </summary>
    /// <param name="toolName">The called tool's name, used only for log wording and the marshaller-hook check.</param>
    /// <param name="solutionRoot">Lazy accessor for the loaded solution root (null when no solution is loaded).</param>
    /// <param name="logger">Logger for the miss and large-result lines; may be null.</param>
    /// <param name="next">The rest of the call-tool pipeline.</param>
    /// <param name="cancellationToken">Cancellation for the store write.</param>
    public static async ValueTask<CallToolResult> InvokeAsync(
        string? toolName,
        Func<string?> solutionRoot,
        ILogger? logger,
        Func<ValueTask<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        // The marshaller raises OversizeUnitSignal when it returns a response over budget on purpose (proposal_sequence_pager.md
        // Decided 4: one unit larger than a page goes out alone). Re-wrapping that under a new resultId would hand the
        // model a second pointer next to the marshaller's own (or replace a result with nothing left to page), so the
        // text passes through; the marshaller has already written the contract violation to the server log.
        using var oversizeSignal = OversizeUnitSignal.Open();
        // The marshaller records the threshold it resolved for the response here (Default or Whole class).
        using var budgetSignal = InlineBudgetSignal.Open();
        var result = await next();
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
                var threshold = budgetSignal.ThresholdChars ?? LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Default);
                if (string.IsNullOrEmpty(text) || text.Length <= threshold)
                {
                    continue;
                }

                // With no signal value (marshaller off or passthrough, or an exception catch-all) the body's shape picks the
                // class: an error-only failure gets the Whole threshold. The parse is reused for the pointer hints below.
                JsonNode? root = null;
                var parsed = false;
                if (budgetSignal.ThresholdChars is null)
                {
                    root = TryParseBody(text);
                    parsed = true;
                    if (LargeResultHelper.IsErrorOnlyBody(root))
                    {
                        threshold = LargeResultHelper.OffloadThresholdChars(InlineBudgetClass.Whole);
                        if (text.Length <= threshold)
                        {
                            continue;
                        }
                    }
                }

                // Result marshaller miss detector (plan_wire_result_marshaller.md Step 4, Step 10 slice D1): in mode On a tool
                // built through SentinelToolFactory (SentinelToolFactory.IsHooked) is marshalled, so a body that still
                // arrives here over the threshold is a body the marshaller never fitted: an exception catch-all or refusal
                // body, a result it handed back whole (non-object JSON root, plain string or content-block return, nothing
                // pageable), or a pager bug (a fitted result stays within its threshold). Each is an error-level signal; the
                // body is still stored below as the backstop. In mode Off or Passthrough the marshaller is deliberately not
                // cutting, so the original warning is all that is logged.
                if (ResultMarshallerOptions.Mode == ResultMarshallerMode.On && SentinelToolFactory.IsHooked(toolName))
                {
                    logger?.LogError(
                        "Result marshaller miss: hooked tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdChars}) the marshaller did not cut; the offload filter is handling it as the backstop. Check ResultPager hints and the inline budget class.",
                        toolName, text.Length, threshold);
                }
                else
                {
                    logger?.LogWarning(
                        "Large tool result: tool '{Tool}' returned {SizeChars} chars (threshold: {OffloadThresholdChars})",
                        toolName, text.Length, threshold);
                }

                var resolvedSolutionRoot = solutionRoot();
                var stored = await LargeResultHelper.StoreEnvelopeAsync(text, resolvedSolutionRoot, cancellationToken);
                if (!stored.offloaded)
                {
                    // No solution loaded, or the write failed to qualify -> fail closed to
                    // pass-through rather than blocking the call on a guardrail defect.
                    continue;
                }

                if (!parsed)
                {
                    root = TryParseBody(text);
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
                JsonNode? listSummary = null;
                bool? isError = null;
                ResultError? errorData = null;
                int? errorMessageTotalChars = null;
                try
                {
                    if (root is JsonObject rootObject)
                    {
                        if (rootObject.TryGetPropertyValue(LargeResultHelper.IsErrorKey, out var isErrorNode) &&
                            isErrorNode != null &&
                            (isErrorNode.GetValueKind() == System.Text.Json.JsonValueKind.True ||
                             isErrorNode.GetValueKind() == System.Text.Json.JsonValueKind.False))
                        {
                            isError = isErrorNode.GetValue<bool>();
                        }

                        // listSummary (per-file counts, already capped by SummarizeListResult) is
                        // the only per-file hint once a list tool's statusMessage stops repeating
                        // the file paths, and a raw offload drops it unless it is relayed here.
                        if (rootObject.TryGetPropertyValue("listSummary", out var listSummaryNode) &&
                            listSummaryNode is JsonObject)
                        {
                            listSummary = listSummaryNode;
                        }

                        if (rootObject.TryGetPropertyValue("totalRecords", out var totalRecordsNode) &&
                            totalRecordsNode != null &&
                            totalRecordsNode.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                        {
                            itemCount = totalRecordsNode.GetValue<int>();
                        }
                        else if (rootObject.TryGetPropertyValue(LargeResultHelper.SuccessDataKey, out var successDataNode) && successDataNode != null)
                        {
                            itemCount = LargeResultHelper.CountResultItems(successDataNode);
                        }

                        if (rootObject.TryGetPropertyValue("statusMessage", out var statusMessageNode) &&
                            statusMessageNode != null &&
                            statusMessageNode.GetValueKind() == System.Text.Json.JsonValueKind.String)
                        {
                            statusMessage = statusMessageNode.GetValue<string>();
                        }

                        // An error-only body keeps its error code and message in the pointer (message capped at the
                        // default budget so the pointer stays small; the full length is reported when it is cut).
                        if (LargeResultHelper.IsErrorOnlyBody(rootObject) &&
                            rootObject[LargeResultHelper.ErrorDataKey] is JsonObject errorNode)
                        {
                            var errorCode = ReadString(errorNode, "errorCode");
                            var errorMessage = ReadString(errorNode, "message");
                            if (errorCode != null || errorMessage != null)
                            {
                                errorMessage ??= string.Empty;
                                var cap = LargeResultHelper.DefaultBudgetChars;
                                if (errorMessage.Length > cap)
                                {
                                    errorMessageTotalChars = errorMessage.Length;
                                    errorMessage = errorMessage.Substring(0, cap);
                                }

                                errorData = new ResultError(errorCode ?? string.Empty, errorMessage);
                            }
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

                var pointer = new OffloadPointerResult
                {
                    SolutionRoot = resolvedSolutionRoot,
                    Offloaded = true,
                    ResultId = stored.resultId,
                    SizeBytes = text.Length,
                    SizeChars = text.Length,
                    ItemCount = itemCount,
                    IsError = isError,
                    StatusMessage = statusMessage,
                    ListSummary = listSummary,
                    Message = $"Result is {text.Length} chars (threshold: {threshold}).{hint} Use {LargeResultHints.FetchCall(stored.resultId!)} to page through results.",
                    ErrorData = errorData,
                    ErrorMessageTotalChars = errorMessageTotalChars,
                };
                result.Content = [new ModelContextProtocol.Protocol.TextContentBlock
                    {
                        Text = System.Text.Json.JsonSerializer.Serialize(pointer, SentinelToolFactory.WireSerializerOptions)
                    }];
                break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Large result offload filter failed: {ex}");
        }

        return result;
    }

    // Parses a response body for the size hints and the error-only check; null when it is not JSON (never throws).
    private static JsonNode? TryParseBody(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (Exception parseEx)
        {
            Debug.WriteLine($"Large result offload size-hint parse failed: {parseEx}");
            return null;
        }
    }

    // The string value of a property, or null when the property is absent or not a string.
    private static string? ReadString(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
