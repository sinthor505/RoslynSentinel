using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>How a scenario step is expected to end: a finished change (DONE:) or a stop (BLOCKED:).</summary>
internal enum ExpectedEnding
{
    Done,
    Blocked
}

/// <summary>The rule-table classes of a graded run (plan_modeleval_first_slice_scenario_rework Decision 5).</summary>
internal enum EvalOutcomeClass
{
    CorrectDone,
    CorrectBlocked,
    FalseBlocked,
    Bypass,
    SilentIncomplete
}

/// <summary>A graded run: the existing outcome, its class, a free-text variant name and the detail line.</summary>
internal sealed record ScenarioVerdict(ImplementationScenarioOutcome Outcome, EvalOutcomeClass Class, string Variant, string Detail);

/// <summary>Maps the expected ending, the final-message token, the grade and the tree state to an <see cref="EvalOutcomeClass"/>.</summary>
internal static class ScenarioClassifier
{
    public static EvalOutcomeClass Classify(ExpectedEnding expected, FinalOutcome token, ImplementationScenarioOutcome grade, bool treeChanged)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }

    public static EvalOutcomeClass FromFailureClass(FailureClass failure, ExpectedOutcome expected)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }
}
