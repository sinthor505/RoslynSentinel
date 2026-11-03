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

    private async Task<string> FitAndStoreAsync(
        string toolName, object? result, string text, JsonSerializerOptions options, int budget, CancellationToken cancellationToken)
    {
        if (JsonNode.Parse(text) is not JsonObject parsed)
        {
            return text;
        }

        var (inline, omitted) = ResultPager.Fit(parsed, budget, options, SuccessDataTypeOf(result));
        if (omitted.Count == 0 || inline is not JsonObject fitted)
        {
            return text;
        }

        string? resultId = null;
        FilePathWrapper filePath = default;
        string? storeFailure = null;
        try
        {
            var stored = await LargeResultHelper.StoreEnvelopeAsync(text, _solutionRoot(), cancellationToken, omitted).ConfigureAwait(false);
            if (stored.offloaded)
            {
                resultId = stored.resultId;
                filePath = stored.filePath;
            }
            else
            {
                storeFailure = "no solution is loaded to hold the full result";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            storeFailure = "the full result could not be written to the result store";
            _logger.LogError(ex, "Storing the full result of tool {ToolName} failed; returning the fitted result without a resultId.", toolName);
        }

        string summary = string.Join(
            "; ",
            omitted.Take(3).Select(p => p.Path + " " + p.Returned + "/" + p.Total + " " + p.Unit)) + (omitted.Count > 3 ? "; ..." : string.Empty);

        if (resultId is not null)
        {
            var pointer = new LargeResultInfo(
                resultType: nameof(ResultWrapperType.Envelope),
                writtenToFile: true,
                filePath: filePath,
                resultId: resultId,
                sizeBytes: Encoding.UTF8.GetByteCount(text),
                totalRecords: omitted[0].Total,
                message: $"Result is {text.Length} chars; cut to fit: {summary}. Use {LargeResultHints.FetchCall(resultId)} to page through the full result.")
            {
                Omitted = omitted,
            };
            fitted["largeResult"] = JsonSerializer.SerializeToNode(pointer, options);
        }
        else
        {
            string warning = $"Result is {text.Length} chars and was cut to fit ({summary}), but {storeFailure}; the omitted part is not retrievable. Narrow the request or page by offset.";
            fitted["largeResult"] = JsonSerializer.SerializeToNode(
                new
                {
                    resultType = nameof(ResultWrapperType.Envelope),
                    writtenToFile = false,
                    totalRecords = omitted[0].Total,
                    message = warning,
                    omitted,
                },
                options);
            fitted["warningDetails"] = fitted["warningDetails"] is JsonValue existing && existing.TryGetValue(out string? previous) && !string.IsNullOrEmpty(previous)
                ? previous + " " + warning
                : warning;
        }

        return fitted.ToJsonString(options);
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
