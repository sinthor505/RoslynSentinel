using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Sibling of WidenToReadOnlyDictionary whose prompt names the leaf-first retype order; not registered until a later step.
/// </summary>
internal static class WidenDictionaryLeafFirstSlipScenario
{
    internal sealed record RetypeTarget(string TypeName, string MethodName, int ParameterCount);

    // Forwarding edges 2->1, 3->1, 4->3, 9->5 and 6, 10->2 (1-based indexes; the callee index is lower than the caller's).
    internal static IReadOnlyList<RetypeTarget> LeafFirstOrder { get; } = new[]
    {
        new RetypeTarget("ChangeValidator", "Validate", 2),
        new RetypeTarget("ChangeValidator", "Validate", 1),
        new RetypeTarget("ChangeValidator", "ValidateWithCandidate", 4),
        new RetypeTarget("ChangeValidator", "ValidateWithCandidate", 3),
        new RetypeTarget("ChangeHelper", "Count", 1),
        new RetypeTarget("ChangeHelper", "TotalLength", 1),
        new RetypeTarget("ChangeHelper", "SortedPaths", 1),
        new RetypeTarget("ChangeHelper", "Touches", 2),
        new RetypeTarget("ChangeHelper", "Describe", 1),
        new RetypeTarget("ChangeHelper", "IsValid", 1),
    };

    public static ImplementationScenario Definition { get; } = Build();

    private static ImplementationScenario Build()
    {
        var task = WidenToReadOnlyDictionaryScenario.StepTask with
        {
            Conditions = WidenToReadOnlyDictionaryScenario.StepTask.Conditions
                + " Retype the parameters in this order; each change compiles on its own: "
                + string.Join("; ", LeafFirstOrder.Select((t, i) => $"{i + 1}. {t.TypeName}.{t.MethodName} ({t.ParameterCount} parameters)")),
        };
        var step1 = WidenToReadOnlyDictionaryScenario.Definition.Steps[0] with
        {
            Prompt = EvalContractOptions.Render(task),
            Variants = null,
            PromptVariants = null,
        };
        return WidenToReadOnlyDictionaryScenario.Definition with
        {
            Id = "WidenDictionaryLeafFirstSlip",
            Title = "Widen two files' change-set parameters in a named leaf-first order",
            Axis = "Transcription: escape-heavy doc comments and crefs; the edit order is named",
            Source = "Sibling of WidenToReadOnlyDictionary; plans/plan_slip_focused_scenario_variants.md",
            PrimaryAxis = EvalAxis.Transcription,
            Steps = [step1],
        };
    }
}
