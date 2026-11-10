using System.Text.Json;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// The full-suite marker (.roslynsentinel/full-test-marker.json): update rules for RunTest, the staleness decision and the
// warning finding Git(commit) carries. Everything runs against temp directories; no external process is started.
// Not parallelizable: the fixture swaps the static ToolCallHint catalog and the staleness options.
[TestFixture]
[NonParallelizable]
public class FullTestMarkerStoreTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan TwoDays = TimeSpan.FromDays(2);

    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rs_marker_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);

        using var schema = JsonDocument.Parse(
            """{"type":"object","properties":{"reason":{"type":"string"},"suite":{"type":"string","enum":["fast","full"]}},"required":["reason"]}""");
        ToolCallHint.Current = ToolSignatureCatalog.FromSchemas(
            [new KeyValuePair<string, JsonElement>("RunTest", schema.RootElement.Clone())]);
        FullSuiteStalenessOptions.Configure(Array.Empty<string>());
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallHint.Current = null!;
        FullSuiteStalenessOptions.Configure(Array.Empty<string>());
        Directory.Delete(_root, recursive: true);
    }

    private static TestRunResult Run(bool completed = true, int total = 100, int failed = 0) => new(
        RunSucceeded: completed && failed == 0, ExitCode: 0, TotalCount: total, PassedCount: total - failed, FailedCount: failed, SkippedCount: 0,
        FailureSummary: [], Results: [], StdoutTail: null, StderrTail: null, Duration: TimeSpan.FromSeconds(1), RunCompleted: completed);

    private void WriteMarkerFile(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".roslynsentinel"));
        File.WriteAllText(FullTestMarkerStore.PathFor(_root), json);
    }

    private void SeedMarker(DateTime lastFullRunUtc, int fastRunsSince, int total = 100, int failed = 0) =>
        WriteMarkerFile(JsonSerializer.Serialize(new { lastFullRunUtc, totalCount = total, failedCount = failed, fastRunsSince }));

    // ---- update rules (pure) ----

    [Test]
    public void NextMarker_FullSolutionRun_ResetsTheMarker()
    {
        var previous = new FullTestMarker(Now.AddDays(-5), 90, 1, 12);

        var next = FullTestMarkerStore.NextMarker(previous, TestSuite.full, ToolScope.solution, runCompleted: true, totalCount: 120, failedCount: 3, Now);

        Assert.That(next, Is.EqualTo(new FullTestMarker(Now, 120, 3, 0)));
    }

    [Test]
    public void NextMarker_FullSolutionRunWithNoPreviousMarker_CreatesIt()
    {
        var next = FullTestMarkerStore.NextMarker(null, TestSuite.full, ToolScope.solution, runCompleted: true, totalCount: 7, failedCount: 0, Now);

        Assert.That(next, Is.EqualTo(new FullTestMarker(Now, 7, 0, 0)));
    }

    [Test]
    public void NextMarker_FullProjectRun_NeitherResetsNorIncrements()
    {
        var previous = new FullTestMarker(Now.AddDays(-1), 90, 0, 4);

        Assert.That(FullTestMarkerStore.NextMarker(previous, TestSuite.full, ToolScope.project, runCompleted: true, totalCount: 10, failedCount: 0, Now), Is.Null);
    }

    [Test]
    public void NextMarker_FilteredFullSolutionRun_NeitherResetsNorIncrements()
    {
        var previous = new FullTestMarker(Now.AddDays(-1), 90, 0, 4);

        Assert.That(FullTestMarkerStore.NextMarker(previous, TestSuite.full, ToolScope.solution, runCompleted: true, totalCount: 10, failedCount: 0, Now, filtered: true), Is.Null);
    }

    [Test]
    public void RecordRun_FilteredFullSolutionRun_LeavesTheMarkerByteForByte()
    {
        SeedMarker(Now.AddHours(-1), fastRunsSince: 3);
        var before = File.ReadAllText(FullTestMarkerStore.PathFor(_root));

        FullTestMarkerStore.RecordRun(_root, TestSuite.full, ToolScope.solution, Run(), Now, filter: "FullyQualifiedName~Foo");

        Assert.That(File.ReadAllText(FullTestMarkerStore.PathFor(_root)), Is.EqualTo(before));
    }

    [TestCase(ToolScope.solution)]
    [TestCase(ToolScope.project)]
    public void NextMarker_FastRunOfAnyScope_IncrementsFastRunsSince(ToolScope scope)
    {
        var previous = new FullTestMarker(Now.AddDays(-1), 90, 0, 4);

        var next = FullTestMarkerStore.NextMarker(previous, TestSuite.fast, scope, runCompleted: true, totalCount: 10, failedCount: 0, Now);

        Assert.That(next, Is.EqualTo(previous with { FastRunsSince = 5 }));
    }

    [Test]
    public void NextMarker_FastRunWithNoMarker_ChangesNothing()
    {
        Assert.That(FullTestMarkerStore.NextMarker(null, TestSuite.fast, ToolScope.solution, runCompleted: true, totalCount: 10, failedCount: 0, Now), Is.Null);
    }

    [TestCase(TestSuite.fast, ToolScope.solution)]
    [TestCase(TestSuite.full, ToolScope.solution)]
    public void NextMarker_IncompleteRun_ChangesNothing(TestSuite suite, ToolScope scope)
    {
        var previous = new FullTestMarker(Now.AddDays(-1), 90, 0, 4);

        Assert.That(FullTestMarkerStore.NextMarker(previous, suite, scope, runCompleted: false, totalCount: 0, failedCount: 0, Now), Is.Null);
    }

    // ---- the file on disk ----

    [Test]
    public void RecordRun_FullSolutionRun_WritesCamelCaseJsonAndLeavesNoTempFile()
    {
        FullTestMarkerStore.RecordRun(_root, TestSuite.full, ToolScope.solution, Run(total: 250, failed: 2), Now);

        var text = File.ReadAllText(FullTestMarkerStore.PathFor(_root));
        using var json = JsonDocument.Parse(text);
        Assert.That(json.RootElement.GetProperty("totalCount").GetInt32(), Is.EqualTo(250));
        Assert.That(json.RootElement.GetProperty("failedCount").GetInt32(), Is.EqualTo(2));
        Assert.That(json.RootElement.GetProperty("fastRunsSince").GetInt32(), Is.EqualTo(0));
        Assert.That(json.RootElement.GetProperty("lastFullRunUtc").GetString(), Does.StartWith("2026-11-10T12:00:00"));
        Assert.That(Directory.GetFiles(Path.Combine(_root, ".roslynsentinel"), "*.tmp"), Is.Empty);
        Assert.That(FullTestMarkerStore.TryRead(_root), Is.EqualTo(new FullTestMarker(Now, 250, 2, 0)));
    }

    [Test]
    public void RecordRun_FastRuns_IncrementTheMarkerOnDisk()
    {
        SeedMarker(Now.AddHours(-1), fastRunsSince: 3);

        FullTestMarkerStore.RecordRun(_root, TestSuite.fast, ToolScope.project, Run(), Now);
        FullTestMarkerStore.RecordRun(_root, TestSuite.fast, ToolScope.solution, Run(), Now);

        Assert.That(FullTestMarkerStore.TryRead(_root)!.FastRunsSince, Is.EqualTo(5));
    }

    [Test]
    public void RecordRun_FullProjectRun_LeavesTheMarkerByteForByte()
    {
        SeedMarker(Now.AddHours(-1), fastRunsSince: 3);
        var before = File.ReadAllText(FullTestMarkerStore.PathFor(_root));

        FullTestMarkerStore.RecordRun(_root, TestSuite.full, ToolScope.project, Run(), Now);

        Assert.That(File.ReadAllText(FullTestMarkerStore.PathFor(_root)), Is.EqualTo(before));
    }

    [Test]
    public void RecordRun_FastRunWithMissingMarker_DoesNotCreateOne()
    {
        FullTestMarkerStore.RecordRun(_root, TestSuite.fast, ToolScope.solution, Run(), Now);

        Assert.That(File.Exists(FullTestMarkerStore.PathFor(_root)), Is.False);
    }

    [Test]
    public void RecordRun_IncompleteFullRun_DoesNotWrite()
    {
        FullTestMarkerStore.RecordRun(_root, TestSuite.full, ToolScope.solution, Run(completed: false, total: 0), Now);

        Assert.That(File.Exists(FullTestMarkerStore.PathFor(_root)), Is.False);
    }

    [TestCase("{ not json")]
    [TestCase("")]
    [TestCase("null")]
    [TestCase("{}")]
    [TestCase("{\"lastFullRunUtc\":\"yesterday\",\"totalCount\":1,\"failedCount\":0,\"fastRunsSince\":0}")]
    public void TryRead_CorruptMarker_CountsAsMissing(string content)
    {
        WriteMarkerFile(content);

        Assert.That(FullTestMarkerStore.TryRead(_root), Is.Null);
        Assert.That(FullTestMarkerStore.Evaluate(FullTestMarkerStore.TryRead(_root), Now, TwoDays, 40)!.Reason, Is.EqualTo("no full run recorded"));
    }

    [Test]
    public void RecordRun_CorruptMarker_IsOverwrittenByTheNextFullSolutionRun()
    {
        WriteMarkerFile("{ not json");

        FullTestMarkerStore.RecordRun(_root, TestSuite.full, ToolScope.solution, Run(total: 5), Now);

        Assert.That(FullTestMarkerStore.TryRead(_root), Is.EqualTo(new FullTestMarker(Now, 5, 0, 0)));
    }

    [Test]
    public void RecordRun_WhenTheMarkerCannotBeWritten_DoesNotThrow()
    {
        // The "solution root" is a file, so .roslynsentinel cannot be created under it.
        var fileAsRoot = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(fileAsRoot, "x");

        Assert.DoesNotThrow(() => FullTestMarkerStore.RecordRun(fileAsRoot, TestSuite.full, ToolScope.solution, Run(), Now));
        Assert.DoesNotThrow(() => FullTestMarkerStore.RecordRun(null, TestSuite.full, ToolScope.solution, Run(), Now));
        Assert.That(FullTestMarkerStore.TryRead(fileAsRoot), Is.Null);
    }

    // ---- staleness evaluation ----

    [Test]
    public void Evaluate_MissingMarker_IsStaleWithNoFullRunRecorded()
    {
        var stale = FullTestMarkerStore.Evaluate(null, Now, TwoDays, 40);

        Assert.That(stale, Is.EqualTo(new FullSuiteStale("no full run recorded", null, 0)));
    }

    [Test]
    public void Evaluate_RecentRunWithFewFastRuns_IsFresh()
    {
        Assert.That(FullTestMarkerStore.Evaluate(new FullTestMarker(Now.AddHours(-5), 10, 0, 12), Now, TwoDays, 40), Is.Null);
    }

    [Test]
    public void Evaluate_ExactlyAtTheLimits_IsStillFresh()
    {
        Assert.That(FullTestMarkerStore.Evaluate(new FullTestMarker(Now - TwoDays, 10, 0, 40), Now, TwoDays, 40), Is.Null);
    }

    [Test]
    public void Evaluate_OlderThanMaxAge_IsStaleByDays()
    {
        var last = Now.AddDays(-3);

        var stale = FullTestMarkerStore.Evaluate(new FullTestMarker(last, 10, 0, 5), Now, TwoDays, 40);

        Assert.That(stale, Is.Not.Null);
        Assert.That(stale!.Reason, Does.Contain("3 days ago").And.Contain("limit 2 days"));
        Assert.That(stale.LastFullRunUtc, Is.EqualTo(last));
        Assert.That(stale.FastRunsSince, Is.EqualTo(5));
    }

    [Test]
    public void Evaluate_MoreFastRunsThanTheLimit_IsStaleByCount()
    {
        var stale = FullTestMarkerStore.Evaluate(new FullTestMarker(Now.AddHours(-1), 10, 0, 41), Now, TwoDays, 40);

        Assert.That(stale, Is.Not.Null);
        Assert.That(stale!.Reason, Does.Contain("41 fast runs").And.Contain("limit 40"));
        Assert.That(stale.Reason, Does.Not.Contain("ago (limit"));
    }

    [Test]
    public void EvaluateForRoot_UsesTheConfiguredThresholds()
    {
        SeedMarker(Now.AddHours(-30), fastRunsSince: 3);

        Assert.That(FullTestMarkerStore.EvaluateForRoot(_root, Now), Is.Null, "default limit is 2 days");

        FullSuiteStalenessOptions.Configure(["--full-suite-max-age-days=1"]);
        Assert.That(FullTestMarkerStore.EvaluateForRoot(_root, Now)!.Reason, Does.Contain("limit 1 days"));

        FullSuiteStalenessOptions.Configure(["--full-suite-max-age-days=1", "--full-suite-max-fast-runs=2"]);
        Assert.That(FullTestMarkerStore.EvaluateForRoot(_root, Now)!.Reason, Does.Contain("3 fast runs").And.Contain("limit 2"));
    }

    [Test]
    public void EvaluateForRoot_WithNoSolutionRoot_IsNull()
    {
        Assert.That(FullTestMarkerStore.EvaluateForRoot(null, Now), Is.Null);
        Assert.That(FullTestMarkerStore.EvaluateForRoot("", Now), Is.Null);
    }

    // ---- messages and the Git commit warning ----

    [Test]
    public void Describe_StaleRun_NamesAgeFastRunsAndTheFullRunCall()
    {
        var stale = new FullSuiteStale("whatever", Now.AddDays(-3.5), 52);

        var line = FullTestMarkerStore.Describe(stale, Now);

        Assert.That(line, Is.EqualTo("Full suite last run 3 days / 52 fast runs ago. Run RunTest(reason: \"<why you call this>\", suite: full) before committing."));
    }

    [Test]
    public void Describe_NoRecordedRun_SaysSo()
    {
        var line = FullTestMarkerStore.Describe(new FullSuiteStale("no full run recorded", null, 0), Now);

        Assert.That(line, Does.StartWith("No full suite run recorded.").And.Contain("suite: full"));
    }

    [Test]
    public void StaleFindings_StaleSuite_YieldsOneWarningFindingAndLeavesTheMarkerAlone()
    {
        SeedMarker(Now.AddDays(-3), fastRunsSince: 52);
        var before = File.ReadAllText(FullTestMarkerStore.PathFor(_root));

        var findings = FullTestMarkerStore.StaleFindings(_root, Now);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Severity, Is.EqualTo(FindingSeverity.Warning));
        Assert.That(findings[0].Source, Is.EqualTo(FullTestMarkerStore.FindingSource));
        Assert.That(findings[0].Message, Does.StartWith("Full suite last run 3 days / 52 fast runs ago."));
        Assert.That(File.ReadAllText(FullTestMarkerStore.PathFor(_root)), Is.EqualTo(before), "the commit warning reads the marker only");
    }

    [Test]
    public void StaleFindings_FreshSuiteOrNoRoot_YieldsNone()
    {
        SeedMarker(Now.AddHours(-2), fastRunsSince: 1);

        Assert.That(FullTestMarkerStore.StaleFindings(_root, Now), Is.Empty);
        Assert.That(FullTestMarkerStore.StaleFindings(null, Now), Is.Empty);
    }

    [Test]
    public void StaleFindings_MissingMarker_WarnsThatNoFullRunIsRecorded()
    {
        var findings = FullTestMarkerStore.StaleFindings(_root, Now);

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.StartWith("No full suite run recorded."));
    }
}

