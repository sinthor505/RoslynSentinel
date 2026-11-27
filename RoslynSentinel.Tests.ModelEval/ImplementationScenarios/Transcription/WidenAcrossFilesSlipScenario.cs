using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Sibling of WideningRejectedByGate with the compile-gate trap removed (CS9174, CS1503, CS0144): the callers are
/// converted in the seed, so the plain retype of Apply builds and the task is a multi-file retype of long generic types.
/// Ends Done; not registered until a later step.
/// </summary>
internal static class WidenAcrossFilesSlipScenario
{
    public static ImplementationScenario Definition { get; } = Build();

    private static ImplementationScenario Build()
    {
        var seeds = new Dictionary<string, string>(WideningRejectedByGateScenario.Definition.SeedFiles);
        seeds[WideningRejectedByGateScenario.CoreFolder + "ChangeJournal.cs"] = ScenarioTemplateEdits.ReplaceExactly(
            seeds[WideningRejectedByGateScenario.CoreFolder + "ChangeJournal.cs"],
            "Append(Dictionary<FilePath, string> entries)",
            "Append(IReadOnlyDictionary<FilePath, string> entries)");
        seeds[WideningRejectedByGateScenario.PublisherPath] = ScenarioTemplateEdits.ReplaceExactly(
            seeds[WideningRejectedByGateScenario.PublisherPath],
            "changes: []",
            "changes: new Dictionary<FilePath, string>()");
        seeds[WideningRejectedByGateScenario.TestsPath] = ScenarioTemplateEdits.ReplaceExactly(
            seeds[WideningRejectedByGateScenario.TestsPath],
            "new() {",
            "new Dictionary<FilePath, string> {",
            3);

        var golden = new Dictionary<string, string>
        {
            [WideningRejectedByGateScenario.InterfacePath] = ScenarioTemplateEdits.ReplaceExactly(
                seeds[WideningRejectedByGateScenario.InterfacePath],
                "Dictionary<FilePath, string> changes",
                "IReadOnlyDictionary<FilePath, string> changes"),
            [WideningRejectedByGateScenario.WriterPath] = ScenarioTemplateEdits.ReplaceExactly(
                seeds[WideningRejectedByGateScenario.WriterPath],
                "Dictionary<FilePath, string> changes",
                "IReadOnlyDictionary<FilePath, string> changes"),
            [WideningRejectedByGateScenario.FakePath] = ScenarioTemplateEdits.ReplaceExactly(
                seeds[WideningRejectedByGateScenario.FakePath],
                "Dictionary<FilePath, string> changes",
                "IReadOnlyDictionary<FilePath, string> changes"),
        };

        var task = WideningRejectedByGateScenario.StepTask with
        {
            IfFails = EvalContractOptions.PlainIfFails,
            Report = null,
        };
        var step1 = WideningRejectedByGateScenario.Definition.Steps[0] with
        {
            Prompt = EvalContractOptions.Render(task),
            GoldenFiles = golden,
            ExpectedEnding = ExpectedEnding.Done,
            ExpectedRedTests = null,
            ExtraCheck = null,
            Variants = null,
            PromptVariants = null,
            CannedFinalMessage = "DONE: retyped the changes parameter of Apply to IReadOnlyDictionary<FilePath, string> in IChangeWriter.cs, ChangeWriter.cs and FakeChangeWriter.cs.",
        };

        return WideningRejectedByGateScenario.Definition with
        {
            Id = "WidenAcrossFilesSlip",
            Title = "Widen Apply's change-set parameter across three files",
            Axis = "Transcription: long generic types in a multi-file batch; the gate trap is removed",
            Source = "Sibling of WideningRejectedByGate; plans/plan_slip_focused_scenario_variants.md",
            SeedFiles = seeds,
            PrimaryAxis = EvalAxis.Transcription,
            Steps = [step1],
        };
    }
}
