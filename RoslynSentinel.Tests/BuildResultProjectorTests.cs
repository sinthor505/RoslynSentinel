using System.Text.Json;

namespace RoslynSentinel.Tests;

[TestFixture]
[Category("BuildResultProjector")]
public class BuildResultProjectorTests
{
    private static readonly BuildProjection Defaults = new(MaxDetails: 20, IncludeOutput: false, IncludeWarnings: false);

    private static DiagnosticInfo Err(string project, string file, string id = "CS0246", string? message = null, int line = 1) =>
        new(id, "Error", message ?? $"The type or namespace name 'T{line}' could not be found", file, line, 1, line, 2, project);

    private static DiagnosticInfo Warn(string project, string file, int line = 1) =>
        new("CS0168", "Warning", $"The variable 'v{line}' is declared but never used", file, line, 1, line, 2, project);

    // Mirrors what the engine hands over: capped Errors/Warnings, the uncapped lists in FullDiagnostics.
    private static BuildResult Failed(
        List<DiagnosticInfo> errors,
        List<DiagnosticInfo>? warnings = null,
        string? stdout = "stdout tail",
        string? stderr = "stderr tail",
        int? errorCount = null,
        string? detail = null)
    {
        warnings ??= [];
        return new BuildResult(
            Outcome: BuildOutcome.Failed,
            Level: BuildVerifyLevel.fullBuild,
            ProjectsCompiled: ["Core", "App"],
            DiagnosticsComplete: true,
            ErrorCount: errorCount ?? errors.Count,
            WarningCount: warnings.Count,
            Errors: errors.Take(50).ToList(),
            Warnings: warnings.Take(50).ToList(),
            ErrorSummary: errors.GroupBySeverity(10),
            WarningSummary: warnings.GroupBySeverity(10),
            StdoutTail: stdout,
            StderrTail: stderr,
            Duration: TimeSpan.FromSeconds(3),
            ExitCode: 1,
            Detail: detail,
            FullDiagnostics: new BuildFullDiagnostics(errors, warnings));
    }

