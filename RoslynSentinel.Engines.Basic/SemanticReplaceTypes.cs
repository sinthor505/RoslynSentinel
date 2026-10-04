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
