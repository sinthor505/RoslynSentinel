using Microsoft.CodeAnalysis;

using ModelContextProtocol;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Fakes;

// Minimal IWorkspaceManager fake for tests that only need CurrentSolution / GetCurrentSolutionAsync,
// or a GetSolutionRoot()-backed directory without a real Roslyn solution loaded at all (set
// SolutionPath directly - see SentinelGitToolsSmokeTests.cs for an example). Every other member throws
// NotImplementedException - extend as a test actually needs a member. If a test needs a real
// on-disk solution instead (actual file I/O, MSBuild load, watcher behavior), use
// RoslynSentinel.Tests.TestSolutionFixture (backed by PersistentWorkspaceManager) instead of this class.
public sealed class FakeWorkspaceManager : IWorkspaceManager, ISolutionProvider, IManualCircuitBreaker, IAutomaticCircuitBreaker, IWorkspaceHealthReporter, IWorkspaceMutator, IRateLimiter, ISymbolResolver
{
    public Solution? CurrentSolution { get; private set; }

    public void SetTestSolution(Solution solution) => CurrentSolution = solution;

    public Task<Solution> GetCurrentSolutionAsync(CancellationToken cancellationToken)
        => Task.FromResult(CurrentSolution
            ?? throw new SolutionNotLoadedException("No solution is loaded. Call load_solution with a .sln or .csproj path."));

    // --- Everything below: not needed by DiagnosticEngine, so left unimplemented on purpose ---

    public string? BaseRepoDirectory { get; set; }
    public int ProjectCount => CurrentSolution?.ProjectIds.Count ?? 0;
    public string? SolutionPath { get; set; }
    public int WorkspaceVersion => 0;
    public Guid SessionId => Guid.Empty;

    public Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePathWrapper, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePathWrapper>? deletePaths = null)
        => throw new NotImplementedException();
    public BatchResultSummary? CheckBreaker() => throw new NotImplementedException();
    // Always under limit — tests exercising real rate-limit behavior use their own IRateLimiter (see RunTestTests).
    public string? CheckRateLimit(string toolName, int defaultLimit) => null;
    public void ClearExternalFileChanges() => throw new NotImplementedException();
    public void ClearSessionHalt() => throw new NotImplementedException();
    public void Dispose() { }
    public string GetBreakerDirective() => throw new NotImplementedException();
    public string GetBreakerSeverity() => throw new NotImplementedException();
    public BreakerStatusReport GetBreakerStatus() => throw new NotImplementedException();
    public Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public IEnumerable<string> GetDiagnostics() => throw new NotImplementedException();
    public List<string> GetExternalFileChanges() => throw new NotImplementedException();
    public HealthComponents GetHealthComponents() => throw new NotImplementedException();
    public bool IsSessionHalted() => false;
    public List<(string RelativePath, string SolutionFolder)> GetSolutionFolderItems() => throw new NotImplementedException();

    // Mirrors PersistentWorkspaceManager.GetSolutionRoot(): CurrentSolution built via
    // TestSolutionBuilder has no FilePathWrapper (it's an AdhocWorkspace solution), so this falls
    // back to SolutionPath, which tests can set directly when a root is needed.
    public string? GetSolutionRoot()
    {
        var filePath = CurrentSolution?.FilePath ?? SolutionPath;
        return filePath is not null ? Path.GetDirectoryName(filePath) : null;
    }

    // A fake was never "loaded" from disk, so there are no accumulated load errors to report.
    public List<string> GetWorkspaceLoadErrors() => new();

    // A fake was never "loaded" from disk, so there's no staleness to report - always fresh.
    public WorkspaceStatus GetWorkspaceStatus() => new(
        State: CurrentSolution != null ? 2 : 0,
        SolutionLoaded: CurrentSolution != null,
        SolutionPath: SolutionPath,
        ProjectCount: ProjectCount,
        DocumentCount: CurrentSolution?.Projects.SelectMany(p => p.Documents).Count() ?? 0);

    public bool IsCurrentSession(string sessionId) => throw new NotImplementedException();
    public Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped) => throw new NotImplementedException();
    public Task RemoveDocumentByPathAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    bool ICircuitBreaker.IsTripped() => throw new NotImplementedException();
    string? ICircuitBreaker.StateMessage() => throw new NotImplementedException();
    void IManualCircuitBreaker.Reset() => throw new NotImplementedException();
    bool IManualCircuitBreaker.IsTripped() => throw new NotImplementedException();
    string? IManualCircuitBreaker.StateMessage() => throw new NotImplementedException();
    void IAutomaticCircuitBreaker.RecordSearchOutcome(int matchCount) => throw new NotImplementedException();
    bool IAutomaticCircuitBreaker.IsTripped() => throw new NotImplementedException();
    string? IAutomaticCircuitBreaker.StateMessage() => throw new NotImplementedException();
    void IAutomaticCircuitBreaker.Reset() => throw new NotImplementedException();

    // Really implemented rather than a throw-stub, unlike the two breakers above: apply paths
    // running against this fake trip it on a blob-write failure, and the fake has no solution root
    // so blob writes legitimately don't happen. Throwing here would turn "no blob owed" into a
    // NotImplementedException from inside every fake-backed apply test. State is exposed so a test
    // can assert the trip without needing a real workspace.
    private string? _unrecoverableHaltMessage;

    void IUnrecoverableBreaker.Trip(string toolName, string changeId, string diagnostic) =>
        _unrecoverableHaltMessage ??= $"{toolName}/{changeId}: {diagnostic}";

    bool IUnrecoverableBreaker.IsTripped() => _unrecoverableHaltMessage is not null;
    string? IUnrecoverableBreaker.StateMessage() => _unrecoverableHaltMessage;
    public Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public string CachePendingChangeset(Dictionary<FilePathWrapper, string> changes, int retryCount, bool validateOnApply) => throw new NotImplementedException();
    public (Dictionary<FilePathWrapper, string> Changes, int RetryCount, bool ValidateOnApply)? TakePendingChangeset(string confirmationCode) => throw new NotImplementedException();
    // Mirrors PersistentWorkspaceManager.SetFilePath(): checks CurrentSolution directly (not
    // GetSolutionRoot()) to distinguish "no solution loaded" from "a solution is loaded but has no
    // on-disk root" (e.g. an in-memory SetTestSolution solution) — the latter must fall through to
    // normal path resolution, not be misreported as "no solution loaded."
    public FilePathWrapper SetFilePath(string? filepath)
    {
        var solutionRoot = GetSolutionRoot();

        if (CurrentSolution is null)
        {
            return new FilePathWrapper(string.Empty, solutionRoot, failureReason: FilePathFailureReason.NoSolutionLoaded);
        }

        if (string.IsNullOrWhiteSpace(filepath))
        {
            return new FilePathWrapper(string.Empty, solutionRoot, failureReason: FilePathFailureReason.PathInvalid);
        }

        return FilePathWrapper.FromWire(filepath, solutionRoot);
    }

    public void TrackSymbol(string agentHandle, SymbolHandle handle) => throw new NotImplementedException();
}