    // FilePathWrapper canonicalizes separators, so expected file keys go through it too.
    private static string P(string path) => ((FilePathWrapper)path).ToString();

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> AppDependsOnCore() =>
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["Core"] = new HashSet<string>(),
            ["App"] = new HashSet<string> { "Core" },
        };

    [Test]
    public void Green_ReturnsOnlyProjectsAndCounts()
    {
        var warnings = Enumerable.Range(1, 3).Select(i => Warn("Core", "Core/A.cs", i)).ToList();
        var green = new BuildResult(
            Outcome: BuildOutcome.Succeeded,
            Level: BuildVerifyLevel.fullBuild,
            ProjectsCompiled: ["Core", "App"],
            DiagnosticsComplete: true,
            ErrorCount: 0,
            WarningCount: 3,
            Errors: [],
            Warnings: warnings,
            ErrorSummary: [],
            WarningSummary: warnings.GroupBySeverity(10),
            StdoutTail: "Build succeeded.",
            StderrTail: "",
            Duration: TimeSpan.FromSeconds(7),
            ExitCode: 0);

        var projected = BuildResultProjector.Project(green, Defaults, null);

        Assert.That(projected.Outcome, Is.EqualTo(BuildOutcome.Succeeded));
        Assert.That(projected.ProjectsCompiled, Is.EqualTo(new[] { "Core", "App" }));
        Assert.That(projected.WarningCount, Is.EqualTo(3));
        Assert.That(projected.ErrorCount, Is.EqualTo(0));
        Assert.That(projected.Duration, Is.EqualTo(TimeSpan.FromSeconds(7)));
        Assert.That(projected.Errors, Is.Empty);
        Assert.That(projected.Warnings, Is.Empty);
        Assert.That(projected.ErrorSummary, Is.Empty);
        Assert.That(projected.WarningSummary, Is.Empty);
        Assert.That(projected.StdoutTail, Is.Null);
        Assert.That(projected.StderrTail, Is.Null);
        Assert.That(projected.Breakdown, Is.Null);
        Assert.That(projected.OmittedErrorCount, Is.EqualTo(0));
        Assert.That(projected.SuppressedDownstreamErrorCount, Is.EqualTo(0));
    }

    [Test]
    public void OneError_ReturnedInFull()
    {
        var error = Err("Core", "Core/A.cs");
        var projected = BuildResultProjector.Project(Failed([error]), Defaults, null);

        Assert.That(projected.ErrorCount, Is.EqualTo(1));
        Assert.That(projected.Errors, Has.Count.EqualTo(1));
        Assert.That(projected.Errors[0], Is.EqualTo(error));
        Assert.That(projected.OmittedErrorCount, Is.EqualTo(0));
        Assert.That(projected.SuppressedDownstreamErrorCount, Is.EqualTo(0));
        Assert.That(projected.Breakdown, Is.Not.Null);
        Assert.That(projected.Breakdown!.ByProject, Is.EqualTo(new[] { new BuildProjectErrorCount("Core", 1, true) }));
        Assert.That(projected.Breakdown.ByFile, Is.EqualTo(new[] { new BuildFileErrorCount(P("Core/A.cs"), 1) }));
        Assert.That(projected.ErrorSummary, Has.Count.EqualTo(1));
        Assert.That(projected.StdoutTail, Is.Null, "errors were parsed, so the tails are dropped");
        Assert.That(projected.StderrTail, Is.Null);
    }

    [Test]
    public void TenErrors_AllReturnedGroupedByProject()
    {
        var errors = new List<DiagnosticInfo>();
        for (int i = 1; i <= 6; i++)
        {
            errors.Add(Err("Core", i <= 4 ? "Core/A.cs" : "Core/B.cs", line: i));
        }

        for (int i = 1; i <= 4; i++)
        {
            errors.Add(Err("Tools", "Tools/T.cs", line: i));
        }

        var projected = BuildResultProjector.Project(Failed(errors), Defaults, null);

        Assert.That(projected.Errors, Has.Count.EqualTo(10));
        Assert.That(projected.OmittedErrorCount, Is.EqualTo(0));
        Assert.That(projected.SuppressedDownstreamErrorCount, Is.EqualTo(0));
        Assert.That(
            projected.Breakdown!.ByProject,
            Is.EqualTo(new[] { new BuildProjectErrorCount("Core", 6, true), new BuildProjectErrorCount("Tools", 4, true) }),
            "null graph: every erroring project is a root cause; sorted by count descending");
        Assert.That(
            projected.Breakdown.ByFile,
            Is.EqualTo(new[]
            {
                new BuildFileErrorCount(P("Core/A.cs"), 4),
                new BuildFileErrorCount(P("Tools/T.cs"), 4),
                new BuildFileErrorCount(P("Core/B.cs"), 2),
            }));
    }

    [Test]
    public void HundredErrors_CapsDetailsAndReportsOmitted()
    {
        var errors = Enumerable.Range(1, 100).Select(i => Err("Core", $"Core/F{i % 7}.cs", line: i)).ToList();

        var projected = BuildResultProjector.Project(Failed(errors), Defaults, null);

        Assert.That(projected.ErrorCount, Is.EqualTo(100), "the exact count is never changed");
        Assert.That(projected.Errors, Has.Count.EqualTo(20));
        Assert.That(projected.OmittedErrorCount, Is.EqualTo(80));
        Assert.That(projected.Errors.Select(e => e.StartLine), Is.EqualTo(Enumerable.Range(1, 20)), "original order is kept");
        Assert.That(projected.Breakdown!.ByProject.Single().ErrorCount, Is.EqualTo(100));
        Assert.That(projected.FullDiagnostics!.Errors, Has.Count.EqualTo(100), "the uncapped carrier is left for the tool layer");
        Assert.That(projected.FullDiagnosticsResultId, Is.Null, "the tool layer sets the id");
    }

    [Test]
    public void ThousandErrorsAcrossRootAndDownstream_SuppressesDownstreamAndFlagsRootCause()
    {
        // Downstream (App) errors come first in the list, so the root-cause-first ordering is observable.
        var errors = Enumerable.Range(1, 700).Select(i => Err("App", "App/P.cs", line: i))
            .Concat(Enumerable.Range(1, 300).Select(i => Err("Core", "Core/A.cs", line: i)))
            .ToList();

        var projected = BuildResultProjector.Project(Failed(errors), Defaults, AppDependsOnCore());

        Assert.That(projected.ErrorCount, Is.EqualTo(1000));
        Assert.That(projected.SuppressedDownstreamErrorCount, Is.EqualTo(700));
        Assert.That(projected.OmittedErrorCount, Is.EqualTo(980));
        Assert.That(projected.Errors, Has.Count.EqualTo(20));
        Assert.That(projected.Errors.All(e => e.Project == "Core"), Is.True, "root-cause errors are listed first");
        Assert.That(
            projected.Breakdown!.ByProject,
            Is.EqualTo(new[] { new BuildProjectErrorCount("Core", 300, true), new BuildProjectErrorCount("App", 700, false) }),
            "root causes sort before downstream projects even when the downstream count is larger");
    }

    [Test]
    public void FailedWithZeroParsedErrors_KeepsTails()
    {
        var projected = BuildResultProjector.Project(
            Failed([], stdout: "error NU1101: Unable to find package Foo", stderr: "restore failed", detail: "exit 1"),
            Defaults,
            null);

        Assert.That(projected.Outcome, Is.EqualTo(BuildOutcome.Failed));
        Assert.That(projected.Errors, Is.Empty);
        Assert.That(projected.Breakdown, Is.Null);
        Assert.That(projected.OmittedErrorCount, Is.EqualTo(0));
        Assert.That(projected.StdoutTail, Is.EqualTo("error NU1101: Unable to find package Foo"));
        Assert.That(projected.StderrTail, Is.EqualTo("restore failed"));
        Assert.That(projected.Detail, Is.EqualTo("exit 1"));
    }

    [Test]
    public void IncludeOutputAndIncludeWarnings_AreHonoured()
    {
        var errors = new List<DiagnosticInfo> { Err("Core", "Core/A.cs") };
        var warnings = Enumerable.Range(1, 30).Select(i => Warn("Core", "Core/W.cs", i)).ToList();
        var full = Failed(errors, warnings);

        var quiet = BuildResultProjector.Project(full, Defaults, null);
        Assert.That(quiet.Warnings, Is.Empty);
        Assert.That(quiet.WarningSummary, Is.Empty);
        Assert.That(quiet.WarningCount, Is.EqualTo(30), "the warning count is always kept");
        Assert.That(quiet.ErrorSummary, Is.Not.Empty, "the error summary is always kept");
        Assert.That(quiet.StdoutTail, Is.Null);
        Assert.That(quiet.StderrTail, Is.Null);

        var loud = BuildResultProjector.Project(full, new BuildProjection(MaxDetails: 5, IncludeOutput: true, IncludeWarnings: true), null);
        Assert.That(loud.Warnings, Has.Count.EqualTo(5));
        Assert.That(loud.WarningSummary, Is.Not.Empty);
        Assert.That(loud.WarningCount, Is.EqualTo(30));
        Assert.That(loud.StdoutTail, Is.EqualTo("stdout tail"));
        Assert.That(loud.StderrTail, Is.EqualTo("stderr tail"));
        Assert.That(loud.Errors, Has.Count.EqualTo(1));

        var greenWithOutput = BuildResultProjector.Project(
            full with { Outcome = BuildOutcome.Succeeded, ErrorCount = 0, Errors = [], FullDiagnostics = null },
            new BuildProjection(MaxDetails: 20, IncludeOutput: true, IncludeWarnings: false),
            null);
        Assert.That(greenWithOutput.StdoutTail, Is.EqualTo("stdout tail"), "includeOutput keeps the tails even on a green build");
    }

    [Test]
    public void LongMessage_IsCutTo300Characters()
    {
        string longMessage = new('x', 1000);
        var errors = new List<DiagnosticInfo>
        {
            Err("Core", "Core/A.cs", message: longMessage),
            Err("Core", "Core/B.cs", id: "CS1002", message: new string('y', 300)),
            Err("Core", "Core/C.cs", id: "CS1003", message: "short"),
        };
        var warnings = new List<DiagnosticInfo>
        {
            Warn("Core", "Core/W.cs") with { Message = longMessage },
        };

        var projected = BuildResultProjector.Project(
            Failed(errors, warnings),
            new BuildProjection(MaxDetails: 20, IncludeOutput: false, IncludeWarnings: true),
            null);

        Assert.That(projected.Errors[0].Message, Has.Length.EqualTo(300), "an over-long message is cut to exactly 300 characters");
        Assert.That(projected.Errors[0].Message, Does.EndWith("..."));
        Assert.That(projected.Errors[1].Message, Is.EqualTo(new string('y', 300)), "a message of exactly 300 characters is untouched");
        Assert.That(projected.Errors[2].Message, Is.EqualTo("short"));
        Assert.That(projected.Warnings[0].Message, Has.Length.LessThanOrEqualTo(300));
        Assert.That(projected.ErrorSummary.All(s => s.MessageTemplate.Length <= 300), Is.True);
        Assert.That(projected.FullDiagnostics!.Errors[0].Message, Has.Length.EqualTo(1000), "the carrier keeps the full text");
    }

    [Test]
    public void ProjectedFailedResult_SerializesUnder15KB()
    {
        // Realistic failed build: 100 errors over three projects and many files, compiler-length messages.
        string[] ids = ["CS0246", "CS0103", "CS1061", "CS0117"];
        string[] projects = ["Core", "Engines", "App"];
        var errors = Enumerable.Range(1, 100)
            .Select(i => Err(
                projects[i % 3],
                $"RoslynSentinel.{projects[i % 3]}/Services/Feature{i % 13}/SomeLongFileName{i % 13}.cs",
                id: ids[i % 4],
                message: $"The type or namespace name 'Missing{i}' could not be found (are you missing a using directive or an assembly reference?)",
                line: i))
            .ToList();

        var deps = new Dictionary<string, IReadOnlySet<string>>
        {
            ["Core"] = new HashSet<string>(),
            ["Engines"] = new HashSet<string> { "Core" },
            ["App"] = new HashSet<string> { "Core", "Engines" },
        };

        var projected = BuildResultProjector.Project(Failed(errors), Defaults, deps);
        string json = JsonSerializer.Serialize(projected, SharedJsonOptions.Default);

        Assert.That(projected.ErrorSummary.All(s => s.Locations.Count <= BuildResultProjector.MaxSummaryLocations), Is.True);
        Assert.That(json, Does.Not.Contain("\"FullDiagnostics\"").IgnoreCase, "the carrier is [JsonIgnore]");
        Assert.That(
            json.Length,
            Is.LessThan(LargeResultHelper.OffloadThresholdBytes),
            $"projected payload was {json.Length} characters");
    }
}
