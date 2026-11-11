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

    /// <summary>
    /// A name this edit uses that is declared nowhere in the solution and appears only in a recent edit of another file that the compile gate rejected
    /// (parallel calls are validated one at a time against the committed solution, so the second call cannot see the first). <see cref="SymbolChange.OldName"/>
    /// is the name and <see cref="SymbolChange.File"/> the file of the rejected edit.
    /// </summary>
    Unapplied,
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
/// <param name="Verdicts">The machine-readable form of <paramref name="Changes"/> (added declarations left out, capped), for the wire.</param>
public sealed record EditImpactResult(
    string Text,
    string NextStepLine,
    IReadOnlyList<SymbolChange> Changes,
    IReadOnlyList<DiagnosticInfo> AttributedDiagnostics,
    IReadOnlyList<DiagnosticInfo> UnattributedDiagnostics,
    IReadOnlyList<ImpactVerdict>? Verdicts = null);

/// <summary>
/// What the compile gate concluded about one symbol change of an edit, as an item of <c>errorData.structuredDetail</c> on a rejected ReplaceSnippet (and as
/// text in the findings of a lifted write): lets a harness assert on the verdict instead of on message text. Property names are the wire contract.
/// </summary>
/// <param name="Kind">"renamed", "removed", "signatureChanged", "accessibilityChanged" or "unapplied" (a name only a rejected sibling edit declares).</param>
/// <param name="DeclarationKind">"class", "method", "property", "local", "local function", "name", ... as in <see cref="SymbolChange.DeclarationKind"/>.</param>
/// <param name="Container">Namespace and type path of the declaration; for a local, "Type.Member".</param>
/// <param name="OldName">The name in the baseline.</param>
/// <param name="NewName">The new name for a rename; otherwise null.</param>
/// <param name="Location">"File.cs:line" of the declaration after the edit (before it for a removal); null when unknown.</param>
/// <param name="Verdict">"valid" (nothing outside the edit breaks), "breaksUses" (<paramref name="BrokenUseCount"/> uses break), "lifted" (the rename was applied as RenameSymbol) or "liftRejected" (it was tried and the result still had errors).</param>
/// <param name="BrokenUseCount">How many diagnostics outside the edit follow from this change (for "lifted": the call sites the rename rewrote).</param>
/// <param name="BrokenUses">The first few of those diagnostics; <paramref name="BrokenUseCount"/> is the total.</param>
/// <param name="SuggestedTool">The tool the next step names, when one fits and is callable; otherwise null.</param>
/// <param name="NextStep">The next-step sentence for this change when it breaks uses; otherwise null.</param>
public sealed record ImpactVerdict(
    string Kind,
    string DeclarationKind,
    string Container,
    string OldName,
    string? NewName,
    string? Location,
    string Verdict,
    int BrokenUseCount,
    IReadOnlyList<DiagnosticInfo> BrokenUses,
    string? SuggestedTool,
    string? NextStep);

/// <summary>
/// The single item of <c>errorData.structuredDetail</c> on a rejected ReplaceSnippet (single and batch): the impact verdicts and the compiler diagnostics
/// that rejected the edit, as two named lists instead of one mixed list. <c>structuredDetail</c> is a list of untyped items, so this is its only element.
/// </summary>
/// <param name="Verdicts">One <see cref="ImpactVerdict"/> per changed symbol (see <see cref="EditImpactResult.Verdicts"/>), the lift attempt first; empty when no impact analysis was attributable.</param>
/// <param name="Diagnostics">The compile gate's diagnostics for the rejected edit.</param>
public sealed record EditImpactDetail(
    IReadOnlyList<ImpactVerdict> Verdicts,
    IReadOnlyList<DiagnosticInfo> Diagnostics);
