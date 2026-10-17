namespace RoslynSentinel.Common;

/// <summary>One same-named symbol that made a by-name lookup ambiguous; retry with FilePath + ContextSnippet (+ LineBefore when present). Counted=false marks a candidate dropped by narrowing (e.g. an interface member); FilePath alone selects it.</summary>
public sealed record AmbiguousCandidate(string FilePath, int Line, string Kind, string Signature, string ContainingType, string ContextSnippet, string? LineBefore, bool Counted);
