using Microsoft.CodeAnalysis;

using ModelContextProtocol;

namespace RoslynSentinel.Common;

public interface IWorkspaceManager
{
    string? BaseRepoDirectory
    {
        get;
        set;
    }
    Solution? CurrentSolution
    {
        get;
    }
    int ProjectCount
    {
        get;
    }
    string? SolutionPath
    {
        get;
        set;
    }
    int WorkspaceVersion
    {
        get;
    }

    Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePath, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<ProgressNotificationValue>? progress = null, CancellationToken cancellationToken = default);
    BatchResultSummary? CheckBreaker();
    string? CheckRateLimit(string toolName, int defaultLimit);
    void ClearDrift();
    void Dispose();
    Task<Solution> GetBranchedSolutionAsync(CancellationToken cancellationToken);
    string GetBreakerDirective();
    string GetBreakerSeverity();
    BreakerStatusReport GetBreakerStatus();
    Task<List<string>> GetContentDriftAsync(CancellationToken cancellationToken = default);
    IEnumerable<string> GetDiagnostics();
    List<string> GetExternalDrift();
    HealthComponents GetHealthComponents();
    List<(string RelativePath, string SolutionFolder)> GetSolutionFolderItems();
    string? GetSolutionRoot();
    List<string> GetWorkspaceLoadErrors();
    WorkspaceStatus GetWorkspaceStatus();
    bool IsCurrentSession(string sessionId);
    Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);
    Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default);
    void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped);
    Task RemoveDocumentByPathAsync(FilePath filePath, CancellationToken cancellationToken = default);
    void ResetBreaker();
    Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default);
    Task<SymbolResolution> ResolveFromWireAsync(string sessionId, string projectName, string docCommentId, CancellationToken cancellationToken);
    Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken);
    Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3);
    FilePath SetFilePath(string? filepath);
    void SetTestSolution(Solution solution);
    void TrackSymbol(string agentHandle, SymbolHandle handle);
    Guid SessionId
    {
        get;
    }
}