using Microsoft.CodeAnalysis;

using ModelContextProtocol;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Fakes;

// Minimal IWorkspaceManager fake for tests that only need CurrentSolution / GetCurrentSolutionAsync,
// or a GetSolutionRoot()-backed directory without a real Roslyn solution loaded at all (set
// SolutionPath directly - see GitToolsSmokeTests.cs for an example). Every other member throws
// NotImplementedException - extend as a test actually needs a member. If a test needs a real
// on-disk solution instead (actual file I/O, MSBuild load, watcher behavior), use
// RoslynSentinel.Tests.TestSolutionFixture (backed by PersistentWorkspaceManager) instead of this class.
public sealed class FakeWorkspaceManager : IWorkspaceManager, ISolutionProvider, ICircuitBreaker, IWorkspaceHealthReporter, IWorkspaceMutator, IRateLimiter, ISymbolResolver
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

    public Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePath, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePath>? deletePaths = null)
        => throw new NotImplementedException();
    public BatchResultSummary? CheckBreaker() => throw new NotImplementedException();
    public string? CheckRateLimit(string toolName, int defaultLimit) => throw new NotImplementedException();
    public void ClearExternalFileChanges() => throw new NotImplementedException();
    public void Dispose() { }
    public string GetBreakerDirective() => throw new NotImplementedException();
    public string GetBreakerSeverity() => throw new NotImplementedException();
    public BreakerStatusReport GetBreakerStatus() => throw new NotImplementedException();
    public Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public IEnumerable<string> GetDiagnostics() => throw new NotImplementedException();
    public List<string> GetExternalFileChanges() => throw new NotImplementedException();
    public HealthComponents GetHealthComponents() => throw new NotImplementedException();
    public List<(string RelativePath, string SolutionFolder)> GetSolutionFolderItems() => throw new NotImplementedException();

    // Mirrors PersistentWorkspaceManager.GetSolutionRoot(): CurrentSolution built via
    // TestSolutionBuilder has no FilePath (it's an AdhocWorkspace solution), so this falls
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
    public Task RemoveDocumentByPathAsync(FilePath filePath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void ResetBreaker() => throw new NotImplementedException();
    public Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    // Mirrors PersistentWorkspaceManager.SetFilePath(): resolves a wire path against
    // GetSolutionRoot(). Returns an unvalidated FilePath (SolutionRoot null/empty) rather than
    // throwing, same as the real implementation, when no root is set.
    public FilePath SetFilePath(string? filepath)
    {
        FilePath filePath = default;
        var solutionRoot = GetSolutionRoot();

        if (!string.IsNullOrWhiteSpace(filepath) && !string.IsNullOrWhiteSpace(solutionRoot))
        {
            filePath = FilePath.FromWire(filepath, solutionRoot);
        }

        return filePath;
    }

    public void TrackSymbol(string agentHandle, SymbolHandle handle) => throw new NotImplementedException();
}
