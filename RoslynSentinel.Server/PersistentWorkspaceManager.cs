using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace RoslynSentinel.Server;

public class PersistentWorkspaceManager : IDisposable
{
    private readonly ILogger<PersistentWorkspaceManager> _logger;
    private MSBuildWorkspace? _workspace;
    private Solution? _currentSolution;
    private readonly SemaphoreSlim _solutionLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly List<string> _workspaceLoadErrors = new();
    private readonly ConcurrentBag<string> _externalChanges = new();
    private volatile bool _disposed = false;
    private readonly ConcurrentDictionary<string, string> _failedChangesCache = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _stagedChanges = new();
    private readonly ConcurrentDictionary<string, DateTime> _internalChanges = new();
    private volatile int _workspaceVersion = 0;
    private DateTime _lastLoadedAt = DateTime.MinValue;
    private readonly Timer _debounceTimer;

    public record StagedChangeSummary(
        string ChangeId,
        List<string> AffectedFiles,
        string Description
    );


    public PersistentWorkspaceManager(ILogger<PersistentWorkspaceManager> logger)
    {
        _logger = logger;
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        if (!MSBuildLocator.IsRegistered)
        {
            _logger.LogInformation("Registering MSBuild defaults...");
            MSBuildLocator.RegisterDefaults();
        }
    }

    /// <summary>
    /// Returns a list of files that have been modified externally since the last sync.
    /// </summary>
    public List<string> GetExternalDrift()
    {
        return _externalChanges.Distinct().ToList();
    }

    /// <summary>
    /// Clears the drift list, indicating the AI has acknowledged and synced with disk.
    /// </summary>
    public void ClearDrift()
    {
        // ConcurrentBag has no Clear(); swap to a new instance atomically is not possible,
        // so drain it with TryTake instead.
        while (_externalChanges.TryTake(out _)) { }
    }

    public async Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        await _solutionLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Loading solution: {SolutionPath}", solutionPath);

            _workspace?.Dispose();
            // Suppress NuGet vulnerability audit during workspace load — this is a code-analysis
            // workspace, not a production build. Audit warnings (NU1901-NU1904) are MSBuild
            // design-time errors that block project loading but are irrelevant for code analysis.
            _workspace = MSBuildWorkspace.Create(new Dictionary<string, string>
            {
                { "NuGetAudit", "false" },
                { "NuGetAuditLevel", "critical" }
            });
            _workspaceLoadErrors.Clear();
            _workspace.RegisterWorkspaceFailedHandler((d) =>
            {
                _logger.LogWarning("Workspace error: {Message}", d.Diagnostic.Message);
                _workspaceLoadErrors.Add(d.Diagnostic.Message);
            });

            try
            {
                _currentSolution = await _workspace.OpenSolutionAsync(solutionPath, null, cancellationToken);
                _logger.LogInformation("Solution loaded with {ProjectCount} projects.", _currentSolution.ProjectIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open solution '{SolutionPath}'. Some projects might not load correctly.", solutionPath);
                _workspaceLoadErrors.Add($"Failed to open solution: {ex.Message}");
                // Even if solution fails to open, try to get current partial solution if any
                _currentSolution = _workspace.CurrentSolution;
                if (_currentSolution?.ProjectIds.Count == 0 && _workspaceLoadErrors.Count == 0)
                {
                    _workspaceLoadErrors.Add($"Solution '{solutionPath}' opened but no projects were found. This often indicates MSBuild errors. Check server logs for details.");
                }
            }

            _lastLoadedAt = DateTime.UtcNow;
            SetupWatcher(Path.GetDirectoryName(solutionPath)!);
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    private void SetupWatcher(string directory)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            Filter = "*.*"
        };

