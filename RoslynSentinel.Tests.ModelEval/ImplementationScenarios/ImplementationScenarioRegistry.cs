using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>The 14 implementation scenarios in the order of the plan's scenario table.</summary>
internal static class ImplementationScenarioRegistry
{
    private const string ScenarioNamespace = "RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios";

    public static IReadOnlyList<ImplementationScenario> All { get; } =
    [
        PlanStalenessReviewScenario.Definition,
        WidenToReadOnlyDictionaryScenario.Definition,
        WideningRejectedByGateScenario.Definition,
        ChangeSetTypeFromSpecScenario.Definition,
        RetypeSliceExactLinesScenario.Definition,
        SweepConstructionSitesScenario.Definition,
        PropertyFirstRetypeScenario.Definition,
        RemovePinnedSentenceScenario.Definition,
        ThreadOptionalParameterScenario.Definition,
        NotFoundToTypedThrowScenario.Definition,
        BreakCycleWithForwardersScenario.Definition,
        ShareHelperDifferingRulesScenario.Definition,
        ObsoleteNamespaceCopyScenario.Definition,
        MigrateOntoEnvironmentHelperScenario.Definition,
    ];

    /// <summary>Case-insensitive lookup; throws listing the known ids when nothing matches.</summary>
    public static ImplementationScenario Find(string id)
    {
        var found = All.FirstOrDefault(scenario => string.Equals(scenario.Id, id, StringComparison.OrdinalIgnoreCase));
        return found ?? throw new ArgumentException(
            $"Unknown implementation scenario '{id}'. Known ids: {string.Join(", ", All.Select(scenario => scenario.Id))}.",
            nameof(id));
    }

    /// <summary>The scenario's definition class <c>&lt;Id&gt;Scenario</c> in the Scenarios namespace, or null when it does not exist.</summary>
    public static Type? FindScenarioClass(string id) =>
        typeof(ImplementationScenarioRegistry).Assembly.GetType($"{ScenarioNamespace}.{id}Scenario");
}
