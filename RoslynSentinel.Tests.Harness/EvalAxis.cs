namespace RoslynSentinel.Tests.ModelEval;

/// <summary>The one capability axis a scenario primarily measures (proposal_modeleval_standing_workflow_contract section 3).</summary>
public enum EvalAxis
{
    Unassigned,
    ToolUse,
    MultiStepState,
    AmbiguityResolution,
    InstructionAdherence,
    ErrorRecovery,
    Efficiency,
    RealisticPlan
}

/// <summary>Builds the NUnit category for an axis, so the category is generated from the property and cannot drift.</summary>
public static class EvalAxisCategory
{
    public static string Name(EvalAxis axis) => "Axis:" + axis;
}
