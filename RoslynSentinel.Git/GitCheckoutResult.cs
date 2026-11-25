namespace RoslynSentinel.Tools.Basic;

public record GitCheckoutResult : GitResult
{
    public string Branch { get; set; } = "";
    public bool CreatedNewBranch
    {
        get; set;
    }

    /// <summary>Set when the call did something other than what its parameters literally asked
    /// for, e.g. createBranch=true on a branch that already existed (plain checkout, startPoint
    /// ignored). Null otherwise.</summary>
    public string? Note
    {
        get; set;
    }
}
