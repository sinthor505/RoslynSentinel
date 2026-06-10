namespace RoslynSentinel.Common;

/// <summary>
/// Slim return type for <c>flag_migration_candidate</c>.
/// The attribute has already been written to disk before this result is returned.
/// </summary>
/// <param name="FilePath">Absolute path of the file that was modified.</param>
/// <param name="MethodName">Name of the method that received the <c>[MigrationCandidate]</c> attribute.</param>
/// <param name="Pattern">Migration pattern string that was applied (e.g. <c>"AsyncBridgeCandidate"</c>).</param>
/// <param name="Line">1-based source line of the method declaration after rewriting.</param>
/// <param name="WasAlreadyFlagged">
/// <c>true</c> if the method already carried a <c>[MigrationCandidate]</c> attribute with this exact
/// pattern — the attribute was replaced (idempotent update); <c>false</c> for a fresh flag.
/// </param>
/// <param name="PreviousPattern">
/// The pattern string from a pre-existing <c>[MigrationCandidate]</c> attribute, or <c>null</c>
/// if the method was not previously flagged.
/// </param>
/// <param name="AttributeClassInjected">
/// <c>true</c> if a new <c>MigrationCandidateAttribute.cs</c> file was generated alongside the
/// modification; <c>false</c> if the attribute class was already present in the solution.
/// </param>
/// <param name="Summary">Human-readable summary of the flag action for log display.</param>
public record FlagMigrationCandidateResult(
    FilePath filePath,
    string MethodName,
    string Pattern,
    int Line,
    bool WasAlreadyFlagged,
    string? PreviousPattern,
    bool AttributeClassInjected,
    string Summary
);
// v2 — ScanOptions() now derived from SentinelScanTools.scan_descriptors (single source of truth)