        _watcher.Changed += OnFileSystemChanged;
        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (ext is ".cs" or ".csproj" or ".sln")
        {
            _pendingChanges[e.FullPath] = DateTime.UtcNow;
            _externalChanges.Add(e.FullPath);
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private async void OnDebounceTimerElapsed(object? state)
    {
        if (_disposed) return;
        bool acquired = false;
        try
        {
            await _solutionLock.WaitAsync();
            acquired = true;
            var changes = _pendingChanges.Keys.ToList();
            _pendingChanges.Clear();

            if (_workspace == null || _currentSolution == null) return;

            _logger.LogInformation("Processing {Count} file system changes...", changes.Count);

            bool solutionNeedsReload = false;
            var projectsToReload = new HashSet<ProjectId>();

            foreach (var path in changes)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".sln")
                {
                    solutionNeedsReload = true;
                    break;
                }

                if (ext == ".csproj")
                {
                    var project = _currentSolution.Projects.FirstOrDefault(p => p.FilePath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
                    if (project != null)
                    {
                        projectsToReload.Add(project.Id);
                    }
                    else
                    {
                        solutionNeedsReload = true;
                        break;
                    }
                }
            }

            if (solutionNeedsReload)
            {
                _logger.LogInformation("Reloading entire solution...");
                var slnPath = _workspace.CurrentSolution.FilePath;
                if (!string.IsNullOrEmpty(slnPath))
                {
                    _currentSolution = await _workspace.OpenSolutionAsync(slnPath);
                }
            }
            else if (projectsToReload.Count > 0)
            {
                foreach (var projectId in projectsToReload)
                {
                    var project = _currentSolution.GetProject(projectId);
                    if (project?.FilePath != null)
                    {
                        _logger.LogInformation("Reloading project: {ProjectName}", project.Name);
                        await _workspace.OpenProjectAsync(project.FilePath);
                    }
                }
                _currentSolution = _workspace.CurrentSolution;
            }
            else
            {
                _currentSolution = _workspace.CurrentSolution;
            }
        }
        catch (ObjectDisposedException)
        {
            // Timer fired after Dispose() — semaphore or workspace already gone, safe to ignore.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing workspace.");
        }
        finally
        {
            if (acquired) _solutionLock.Release();
        }
    }

    public Solution? CurrentSolution => _currentSolution;

    public int ProjectCount => _currentSolution?.ProjectIds.Count ?? 0;

    public string? SolutionPath { get; set; }

    public IEnumerable<string> GetDiagnostics()
    {
        return _workspace?.Diagnostics.Select(d => d.Message) ?? Enumerable.Empty<string>();
    }

    public List<string> GetWorkspaceLoadErrors() => _workspaceLoadErrors.Distinct().ToList();

