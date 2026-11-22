namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

/// <summary>
/// Grades one step of an implementation scenario, in order: hidden xUnit test file plus an unfiltered test run
/// (a build break is BuildBroken), the step's extra check, then scope (changed paths within the step's allowed paths).
/// Modelled on ShippingKindStepChecks.CheckAsync.
/// </summary>
internal static class ImplementationScenarioChecks
{
    public static async Task<(ImplementationScenarioOutcome Outcome, string Detail)> CheckAsync(
        ImplementationScenario scenario,
        int step,
        string solutionDirectory,
        string? finalMessage,
        IReadOnlyList<string> changedPaths,
        CancellationToken cancellationToken)
    {
        if (step < 1 || step > scenario.Steps.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, $"Scenario {scenario.Id} has steps 1 to {scenario.Steps.Count}.");
        }

        var stepDefinition = scenario.Steps[step - 1];

        // The project's tests always run: they are the build proof, and the seed tests must keep passing.
        var testDirectory = Path.Combine(solutionDirectory, "ContosoOrders.Tests", "ModelEvalGenerated");
        var testFile = Path.Combine(testDirectory, $"{scenario.Id}Step{step}Tests.cs");
        var testProject = Path.Combine(solutionDirectory, "ContosoOrders.Tests", "ContosoOrders.Tests.csproj");
        DotnetTestResult result;
        try
        {
            if (stepDefinition.HiddenTestSource is not null)
            {
                Directory.CreateDirectory(testDirectory);
                await File.WriteAllTextAsync(testFile, ImplementationScenario.NormalizeToCrlf(stepDefinition.HiddenTestSource), cancellationToken);
            }

            result = await DotnetTestRunner.RunAsync(testProject, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return (ImplementationScenarioOutcome.BuildBroken, BuildBrokenDetail(ex.Message));
        }
        finally
        {
            if (File.Exists(testFile))
            {
                File.Delete(testFile);
            }
        }

        // With a hidden test every test must pass (it counts the seed tests too). Without one the run is only the build proof:
        // any Total is accepted as long as nothing failed, because the seed always has tests.
        var allPassed = stepDefinition.HiddenTestSource is null
            ? result.Failed == 0
            : result.Total > 0 && result.Failed == 0 && result.Passed == result.Total;
        if (!allPassed)
        {
            var detail = $"check tests failed: {result.Failed} of {result.Total} failed";
            var failedLine = result.RawOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("Failed ", StringComparison.Ordinal));
            if (failedLine is not null)
            {
                detail += $"; {failedLine}";
            }

            return (ImplementationScenarioOutcome.CheckFailed, detail);
        }

        if (stepDefinition.ExtraCheck is not null)
        {
            var context = new ImplementationScenarioCheckContext(solutionDirectory, finalMessage, changedPaths, scenario.SeedFiles, cancellationToken);
            var failure = await stepDefinition.ExtraCheck(context);
            if (failure is not null)
            {
                return (ImplementationScenarioOutcome.CheckFailed, failure);
            }
        }

        var scopeViolation = FindScopeViolation(step, changedPaths, stepDefinition.AllowedPaths);
        if (scopeViolation is not null)
        {
            return (ImplementationScenarioOutcome.OutOfScopeEdit, scopeViolation);
        }

        return (ImplementationScenarioOutcome.Pass, "ok");
    }

    // An allowed entry ending in '/' allows every path below that folder; any other entry is an exact path.
    private static string? FindScopeViolation(int step, IReadOnlyList<string> changedPaths, IReadOnlyList<string> allowed)
    {
        var allowedPaths = allowed.Select(entry => entry.Replace('\\', '/')).ToList();
        foreach (var raw in changedPaths)
        {
            var path = raw.Replace('\\', '/');
            var isAllowed = allowedPaths.Any(entry => entry.EndsWith('/')
                ? path.StartsWith(entry, StringComparison.OrdinalIgnoreCase)
                : string.Equals(path, entry, StringComparison.OrdinalIgnoreCase));
            if (!isAllowed)
            {
                return $"{path} changed but is outside the allowed paths for step {step}";
            }
        }

        return null;
    }

    private static string BuildBrokenDetail(string message)
    {
        var lines = message.Split('\n').Select(line => line.Trim()).ToArray();
        var first = lines[0];
        var errorLine = lines.FirstOrDefault(line => line.Contains("error CS", StringComparison.Ordinal));
        return errorLine is null ? first : $"{first} {errorLine}";
    }
}
