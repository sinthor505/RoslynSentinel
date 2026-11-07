using System.Collections;

namespace RoslynSentinel.Common;

/// <summary>
/// Non-generic view of something that carries detached payloads (a <c>SentinelCallToolResult</c> or a
/// <see cref="ToolCallContext"/>), so the marshaller can read them without reflection. Never serialized.
/// </summary>
internal interface IDetachedPayloadSource
{
    /// <summary>The payloads that belong in the result store rather than in the wire text, in the order they were detached.</summary>
    IReadOnlyList<(string Name, object Payload)> Detached { get; }
}

/// <summary>
/// The rules a detached payload must meet (proposal_side_stores_never_inline.md, slice 2): a plain name that is one
/// property under <c>successData</c>, and a value that serializes to a JSON array or string so <c>GetLargeResult</c>
/// can page it by <c>path</c>. Shared by <c>SentinelCallToolResult.Detach</c> and <see cref="ToolCallContext.Detach"/>.
/// </summary>
internal static class DetachedPayloads
{
    /// <summary>Throws <see cref="ArgumentException"/> when <paramref name="name"/> or <paramref name="payload"/> cannot be detached.</summary>
    internal static void Validate(string name, object payload)
    {
        if (string.IsNullOrWhiteSpace(name) || name.AsSpan().IndexOfAny('.', '[', ']') >= 0)
        {
            throw new ArgumentException(
                "A detached payload name must be one non-empty property name with no '.', '[' or ']' (it becomes the path successData.<name>).",
                nameof(name));
        }

        ArgumentNullException.ThrowIfNull(payload);

        if (payload is string)
        {
            return;
        }

        if (payload is IEnumerable && !IsMapLike(payload))
        {
            return;
        }

        throw new ArgumentException(
            $"A detached payload must be a list or a string so it can be paged by path; '{name}' is a {payload.GetType().Name}. Convert a dictionary to a list of records first.",
            nameof(payload));
    }

    // A dictionary enumerates (as key/value pairs) but serializes to a JSON object, which GetLargeResult cannot page.
    private static bool IsMapLike(object payload)
    {
        if (payload is IDictionary)
        {
            return true;
        }

        foreach (Type contract in payload.GetType().GetInterfaces())
        {
            if (contract.IsGenericType)
            {
                Type definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
