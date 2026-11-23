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
        return variant switch
        {
            ContractVariant.Legacy => AgentSystemPrompts.CodingAgent,
            ContractVariant.Standing => AgentSystemPrompts.CodingAgentContract,
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown contract variant."),
        };
    }

    /// <summary>Returns a short hash of the contract text, used to stamp runs.</summary>
    public static string Hash(string contractText)
    {
        var normalised = contractText.Replace("\r\n", "\n");
        return ContentFingerprint.Compute(normalised)[..HashLength];
    }

    /// <summary>Returns the stable name of an endings placement.</summary>
    public static string PlacementName(EndingsPlacement placement)
    {
        return placement switch
        {
            EndingsPlacement.System => "system",
            EndingsPlacement.SystemAndUser => "system+user",
            _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, "Unknown endings placement."),
        };
    }

    /// <summary>Parses a variant name; null or unknown values are handled by the implementation.</summary>
    public static ContractVariant ParseVariant(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "legacy", StringComparison.OrdinalIgnoreCase))
        {
            return ContractVariant.Legacy;
        }

        if (string.Equals(trimmed, "standing", StringComparison.OrdinalIgnoreCase))
        {
            return ContractVariant.Standing;
        }

        throw new ArgumentException($"Unknown contract variant '{value}'. Accepted values: legacy, standing.", nameof(value));
    }

    /// <summary>Parses an endings placement name; null or unknown values are handled by the implementation.</summary>
    public static EndingsPlacement ParsePlacement(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "system", StringComparison.OrdinalIgnoreCase))
        {
            return EndingsPlacement.System;
        }

        if (string.Equals(trimmed, "user", StringComparison.OrdinalIgnoreCase))
        {
            return EndingsPlacement.SystemAndUser;
        }

        throw new ArgumentException($"Unknown endings placement '{value}'. Accepted values: system, user.", nameof(value));
    }
}
