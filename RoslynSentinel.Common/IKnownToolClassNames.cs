namespace RoslynSentinel.Common;

/// <summary>Every tool-class name the registry knows about, implemented in the Server layer because the registry lives there.</summary>
public interface IKnownToolClassNames
{
    IReadOnlySet<string> Names { get; }
}
