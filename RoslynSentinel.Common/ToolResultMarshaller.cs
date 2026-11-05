using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common;

/// <summary>
/// The <see cref="IToolResultMarshaller"/> registered when <c>ROSLYNSENTINEL_RESULT_MARSHALLER</c> is not <c>off</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pass-through</b> (<c>passthrough</c>, and <c>on</c> for any result that fits): results the SDK already handles as
/// content are returned unchanged; every other result is serialized once, exactly as the SDK's default branch does, and
/// returned as a <see cref="string"/>, which the SDK emits verbatim. The text is byte-identical to what the SDK produces
/// without a marshaller.
/// </para>
/// <para>
/// <b>On</b> (plan_wire_result_marshaller.md Step 4): when that text is longer than
/// <see cref="LargeResultHelper.InlineBudgetChars"/> minus <see cref="LargeResultHelper.EchoReserveChars"/>, it is parsed, fitted
/// with <see cref="ResultPager"/>, the FULL text is stored as an <see cref="ResultWrapperType.Envelope"/> file, and the fitted
/// envelope is returned with a <c>largeResult</c> pointer (<c>resultType: "Envelope"</c>, <c>resultId</c>, <c>omitted[]</c>, the
/// fetch hint). If the store fails the fitted envelope is returned with <c>largeResult.omitted</c> and a
/// <c>warningDetails</c> note but no <c>resultId</c>. The pointer itself is not part of the budget (the reserve absorbs it
/// together with the echo and stamps). A result whose JSON root is not an object, or that the pager cannot cut, is returned
/// as the full text for the offload filter to handle.
/// </para>
/// <para>
/// <b>Sequence paging</b> (proposal_sequence_pager.md slice 5): before <see cref="ResultPager"/> the marshaller asks
/// <see cref="SequencePager.ResolveSequence"/> for the result's sequence (the envelope's <c>Paging</c> hints, a
/// <see cref="ResultRecordsAttribute"/> list, every array under <c>successData</c>, or its one large string). When there
/// is one, the first response is the page at offset 0, the stored file also holds the sequence descriptor, and
/// <c>largeResult</c> carries <c>returned</c> and <c>nextOffset</c> instead of <c>omitted</c>. A result with no sequence
/// (or whose sequence already fits whole) takes the <see cref="ResultPager"/> path above unchanged.
/// </para>
/// <para>
/// A <see cref="string"/> return also reaches the SDK's structured-content step, which is a no-op for every tool today (no
/// tool sets <c>UseStructuredContent = true</c>, so none has an output schema). Never throws: any exception returns the
/// original object and is logged at error level (server-side only, nothing tool-visible).
/// </para>
/// </remarks>
public sealed class ToolResultMarshaller : IToolResultMarshaller
{
    private readonly ILogger<ToolResultMarshaller> _logger;
    private readonly Func<string?> _solutionRoot;
    private readonly Func<ResultMarshallerMode> _mode;

    /// <summary>Pass-through only, whatever the switch says: there is no solution root, so mode <c>on</c> could not store.</summary>
    public ToolResultMarshaller(ILogger<ToolResultMarshaller> logger)
        : this(logger, static () => null, static () => ResultMarshallerMode.Passthrough)
    {
    }

    /// <summary>The DI constructor: the solution root comes from the loaded workspace, looked up on each call.</summary>
    public ToolResultMarshaller(ILogger<ToolResultMarshaller> logger, IServiceProvider services)
        : this(logger, () => services.GetService<PersistentWorkspaceManager>()?.GetSolutionRoot())
    {
    }

    /// <param name="logger">Server-side log for failures.</param>
    /// <param name="solutionRoot">Where large results are stored (null = none, the store fails closed).</param>
    /// <param name="mode">The marshaller mode; default reads <see cref="ResultMarshallerOptions.Mode"/> on every call.</param>
    public ToolResultMarshaller(ILogger<ToolResultMarshaller> logger, Func<string?> solutionRoot, Func<ResultMarshallerMode>? mode = null)
    {
        _logger = logger;
        _solutionRoot = solutionRoot;
        _mode = mode ?? (static () => ResultMarshallerOptions.Mode);
    }

    /// <inheritdoc />
    public async ValueTask<object?> MarshalAsync(
        string toolName,
        MethodInfo method,
        object? result,
        Type? returnType,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        ForwardGuidance(result);
        try
        {
            object? passthrough = Passthrough(result, serializerOptions);
            if (_mode() != ResultMarshallerMode.On || ReferenceEquals(passthrough, result) || passthrough is not string text)
            {
                return passthrough;
            }

            const int budget = LargeResultHelper.InlineBudgetChars - LargeResultHelper.EchoReserveChars;
            if (text.Length <= budget)
            {
                return text;
            }

            return await FitAndStoreAsync(toolName, result, text, serializerOptions, budget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Result marshalling failed for tool {ToolName} (declared return type {ReturnType}); returning the original result for the SDK to serialize.",
                toolName,
                returnType?.FullName ?? "(none)");
            return result;
        }
    }

