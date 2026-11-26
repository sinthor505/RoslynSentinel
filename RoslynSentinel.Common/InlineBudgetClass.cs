namespace RoslynSentinel.Common;

/// <summary>
/// Inline budget class a tool declares through <see cref="InlineBudgetAttribute"/>: how much of a tool result
/// body may be returned inline before the result is paged.
/// </summary>
public enum InlineBudgetClass
{
    /// <summary>The configured default budget, for routine results that page harmlessly.</summary>
    Default,

    /// <summary>Results the caller needs in one piece.</summary>
    Whole,
}
