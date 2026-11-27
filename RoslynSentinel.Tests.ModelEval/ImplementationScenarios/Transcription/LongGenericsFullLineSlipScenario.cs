namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Variant A of the LongGenerics slip family: the same 36 sites as <see cref="LongGenericsSlipScenario"/>, with the prompt giving each changed site's complete new declaration.
/// </summary>
/// <remarks>
/// Measured data and the sibling comparison: the variant guide on <see cref="LongGenericsSlipScenario"/>. Here: residual 0.01 / 0.01,
/// collateral 0.17 / 0.20, pass 4/6 and 3/6, E2 3 / 3 runs.
/// Strength: the purest copy task; almost no per-site slips, because the model pastes text it was given.
/// Weakness: it changes the task shape. With every new line in the prompt, runs write a few large blocks (3-6 edits) instead of
/// one edit per site, and two of eleven graded runs deleted all six untouched controls. The residual rate hides that; always read
/// the collateral rate beside it.
/// Use it to measure block-copy behaviour (dropped or altered untouched members), not per-site transcription slips.
/// </remarks>
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
