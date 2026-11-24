namespace RoslynSentinel.Common.AgentLoop;

/// <summary>The leading outcome token of a model's final message: DONE:, BLOCKED: or neither.</summary>
public enum FinalOutcome
{
    None,
    Done,
    Blocked
}

/// <summary>Reads the DONE:/BLOCKED: token that starts a final message. Pure functions, no state.</summary>
public static class FinalOutcomeToken
{
    /// <summary>Done or Blocked when the first non-blank line starts with that token; otherwise None.</summary>
    public static FinalOutcome Parse(string? message)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }
}
