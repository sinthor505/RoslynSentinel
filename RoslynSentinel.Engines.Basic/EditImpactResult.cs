namespace RoslynSentinel.Engines.Basic;

/// <summary>How one declaration differs between the baseline and the candidate text of the edited files.</summary>
public enum SymbolChangeKind
{
    /// <summary>Declared before, gone after, and not paired with an added declaration by the conservative rename rule.</summary>
    Removed,

    /// <summary>Declared after, not before.</summary>
    Added,

    /// <summary>Same container, kind and name; parameter types or return type differ.</summary>
    SignatureChanged,

    /// <summary>Same container, kind, name, parameters and return type; only the declared accessibility differs.</summary>
    AccessibilityChanged,

    /// <summary>One removed and one added declaration of the same kind in the same container with equal parameter types and return type, unique on both sides; the name, and possibly the accessibility, differ.</summary>
    Renamed,
}

/// <summary>
/// One entry of the symbol diff produced by <see cref="EditImpactAnalyzer.DiffSymbols"/>.
/// </summary>
/// <param name="Kind">How the declaration changed.</param>
/// <param name="DeclarationKind">"class", "struct", "record", "interface", "enum", "delegate", "method", "ctor", "property", "indexer", "field", "event" or "enum member".</param>
/// <param name="Container">Namespace and containing type path, or empty at the top level.</param>
/// <param name="OldName">The name in the baseline (for an added declaration, the new name).</param>
/// <param name="NewName">The new name for <see cref="SymbolChangeKind.Renamed"/>; otherwise null.</param>
/// <param name="File">File name (no directory) holding the declaration in the candidate (baseline for a removed one); null when unknown.</param>
/// <param name="Line">1-based line of the declaration in <paramref name="File"/>; 0 when unknown.</param>
/// <param name="OldSignature">Rendered parameter list and return type before the edit (null for an added declaration).</param>
/// <param name="NewSignature">Rendered parameter list and return type after the edit (null for a removed declaration).</param>
/// <param name="AccessibilityNarrowed">True for <see cref="SymbolChangeKind.AccessibilityChanged"/> when the new accessibility is more restrictive.</param>
/// <param name="NewAccessibility">For <see cref="SymbolChangeKind.Renamed"/> of a member whose declared accessibility also changed: the new accessibility (for example "internal"); otherwise null.</param>
public sealed record SymbolChange(
    SymbolChangeKind Kind,
    string DeclarationKind,
    string Container,
    string OldName,
    string? NewName,
    string? File,
    int Line,
    string? OldSignature,
    string? NewSignature,
    bool AccessibilityNarrowed = false,
    string? NewAccessibility = null);

/// <summary>
/// What a rejected edit did to the symbols it touched, and which diagnostics in files the edit did not change follow from it.
/// Produced only when at least one such diagnostic is attributed to a diff entry; callers fall back to the plain
/// compiler-error text otherwise. Engine-internal: it is not an operation result and carries no status.
/// </summary>
/// <param name="Text">Short report (about 10 lines, next step first) ready to prepend to the error text.</param>
/// <param name="NextStepLine">The first line of <paramref name="Text"/>, without its "Next step:" prefix.</param>
/// <param name="Changes">The whole symbol diff of the edited files, attributed or not.</param>
/// <param name="AttributedDiagnostics">Diagnostics in unedited files that were linked to a diff entry by the old identifier.</param>
/// <param name="UnattributedDiagnostics">Diagnostics in unedited files with no matching diff entry; the caller lists these as it does today.</param>
public sealed record EditImpactResult(
    string Text,
    string NextStepLine,
    IReadOnlyList<SymbolChange> Changes,
    IReadOnlyList<DiagnosticInfo> AttributedDiagnostics,
    IReadOnlyList<DiagnosticInfo> UnattributedDiagnostics);
