using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public readonly struct SymbolResolution
{
    public ISymbol? Symbol { get; init; }
    public SymbolHandle Handle { get; init; }
    public EngineError? Error { get; init; }

    public bool Resolved => Error is null;
}
