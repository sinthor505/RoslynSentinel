namespace RoslynSentinel.Utilities.PlanStepRunner;

/// <summary>
/// Builds the user prompts the PlanStepRunner sends to the model.
/// </summary>
public static class StepPrompts
{
    /// <summary>
    /// Builds the user prompt for one plan step. It names no tool, so it cannot mention a tool
    /// outside the model's allow-list (tests check this).
    /// </summary>
    /// <remarks>
    /// The step text is inlined into the prompt rather than referenced by path. Handing over a
    /// path made the model re-resolve it through ProjectDoc, and in run 20260910-013550-398 that
    /// lookup silently answered with a *different* step file -> so the model spent the whole run
    /// implementing a plan nobody asked for. Passing the content by value removes the lookup,
    /// and with it any chance of substitution, regardless of how ProjectDoc resolves names.
    /// step.Body already has any frontmatter stripped, so the model sees only the step itself.
    /// </remarks>
    public static string BuildUserPrompt(PlanStepFile step)
    {
        return "The solution is already loaded. Start by reading or editing the files the step names.\n" +
            "Implement the plan step below. It is reproduced here in full - do not look for it on " +
            "disk, and do not read any other plan step file.\n\n" +
            $"=== BEGIN PLAN STEP: {step.FileName} ===\n{step.Body}\n=== END PLAN STEP ===";
    }
}
