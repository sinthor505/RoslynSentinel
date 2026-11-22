using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>How a caller file is switched to the new namespace by <see cref="CopyAndObsoleteCallerRewrite"/>.</summary>
public enum CallerRewriteMode
{
    /// <summary>A <c>using &lt;NewNs&gt;;</c> directive is added and <c>using &lt;OldNs&gt;;</c> is removed when nothing else in the file needs it.</summary>
    Using,

    /// <summary>Each reference is written <c>&lt;NewNs&gt;.&lt;Type&gt;</c>; the using directives stay as they are.</summary>
    Qualify,
}

/// <summary>One caller file that <see cref="CopyAndObsoleteCallerRewrite"/> changed.</summary>
/// <param name="FilePath">The caller file, as the solution knows it.</param>
/// <param name="NewText">The caller's text after the rewrite; everything outside the edited spots (BOM, line endings, comments) is as it was.</param>
/// <param name="Mode">The mode that was applied (the forced one when the caller of the pass named one for this file).</param>
/// <param name="ReferencesRewritten">References to the moved types in this file that now name the new namespace, by a text edit or through the added using directive.</param>
public sealed record CallerFileRewrite(FilePathWrapper FilePath, string NewText, CallerRewriteMode Mode, int ReferencesRewritten);

/// <summary>
/// Outcome of one <see cref="CopyAndObsoleteCallerRewrite"/> pass: the caller files that changed, plus the facts a report carries
/// (why a file was qualified instead of getting a using, generated files left alone, a global using of the old namespace), or a refusal.
/// </summary>
/// <param name="Files">The changed caller files, ordered by path; files with no reference, or whose text would not change, are absent.</param>
/// <param name="Facts">Observations about the pass, one sentence each; empty when there is nothing to note.</param>
/// <param name="Refusal">The refusal, with its error code and a facts-and-options message; null when the pass ran.</param>
public sealed record CallerRewriteResult(IReadOnlyList<CallerFileRewrite> Files, IReadOnlyList<string> Facts, ResultError? Refusal)
{
    /// <summary>True when the pass did not run.</summary>
    public bool IsRefusal => Refusal is not null;

    /// <summary>A refusal with a freshly built error.</summary>
    public static CallerRewriteResult Refused(string errorCode, string message) => Refused(new ResultError(errorCode, message));

    /// <summary>A refusal that carries an existing error (for example one a check or lookup already produced).</summary>
    public static CallerRewriteResult Refused(ResultError error) => new([], [], error);
}
