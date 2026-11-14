namespace RoslynSentinel.Tests.ModelEval;

internal enum ShippingKindStepOutcome
{
    Pass,
    OutOfScopeEdit,
    CheckFailed,
    BuildBroken,
    Blocked,
    NotConverged
}

internal static class ShippingKindStepChecks
{
    public static Task<(ShippingKindStepOutcome Outcome, string Detail)> CheckAsync(int step, string solutionDirectory, string? finalMessage, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)
    {
        throw new NotImplementedException(); // TODO implement (Step 4)
    }
}
