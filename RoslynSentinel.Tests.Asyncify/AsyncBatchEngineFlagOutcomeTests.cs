using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// RunBridgeBatchAsync, validation-failure skip: the skip message must report the real outcome of the
/// NeedsManualReview flag write, never claim a flag that was refused.
/// </summary>
[TestFixture]
[Category("AsyncBatchEngine")]
public class AsyncBatchEngineFlagOutcomeTests
{
    private IWorkspaceManager _workspaceManager;
    private AsyncBatchEngine _batchEngine;
    private string _tempDir;

    private const string AttrStub = """
        internal sealed class MigrationCandidateAttribute : System.Attribute
        {
            public MigrationCandidateAttribute(string p) {}
            public int Score { get; set; }
            public string? Reason { get; set; }
            public string? FlaggedDate { get; set; }
        }
        """;

    // No using directives: the bridge adds Task<>, CancellationToken and [Obsolete] references that cannot bind,
    // so in-memory validation fails and the batch takes the "flag NeedsManualReview" path.
    private const string BrokenBridgeSource = """
        public class TripService
        {
            [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
            public int GetTrips(int companyId)
            {
                return companyId;
            }
        }
        """ + "\n" + AttrStub;

    [SetUp]
    public void SetUp()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _tempDir = Path.Combine(Path.GetTempPath(), "AsyncBatchEngineFlagOutcomeTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _workspaceManager.SolutionPath = Path.Combine(_tempDir, "Test.sln");

        var validationEngine = new ValidationEngine(
            _workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        _batchEngine = new AsyncBatchEngine(
            _workspaceManager,
            new AsyncOptimizationEngine(_workspaceManager),
            validationEngine,
            new AntiPatternEngine(_workspaceManager),
            new MigrationLedger(),
            NullLogger<AsyncBatchEngine>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _workspaceManager.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // The workspace document holds workspaceText; the file on disk holds diskText. When their line-ending styles
    // differ, the write chokepoint's EOL guard refuses any proposed change built from the workspace text.
    private void SetSource(string workspaceText, string diskText)
    {
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.WriteAllText(path, diskText);
        var solution = TestSolutionBuilder.CreateSolutionWithProject(
            "TestProj", Path.Combine(_tempDir, "TestProj.csproj"), [("TripService.cs", workspaceText, path)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_ValidationFails_FlagWriteRefused_MessageDoesNotClaimFlag()
    {
        var lf = BrokenBridgeSource.Replace("\r\n", "\n");
        SetSource(workspaceText: lf.Replace("\n", "\r\n"), diskText: lf);

        // A read-only target makes the disk write fail, so the write chokepoint rolls back and the flag is refused.
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

            Assert.That(result.Applied, Is.Empty, "A bridge that fails validation must not be applied.");
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            var message = result.Skipped[0].Reason;
            Assert.That(message, Does.StartWith("Validation produced"));
            Assert.That(message, Does.Not.Contain("flagged NeedsManualReview"),
                "The flag write was refused, so the message must not claim the flag was written.");
            Assert.That(message, Does.Contain("flag could not be written"));
            Assert.That(File.ReadAllText(path), Does.Not.Contain("NeedsManualReview"),
                "The read-only write was refused, so the file on disk must not carry the NeedsManualReview flag.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_ValidationFails_FlagWriteSucceeds_MessageReportsFlag()
    {
        var lf = BrokenBridgeSource.Replace("\r\n", "\n");
        SetSource(workspaceText: lf, diskText: lf);

        var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

        Assert.That(result.Applied, Is.Empty, "A bridge that fails validation must not be applied.");
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        var message = result.Skipped[0].Reason;
        Assert.That(message, Does.StartWith("Validation produced"));
        Assert.That(message, Does.EndWith("; flagged NeedsManualReview"));
        Assert.That(File.ReadAllText(Path.Combine(_tempDir, "TripService.cs")), Does.Contain("NeedsManualReview"),
            "The flag write succeeded, so the file on disk must carry the NeedsManualReview flag.");
    }

    // RunBridgeBatchAsync CT-add fallback: the async overload exists without a CancellationToken, so the batch
    // adds one. System.Threading is not imported, so the CT-add result fails in-memory validation and the
    // skip message must report the flag only because the NeedsManualReview write really reached disk.
    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_CtAddValidationFails_FlagOnDisk_MessageReportsFlag()
    {
        var source = """
            using System.Threading.Tasks;

            public class TripService
            {
                [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
                public int GetTrips(int companyId)
                {
                    return companyId;
                }

                public Task<int> GetTripsAsync(int companyId)
                {
                    return Task.FromResult(companyId);
                }
            }

            internal sealed class MigrationCandidateAttribute : System.Attribute
            {
                public MigrationCandidateAttribute(string p) {}
                public int Score { get; set; }
                public string? Reason { get; set; }
                public string? FlaggedDate { get; set; }
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");

        var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

        Assert.That(result.Applied, Is.Empty, "A CT-add that fails validation must not be applied.");
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        var message = result.Skipped[0].Reason;
        Assert.That(message, Does.StartWith("CT-add validation failed"));
        Assert.That(message, Does.EndWith("; flagged NeedsManualReview"),
            "The flag write succeeded, so the skip message must report it.");
        Assert.That(File.ReadAllText(path), Does.Contain("NeedsManualReview"),
            "The message claims the flag was written, so the file on disk must carry NeedsManualReview.");
    }

    // CT-add validation fails in memory, then the NeedsManualReview flag write is refused because the candidate
    // file is read-only. The skip message must not claim the flag was written.
    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_CtAddValidationFails_FlagWriteRefused_MessageDoesNotClaimFlagged()
    {
        var source = """
            using System.Threading.Tasks;

            public class TripService
            {
                [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
                public int GetTrips(int companyId)
                {
                    return companyId;
                }

                public Task<int> GetTripsAsync(int companyId)
                {
                    return Task.FromResult(companyId);
                }
            }

            internal sealed class MigrationCandidateAttribute : System.Attribute
            {
                public MigrationCandidateAttribute(string p) {}
                public int Score { get; set; }
                public string? Reason { get; set; }
                public string? FlaggedDate { get; set; }
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

            Assert.That(result.Applied, Is.Empty, "A CT-add that fails validation must not be applied.");
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            var message = result.Skipped[0].Reason;
            Assert.That(message, Does.StartWith("CT-add validation failed"));
            Assert.That(message, Does.Contain("skipped; the manual-review flag could not be written"));
            Assert.That(message, Does.Not.Contain("flagged NeedsManualReview"),
                "The flag write was refused, so the message must not claim the flag was written.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // Body-rewrite fallback: the async overload already takes a CancellationToken, but its body still calls a sync
    // [Obsolete("Asyncify-bridge: ...")] wrapper with no XAsync sibling. The rewrite emits an awaited LoadTripsAsync
    // that does not exist, so in-memory validation fails and the skip message must report the flag only because the
    // NeedsManualReview write really reached disk.
    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_BodyRewriteValidationFails_FlagOnDisk_MessageReportsFlag()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
                public int GetTrips(int companyId)
                {
                    return companyId;
                }

                public async Task<int> GetTripsAsync(int companyId, CancellationToken cancellationToken)
                {
                    return LoadTrips(companyId);
                }

                [System.Obsolete("Asyncify-bridge: call LoadTripsAsync instead.")]
                public int LoadTrips(int companyId)
                {
                    return companyId;
                }
            }

            internal sealed class MigrationCandidateAttribute : System.Attribute
            {
                public MigrationCandidateAttribute(string p) {}
                public int Score { get; set; }
                public string? Reason { get; set; }
                public string? FlaggedDate { get; set; }
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");

        var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

        Assert.That(result.Applied, Is.Empty, "A body rewrite that fails validation must not be applied.");
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        var message = result.Skipped[0].Reason;
        Assert.That(message, Does.StartWith("Body-rewrite validation failed"));
        Assert.That(message, Does.EndWith("; flagged NeedsManualReview"),
            "The flag write succeeded, so the skip message must report it.");
        Assert.That(File.ReadAllText(path), Does.Contain("NeedsManualReview"),
            "The message claims the flag was written, so the file on disk must carry NeedsManualReview.");
    }

    // Body-rewrite fallback with the NeedsManualReview flag write refused because the candidate file is read-only.
    // The skip message must not claim the flag was written.
    [Test, CancelAfter(60000)]
    public async Task RunBridgeBatch_BodyRewriteValidationFails_FlagWriteRefused_MessageDoesNotClaimFlagged()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                [MigrationCandidate("AsyncBridgeCandidate", Score = 70, Reason = "calls-CommonSearch:70", FlaggedDate = "2026-06-01")]
                public int GetTrips(int companyId)
                {
                    return companyId;
                }

                public async Task<int> GetTripsAsync(int companyId, CancellationToken cancellationToken)
                {
                    return LoadTrips(companyId);
                }

                [System.Obsolete("Asyncify-bridge: call LoadTripsAsync instead.")]
                public int LoadTrips(int companyId)
                {
                    return companyId;
                }
            }

            internal sealed class MigrationCandidateAttribute : System.Attribute
            {
                public MigrationCandidateAttribute(string p) {}
                public int Score { get; set; }
                public string? Reason { get; set; }
                public string? FlaggedDate { get; set; }
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var result = await _batchEngine.RunBridgeBatchAsync(maxBridges: 1);

            Assert.That(result.Applied, Is.Empty, "A body rewrite that fails validation must not be applied.");
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            var message = result.Skipped[0].Reason;
            Assert.That(message, Does.StartWith("Body-rewrite validation failed"));
            Assert.That(message, Does.Contain("flag could not be written"));
            Assert.That(message, Does.Not.Contain("flagged NeedsManualReview"),
                "The flag write was refused, so the message must not claim the flag was written.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // PropagateCancellationTokenBatchAsync: the forwarded source validates in memory, but the write to disk is
    // refused because the file is read-only. The file must be reported as Failed, never as Applied.
    [Test, CancelAfter(60000)]
    public async Task PropagateCtBatch_WriteRefused_FileReportedFailedNotApplied()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                public async Task RunAsync(CancellationToken cancellationToken)
                {
                    await WorkAsync();
                }

                public Task WorkAsync() => Task.CompletedTask;

                public Task WorkAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var input = new PropagateCtBatchInput
            {
                Targets = [new PropagateCtFileTarget { FilePath = path }],
                FlagFailures = false,
            };

            var result = await _batchEngine.PropagateCancellationTokenBatchAsync(input);

            Assert.That(result.Applied, Is.Empty, "A refused write must not be reported as applied.");
            Assert.That(result.TotalForwarded, Is.EqualTo(0), "A refused write must not count its forwarded call sites.");
            Assert.That(result.Failed, Has.Count.EqualTo(1));
            Assert.That(result.Failed[0].Reason, Does.StartWith("The updated file could not be written"));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // RunUpliftBatchAsync: the uplifted caller validates in memory, but the write to disk is refused because the
    // file is read-only. The caller must be reported as skipped with the write-refused reason, never as uplifted.
    [Test, CancelAfter(60000)]
    public async Task RunUpliftBatch_WriteRefused_CallerSkippedNotUplifted()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                [System.Obsolete("Asyncify-bridge: call LoadTripsAsync instead.")]
                public int LoadTrips(int companyId)
                {
                    return companyId;
                }

                public Task<int> LoadTripsAsync(int companyId, CancellationToken cancellationToken)
                {
                    return Task.FromResult(companyId);
                }

                public async Task<int> CallerAsync(CancellationToken cancellationToken)
                {
                    return LoadTrips(1);
                }
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var result = await _batchEngine.RunUpliftBatchAsync("LoadTrips", maxCallers: 1);

            Assert.That(result.Uplifted, Is.Empty, "A refused write must not be reported as uplifted.");
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            Assert.That(result.Skipped[0].Reason, Does.StartWith("skipped; the uplifted caller could not be written"),
                "Only the write-refused branch produces this reason; anything else means the branch was not reached.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // Records formatted warning messages so a test can prove a specific log-and-continue branch ran.
    private sealed class WarningCapturingLogger : Microsoft.Extensions.Logging.ILogger<AsyncBatchEngine>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    // PropagateCancellationTokenBatchAsync with FlagFailures: the forwarded source fails validation (the file has a
    // pre-existing compile error), so the NeedsManualReview flag path runs; the flag write is refused because the
    // file is read-only. The method must not be listed in FlaggedMethods and a warning must be logged.
    [Test, CancelAfter(60000)]
    public async Task PropagateCtBatch_ValidationFails_FlagWriteRefused_MethodNotListedAsFlagged()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;

            public class TripService
            {
                public async Task RunAsync(CancellationToken cancellationToken)
                {
                    var value = await WorkAsync();
                }

                public Task<int> WorkAsync() => Task.FromResult(1);

                public Task WorkAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            }
            """.Replace("\r\n", "\n");

        SetSource(workspaceText: source, diskText: source);
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var logger = new WarningCapturingLogger();
        var validationEngine = new ValidationEngine(
            _workspaceManager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        var batchEngine = new AsyncBatchEngine(
            _workspaceManager,
            new AsyncOptimizationEngine(_workspaceManager),
            validationEngine,
            new AntiPatternEngine(_workspaceManager),
            new MigrationLedger(),
            logger);

        try
        {
            var input = new PropagateCtBatchInput
            {
                Targets = [new PropagateCtFileTarget { FilePath = path }],
                FlagFailures = true,
            };

            var result = await batchEngine.PropagateCancellationTokenBatchAsync(input);

            Assert.That(result.Applied, Is.Empty, "A forwarded source that fails validation must not be applied.");
            Assert.That(result.Failed, Has.Count.EqualTo(1));
            Assert.That(result.Failed[0].Reason, Does.StartWith("Validation produced"));
            Assert.That(logger.Warnings, Has.Some.Contains("NeedsManualReview flag write refused"),
                "The flag path must have run and logged the refused write.");
            Assert.That(result.Failed[0].FlaggedMethods, Is.Empty,
                "The flag write was refused, so the method must not be listed as flagged.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // RunUpliftBatchAsync: the uplifted caller fails in-memory validation (no usings, so Task and CancellationToken
    // cannot bind) and the flag write to the read-only file is refused. The skip reason must not claim the flag.
    [Test, CancelAfter(60000)]
    public async Task RunUpliftBatch_ValidationFails_FlagWriteRefused_MessageDoesNotClaimFlagged()
    {
        var source = """
            public class TripService
            {
                [System.Obsolete("Asyncify-bridge: call LoadTripsAsync instead.")]
                public int LoadTrips(int companyId)
                {
                    return companyId;
                }

                public async Task<int> CallerAsync(CancellationToken cancellationToken)
                {
                    return LoadTrips(1);
                }
            }

            """ + "\n" + AttrStub;
        var lf = source.Replace("\r\n", "\n");
        SetSource(workspaceText: lf, diskText: lf);
        var path = Path.Combine(_tempDir, "TripService.cs");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var result = await _batchEngine.RunUpliftBatchAsync("LoadTrips", maxCallers: 1);

            Assert.That(result.Uplifted, Is.Empty, "A caller that fails validation must not be reported as uplifted.");
            Assert.That(result.Skipped, Has.Count.EqualTo(1));
            var message = result.Skipped[0].Reason;
            Assert.That(message, Does.StartWith("Validation produced"));
            Assert.That(message, Does.Contain("flag could not be written"));
            Assert.That(message, Does.Not.Contain("flagged NeedsManualReview"),
                "The flag write was refused, so the message must not claim the flag was written.");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    // Same broken uplift as the read-only case, but the file is writable: the NeedsManualReview flag lands on disk
    // and the skip reason reports it.
    [Test, CancelAfter(60000)]
    public async Task RunUpliftBatch_ValidationFails_FlagWriteSucceeds_MessageReportsFlag()
    {
        var source = """
            public class TripService
            {
                [System.Obsolete("Asyncify-bridge: call LoadTripsAsync instead.")]
                public int LoadTrips(int companyId)
                {
                    return companyId;
                }

                public async Task<int> CallerAsync(CancellationToken cancellationToken)
                {
                    return LoadTrips(1);
                }
            }

            """ + "\n" + AttrStub;
        var lf = source.Replace("\r\n", "\n");
        SetSource(workspaceText: lf, diskText: lf);

        var result = await _batchEngine.RunUpliftBatchAsync("LoadTrips", maxCallers: 1);

        Assert.That(result.Uplifted, Is.Empty, "A caller that fails validation must not be reported as uplifted.");
        Assert.That(result.Skipped, Has.Count.EqualTo(1));
        var message = result.Skipped[0].Reason;
        Assert.That(message, Does.StartWith("Validation produced"));
        Assert.That(message, Does.EndWith("; flagged NeedsManualReview"));
        Assert.That(File.ReadAllText(Path.Combine(_tempDir, "TripService.cs")), Does.Contain("NeedsManualReview"),
            "The flag write succeeded, so the file on disk must carry the NeedsManualReview flag.");
    }
}
