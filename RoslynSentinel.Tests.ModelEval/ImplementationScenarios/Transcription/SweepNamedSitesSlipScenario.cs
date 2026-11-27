using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.ErrorRecovery;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Transcription;

/// <summary>
/// Sibling of SweepConstructionSites with the 3 unconvertible sites, the FailedFiles decoy and the Report check removed; not registered until a later step.
/// </summary>
internal static class SweepNamedSitesSlipScenario
{
    internal sealed record SweepSite(string FileName, string Method);

    // Combine holds 2 creations (one per Merge argument), so it is listed once here but converts two sites.
    internal static IReadOnlyList<SweepSite> SitesToConvert { get; } = new[]
    {
        new SweepSite("Planner.cs", "PlanAdd"),
        new SweepSite("Planner.cs", "PlanTwo"),
        new SweepSite("Planner.cs", "PlanCopy"),
        new SweepSite("Renamer.cs", "RenameAll"),
        new SweepSite("Renamer.cs", "Preview"),
        new SweepSite("Renamer.cs", "None"),
        new SweepSite("Mover.cs", "Move"),
        new SweepSite("Mover.cs", "MoveWith"),
        new SweepSite("Mover.cs", "Stage"),
        new SweepSite("Mover.cs", "Overlay"),
        new SweepSite("Merger.cs", "Merge"),
        new SweepSite("Merger.cs", "MergeAll"),
        new SweepSite("Merger.cs", "Combine"),
    };

    public static ImplementationScenario Definition { get; } = Build();

    private static ImplementationScenario Build()
    {
        var seeds = new Dictionary<string, string>(SweepConstructionSitesScenario.Definition.SeedFiles);
        foreach (var path in SweepConstructionSitesScenario.TargetPaths)
        {
            seeds[path] = EditTarget(path, seeds[path]);
        }

        var planResultPath = SweepConstructionSitesScenario.CoreFolder + "PlanResult.cs";
        seeds[planResultPath] = ScenarioTemplateEdits.ReplaceExactly(
            ScenarioTemplateEdits.ReplaceExactly(
                seeds[planResultPath],
                "public sealed record PlanResult(Dictionary<FilePath, string> Changes, Dictionary<FilePath, string> FailedFiles);",
                "public sealed record PlanResult(Dictionary<FilePath, string> Changes);"),
            "/// <summary>New content per path, and a failure reason per path that could not be planned.</summary>",
            "/// <summary>New content per path.</summary>");

        var sweepTestsPath = SweepConstructionSitesScenario.TestFolder + "SweepTests.cs";
        seeds[sweepTestsPath] = ScenarioTemplateEdits.ReplaceExactly(
            seeds[sweepTestsPath],
            "\n        Assert.Empty(result.FailedFiles);",
            "");

        var constructionTestsPath = SweepConstructionSitesScenario.TestFolder + "ConstructionSiteTests.cs";
        seeds[constructionTestsPath] = ScenarioTemplateEdits.RemoveMethods(
            seeds[constructionTestsPath],
            "Plan",
            "PlanAdd_FailedFilesStaysAPlainDictionary",
            "FromResult_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow",
            "FromResult_WithFailedFiles_ReturnsAnEmptyMap",
            "Choose_WithAPlainDictionary_ReturnsItsEntriesAndDoesNotThrow",
            "Choose_WithNoChanges_ReturnsAnEmptyMap",
            "Latest_PrefersTheSecondWhenItHasEntries_AndNeverThrows");

        var golden = new Dictionary<string, string>();
        foreach (var path in SweepConstructionSitesScenario.TargetPaths)
        {
            golden[path] = EditTarget(path, SweepConstructionSitesScenario.Definition.Steps[0].GoldenFiles[path]);
        }

        var task = SweepConstructionSitesScenario.StepTask with
        {
            Conditions = "Convert exactly these methods' maps: " + RenderSites()
                + ". No method may start throwing; behaviour is otherwise unchanged.",
            IfFails = EvalContractOptions.PlainIfFails,
            Report = null,
        };

        var step1 = SweepConstructionSitesScenario.Definition.Steps[0] with
        {
            Prompt = EvalContractOptions.Render(task),
            GoldenFiles = golden,
            ExtraCheck = null,
            Variants = null,
            PromptVariants = null,
            CannedFinalMessage = "DONE: converted every listed map in Planner.cs, Renamer.cs, Mover.cs and Merger.cs to new DocumentChangeSet.",
        };

        return SweepConstructionSitesScenario.Definition with
        {
            Id = "SweepNamedSitesSlip",
            Title = "Convert 13 named construction sites to DocumentChangeSet",
            Axis = "Transcription: many similar call sites; unconvertible sites and the Report check removed",
            Source = "Sibling of SweepConstructionSites; plans/plan_slip_focused_scenario_variants.md",
            SeedFiles = seeds,
            PrimaryAxis = EvalAxis.Transcription,
            Steps = [step1],
        };
    }

    private static string RenderSites() =>
        string.Join(
            "; ",
            SitesToConvert.GroupBy(site => site.FileName)
                .Select(group => group.Key + ": " + string.Join(", ", group.Select(site => site.Method))));

    private static string EditTarget(string path, string text)
    {
        if (path == SweepConstructionSitesScenario.TargetPaths[0])
        {
            return ScenarioTemplateEdits.ReplaceExactly(
                text,
                "return new PlanResult(changes, FailedFiles: new Dictionary<FilePath, string>());",
                "return new PlanResult(changes);");
        }

        if (path == SweepConstructionSitesScenario.TargetPaths[1])
        {
            return ScenarioTemplateEdits.RemoveMethods(text, "FromResult");
        }

        if (path == SweepConstructionSitesScenario.TargetPaths[2])
        {
            return ScenarioTemplateEdits.RemoveMethods(text, "Choose");
        }

        if (path == SweepConstructionSitesScenario.TargetPaths[3])
        {
            return ScenarioTemplateEdits.RemoveMethods(text, "Latest");
        }

        throw new ArgumentOutOfRangeException(nameof(path), path, "Not a target file of SweepConstructionSites.");
    }
}
