namespace RoslynSentinel.Common.AgentLoop;

/// <summary>Which workflow contract text a scenario run uses.</summary>
public enum ContractVariant
{
    /// <summary>The pre-contract prompt wording.</summary>
    Legacy,

    /// <summary>The standing contract wording.</summary>
    Standing,
}

/// <summary>Where the contract endings block is placed in the prompt.</summary>
public enum EndingsPlacement
{
    /// <summary>Endings go in the system prompt only.</summary>
    System,

    /// <summary>Endings go in the system prompt and are repeated in the user turn.</summary>
    SystemAndUser,
}

/// <summary>Helpers for selecting, hashing and naming the workflow contract text.</summary>
public static class WorkflowContract
{
    /// <summary>Number of hex characters kept from the contract hash.</summary>
    public const int HashLength = 12;

    /// <summary>Returns the contract text for a variant.</summary>
    public static string TextFor(ContractVariant variant)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>Returns a short hash of the contract text, used to stamp runs.</summary>
    public static string Hash(string contractText)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>Returns the stable name of an endings placement.</summary>
    public static string PlacementName(EndingsPlacement placement)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>Parses a variant name; null or unknown values are handled by the implementation.</summary>
    public static ContractVariant ParseVariant(string? value)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    /// <summary>Parses an endings placement name; null or unknown values are handled by the implementation.</summary>
    public static EndingsPlacement ParsePlacement(string? value)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }
}