// The staleness thresholds are startup options: --full-suite-max-age-days and --full-suite-max-fast-runs, then the matching
// ROSLYNSENTINEL_FULL_SUITE_* environment variables, then the defaults (2 days, 40 fast runs).
[TestFixture]
[NonParallelizable]
public class FullSuiteStalenessOptionsTests
{
    private const string AgeVariable = "ROSLYNSENTINEL_FULL_SUITE_MAX_AGE_DAYS";
    private const string RunsVariable = "ROSLYNSENTINEL_FULL_SUITE_MAX_FAST_RUNS";

    [SetUp]
    [TearDown]
    public void Reset()
    {
        Environment.SetEnvironmentVariable(AgeVariable, null);
        Environment.SetEnvironmentVariable(RunsVariable, null);
        FullSuiteStalenessOptions.Configure(Array.Empty<string>());
    }

    [Test]
    public void Configure_NoArguments_UsesTheDefaults()
    {
        Assert.That(FullSuiteStalenessOptions.MaxAgeDays, Is.EqualTo(2));
        Assert.That(FullSuiteStalenessOptions.MaxFastRuns, Is.EqualTo(40));
    }

    [Test]
    public void Configure_Arguments_OverrideTheDefaults()
    {
        FullSuiteStalenessOptions.Configure(["--full-suite-max-age-days=0.5", "--full-suite-max-fast-runs", "7"]);

        Assert.That(FullSuiteStalenessOptions.MaxAgeDays, Is.EqualTo(0.5));
        Assert.That(FullSuiteStalenessOptions.MaxFastRuns, Is.EqualTo(7));
    }