    // Pointer first: a model reads the leading keys, so largeResult must be the envelope's first property.
    // Clear detaches the nodes, so the remaining properties are re-added as deep clones in their original order.
    private static string WithPointerFirst(JsonObject fitted, JsonNode pointerNode, JsonSerializerOptions options)
    {
        var rest = fitted.Where(kv => kv.Key != "largeResult").Select(kv => (kv.Key, Value: kv.Value?.DeepClone())).ToList();
        fitted.Clear();
        fitted["largeResult"] = pointerNode;
        foreach (var (key, value) in rest)
        {
            fitted[key] = value;
        }

        return fitted.ToJsonString(options);
    }

    // Guidance-adherence telemetry: the telemetry filter only sees the serialized CallToolResult, so the envelope's
    // never-serialized GuidanceHint is handed to the per-call ToolGuidanceCollector here, where the typed result is still
    // in hand (this runs inside the tool call's async flow). A no-op without an open collector; never throws.
    private static void ForwardGuidance(object? result)
    {
        try
        {
            if (!ToolGuidanceCollector.IsOpen)
            {
                return;
            }

            for (Type? type = result?.GetType(); type is not null; type = type.BaseType)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SentinelCallToolResult<,>))
                {
                    if (type.GetProperty(nameof(SentinelCallToolResult<object, object>.GuidanceHint))?.GetValue(result) is GuidanceHint hint)
                    {
                        foreach (var toolName in hint.SuggestedToolNames ?? [])
                        {
                            ToolGuidanceCollector.Record(toolName);
                        }
                    }

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tool guidance forward failed: {ex}");
        }
    }

    // The envelope's server-side Paging hints (SentinelCallToolResult<,>.Paging, never serialized); null for any other
    // result shape. SuccessData stays typed as the tool's own type - an interface there would serialize as {}.
    private static PagingHints? PagingHintsOf(object? result)
    {
        for (Type? type = result?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SentinelCallToolResult<,>))
            {
                return type.GetProperty(nameof(SentinelCallToolResult<object, object>.Paging))?.GetValue(result) as PagingHints;
            }
        }

        return null;
    }

    // Stores the full wire text as an Envelope file (with the cuts or the sequence, whichever applies). Never throws except
    // for cancellation: a failed store is reported as a short reason so the caller can degrade to a warning.
    private async Task<(string? ResultId, FilePathWrapper FilePath, string? Failure)> TryStoreAsync(
        string toolName, string text, IReadOnlyList<OmittedPart>? omitted, SequenceDescriptor? sequence, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await LargeResultHelper.StoreEnvelopeAsync(text, _solutionRoot(), cancellationToken, omitted, sequence).ConfigureAwait(false);
            return stored.offloaded
                ? (stored.resultId, stored.filePath, null)
                : (null, default, "no solution is loaded to hold the full result");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Storing the full result of tool {ToolName} failed; returning the fitted result without a resultId.", toolName);
            return (null, default, "the full result could not be written to the result store");
        }
    }

    // First response of a sequence-paged result (proposal_sequence_pager.md slice 5): the page at offset 0, the full text
    // stored with the sequence, and a largeResult pointer carrying returned / nextOffset. Returns null when the page already
    // holds the whole sequence (the over-budget part is then outside the sequence), so the caller falls back to Fit.
    private async Task<string?> PageSequenceAsync(
        string toolName, JsonObject parsed, string text, SequenceDescriptor sequence, JsonSerializerOptions options, int budget, CancellationToken cancellationToken)
    {
        SequencePage page = SequencePager.BuildPage(parsed, sequence, 0, 0, budget, options);
        if (page.ContractViolation)
        {
            _logger.LogWarning("Tool {ToolName} breaks the paging unit contract: {Detail}", toolName, page.ViolationDetail);
        }

        // No page to offer: the sequence already fits whole, or the parts outside it are over budget by themselves and
        // only Fit can cut those.
        if (page.NextOffset is null || page.PartsOutsideOverBudget)
        {
            return null;
        }

        // The sequence pager cut this result, so the tool's own re-query hint (ReadFile's "call ReadFile(startLine..endLine)")
        // and its paging fields must not sit beside the largeResult offset call; the envelope-level hasMoreData is set to
        // true to agree with largeResult.nextOffset. The stored full text keeps the tool's original fields.
        SequenceMessages.StripToolPaging(page.Page);
        page.Page["hasMoreData"] = true;

        var (resultId, filePath, storeFailure) = await TryStoreAsync(toolName, text, omitted: null, sequence, cancellationToken).ConfigureAwait(false);

        JsonNode pointerNode;
        if (resultId is not null)
        {
            var pointer = new LargeResultInfo(
                resultType: nameof(ResultWrapperType.Envelope),
                writtenToFile: true,
                filePath: filePath,
                resultId: resultId,
                sizeBytes: Encoding.UTF8.GetByteCount(text),
                totalRecords: page.Total,
                message: SequenceMessages.ForPage(parsed, sequence, page, LargeResultHints.FetchCall(resultId, offset: page.NextOffset)))
            {
                Returned = page.Returned,
                NextOffset = page.NextOffset,
            };
            pointerNode = JsonSerializer.SerializeToNode(pointer, options)!;
        }
        else
        {
            string unit = sequence.Kind == SequenceKind.Lines ? "lines" : "records";
            string warning = $"Result is {text.Length} chars and only {page.Returned} of {page.Total} {unit} are shown, but {storeFailure}; the rest is not retrievable. Narrow the request or page by offset.";
            pointerNode = JsonSerializer.SerializeToNode(
                new
                {
                    resultType = nameof(ResultWrapperType.Envelope),
                    writtenToFile = false,
                    totalRecords = page.Total,
                    message = warning,
                    returned = page.Returned,
                },
                options)!;
            JsonObject shown = page.Page;
            shown["warningDetails"] = shown["warningDetails"] is JsonValue existing && existing.TryGetValue(out string? previous) && !string.IsNullOrEmpty(previous)
                ? previous + " " + warning
                : warning;
        }

        return WithPointerFirst(page.Page, pointerNode, options);
    }

    private async Task<string> FitAndStoreAsync(
        string toolName, object? result, string text, JsonSerializerOptions options, int budget, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(text) is not JsonObject parsed)
        {
            return text;
        }

        // Sequence pager first (proposal_sequence_pager.md): a result with a list or a large string directly under
        // successData pages by units. Anything else (dictionaries, nested shapes, scalar-only data) keeps the Fit path.
        Type? successType = SuccessDataTypeOf(result);
        SequenceDescriptor sequence = SequencePager.ResolveSequence(parsed, PagingHintsOf(result), successType, options, _logger);
        if (sequence.Kind != SequenceKind.None)
        {
            string? paged = await PageSequenceAsync(toolName, parsed, text, sequence, options, budget, cancellationToken).ConfigureAwait(false);
            if (paged is not null)
            {
                return paged;
            }
        }

        var (inline, omitted) = ResultPager.Fit(parsed, budget, options, successType);
        if (omitted.Count == 0 || inline is not JsonObject fitted)
        {
            return text;
        }

        var (resultId, filePath, storeFailure) = await TryStoreAsync(toolName, text, omitted, sequence: null, cancellationToken).ConfigureAwait(false);

        string summary = string.Join(
            "; ",
            omitted.Take(3).Select(p => p.Path + " " + p.Returned + "/" + p.Total + " " + p.Unit)) + (omitted.Count > 3 ? "; ..." : string.Empty);

        JsonNode pointerNode;
        if (resultId is not null)
        {
            var pointer = new LargeResultInfo(
                resultType: nameof(ResultWrapperType.Envelope),
                writtenToFile: true,
                filePath: filePath,
                resultId: resultId,
                sizeBytes: Encoding.UTF8.GetByteCount(text),
                totalRecords: omitted[0].Total,
                message: $"INCOMPLETE: this result was cut to fit ({summary}); the cut parts are stored in full. Call {LargeResultHints.FetchCall(resultId, path: omitted[0].Path)} to read the first cut part (add offset/limit to page; every cut part is listed in omitted).")
            {
                Omitted = omitted,
            };
            pointerNode = JsonSerializer.SerializeToNode(pointer, options)!;
        }
        else
        {
            string warning = $"Result is {text.Length} chars and was cut to fit ({summary}), but {storeFailure}; the omitted part is not retrievable. Narrow the request or page by offset.";
            pointerNode = JsonSerializer.SerializeToNode(
                new
                {
                    resultType = nameof(ResultWrapperType.Envelope),
                    writtenToFile = false,
                    totalRecords = omitted[0].Total,
                    message = warning,
                    omitted,
                },
                options)!;
            fitted["warningDetails"] = fitted["warningDetails"] is JsonValue existing && existing.TryGetValue(out string? previous) && !string.IsNullOrEmpty(previous)
                ? previous + " " + warning
                : warning;
        }

        return WithPointerFirst(fitted, pointerNode, options);
    }

    // The runtime type of SentinelCallToolResult<TSuccess, TError>.SuccessData, where the typed hints ([ResultRecords],
    // [NeverInline]) live; null for any other result shape or an empty SuccessData.
    private static Type? SuccessDataTypeOf(object? result)
    {
        for (Type? type = result?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(SentinelCallToolResult<,>))
            {
                return type.GetProperty(nameof(SentinelCallToolResult<object, object>.SuccessData))?.GetValue(result)?.GetType();
            }
        }

        return null;
    }

    /// <summary>
    /// null, string, ContentBlock, AIContent, CallToolResult and enumerables of ContentBlock or AIContent are the
    /// shapes the SDK turns into content itself and are returned unchanged; anything else is serialized with
    /// <paramref name="serializerOptions"/> via <c>GetTypeInfo(typeof(object))</c>, the SDK's own default branch.
    /// </summary>
    internal static object? Passthrough(object? result, JsonSerializerOptions serializerOptions) => result switch
    {
        null or string or ContentBlock or AIContent or CallToolResult or IEnumerable<AIContent> or IEnumerable<ContentBlock> => result,
        _ => JsonSerializer.Serialize(result, serializerOptions.GetTypeInfo(typeof(object))),
    };
}
