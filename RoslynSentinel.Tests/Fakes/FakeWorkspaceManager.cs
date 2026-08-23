using Microsoft.CodeAnalysis;

using ModelContextProtocol;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Fakes;

// Minimal IWorkspaceManager fake for tests that only need CurrentSolution / GetBranchedSolutionAsync.
// Every other member throws NotImplementedException - extend as a test actually needs a member.
public sealed class FakeWorkspaceManager : IWorkspaceManager
{
    public Solution? CurrentSolution { get; private set; }

    public void SetTestSolution(Solution solution) => CurrentSolution = solution;

    public Task<Solution> GetBranchedSolutionAsync(CancellationToken cancellationToken)
        => Task.FromResult(CurrentSolution
            ?? throw new SolutionNotLoadedException("No solution is loaded. Call load_solution with a .sln or .csproj path."));

    // --- Everything below: not needed by DiagnosticEngine, so left unimplemented on purpose ---

    public string? BaseRepoDirectory { get; set; }
    public int ProjectCount => CurrentSolution?.ProjectIds.Count ?? 0;
    public string? SolutionPath { get; set; }
    public int WorkspaceVersion => 0;
    public Guid SessionId => Guid.Empty;

    public Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePath, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public BatchResultSummary? CheckBreaker() => throw new NotImplementedException();
    public string? CheckRateLimit(string toolName, int defaultLimit) => throw new NotImplementedException();
    public void ClearDrift() => throw new NotImplementedException();
    public void Dispose() { }
    public string GetBreakerDirective() => throw new NotImplementedException();
    public string GetBreakerSeverity() => throw new NotImplementedException();
    public BreakerStatusReport GetBreakerStatus() => throw new NotImplementedException();
    public Task<List<string>> GetContentDriftAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public IEnumerable<string> GetDiagnostics() => throw new NotImplementedException();
    public List<string> GetExternalDrift() => throw new NotImplementedException();
    public HealthComponents GetHealthComponents() => throw new NotImplementedException();
    public List<(string RelativePath, string SolutionFolder)> GetSolutionFolderItems() => throw new NotImplementedException();
    public string? GetSolutionRoot() => throw new NotImplementedException();
    public List<string> GetWorkspaceLoadErrors() => throw new NotImplementedException();
    public WorkspaceStatus GetWorkspaceStatus() => throw new NotImplementedException();
    public bool IsCurrentSession(string sessionId) => throw new NotImplementedException();
    public Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped) => throw new NotImplementedException();
    public Task RemoveDocumentByPathAsync(FilePath filePath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void ResetBreaker() => throw new NotImplementedException();
    public Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3) => throw new NotImplementedException();
    public FilePath SetFilePath(string? filepath) => throw new NotImplementedException();
    public void TrackSymbol(string agentHandle, SymbolHandle handle) => throw new NotImplementedException();
}
