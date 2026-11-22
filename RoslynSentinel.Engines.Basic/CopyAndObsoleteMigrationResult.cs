using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// A caller of the moved types that <see cref="CopyAndObsoleteMigration"/> could not move to the new namespace: its text is unchanged and it keeps
/// using the old, now [Obsolete] types (compiler warnings only), so it is left for a hand migration. The error is the one that made the rewrite
/// of this file revert, with the file and line where the compiler reported it (the same file, or a file that depends on it).
/// </summary>
/// <param name="FilePath">The caller file, as the solution knows it.</param>
/// <param name="FirstReferenceLine">1-based line of the first reference to a moved type in the unchanged file; 1 when no reference could be located.</param>
/// <param name="ErrorCode">The compiler error id that blocked the rewrite, for example CS1503.</param>
/// <param name="ErrorMessage">First line of the compiler message, shortened; no stack trace.</param>
/// <param name="BlockingFile">The file the compiler reported the error in.</param>
/// <param name="BlockingLine">1-based line of the error in <paramref name="BlockingFile"/>; 0 when the error has no location.</param>
public sealed record RemainingObsoleteCaller(FilePathWrapper FilePath, int FirstReferenceLine, string ErrorCode, string ErrorMessage, FilePathWrapper BlockingFile, int BlockingLine);

/// <summary>
/// Outcome of <see cref="CopyAndObsoleteMigration.RunAsync"/>: the change set to apply (new file, marked original and the migrated callers) that was
/// compiled error-free in memory, which callers moved and which stay on the old types, or a refusal when the copy and the marked original alone do not compile.
/// </summary>
/// <param name="Changes">Document path to full new text: the copy at the destination, the marked original and every migrated caller; null on a refusal.</param>
/// <param name="MigratedCallers">The callers rewritten in <paramref name="Changes"/>, ordered by path, with the mode that finally compiled.</param>
/// <param name="RemainingCallers">Callers left unchanged because their rewrite did not compile, ordered by path.</param>
/// <param name="AddedUsings">Namespaces a using directive was added for in the copy.</param>
/// <param name="StaticFieldTypeNames">Moved types that hold static non-const fields (the copy has separate static state).</param>
/// <param name="AlreadyObsoleteTypeNames">Moved types that already carried an Obsolete attribute and were left unchanged in the original.</param>
/// <param name="Facts">Observations of the last caller pass and of the repair loop, one sentence each.</param>
/// <param name="RoundsUsed">Rounds of the repair loop that ran: 1 is the initial pass (also when there are no callers), at most <see cref="CopyAndObsoleteMigration.MaxCompileRounds"/>. A final verification compile after the last round is not counted.</param>
/// <param name="FellBackToBaseSet">True when the surviving callers still did not compile together after the last round, so every caller was left unchanged.</param>
/// <param name="Refusal">The refusal, with its error code and a facts-and-options message; null when <paramref name="Changes"/> is populated.</param>
public sealed record CopyAndObsoleteMigrationResult(
    DocumentChangeSet? Changes,
    IReadOnlyList<CallerFileRewrite> MigratedCallers,
    IReadOnlyList<RemainingObsoleteCaller> RemainingCallers,
    IReadOnlyList<string> AddedUsings,
    IReadOnlyList<string> StaticFieldTypeNames,
    IReadOnlyList<string> AlreadyObsoleteTypeNames,
    IReadOnlyList<string> Facts,
    int RoundsUsed,
    bool FellBackToBaseSet,
    ResultError? Refusal)
{
    /// <summary>True when no change set was produced.</summary>
    public bool IsRefusal => Refusal is not null;

    /// <summary>A refusal with a freshly built error.</summary>
    public static CopyAndObsoleteMigrationResult Refused(string errorCode, string message) => Refused(new ResultError(errorCode, message));

    /// <summary>A refusal that carries an existing error (for example one an earlier step produced).</summary>
    public static CopyAndObsoleteMigrationResult Refused(ResultError error) => new(null, [], [], [], [], [], [], 0, false, error);
}
