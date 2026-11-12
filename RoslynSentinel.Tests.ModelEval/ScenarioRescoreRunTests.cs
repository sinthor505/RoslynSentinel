namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Re-scores saved scenario run folders with the current ScenarioScorer rules. No model or LM Studio is used.
/// </summary>
[TestFixture]
public class ScenarioRescoreRunTests
{
    /// <summary>Environment variable: a run folder holding results.csv, or a parent folder whose immediate subfolders hold results.csv.</summary>
    public const string RunVariable = "ROSLYNSENTINEL_MODELEVAL_RESCORE_RUN";

    /// <summary>File name written inside each run folder with the rescored samples.</summary>
    public const string CsvName = "rescored.csv";

    private const string ResultsCsvName = "results.csv";

    [Test]
    [Explicit("Re-scores saved scenario runs with the current rules; no model needed but each Correct-looking sample builds the fixture, so it takes minutes")]
    public async Task RescoreScenarioRuns_WritesRescoredCsv()
    {
        var input = Environment.GetEnvironmentVariable(RunVariable);
        if (string.IsNullOrWhiteSpace(input))
        {
            Assert.Ignore($"Set {RunVariable} to a run folder holding {ResultsCsvName}, or to a parent folder of run folders.");
            return;
        }

        var runFolders = ListRunFolders(input);
        if (runFolders.Count == 0)
        {
            Assert.Fail($"No {ResultsCsvName} found in '{Path.GetFullPath(input)}' or in its immediate subfolders.");
            return;
        }

        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var totalSamples = 0;
        foreach (var runFolder in runFolders)
        {
            var csvPath = Path.Combine(runFolder, CsvName);
            if (File.Exists(csvPath))
            {
                File.Delete(csvPath);
            }

            var samples = await ScenarioRescoring.RescoreRunFolderAsync(
                runFolder,
                ScenarioRegistry.Find,
                VerdictFromSnapshotAsync,
                csvPath,
                cancellationToken);
            totalSamples += samples.Count;
            WriteSummary(runFolder, samples);
        }

        TestContext.Out.WriteLine($"Re-scored {runFolders.Count} run folder(s), {totalSamples} sample(s) in total.");
        Assert.That(totalSamples, Is.GreaterThan(0), "No sample was rescored; check the run folders' results.csv rows.");
    }

    private static List<string> ListRunFolders(string input)
    {
        var root = Path.GetFullPath(input);
        if (File.Exists(Path.Combine(root, ResultsCsvName)))
        {
            return [root];
        }

        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Where(folder => File.Exists(Path.Combine(folder, ResultsCsvName)))
            .OrderBy(folder => Path.GetFileName(folder), StringComparer.Ordinal)
            .ToList();
    }

    private static void WriteSummary(string runFolder, IReadOnlyList<ScenarioSample> samples)
    {
        var scenarioId = samples.Count > 0 ? samples[0].ScenarioId : "none";
        var inconclusive = samples.Count(sample => sample.IsInconclusive);
        var passed = samples.Count(sample => sample.Score?.Passed == true);
        var failureClasses = string.Join(
            ", ",
            samples
                .Where(sample => sample.Score is not null)
                .GroupBy(sample => sample.Score!.FailureClass)
                .OrderBy(group => group.Key)
                .Select(group => $"{group.Key}={group.Count()}"));
        TestContext.Out.WriteLine(
            $"{Path.GetFileName(runFolder)} | scenario {scenarioId} | samples {samples.Count} | inconclusive {inconclusive} | " +
            $"passed {passed} | FailureClass: {(failureClasses.Length > 0 ? failureClasses : "none")}");
    }

    private static async Task<FinalStateVerdict?> VerdictFromSnapshotAsync(
        ScenarioDefinition scenario,
        string finalStateDirectory,
        CancellationToken cancellationToken)
    {
        using var fixture = new RoslynSentinel.Tests.TestSolutionFixture();
        var solutionDirectory = fixture.SolutionDirectory;

        foreach (var key in scenario.StagedFiles.Keys)
        {
            var stagedPath = ScenarioFixtureFiles.Absolute(solutionDirectory, key);
            if (File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }

        var helpersDirectory = Path.Combine(ScenarioFixtureFiles.CoreProjectDirectory(solutionDirectory), "FixtureHelpers");
        if (Directory.Exists(helpersDirectory))
        {
            foreach (var file in Directory.GetFiles(helpersDirectory, "*.cs"))
            {
                File.Delete(file);
            }
        }

        foreach (var file in Directory.GetFiles(finalStateDirectory))
        {
            var target = ScenarioFixtureFiles.Absolute(
                solutionDirectory,
                ScenarioRescoring.TargetPath(scenario.StagedFiles.Keys, Path.GetFileName(file)));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        try
        {
            return await scenario.CheckFinalState(solutionDirectory, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            TestContext.Out.WriteLine($"CheckFinalState failed for scenario {scenario.Id}: {exception.GetType().Name}: {exception.Message}");
            return null;
        }
    }
}