    [Test]
    public void Configure_EnvironmentVariables_AreUsedWhenNoArgumentIsGiven_AndTheArgumentWins()
    {
        Environment.SetEnvironmentVariable(AgeVariable, "5");
        Environment.SetEnvironmentVariable(RunsVariable, "9");

        FullSuiteStalenessOptions.Configure(Array.Empty<string>());
        Assert.That(FullSuiteStalenessOptions.MaxAgeDays, Is.EqualTo(5));
        Assert.That(FullSuiteStalenessOptions.MaxFastRuns, Is.EqualTo(9));

        FullSuiteStalenessOptions.Configure(["--full-suite-max-fast-runs=3"]);
        Assert.That(FullSuiteStalenessOptions.MaxFastRuns, Is.EqualTo(3));
    }

    [TestCase("abc")]
    [TestCase("0")]
    [TestCase("-4")]
    public void Configure_UnusableValues_KeepTheDefaults(string raw)
    {
        FullSuiteStalenessOptions.Configure([$"--full-suite-max-age-days={raw}", $"--full-suite-max-fast-runs={raw}"]);

        Assert.That(FullSuiteStalenessOptions.MaxAgeDays, Is.EqualTo(2));
        Assert.That(FullSuiteStalenessOptions.MaxFastRuns, Is.EqualTo(40));
    }
}
