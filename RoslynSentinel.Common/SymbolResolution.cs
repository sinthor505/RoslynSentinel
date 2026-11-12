using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public readonly struct SymbolResolution
{
    public ISymbol? Symbol { get; init; }
    public SymbolHandle Handle { get; init; }
    public EngineError? Error { get; init; }

    /// <summary>
    /// Set on a resolved result when the id was repaired on the way (a method id without its parameter list resolved to
    /// the only method of that name): says what was passed and the full id to pass next time. Null otherwise. Engine
    /// plumbing, not a wire type: RenameSymbol copies it into its response, other callers may ignore it.
    /// </summary>
    public string? Note { get; init; }

    public bool Resolved => Error is null;
}
