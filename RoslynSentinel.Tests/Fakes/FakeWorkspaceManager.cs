using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Tests.Fakes;

// Minimal IWorkspaceManager fake for tests that only need CurrentSolution / GetCurrentSolutionAsync,
// or a GetSolutionRoot()-backed directory without a real Roslyn solution loaded at all (set
// SolutionPath directly - see GitToolsSmokeTests.cs for an example). Every other member throws
// NotImplementedException - extend as a test actually needs a member. If a test needs a real
// on-disk solution instead (actual file I/O, MSBuild load, watcher behavior), use
// RoslynSentinel.Tests.TestSolutionFixture (backed by PersistentWorkspaceManager) instead of this class.
public sealed class FakeWorkspaceManager : IDisposable, IWorkspaceManager, ISolutionProvider, IManualCircuitBreaker, IAutomaticCircuitBreaker, IUnrecoverableBreaker, IWorkspaceHealthReporter, IWorkspaceMutator, IRateLimiter, ISymbolResolver, IScopedOperationLedger, IWorkspaceReader

{
    private readonly ScopedOperationLedgerEngine _ledger = new();

    public Solution? CurrentSolution
    {
        get; private set;
    }

    public void SetTestSolution(Solution solution) => CurrentSolution = solution;

    public Task<Solution> GetCurrentSolutionAsync(CancellationToken cancellationToken)
        => Task.FromResult(CurrentSolution
            ?? throw new SolutionNotLoadedException(SolutionNotLoadedMessage.Build(LoadState)));

    // Settable so a test can simulate a freshly started server (default) or one that has loaded before.
    public SolutionLoadState LoadState
    {
        get; set;
    } = new(SolutionLoadState.ProcessStartedUtc, null);

    public Task<Solution> GetSolutionAsync(ReadSource source, CancellationToken cancellationToken)
        => GetCurrentSolutionAsync(cancellationToken);

    // The three document members delegate to the same static core PersistentWorkspaceManager uses
    // (DocumentLookup), so fake-backed tests exercise the production lookup rules, not a stand-in.
    public async Task<string?> GetDocumentTextAsync(FilePathWrapper path, ReadSource source, CancellationToken cancellationToken)
    {
        var lookup = await GetDocumentAsync(path, source, cancellationToken);
        if (!lookup.TryGetDocument(out var document))
        {
            return null;
        }

        return (await document.GetTextAsync(cancellationToken)).ToString();
    }

    public async Task<DocumentLookupResult> GetDocumentAsync(FilePathWrapper path, ReadSource source, CancellationToken cancellationToken)
        => DocumentLookup.TryGetDocument(await GetSolutionAsync(source, cancellationToken), path);

    public async Task<IReadOnlyList<Document>> GetDocumentsAsync(DocumentScope scope, ReadSource source, CancellationToken cancellationToken)
        => DocumentLookup.GetDocuments(await GetSolutionAsync(source, cancellationToken), scope);

    // --- Everything below: not needed by DiagnosticEngine, so left unimplemented on purpose ---

    public string? BaseRepoDirectory
    {
        get; set;
    }
    public int ProjectCount => CurrentSolution?.ProjectIds.Count ?? 0;
    public string? SolutionPath
    {
        get; set;
    }
    public int WorkspaceVersion => 0;

