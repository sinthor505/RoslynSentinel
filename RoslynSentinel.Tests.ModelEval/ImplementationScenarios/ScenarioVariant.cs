using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>
/// A named end state of a step (an overlay on <paramref name="BaseStep"/> plus an optional final message) with the outcome
/// the grader must report for it; model-free tests apply each variant and assert the table row.
/// </summary>
internal sealed record ScenarioVariant(
    string Name,
    int BaseStep,
    IReadOnlyDictionary<string, string> Files,
    string? FinalMessage,
    EvalOutcomeClass ExpectedClass,
    ImplementationScenarioOutcome ExpectedOutcome,
    string? DetailContains = null);
