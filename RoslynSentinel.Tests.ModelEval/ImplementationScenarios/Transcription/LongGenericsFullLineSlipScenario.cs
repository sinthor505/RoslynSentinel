namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Variant A of the LongGenerics slip family: the same 36 sites as <see cref="LongGenericsSlipScenario"/>, with the prompt giving each changed site's complete new declaration; not registered until a later step.
/// </summary>
internal static class LongGenericsFullLineSlipScenario
{
    // Initialisation order matters: Sites must be declared before Definition.
    private static readonly SlipSiteSet Sites = LongGenericsSlipScenario.Sites with
    {
        FilePath = "ContosoOrders.Core/Evals/LongGenericsFullLineSlip/LongGenericDeclarations.cs",
        Namespace = "ContosoOrders.Evals.LongGenericsFullLineSlip",
        PromptStyle = SlipPromptStyle.FullLine,
    };

    public static ImplementationScenario Definition { get; } = SlipFamilyBuilder.Build(
        "LongGenericsFullLineSlip",
        "Copy 30 long nested generic declarations given in full",
        "plans/plan_longgenerics_transcription_variants.md (variant A)",
        Sites);
}
