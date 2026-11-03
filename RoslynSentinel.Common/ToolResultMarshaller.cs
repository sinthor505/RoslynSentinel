using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Protocol;

namespace RoslynSentinel.Common;

/// <summary>
/// The <see cref="IToolResultMarshaller"/> registered when <c>ROSLYNSENTINEL_RESULT_MARSHALLER</c> is not <c>off</c>.
/// Pass-through mode (this step): results the SDK already handles as content are returned unchanged; every other
/// result is serialized once, exactly as the SDK's default branch does, and returned as a <see cref="string"/>, which
/// the SDK emits verbatim. The text is therefore byte-identical to what the SDK produces without a marshaller.
/// </summary>
/// <remarks>
/// A <see cref="string"/> return also reaches the SDK's structured-content step, which is a no-op for every tool
/// today (no tool sets <c>UseStructuredContent = true</c>, so none has an output schema). Never throws: any exception
/// returns the original object and is logged at error level (server-side only, nothing tool-visible).
/// </remarks>
public sealed class ToolResultMarshaller : IToolResultMarshaller
{
    private readonly ILogger<ToolResultMarshaller> _logger;

    public ToolResultMarshaller(ILogger<ToolResultMarshaller> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<object?> MarshalAsync(
        string toolName,
        MethodInfo method,
        object? result,
        Type? returnType,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            // TODO Step 4 (plan_wire_result_marshaller): mode On is not implemented yet and behaves as pass-through.
            // Step 4 adds, for mode On over budget: parse the wire text, fit it with ResultPager, store the envelope
            // and add the largeResult pointer.
            return new ValueTask<object?>(Passthrough(result, serializerOptions));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Result marshalling failed for tool {ToolName} (declared return type {ReturnType}); returning the original result for the SDK to serialize.",
                toolName,
                returnType?.FullName ?? "(none)");
            return new ValueTask<object?>(result);
        }
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
