using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Reads the eval workflow contract env vars and builds the system prompt, rendered task and contract stamp from them.</summary>
public static class EvalContractOptions
{
    /// <summary>Environment variable that selects the contract variant (legacy or standing).</summary>
    public const string ContractEnvVar = "ROSLYNSENTINEL_MODELEVAL_CONTRACT";

    /// <summary>Environment variable that selects where the endings sentence is placed (system or system and user).</summary>
    public const string EndingsEnvVar = "ROSLYNSENTINEL_MODELEVAL_CONTRACT_ENDINGS";

    /// <summary>Parses both values; a bad value throws an ArgumentException that names its variable, and the endings-user placement needs the standing variant.</summary>
    public static (ContractVariant Variant, EndingsPlacement Placement) Resolve(string? contractValue, string? endingsValue)
    {
        ContractVariant variant;
        try
        {
            variant = WorkflowContract.ParseVariant(contractValue);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{ContractEnvVar}: {ex.Message}", ex);
        }

        EndingsPlacement placement;
        try
        {
            placement = WorkflowContract.ParsePlacement(endingsValue);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{EndingsEnvVar}: {ex.Message}", ex);
        }

        if (placement == EndingsPlacement.SystemAndUser && variant == ContractVariant.Legacy)
        {
            throw new ArgumentException(
                $"{EndingsEnvVar}=user needs {ContractEnvVar}=standing: the Endings describe DONE:/BLOCKED:, " +
                "which only the standing contract defines.");
        }

        return (variant, placement);
    }

    /// <summary>Resolves the contract and placement from the current process environment on every call; no cached state.</summary>
    public static (ContractVariant Variant, EndingsPlacement Placement) Current() =>
        Resolve(Environment.GetEnvironmentVariable(ContractEnvVar), Environment.GetEnvironmentVariable(EndingsEnvVar));

    /// <summary>Builds the system prompt for the current contract variant and the configured error policy.</summary>
    public static string ComposeSystemPrompt() => ComposeSystemPrompt(Current().Variant, ErrorPolicyOptions.Mode, ErrorPolicyOptions.PolicyFilePath);

    /// <summary>Builds the system prompt for an explicit contract variant, error policy mode and policy file.</summary>
    public static string ComposeSystemPrompt(ContractVariant variant, ErrorPolicyMode mode, string? policyFilePath) =>
        ErrorPolicyWiring.ComposePrompt(WorkflowContract.TextFor(variant), mode, policyFilePath);

    /// <summary>Renders the scenario task using the current endings placement.</summary>
    public static string Render(ScenarioTask task) => Render(task, Current().Placement);

    /// <summary>Renders the scenario task, repeating the endings sentence in the user message when the placement asks for it.</summary>
    public static string Render(ScenarioTask task, EndingsPlacement placement) =>
        task.Render(repeatEndings: placement == EndingsPlacement.SystemAndUser);

    /// <summary>Builds the contract stamp (text hash and placement name) for the current environment.</summary>
    public static (string Hash, string Placement) BuildContractStamp()
    {
        var current = Current();
        return BuildContractStamp(current.Variant, current.Placement);
    }

    /// <summary>Builds the contract stamp (text hash and placement name) for an explicit variant and placement.</summary>
    public static (string Hash, string Placement) BuildContractStamp(ContractVariant variant, EndingsPlacement placement) =>
        (WorkflowContract.Hash(WorkflowContract.TextFor(variant)), WorkflowContract.PlacementName(placement));
}
