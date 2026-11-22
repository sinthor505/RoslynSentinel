using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Outcome of <see cref="CopyAndObsoleteEdits.BuildAsync(Microsoft.CodeAnalysis.Solution, FilePathWrapper, string, string, string, IReadOnlyList{TopLevelType}, CancellationToken)"/>:
/// the text of the new file and of the old file, plus the facts MoveFile reports, or a refusal. Both texts keep the source's BOM and line endings.
/// <see cref="Refusal"/> is null when the texts are populated.
/// </summary>
/// <param name="NewText">The copy: the source with the destination's namespace and the using directives it needs, without [Obsolete]; null on a refusal.</param>
/// <param name="OldText">The original file with [System.Obsolete("Use ...")] on every top-level type that did not already carry one; null on a refusal.</param>
/// <param name="AddedUsings">The namespaces a using directive was added for in the copy, in the order they were inserted; empty when none.</param>
/// <param name="StaticFieldTypeNames">Top-level types that hold static non-const fields (the copy has separate static state), in source order.</param>
/// <param name="AlreadyObsoleteTypeNames">Top-level types that already carried an Obsolete attribute and were left unchanged in <see cref="OldText"/>.</param>
/// <param name="Refusal">The refusal, with its error code and a facts-and-options message; null when the texts are populated.</param>
public sealed record CopyAndObsoleteEditResult(
    string? NewText,
    string? OldText,
    IReadOnlyList<string> AddedUsings,
    IReadOnlyList<string> StaticFieldTypeNames,
    IReadOnlyList<string> AlreadyObsoleteTypeNames,
    ResultError? Refusal)
{
    /// <summary>True when no texts were produced.</summary>
    public bool IsRefusal => Refusal is not null;

    /// <summary>A refusal with a freshly built error.</summary>
    public static CopyAndObsoleteEditResult Refused(string errorCode, string message) => Refused(new ResultError(errorCode, message));

    /// <summary>A refusal that carries an existing error (for example one a check or lookup already produced).</summary>
    public static CopyAndObsoleteEditResult Refused(ResultError error) => new(null, null, [], [], [], error);
}
