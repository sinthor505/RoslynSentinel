namespace RoslynSentinel.Engines.Basic;

/// <summary>Classifies a reference to a symbol by its syntactic role.</summary>
public enum SemanticReplaceRole
{
    /// <summary>Left side of an assignment with a literal value (true/false).</summary>
    WriteLiteral,

    /// <summary>Left side of an assignment with a non-literal expression.</summary>
    WriteExpression,

    /// <summary>Reference used as a value in a read context.</summary>
    Read,

    /// <summary>Reference used in a negated context (inside a ! prefix operator).</summary>
    NegatedRead,

    /// <summary>Reference inside nameof(...).</summary>
    NameOf,

    /// <summary>Initializer in a field or property declaration.</summary>
    DeclarationInitializer,

    /// <summary>Reference in an unsupported syntactic context.</summary>
    Unsupported
}

/// <summary>Describes one reference site that will be or was rewritten by a semantic find-replace operation.</summary>
public record SemanticReplaceSite(
    string FilePath,
    int Line,
    SemanticReplaceRole Role,
    string Before,
    string After,
    string? UnsupportedReason
);
/// <summary>
/// One text edit produced while collecting sites: replace <paramref name="Span"/> (offsets in the ORIGINAL text of the
/// file) with <paramref name="NewText"/>. A zero-length span is a pure insertion at that position.
/// </summary>
/// <remarks>
/// Apply order for one file is "start descending, then end descending" (see <see cref="ApplicationOrder"/>), so earlier
/// edits never shift the spans of later ones. The end-descending tie-break matters only for a zero-length insertion that
/// shares a start with a non-empty edit: the non-empty edit is applied first, then the insertion lands in front of its text.
/// An insertion at the END of another edit's span has the larger start, so it is applied first and lands after that edit's text.
/// </remarks>
public sealed record ReferenceEdit(string FilePath, Microsoft.CodeAnalysis.Text.TextSpan Span, string NewText)
{
    /// <summary>Total order for applying edits: file path (ordinal) ascending, then span start descending, then span end descending.</summary>
    public static int ApplicationOrder(ReferenceEdit left, ReferenceEdit right)
    {
        var byFile = string.CompareOrdinal(left.FilePath, right.FilePath);
        if (byFile != 0)
        {
            return byFile;
        }

        var byStart = right.Span.Start.CompareTo(left.Span.Start);
        return byStart != 0 ? byStart : right.Span.End.CompareTo(left.Span.End);
    }
}

/// <summary>Result of a semantic find-replace operation: sites found, file changes, and any error that occurred.</summary>
public sealed record SemanticReplaceOutcome(List<SemanticReplaceSite> Sites, Dictionary<FilePathWrapper, string> Changes, ResultError? Error);
