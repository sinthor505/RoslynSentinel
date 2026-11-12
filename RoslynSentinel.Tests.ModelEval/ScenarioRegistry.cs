namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Catalogue of targeted model-eval scenarios, looked up by id.
/// </summary>
public static class ScenarioRegistry
{
    // Later slices add entries here.
    private static readonly ScenarioDefinition[] Definitions =
    [
        RenameScenarios.CalcDiscRename,
        TwoRenameScenarios.TwoRenamesWholeFile,
        InterfaceScenarios.InterfaceMemberRedeclared,
        BatchShapeScenarios.MixedSingleAndBatchShape,
        GuardScenarios.TwinApplyCalls,
        GuardScenarios.EmptyInterfaceRemoval,
        FaultScenarios.RenameSymbolTypo,
        FaultScenarios.RenameSymbolNoSuchMethod,
        FaultScenarios.ReplaceSnippetWrongFileNearMatch,
        FaultScenarios.ReplaceSnippetWrongFileNoMatch,
        ResumeScenarios.StaleCallerEditAfterRename,
        ResumeScenarios.StaleWorkspaceAfterRename,
    ];

    /// <summary>All registered scenarios, in declaration order.</summary>
    public static IReadOnlyList<ScenarioDefinition> All => Definitions;

    /// <summary>
    /// Returns the first scenario whose Id equals <paramref name="id"/> ignoring case (ordinal),
    /// or null for a null, empty or unknown id.
    /// </summary>
    public static ScenarioDefinition? Find(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        foreach (var definition in Definitions)
        {
            if (string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }
}
