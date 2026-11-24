using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.AmbiguityResolution;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.ErrorRecovery;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.MultiStepState;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.ToolUse;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>The 15 implementation scenarios: the 14 of the plan's scenario table in its order, then LargeBlockRewrite (edit-tool comparison).</summary>
internal static class ImplementationScenarioRegistry
{
    private const string ScenarioRoot = "RoslynSentinel.Tests.ModelEval.ImplementationScenarios";

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
        LargeBlockRewriteScenario.Definition,
    ];

    /// <summary>Case-insensitive lookup; throws listing the known ids when nothing matches.</summary>
    public static ImplementationScenario Find(string id)
    {
        var found = All.FirstOrDefault(scenario => string.Equals(scenario.Id, id, StringComparison.OrdinalIgnoreCase));
        return found ?? throw new ArgumentException(
            $"Unknown implementation scenario '{id}'. Known ids: {string.Join(", ", All.Select(scenario => scenario.Id))}.",
            nameof(id));
    }

    /// <summary>The scenario's definition class <c>&lt;Id&gt;Scenario</c> in any folder under the scenarios namespace, or null when it does not exist.</summary>
    public static Type? FindScenarioClass(string id) =>
        typeof(ImplementationScenarioRegistry).Assembly.GetTypes()
            .FirstOrDefault(type => type.Name == $"{id}Scenario"
                && type.Namespace is { } ns
                && (ns == ScenarioRoot || ns.StartsWith(ScenarioRoot + ".", StringComparison.Ordinal)));
}
