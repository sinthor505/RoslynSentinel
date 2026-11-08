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
/// How it fits with the call-tool filters and <c>GetLargeResult</c>: docs/current/references/reference_tool_response_pipeline.md.
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
/// <b>Detached payloads</b> (proposal_side_stores_never_inline.md slice 2): a tool or the apply helper can declare a list or
/// string as stored, not inline (<c>SentinelCallToolResult.Detach</c>, <see cref="ToolCallContext.Detach"/>). In mode <c>on</c>,
/// before the budget check, each is grafted into the parsed tree at <c>successData.&lt;name&gt;</c>, the full tree is stored as one
/// Envelope, and the text goes on without that property, with a neutral pointer (<c>Stored, not inline: ...</c>) and an
/// <c>omitted</c> entry. If the remainder is also over budget the usual paging runs on the remainder under its own resultId and
/// the detached pointer is appended to that pointer's message.
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
        result = WithNotFoundNotes(result);
        try
        {
            object? passthrough = Passthrough(result, serializerOptions);
            if (_mode() != ResultMarshallerMode.On || ReferenceEquals(passthrough, result) || passthrough is not string text)
            {
                return passthrough;
            }

            const int budget = LargeResultHelper.InlineBudgetChars - LargeResultHelper.EchoReserveChars;

            // Detached payloads (SentinelCallToolResult.Detach / ToolCallContext.Detach) are stored whatever the size, so they are
            // resolved before the budget check. Only mode On gets here; off and passthrough never serialize them (JsonIgnore), so
            // the payload is dropped, not inlined.
            if (CollectDetached(result) is { Count: > 0 } detached
                && await DetachAsync(toolName, text, detached, serializerOptions, cancellationToken).ConfigureAwait(false) is { } detachment)
            {
                return await FinishDetachedAsync(toolName, result, detachment, serializerOptions, budget, cancellationToken).ConfigureAwait(false);
            }

            if (text.Length <= budget)
            {
                return text;
            }

            // A GetLargeResult page of a stored sequence was built to the budget by SequencePager; when it is still over, one
            // unit alone is larger than a page (Decided 4). Cutting it again would store a NEW resultId and start the re-offload
            // loop, so it goes out as is (the server log names the tool) and the offload filter is told to leave it alone.
            // (The page builder, GetLargeResult, raises the same signal itself, because in mode off no marshaller is registered.)
            if (PagingHintsOf(result) is { AlreadyPaged: true })
            {
                _logger.LogWarning(
                    "Tool {ToolName} returned a stored-sequence page of {Chars} chars, over the {Budget} budget (one unit is larger than a page); returned unchanged, not re-paged or re-stored.",
                    toolName,
                    text.Length,
                    budget);
                OversizeUnitSignal.Raise();
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

    // Evidence notes for a not-found error (NotFoundEvidence), added here where the typed result is in hand and before it is serialized.
    // An error thrown instead of returned never reaches the marshaller; CallToolFailureHandler adds the same notes. Never throws.
    private static object? WithNotFoundNotes(object? result)
    {
        try
        {
            if (result is IFindingsEnvelope { IsError: true } envelope
                && NotFoundEvidence.ForCurrentCall(envelope.Code) is { Count: > 0 } notes)
            {
                return envelope.WithAddedFindings(notes);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Not-found notes failed: {ex}");
        }

        return result;
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

        // The parts outside the sequence are over budget by themselves: only Fit can cut those (stage 2 moves such results to
        // bounded shapes). Named in the server log so the offenders can be found.
        if (page.PartsOutsideOverBudget)
        {
            _logger.LogWarning(
                "Tool {ToolName} breaks the paging unit contract: {Detail}; falling back to ResultPager.Fit (parts outside the sequence must stay bounded).",
                toolName,
                page.ViolationDetail);
            return null;
        }

        if (page.ContractViolation)
        {
            _logger.LogWarning("Tool {ToolName} breaks the paging unit contract: {Detail}", toolName, page.ViolationDetail);
        }

        if (page.NextOffset is null)
        {
            // The whole sequence is one unit and that unit is larger than a page (Decided 4): it is returned alone, whole, with
            // no cut and no omitted entry, and nothing is stored (there is no second page to fetch). The offload filter is told
            // to leave the text alone. A sequence with no violation here fits whole by estimate or is empty; Fit handles those.
            if (page.ContractViolation && page.Total > 0)
            {
                OversizeUnitSignal.Raise();
                return text;
            }

            return null;
        }

        // An oversize first unit goes alone on a page that advances (Decided 4); the page is over budget on purpose.
        if (page.ContractViolation)
        {
            OversizeUnitSignal.Raise();
        }

        // The sequence pager cut this result, so the tool's own re-query hint (ReadFile's "call ReadFile(startLine..endLine)")
        // and its paging fields must not sit beside the largeResult offset call; the envelope-level hasMoreData is set to
        // true to agree with largeResult.nextOffset. The stored full text keeps the tool's original fields.
        // TODO StripToolPaging leaves successData.envelope untouched: a cut GetMethodSource page still says isComplete:true and the pre-cut returnedToLine/lineCount, contradicting largeResult.
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
                SizeChars = text.Length,
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
                SizeChars = text.Length,
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

    // The payloads detached on the result object plus those detached on the ambient call context (code that cannot reach the
    // result, such as the apply helper). The context is null when no filter set one (tests, engines): then only the result's count.
    private static IReadOnlyList<(string Name, object Payload)> CollectDetached(object? result)
    {
        IReadOnlyList<(string Name, object Payload)> fromResult = (result as IDetachedPayloadSource)?.Detached ?? [];
        IReadOnlyList<(string Name, object Payload)> fromContext = ToolCallContext.Current?.Detached ?? [];
        if (fromContext.Count == 0)
        {
            return fromResult;
        }

        return fromResult.Count == 0 ? fromContext : [.. fromResult, .. fromContext];
    }

    // Appends a sentence to the envelope's warningDetails (kept, not replaced, when the tool already set one).
    private static void AddWarning(JsonObject envelope, string warning) =>
        envelope["warningDetails"] = envelope["warningDetails"] is JsonValue existing && existing.TryGetValue(out string? previous) && !string.IsNullOrEmpty(previous)
            ? previous + " " + warning
            : warning;

    // Stage 1 of a detached result (proposal_side_stores_never_inline.md slice 2): each payload is serialized with the wire options,
    // grafted into the parsed tree at successData.<name>, and the FULL grafted tree is stored as one Envelope (a path-addressable
    // file: GetLargeResult(path: "successData.<name>") pages it). Then every grafted path is moved out (ResultPager.MoveOut, which
    // writes the omitted entry) and the now-empty property is removed, so the wire text never carries the payload or an empty shell.
    // Returns the remainder tree plus the pointer for it, or null when there is nothing to store (no successData object, or every
    // payload empty or not a list/string), in which case the caller carries on with the untouched text.
    private async Task<(JsonObject Remainder, JsonNode PointerNode, string Message, bool Stored)?> DetachAsync(
        string toolName, string text, IReadOnlyList<(string Name, object Payload)> detached, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        const string successPrefix = "successData.";
        if (JsonNode.Parse(text) is not JsonObject parsed || parsed["successData"] is not JsonObject successData)
        {
            _logger.LogWarning("Tool {ToolName} detached payloads but its result has no successData object to hold them; they are dropped.", toolName);
            return null;
        }

        var typeInfo = options.GetTypeInfo(typeof(object));
        var names = new List<string>();
        foreach (var (name, payload) in detached)
        {
            JsonNode? node = JsonSerializer.SerializeToNode(payload, typeInfo);
            bool empty;
            if (node is JsonArray array)
            {
                empty = array.Count == 0;
            }
            else if (node is JsonValue value && value.TryGetValue(out string? line))
            {
                empty = line.Length == 0;
            }
            else
            {
                _logger.LogWarning("Tool {ToolName} detached payload {Name}, which serializes to neither a list nor a string; it is dropped.", toolName, name);
                continue;
            }

            if (empty)
            {
                continue;
            }

            if (!names.Contains(name))
            {
                if (successData.ContainsKey(name))
                {
                    _logger.LogWarning("Tool {ToolName} detached payload {Name}, replacing the inline property of the same name.", toolName, name);
                }

                names.Add(name);
            }

            // A name detached twice (result and context) keeps the later payload.
            successData[name] = node;
        }

        if (names.Count == 0)
        {
            return null;
        }

        string fullText = parsed.ToJsonString(options);
        var omitted = new List<OmittedPart>();
        foreach (string name in names)
        {
            ResultPager.MoveOut(parsed, successPrefix + name, omitted);
            successData.Remove(name);
        }

        var (resultId, filePath, failure) = await TryStoreAsync(toolName, fullText, omitted, sequence: null, cancellationToken).ConfigureAwait(false);
        string summary = string.Join(
            ", ",
            omitted.Select(p => $"{p.Path[successPrefix.Length..]} ({p.Total} {(p.Unit == ResultPager.UnitLines ? "lines" : "records")})"));

        if (resultId is not null)
        {
            string calls = string.Join(" ; ", omitted.Select(p => LargeResultHints.FetchCall(resultId, path: p.Path)));
            string message = $"Stored, not inline: {summary}. {calls}";
            var pointer = new LargeResultInfo(
                resultType: nameof(ResultWrapperType.Envelope),
                writtenToFile: true,
                filePath: filePath,
                resultId: resultId,
                sizeBytes: Encoding.UTF8.GetByteCount(fullText),
                totalRecords: omitted[0].Total,
                message: message)
            {
                Omitted = omitted,
                SizeChars = fullText.Length,
            };
            return (parsed, JsonSerializer.SerializeToNode(pointer, options)!, message, true);
        }

        string warning = $"Not inline: {summary} was left out of the text, but {failure}; it is not retrievable. Narrow the request.";
        JsonNode failedPointer = JsonSerializer.SerializeToNode(
            new
            {
                resultType = nameof(ResultWrapperType.Envelope),
                writtenToFile = false,
                totalRecords = omitted[0].Total,
                message = warning,
                omitted,
            },
            options)!;
        return (parsed, failedPointer, warning, false);
    }

    // Stage 2 of a detached result: the remainder (payloads already stored and removed) goes through the usual budget check.
    // Merge rule when the remainder is ALSO over budget: the existing flow (sequence page or Fit, FitAndStoreAsync) runs unchanged
    // on the remainder text and stores that text under its own resultId; the detached payload stays in the first file. They are
    // two files on purpose: a stored sequence is read by offset and ignores GetLargeResult's path, so the payload could not be
    // addressed by path inside a sequence file. The detached pointer is then appended to the existing pointer's message (its
    // omitted/returned fields keep describing the remainder's file), or becomes the pointer when the usual flow added none.
    private async Task<string> FinishDetachedAsync(
        string toolName,
        object? result,
        (JsonObject Remainder, JsonNode PointerNode, string Message, bool Stored) detachment,
        JsonSerializerOptions options,
        int budget,
        CancellationToken cancellationToken)
    {
        JsonObject remainder = detachment.Remainder;
        string remainderText = remainder.ToJsonString(options);
        if (remainderText.Length <= budget)
        {
            if (!detachment.Stored)
            {
                AddWarning(remainder, detachment.Message);
            }

            return WithPointerFirst(remainder, detachment.PointerNode, options);
        }

        string usual = await FitAndStoreAsync(toolName, result, remainderText, options, budget, cancellationToken).ConfigureAwait(false);
        if (JsonNode.Parse(usual) is not JsonObject final)
        {
            return usual;
        }

        if (final["largeResult"] is JsonObject existing)
        {
            if (detachment.Stored)
            {
                existing["message"] = existing["message"] is JsonValue current && current.TryGetValue(out string? previous) && !string.IsNullOrEmpty(previous)
                    ? previous + " " + detachment.Message
                    : detachment.Message;
            }
            else
            {
                AddWarning(final, detachment.Message);
            }

            return final.ToJsonString(options);
        }

        // The usual flow could not cut the remainder (nothing cuttable) and added no pointer: the detached pointer leads.
        if (!detachment.Stored)
        {
            AddWarning(final, detachment.Message);
        }

        return WithPointerFirst(final, detachment.PointerNode, options);
    }

    // The runtime type of SentinelCallToolResult<TSuccess, TError>.SuccessData, where the typed hint ([ResultRecords])
    // lives; null for any other result shape or an empty SuccessData.
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