    public async Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePathWrapper, string> changes, int retryCount = 3, bool validateChanges = false, bool rollbackOnPartialFailure = false, IProgress<EngineProgress>? progress = null, CancellationToken cancellationToken = default, IReadOnlyCollection<FilePathWrapper>? deletePaths = null)
    {
        var solution = CurrentSolution ?? throw new SolutionNotLoadedException("Solution not loaded.");

        // Scoped operation ledger gate: mirrors PersistentWorkspaceManager's per-target check.
        // While a ledger is open, a target is refused unless it is one of the ledger's own
        // tracked files (IsBlocked deliberately allows those through).
        foreach (var target in changes.Keys.Concat(deletePaths ?? []))
        {
            if (_ledger.IsBlocked(target, out var ledgerBlockReason))
            {
                return new ApplyChangesResult(
                    Success: false,
                    SucceededFiles: [],
                    FailedFiles: new Dictionary<FilePathWrapper, string> { [target] = ledgerBlockReason ?? "Blocked by an open scoped operation ledger entry." },
                    Summary: $"Refused - '{Path.GetFileName(target)}' has an open scoped operation ledger entry: {ledgerBlockReason}");
            }
        }

        // Mirrors PersistentWorkspaceManager's pre-apply validation: a change that introduces a new compile error is
        // rejected and nothing is applied, so tests of that rejection path can run on an in-memory workspace.
        if (validateChanges)
        {
            var validationReport = await ValidationEngine.ValidateChangesAsync(solution, changes, cancellationToken: cancellationToken);
            if (!validationReport.Success)
            {
                return new ApplyChangesResult(
                    Success: false,
                    SucceededFiles: [],
                    FailedFiles: [],
                    Summary: $"Validation failed with {validationReport.Diagnostics.Count} new error(s); no files written.",
                    ValidationResult: validationReport);
            }
        }

        var preImages = new Dictionary<string, string?>();
        var succeeded = new List<string>();

        foreach (var (path, newText) in changes)
        {
            string key = path;
            var documentId = solution.GetDocumentIdsWithFilePath(key).FirstOrDefault();
            if (documentId is null)
            {
                preImages[key] = null;
                var project = solution.Projects.First();
                solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), Path.GetFileName(key), SourceText.From(newText), filePath: key);
            }
            else
            {
                preImages[key] = (await solution.GetDocument(documentId)!.GetTextAsync(cancellationToken)).ToString();
                solution = solution.WithDocumentText(documentId, SourceText.From(newText));
            }

            succeeded.Add(key);
        }

        foreach (var path in deletePaths ?? [])
        {
            string key = path;
            var documentId = solution.GetDocumentIdsWithFilePath(key).FirstOrDefault();
            if (documentId is null)
            {
                continue;
            }

            preImages[key] = (await solution.GetDocument(documentId)!.GetTextAsync(cancellationToken)).ToString();
            solution = solution.RemoveDocument(documentId);
            succeeded.Add(key);
        }

        CurrentSolution = solution;
        return new ApplyChangesResult(Success: true, SucceededFiles: succeeded, FailedFiles: [], Summary: $"Applied {succeeded.Count} file(s) in memory.", WorkspaceInSync: true, PreImages: preImages);
    }
    public BatchResultSummary? CheckBreaker() => throw new NotImplementedException();
    // Always under limit -> tests exercising real rate-limit behavior use their own IRateLimiter (see RunTestTests).
    public string? CheckRateLimit(string toolName, int defaultLimit) => null;
    public void ClearExternalFileChanges() => throw new NotImplementedException();
    public void ClearSessionHalt() => throw new NotImplementedException();
    public void Dispose()
    {
    }
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

    public Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped) => throw new NotImplementedException();
    public Task RemoveDocumentByPathAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    bool ICircuitBreaker.IsTripped() => throw new NotImplementedException();
    string? ICircuitBreaker.StateMessage() => throw new NotImplementedException();
    void IManualCircuitBreaker.Reset() => throw new NotImplementedException();
    bool IManualCircuitBreaker.IsTripped() => throw new NotImplementedException();
    string? IManualCircuitBreaker.StateMessage() => throw new NotImplementedException();
    bool IAutomaticCircuitBreaker.RecordSearchOutcome(int matchCount) => throw new NotImplementedException();
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
    public Task<SymbolResolution> ResolveFromWireAsync(string projectName, string docCommentId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public string CachePendingChangeset(Dictionary<FilePathWrapper, string> changes, int retryCount, bool validateOnApply) => throw new NotImplementedException();
    public (Dictionary<FilePathWrapper, string> Changes, int RetryCount, bool ValidateOnApply)? TakePendingChangeset(string confirmationCode) => throw new NotImplementedException();
    // Mirrors PersistentWorkspaceManager.ResolveFromWire(): checks CurrentSolution directly (not
    // GetSolutionRoot()) to distinguish "no solution loaded" from "a solution is loaded but has no
    // on-disk root" (e.g. an in-memory SetTestSolution solution) -> the latter must fall through to
    // normal path resolution, not be misreported as "no solution loaded."
    public FilePathWrapper ResolveFromWire(string? filepath)
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

        return FilePathWrapper.ResolveFromWire(filepath, solutionRoot);
    }

    public void TrackSymbol(string agentHandle, SymbolHandle handle) => throw new NotImplementedException();

    // IScopedOperationLedger: delegates to a real ScopedOperationLedgerEngine so fake-backed tests
    // can exercise the ledger-opening and blocking behavior of MoveMember and other refactoring tools.
    // The engine enforces the invariant that only one ledger is open at a time, and tracks which files
    // are blocked by unresolved ledger entries.
    bool IScopedOperationLedger.TryOpen(string operationName, IReadOnlyList<LedgerEntryBase> entries, out string? rejectionReason, string? openingChangeId)
        => _ledger.TryOpen(operationName, entries, out rejectionReason, openingChangeId);

    bool IScopedOperationLedger.IsBlocked(FilePathWrapper filePath, out string? blockReason)
        => _ledger.IsBlocked(filePath, out blockReason);

    void IScopedOperationLedger.RecordFix(IReadOnlyList<string> entryIds, string changeId)
        => _ledger.RecordFix(entryIds, changeId);

    void IScopedOperationLedger.RecordUndo(string changeId)
        => _ledger.RecordUndo(changeId);

    bool IScopedOperationLedger.TryRelease()
        => _ledger.TryRelease();

    IReadOnlyList<LedgerEntryBase> IScopedOperationLedger.GetOpenEntries()
        => _ledger.GetOpenEntries();



    public async Task<Compilation> GetCompilationAsync(ProjectId projectId, ReadSource source, CancellationToken cancellationToken)
    {
        var solution = await GetCurrentSolutionAsync(cancellationToken);
        var project = solution.GetProject(projectId)
            ?? throw new ArgumentException($"No project with id '{projectId}' in the current solution.", nameof(projectId));
        return await project.GetCompilationAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Project '{project.Name}' does not support compilation.");
    }
}
