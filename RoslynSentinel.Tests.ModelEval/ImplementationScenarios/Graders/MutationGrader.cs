namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>A named set of production-file replacements (relative path to text) overlaid on a run directory to test model-written tests.</summary>
internal sealed record ScenarioMutant(string Name, IReadOnlyDictionary<string, string> Files);

/// <summary>The test counts for one mutant; Killed = failed tests, or a build break against the model's tests (said in Note).</summary>
internal sealed record MutantResult(string Name, int Passed, int Failed, bool Killed, string Note);

/// <summary>Mutation grading for scenarios where the model writes the tests.</summary>
internal static class MutationGrader
{
    public static Task<IReadOnlyList<MutantResult>> RunAsync(
        string solutionDirectory,
        string testProjectRelativePath,
        string testFilter,
        IReadOnlyList<ScenarioMutant> mutants,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 6)
    }
}
