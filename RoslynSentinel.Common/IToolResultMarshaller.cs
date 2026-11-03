using System.Reflection;
using System.Text.Json;

namespace RoslynSentinel.Common;

/// <summary>
/// Owns the wire form of a tool's typed result. <see cref="SentinelToolFactory"/> calls it from the
/// <c>MarshalResult</c> hook of every tool it builds, once per call, with the awaited value the tool method returned
/// (see the MarshalResult semantics in <see cref="SentinelToolFactory"/>). Whatever it returns is then handled by the
/// SDK's result switch: a <see cref="string"/> becomes the text content verbatim, a <c>ContentBlock</c>,
/// <c>AIContent</c> or <c>CallToolResult</c> is used as is, anything else is serialized to JSON text.
/// </summary>
/// <remarks>
/// Implementations must not throw: on any failure they return <paramref name="result"/> unchanged, so the SDK
/// serializes it exactly as it does without a marshaller, and log the failure server-side. Nothing about the failure
/// may reach the tool-visible output. See plan_wire_result_marshaller.md.
/// </remarks>
public interface IToolResultMarshaller
{
    /// <summary>Returns the object the SDK should turn into the call's content.</summary>
    /// <param name="toolName">Wire name of the tool.</param>
    /// <param name="method">The tool method that produced <paramref name="result"/>.</param>
    /// <param name="result">The awaited value the method returned (null for void or <c>Task</c>).</param>
    /// <param name="returnType">The method's declared <c>T</c> (null for void or <c>Task</c>); for <c>object</c> the runtime type comes from <paramref name="result"/>.</param>
    /// <param name="serializerOptions">The tool's wire serializer options (<c>McpServerToolCreateOptions.SerializerOptions ?? McpJsonUtilities.DefaultOptions</c>), the ones the SDK itself would serialize with.</param>
    /// <param name="cancellationToken">The call's cancellation token.</param>
    ValueTask<object?> MarshalAsync(
        string toolName,
        MethodInfo method,
        object? result,
        Type? returnType,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken);
}
