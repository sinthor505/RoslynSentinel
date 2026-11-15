using RoslynSentinel.Common;

namespace RoslynSentinel.Server.Advanced;

/// <summary>
/// Registry-backed <see cref="IKnownToolClassNames"/>: the union of every tool class named in
/// <see cref="ToolClassRegistry.ModeToToolClasses"/>, computed once. Case-insensitive, matching the
/// lookup used for --include-tools/--exclude-tools in ServerStartupHelpers.
/// </summary>
internal sealed class KnownToolClassNames : IKnownToolClassNames
{
    private readonly HashSet<string> _names =
        new(
            ToolClassRegistry.ModeToToolClasses.Values
                .SelectMany(classes => classes),
            StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Names => _names;
}