    public async Task<Solution> GetBranchedSolutionAsync()
    {
        await _solutionLock.WaitAsync();
        try
        {
            return _currentSolution ?? throw new InvalidOperationException(
                "No solution is loaded. Call load_solution with a .sln or .csproj path.");
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    /// <summary>
    /// Forces an in-memory solution for testing purposes, bypassing disk loading.
    /// </summary>
    public void SetTestSolution(Solution solution)
    {
        _currentSolution = solution;
    }

    /// <summary>
    /// Stores proposed changes in memory and returns a unique ID for later application or inspection.
    /// </summary>
    public string StageChanges(Dictionary<string, string> changes, string description)
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        _stagedChanges[id] = changes;
        _logger.LogInformation("Staged change {Id}: {Description} ({Count} files)", id, description, changes.Count);
        return id;
    }

    /// <summary>
    /// Retrieves the content of staged changes by ID.
    /// </summary>
    public Dictionary<string, string> GetStagedChanges(string changeId)
    {
        if (_stagedChanges.TryGetValue(changeId, out var changes)) return changes;
        throw new KeyNotFoundException($"Staged change ID '{changeId}' not found.");
    }

    /// <summary>
    /// Commits a previously staged set of changes to disk.
    /// On partial success, only successfully written files are removed from the staged set.
    /// </summary>
    public async Task<ApplyChangesResult> ApplyStagedChangesAsync(string changeId, int retryCount = 3)
    {
        var changes = GetStagedChanges(changeId);
        var result = await ApplyProposedChangesAsync(changes, retryCount);

        if (result.Success)
        {
            _stagedChanges.TryRemove(changeId, out _);
        }
        else if (result.SucceededFiles.Count > 0)
        {
            // Partial success: Update the staged set to only include the failures for next time.
            var remaining = changes
                .Where(kvp => !result.SucceededFiles.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            _stagedChanges[changeId] = remaining;
            _logger.LogInformation("Updated staged change '{Id}' with remaining {Count} failures.", changeId, remaining.Count);
        }

        return result;
    }

    /// <summary>
    /// Manually removes a staged change set without applying it.
    /// </summary>
    public bool DiscardStagedChanges(string changeId)
    {
        return _stagedChanges.TryRemove(changeId, out _);
    }

    public HealthComponents GetHealthComponents()
    {
        var roslynVersion = typeof(Solution).Assembly.GetName().Version?.ToString() ?? "Unknown";
        var msbuildInstance = MSBuildLocator.IsRegistered
            ? MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault(i => i.MSBuildPath == MSBuildLocator.QueryVisualStudioInstances().First().MSBuildPath)
            : null; // Simplified logic to find registered instance

        // Better way to find if any instance is registered
        var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
        var registeredInstance = instances.FirstOrDefault(); // MSBuildLocator doesn't expose which one is registered easily without trying

        return new HealthComponents(
            RoslynAvailable: true,
            RoslynVersion: roslynVersion,
            MsBuildFound: MSBuildLocator.IsRegistered || instances.Any(),
            MsBuildVersion: instances.FirstOrDefault()?.Version.ToString(),
            DotnetSdkAvailable: true, // We know it's available since we are running
            DotnetSdkVersion: Environment.Version.ToString()
        );
    }

    public WorkspaceStatus GetWorkspaceStatus()
    {
        // Compute staleness: count workspace documents whose on-disk file is newer than
        // the last time the workspace was loaded.
        var sampleStaleFiles = new List<string>();
        int staleCount = 0;
        if (_currentSolution != null && _lastLoadedAt != DateTime.MinValue)
        {
            foreach (var doc in _currentSolution.Projects.SelectMany(p => p.Documents))
            {
                var path = doc.FilePath;
                if (path == null || !File.Exists(path)) continue;
                if (File.GetLastWriteTimeUtc(path) > _lastLoadedAt)
                {
                    staleCount++;
                    if (sampleStaleFiles.Count < 5) sampleStaleFiles.Add(path);
                }
            }
        }

        return new WorkspaceStatus(
            State: _currentSolution != null ? 2 : 0,
            SolutionLoaded: _currentSolution != null,
            SolutionPath: SolutionPath,
            ProjectCount: ProjectCount,
            DocumentCount: _currentSolution?.Projects.SelectMany(p => p.Documents).Count() ?? 0,
            LastLoadedAt: _lastLoadedAt == DateTime.MinValue ? null : _lastLoadedAt,
            StaleDocumentCount: staleCount,
            RequiresReload: staleCount > 0,
            SampleStaleFiles: sampleStaleFiles.Count > 0 ? sampleStaleFiles : null
        );
    }

    /// <summary>
    /// Result of an attempt to apply multiple file changes to disk.
    /// <para><see cref="WorkspaceInSync"/> indicates whether the in-memory workspace was
    /// successfully refreshed after the write. If <c>false</c>, call <c>load_solution</c>
    /// to resync before making further semantic queries.</para>
    /// </summary>
    public record ApplyChangesResult(
        bool Success,
        List<string> SucceededFiles,
        Dictionary<string, string> FailedFiles,
        string Summary,
        bool WorkspaceInSync = false,
        int WorkspaceVersion = 0
    );

    /// <summary>
    /// Writes proposed file changes to disk and updates the in-memory workspace.
    /// Retries on IOExceptions (e.g. file locks).
    /// </summary>
    public async Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<string, string> changes, int retryCount = 3)
    {
        await _solutionLock.WaitAsync();
        var succeeded = new List<string>();
        var failed = new Dictionary<string, string>();

        // Clear retry cache for this specific batch
        foreach (var key in changes.Keys) _failedChangesCache.TryRemove(key, out _);

        try
        {
            if (_currentSolution == null) throw new InvalidOperationException("Solution not loaded.");

            foreach (var change in changes)
            {
                var filePath = change.Key;
                var newContent = change.Value;
                bool success = false;
                string lastError = "";

                // Mark as internal change before writing to avoid FileSystemWatcher loop
                _internalChanges[filePath] = DateTime.UtcNow;

                for (int attempt = 0; attempt <= retryCount; attempt++)
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(filePath);
                        if (directory != null && !Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        await File.WriteAllTextAsync(filePath, newContent);
                        success = true;
                        succeeded.Add(filePath);
                        _logger.LogInformation("Wrote changes to {FilePath} (Attempt {Attempt})", filePath, attempt + 1);
                        break;
                    }
                    catch (IOException ex)
                    {
                        lastError = ex.Message;
                        _logger.LogWarning("IO error writing to {FilePath}: {Message}. Retrying... ({Attempt}/{Max})", filePath, ex.Message, attempt + 1, retryCount);
                        if (attempt < retryCount) await Task.Delay(500);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        _logger.LogError(ex, "Permanent failure writing to {FilePath}", filePath);
                        break;
                    }
                }

                if (!success)
                {
                    failed[filePath] = lastError;
                    _failedChangesCache[filePath] = newContent; // Cache for efficient retry
                }
            }

            // --- Proactive Workspace Sync ---
            bool workspaceInSync = false;
            if (succeeded.Count > 0)
            {
                _logger.LogInformation("Synchronizing workspace with disk changes...");
                try
                {
                    await RefreshWorkspaceInternalAsync(succeeded);
                    workspaceInSync = true;
                    Interlocked.Increment(ref _workspaceVersion);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Workspace refresh failed after applying changes. Workspace may be stale; call load_solution to resync.");
                }
            }

            var summary = $"Applied {succeeded.Count} changes successfully. {failed.Count} failures.";
            return new ApplyChangesResult(failed.Count == 0, succeeded, failed, summary, workspaceInSync, _workspaceVersion);
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    private async Task RefreshWorkspaceInternalAsync(List<string> affectedFiles)
    {
        if (_workspace == null || _currentSolution == null) return;

        // MSBuildWorkspace can be finicky with refreshing individual projects.
        // The most reliable way to ensure a consistent, fresh view of the solution
        // after multiple file writes and additions is to trigger a reload.

        var slnPath = _workspace.CurrentSolution.FilePath;
        if (!string.IsNullOrEmpty(slnPath))
        {
            _logger.LogInformation("Reloading solution to synchronize changes: {SlnPath}", slnPath);

            // We create a new workspace instance to ensure no cached metadata remains
            var newWorkspace = MSBuildWorkspace.Create();
            newWorkspace.RegisterWorkspaceFailedHandler((d) => _logger.LogWarning("Refresh error: {Message}", d.Diagnostic.Message));

            _currentSolution = await newWorkspace.OpenSolutionAsync(slnPath);

            var oldWorkspace = _workspace;
            _workspace = newWorkspace;
            oldWorkspace.Dispose();
            _lastLoadedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Attempts to re-write files that failed in previous attempts using cached content.
    /// </summary>
    public async Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3)
    {
        var toRetry = new Dictionary<string, string>();

        if (specificFiles == null || specificFiles.Count == 0)
        {
            foreach (var kvp in _failedChangesCache) toRetry[kvp.Key] = kvp.Value;
        }
        else
        {
            foreach (var file in specificFiles)
            {
                if (_failedChangesCache.TryGetValue(file, out var content))
                {
                    toRetry[file] = content;
                }
            }
        }

        if (toRetry.Count == 0)
        {
            return new ApplyChangesResult(true, new List<string>(), new Dictionary<string, string>(), "No matching failed changes found in cache to retry.");
        }

        return await ApplyProposedChangesAsync(toRetry, retryCount);
    }

    public void Dispose()
    {
        _disposed = true;
        _workspace?.Dispose();
        _watcher?.Dispose();
        _debounceTimer.Dispose();
        _solutionLock.Dispose();
    }
}
