using System.Collections.Concurrent;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace RoslynSentinel.Server;

public class PersistentWorkspaceManager : IDisposable
{
    private readonly ILogger<PersistentWorkspaceManager> _logger;
    private MSBuildWorkspace? _workspace;
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

    // Per-tool sliding-window rate limiter: maps tool name → timestamps of recent calls.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _rateLimitWindows = new();
    private static readonly Dictionary<string, int> DefaultRateLimits = LoadRateLimits();

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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Loading solution: {SolutionPath}", solutionPath);
            }

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
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("Workspace error: {Message}", d.Diagnostic.Message);
                }
                _workspaceLoadErrors.Add(d.Diagnostic.Message);
            });

            try
            {
                CurrentSolution = await _workspace.OpenSolutionAsync(solutionPath, null, cancellationToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Solution loaded with {ProjectCount} projects.", CurrentSolution.ProjectIds.Count);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(ex, "Failed to open solution '{SolutionPath}'. Some projects might not load correctly.", solutionPath);
                }
                _workspaceLoadErrors.Add($"Failed to open solution: {ex.Message}");
                // Even if solution fails to open, try to get current partial solution if any
                CurrentSolution = _workspace.CurrentSolution;
                if (CurrentSolution?.ProjectIds.Count == 0 && _workspaceLoadErrors.Count == 0)
                {
                    _workspaceLoadErrors.Add($"Solution '{solutionPath}' opened but no projects were found. This often indicates MSBuild errors. Check server logs for details.");
                }
            }

            _lastLoadedAt = DateTime.UtcNow;
            SolutionPath = solutionPath;
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("AsyncUsage", "AsyncFixer03:Fire-and-forget async-void methods or delegates", Justification = "Event handler")]
    private async void OnDebounceTimerElapsed(object? state)
    {
        if (_disposed)
        {
            return;
        }

        bool acquired = false;
        try
        {
            await _solutionLock.WaitAsync();
            acquired = true;
            var changes = _pendingChanges.Keys.ToList();
            _pendingChanges.Clear();

            if (_workspace == null || CurrentSolution == null)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Processing {Count} file system changes...", changes.Count);
            }

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
                    var project = CurrentSolution.Projects.FirstOrDefault(p => p.FilePath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
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
                    CurrentSolution = await _workspace.OpenSolutionAsync(slnPath);
                }
            }
            else if (projectsToReload.Count > 0)
            {
                foreach (var projectId in projectsToReload)
                {
                    var project = CurrentSolution.GetProject(projectId);
                    if (project?.FilePath != null)
                    {
                        if (_logger.IsEnabled(LogLevel.Information))
                        {
                            _logger.LogInformation("Reloading project: {ProjectName}", project.Name);
                        }
                        await _workspace.OpenProjectAsync(project.FilePath);
                    }
                }
                CurrentSolution = _workspace.CurrentSolution;
            }
            else
            {
                CurrentSolution = _workspace.CurrentSolution;
            }
        }
        catch (ObjectDisposedException)
        {
            // Timer fired after Dispose() — semaphore or workspace already gone, safe to ignore.
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Error refreshing workspace.");
            }
        }
        finally
        {
            if (acquired)
            {
                _solutionLock.Release();
            }
        }
    }

    public Solution? CurrentSolution
    {
        get; private set;
    }

    public int ProjectCount => CurrentSolution?.ProjectIds.Count ?? 0;

    public string? SolutionPath
    {
        get; set;
    }

    /// <summary>
    /// Returns the directory that contains the loaded solution file, or <c>null</c> if no
    /// solution is loaded. Documentation tools use this to anchor their docs/ subdirectory.
    /// </summary>
    public string? GetSolutionRoot()
    {
        var filePath = CurrentSolution?.FilePath ?? SolutionPath;
        return filePath is not null ? Path.GetDirectoryName(filePath) : null;
    }

    /// <summary>
    /// Sliding-window rate limiter for MCP tool calls.
    /// Returns <c>null</c> if the call is within the allowed rate, or a diagnostic error
    /// message if the limit is exceeded. The caller should return that message as an error.
    /// </summary>
    /// <param name="toolName">The MCP tool name (used as the per-tool counter key).</param>
    /// <param name="defaultLimit">Calls-per-minute limit to use when no override is configured.</param>
    public string? CheckRateLimit(string toolName, int defaultLimit)
    {
        const int WindowSeconds = 60;
        long windowTicks = TimeSpan.FromSeconds(WindowSeconds).Ticks;
        long now = DateTime.UtcNow.Ticks;
        long cutoff = now - windowTicks;

        int limit = DefaultRateLimits.TryGetValue(toolName, out int configured)
            ? configured
            : defaultLimit;

        var queue = _rateLimitWindows.GetOrAdd(toolName, _ => new ConcurrentQueue<long>());

        // Drain expired entries from the front.
        while (queue.TryPeek(out long oldest) && oldest < cutoff)
            queue.TryDequeue(out _);

        int count = queue.Count;
        if (count >= limit)
        {
            return $"Rate limit: '{toolName}' called {count} times in {WindowSeconds}s (limit {limit}). "
                 + "This usually indicates a retry loop or thrashing. Stop, assess what is failing, "
                 + "and either fix the root cause or — if this is legitimate high-volume work — "
                 + "propose a batch tool that accomplishes it in fewer calls.";
        }

        queue.Enqueue(now);
        return null;
    }

    private static Dictionary<string, int> LoadRateLimits()
    {
        // Defaults from spec (calls per 60-second sliding window).
        var defaults = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["list_project_documentation"]       = 20,
            ["read_project_documentation"]       = 30,
            ["update_project_documentation"]     = 10,
            ["read_plan"]                         = 30,
            ["update_plan"]                       = 10,
            ["read_handoff"]                      = 30,
            ["write_handoff"]                     = 10,
            ["read_completed_work"]               = 30,
            ["append_completed_work"]             = 15,
            ["read_current_state"]                = 30,
            ["update_current_state"]              = 5,
            ["run_bridge_batch"]                  = 5,
            ["run_uplift_batch"]                  = 5,
            ["propagate_cancellation_token_batch"] = 5,
        };

        // Optional override file: rate-limits.json next to the server binary.
        try
        {
            var overridePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rate-limits.json");
            if (File.Exists(overridePath))
            {
                var json = File.ReadAllText(overridePath);
                var overrides = JsonSerializer.Deserialize<Dictionary<string, int>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (overrides is not null)
                {
                    foreach (var (key, value) in overrides)
                        defaults[key] = value;
                }
            }
        }
        catch { /* best effort — bad JSON in the override file does not crash the server */ }

        return defaults;
    }

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
            return CurrentSolution ?? throw new InvalidOperationException(
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
        CurrentSolution = solution;
    }

    /// <summary>
    /// Stores proposed changes in memory and returns a unique ID for later application or inspection.
    /// </summary>
    public string StageChanges(Dictionary<string, string> changes, string description)
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        _stagedChanges[id] = changes;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Staged change {Id}: {Description} ({Count} files)", id, description, changes.Count);
        }
        return id;
    }

    /// <summary>
    /// Retrieves the content of staged changes by ID.
    /// </summary>
    public Dictionary<string, string> GetStagedChanges(string changeId)
    {
        if (_stagedChanges.TryGetValue(changeId, out var changes))
        {
            return changes;
        }

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
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Updated staged change '{Id}' with remaining {Count} failures.", changeId, remaining.Count);
            }
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
            MsBuildFound: MSBuildLocator.IsRegistered || instances.Count != 0,
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
        if (CurrentSolution != null && _lastLoadedAt != DateTime.MinValue)
        {
            foreach (var doc in CurrentSolution.Projects.SelectMany(p => p.Documents))
            {
                var path = doc.FilePath;
                if (path == null || !File.Exists(path))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(path) > _lastLoadedAt)
                {
                    staleCount++;
                    if (sampleStaleFiles.Count < 5)
                    {
                        sampleStaleFiles.Add(path);
                    }
                }
            }
        }

        return new WorkspaceStatus(
            State: CurrentSolution != null ? 2 : 0,
            SolutionLoaded: CurrentSolution != null,
            SolutionPath: SolutionPath,
            ProjectCount: ProjectCount,
            DocumentCount: CurrentSolution?.Projects.SelectMany(p => p.Documents).Count() ?? 0,
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
        foreach (var key in changes.Keys)
        {
            _failedChangesCache.TryRemove(key, out _);
        }

        try
        {
            if (CurrentSolution == null)
            {
                throw new InvalidOperationException("Solution not loaded.");
            }

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
                        if (_logger.IsEnabled(LogLevel.Information))
                        {
                            _logger.LogInformation("Wrote changes to {FilePath} (Attempt {Attempt})", filePath, attempt + 1);
                        }
                        break;
                    }
                    catch (IOException ex)
                    {
                        lastError = ex.Message;
                        if (_logger.IsEnabled(LogLevel.Warning))
                        {
                            _logger.LogWarning("IO error writing to {FilePath}: {Message}. Retrying... ({Attempt}/{Max})", filePath, ex.Message, attempt + 1, retryCount);
                        }
                        if (attempt < retryCount)
                        {
                            await Task.Delay(500);
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        if (_logger.IsEnabled(LogLevel.Error))
                        {
                            _logger.LogError(ex, "Permanent failure writing to {FilePath}", filePath);
                        }
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
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Synchronizing workspace with disk changes...");
                }
                try
                {
                    await RefreshWorkspaceInternalAsync(succeeded);
                    workspaceInSync = true;
                    Interlocked.Increment(ref _workspaceVersion);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(ex, "Workspace refresh failed after applying changes. Workspace may be stale; call load_solution to resync.");
                    }
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
        if (_workspace == null || CurrentSolution == null)
        {
            return;
        }

        // MSBuildWorkspace can be finicky with refreshing individual projects.
        // The most reliable way to ensure a consistent, fresh view of the solution
        // after multiple file writes and additions is to trigger a reload.

        var slnPath = _workspace.CurrentSolution.FilePath;
        if (!string.IsNullOrEmpty(slnPath))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Reloading solution to synchronize changes: {SlnPath}", slnPath);
            }

            // We create a new workspace instance to ensure no cached metadata remains
            var newWorkspace = MSBuildWorkspace.Create();
            newWorkspace.RegisterWorkspaceFailedHandler((d) =>
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("Refresh error: {Message}", d.Diagnostic.Message);
                }
            });

            CurrentSolution = await newWorkspace.OpenSolutionAsync(slnPath);

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
            foreach (var kvp in _failedChangesCache)
            {
                toRetry[kvp.Key] = kvp.Value;
            }
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
        GC.SuppressFinalize(this);
    }
}
