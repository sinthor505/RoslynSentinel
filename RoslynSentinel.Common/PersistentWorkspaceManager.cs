using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace RoslynSentinel.Common;

public partial class PersistentWorkspaceManager : IDisposable
{
    private readonly ILogger<PersistentWorkspaceManager> _logger;
    private MSBuildWorkspace? _workspace;
    private readonly SemaphoreSlim _solutionLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly List<string> _workspaceLoadErrors = new();
    private readonly ConcurrentBag<string> _externalChanges = new();
    private volatile bool _disposed = false;
    private readonly ConcurrentDictionary<FilePath, string> _failedChangesCache = new();
    private readonly ConcurrentDictionary<string, Dictionary<FilePath, string>> _stagedChanges = new();
    private readonly ConcurrentDictionary<string, Dictionary<FilePath, string>> _appliedChanges = new();
    private readonly ConcurrentDictionary<string, Dictionary<FilePath, string>> _revertedChanges = new();
    private readonly ConcurrentDictionary<string, DateTime> _internalChanges = new();
    private volatile int _workspaceVersion = 0;
    private DateTime _lastLoadedAt = DateTime.MinValue;
    private readonly Timer _debounceTimer;
    public readonly Guid SessionId = Guid.NewGuid();
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    // Per-tool sliding-window rate limiter: maps tool name → timestamps of recent calls.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<long>> _rateLimitWindows = new();
    private static readonly Dictionary<string, int> DefaultRateLimits = LoadRateLimits();

    // ── Circuit breaker state ─────────────────────────────────────────────────
    // Thresholds — start generous; tighten on observed session data.
    private const int BreakerStreakThreshold = 8;     // consecutive batches with zero successes
    private const int BreakerRateMinAttempts = 20;    // min attempts before rate-trip fires
    private const double BreakerRateThreshold = 0.30;  // >30% failure rate → halt
    private const int BreakerRollbackScoreThreshold = 20;    // weighted score (rollback=2, fail=1)
    private const int CautionStreakThreshold = 4;
    private const int CautionRateMinAttempts = 10;
    private const double CautionRateThreshold = 0.15;
    private const int CautionRollbackScoreThreshold = 10;

    private readonly object _breakerLock = new();
    private bool _breakerOpen;
    private int _consecutiveFailureStreak;
    private int _totalAttempts;
    private int _totalFailures;
    private int _weightedRollbackScore;

    public record StagedChangeSummary(
        string ChangeId,
        List<FilePath> AffectedFiles,
        string Description
    );

    public PersistentWorkspaceManager(ILogger<PersistentWorkspaceManager> logger)
    {
        _logger = logger;
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        if (!MSBuildLocator.IsRegistered)
        {
            _logger.LogInformation("Registering MSBuild defaults...");
            var instance = MSBuildLocator.RegisterDefaults();
            Debug.WriteLine($"MSBuild: {instance.MSBuildPath}");
            Debug.WriteLine($"Version: {instance.Version}");
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
        if (ext is not (".cs" or ".csproj" or ".sln"))
        {
            return;
        }

        // Ignore files written by ApplyProposedChangesAsync — they are already reflected in
        // the in-memory workspace and a redundant reload would hold _solutionLock for tens of
        // seconds, starving every other caller.
        if (_internalChanges.TryGetValue(e.FullPath, out var changedAt) &&
            (DateTime.UtcNow - changedAt).TotalSeconds < 5)
        {
            return;
        }

        // Ignore files generated by MSBuild under obj/ and bin/ directories.
        // These are written during OpenSolutionAsync and would otherwise rearm the debounce
        // timer indefinitely, creating an infinite solution-reload loop.
        var sep = Path.DirectorySeparatorChar;
        if (e.FullPath.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) ||
            e.FullPath.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingChanges[e.FullPath] = DateTime.UtcNow;
        _externalChanges.Add(e.FullPath);
        _debounceTimer.Change(500, Timeout.Infinite);
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
                _logger.LogInformation("Processing {Count} file system changes and reloading solution if necessary...", changes.Count);
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
                    var project = CurrentSolution.Projects.FirstOrDefault(p => p.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase) == true);
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
            ["list_project_documentation"] = 20,
            ["read_project_documentation"] = 30,
            ["update_project_documentation"] = 10,
            ["read_plan"] = 30,
            ["update_plan"] = 10,
            ["read_handoff"] = 30,
            ["write_handoff"] = 10,
            ["read_completed_work"] = 30,
            ["append_completed_work"] = 15,
            ["read_current_state"] = 30,
            ["update_current_state"] = 5,
            ["run_bridge_batch"] = 5,
            ["run_uplift_batch"] = 5,
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
                    _jsonOptions);
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
    public string StageChanges(Dictionary<FilePath, string> changes, string description)
    {
        var id = Guid.NewGuid().ToString("n")[..8];
        _stagedChanges[id] = changes;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Staged change {DocCommentId}: {Description} ({Count} files)", id, description, changes.Count);
        }
        return id;
    }

    /// <summary>
    /// Retrieves the content of all applied changes.
    /// </summary>
    public Dictionary<FilePath, string> GetAllAppliedChanges()
    {
        if (_appliedChanges == null || _appliedChanges.Count == 0)
        {
            return new Dictionary<FilePath, string>();
        }

        var result = new Dictionary<FilePath, string>();
        foreach (var innerDict in _appliedChanges.Values.Where(d => d != null))
        {
            foreach (var kvp in innerDict)
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// Retrieves the content of applied changes by ID.
    /// </summary>
    public Dictionary<FilePath, string> GetAppliedChanges(string changeId)
    {
        if (_appliedChanges.TryGetValue(changeId, out var changes))
        {
            return changes;
        }

        throw new KeyNotFoundException($"Applied change ID '{changeId}' not found.");
    }

    /// <summary>
    /// Retrieves the content of staged changes by ID.
    /// </summary>
    public Dictionary<FilePath, string> GetAllStagedChanges()
    {
        if (_stagedChanges == null || _stagedChanges.Count == 0)
        {
            return new Dictionary<FilePath, string>();
        }

        var result = new Dictionary<FilePath, string>();
        foreach (var innerDict in _stagedChanges.Values.Where(d => d != null))
        {
            foreach (var kvp in innerDict)
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// Retrieves the content of staged changes by ID.
    /// </summary>
    public Dictionary<FilePath, string> GetStagedChanges(string changeId)
    {
        _stagedChanges.TryGetValue(changeId, out var staged);
        _appliedChanges.TryGetValue(changeId, out var applied);
        _revertedChanges.TryGetValue(changeId, out var reverted);

        if (staged != null && staged.Count > 0)
        {
            return staged;
        }

        if (applied != null && applied.Count > 0)
        {
            return applied;
        }

        if (reverted != null && reverted.Count > 0)
        {
            return reverted;
        }

        throw new KeyNotFoundException($"Staged change ID '{changeId}' not found in current staged, applied, or reverted changes. Valid staged change IDs: [{string.Join(", ", _stagedChanges.Keys)}]; Valid applied change IDs: [{string.Join(", ", _appliedChanges.Keys)}]; Valid reverted change IDs: [{string.Join(", ", _revertedChanges.Keys)}]");
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
            _appliedChanges.AddOrUpdate(changeId, changes, (key, oldValue) => changes);
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
                _logger.LogInformation("Updated staged change '{DocCommentId}' with remaining {Count} failures.", changeId, remaining.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// Manually removes a staged change set without applying it.
    /// </summary>
    public bool DiscardStagedChanges(string changeId)
    {
        var reverted = _revertedChanges.TryRemove(changeId, out _);

        if (!reverted)
        {
            throw new KeyNotFoundException($"Staged change ID '{changeId}' not found in current staged, applied, or reverted changes. Valid staged change IDs: [{string.Join(", ", _stagedChanges.Keys)}]; Valid applied change IDs: [{string.Join(", ", _appliedChanges.Keys)}]; Valid reverted change IDs: [{string.Join(", ", _revertedChanges.Keys)}]");
        }

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
    /// <para><see cref="PreImages"/> maps each file path to its content immediately before
    /// the write (null if the file did not exist). Callers use this to populate
    /// <see cref="OperationItemRecord.BeforeSource"/> in forensic blobs, enabling undo.</para>
    /// </summary>
    public record ApplyChangesResult(
        bool Success,
        List<string> SucceededFiles,
        Dictionary<FilePath, string> FailedFiles,
        string Summary,
        bool WorkspaceInSync = false,
        int WorkspaceVersion = 0,
        IReadOnlyDictionary<string, string?>? PreImages = null
    );

    /// <summary>
    /// Writes proposed file changes to disk and updates the in-memory workspace.
    /// Captures a pre-image of every file before writing so callers can populate
    /// BeforeSource on OperationItemRecords for undo support.
    /// Retries on IOExceptions (e.g. file locks).
    /// </summary>
    public async Task<ApplyChangesResult> ApplyProposedChangesAsync(Dictionary<FilePath, string> changes, int retryCount = 3)
    {
        await _solutionLock.WaitAsync();
        var succeeded = new List<string>();
        var failed = new Dictionary<FilePath, string>();
        bool needsFullReload = false;

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

            // ── Pre-image capture ─────────────────────────────────────────────
            // Read every file BEFORE writing. null means the file did not previously
            // exist (undo should delete it rather than restore content).
            // Must run inside the lock and before the first write.
            var preImages = new Dictionary<string, string?>();
            foreach (var key in changes.Keys)
            {
                try
                {
                    preImages[key] = File.Exists(key) ? await File.ReadAllTextAsync(key) : null;
                }
                catch (Exception ex)
                {
                    // Cannot read pre-image — log and record null so the caller knows undo
                    // for this specific file is unavailable, but do not abort the whole batch.
                    preImages[key] = null;
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning("Pre-image capture failed for {FilePath}: {Message}", key, ex.Message);
                    }
                }
            }

            foreach (var change in changes)
            {
                var filePath = change.Key;
                var newContent = change.Value;
                bool success = false;
                string lastError = "";

                preImages.TryGetValue(filePath, out var preImage);
                if (preImage == newContent)
                {
                    _logger.LogWarning("Skipping no-op write for {FilePath}: proposed content is identical to existing content.", filePath);
                    Debug.WriteLine($"[Warning] Skipping no-op write for {filePath}: proposed content is identical to existing content.");
                    succeeded.Add(filePath);
                    continue;
                }

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
                    needsFullReload = await ApplyInMemoryDocumentUpdatesAsync(succeeded, CancellationToken.None);
                    workspaceInSync = !needsFullReload;
                    if (!needsFullReload)
                    {
                        Interlocked.Increment(ref _workspaceVersion);
                    }
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
            return new ApplyChangesResult(failed.Count == 0, succeeded, failed, summary,
                workspaceInSync, _workspaceVersion, preImages);
        }
        finally
        {
            _solutionLock.Release();

            if (needsFullReload && _workspace != null && !string.IsNullOrEmpty(SolutionPath))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ReloadWorkspaceFromDiskAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Error))
                        {
                            _logger.LogError(ex, "Background workspace reload failed.");
                        }
                    }
                });
            }
        }
    }

    // Fast in-memory path — O(files), no MSBuild, no I/O beyond reading .cs file content.
    // Returns true when a structural file (.csproj / .sln) was among the affected files and a
    // full MSBuild reload is needed; the caller fires that reload after releasing the lock.
    // Guards only on CurrentSolution == null so it also works in SetTestSolution test scenarios.
    private async Task<bool> ApplyInMemoryDocumentUpdatesAsync(List<string> affectedFiles, CancellationToken ct)
    {
        if (CurrentSolution == null)
        {
            return false;
        }

        bool needsFullReload = false;

        foreach (var filePath in affectedFiles)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext is ".csproj" or ".sln")
            {
                needsFullReload = true;
                continue;
            }

            if (ext != ".cs")
            {
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, ct);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("Could not read {FilePath} for in-memory update: {Message}", filePath, ex.Message);
                }
                continue;
            }

            var sourceText = SourceText.From(content, Encoding.UTF8);

            var doc = CurrentSolution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (doc != null)
            {
                CurrentSolution = CurrentSolution.WithDocumentText(doc.Id, sourceText);
            }
            else
            {
                var project = FindContainingProject(CurrentSolution, filePath);
                if (project != null)
                {
                    var docId = DocumentId.CreateNewId(project.Id);
                    var fileName = Path.GetFileName(filePath);
                    CurrentSolution = CurrentSolution.AddDocument(docId, fileName, sourceText, filePath: filePath);
                }
                else
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning("New .cs file {FilePath} does not belong to any project in the solution; skipping in-memory update.", filePath);
                    }
                }
            }
        }

        _lastLoadedAt = DateTime.UtcNow;

        // Prune expired _internalChanges entries to prevent the FileSystemWatcher from
        // treating stale entries as live and re-arming the debounce timer.
        var cutoff = DateTime.UtcNow.AddSeconds(-5);
        foreach (var key in _internalChanges.Keys.ToList())
        {
            if (_internalChanges.TryGetValue(key, out var ts) && ts < cutoff)
            {
                _internalChanges.TryRemove(key, out _);
            }
        }

        return needsFullReload;
    }

    // Longest-prefix match: returns the project whose .csproj directory is the deepest
    // ancestor of filePath, or null if no project contains it.
    private static Project? FindContainingProject(Solution solution, string filePath)
    {
        Project? best = null;
        int bestLen = -1;

        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null)
            {
                continue;
            }

            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (projectDir == null)
            {
                continue;
            }

            if (filePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase) &&
                projectDir.Length > bestLen)
            {
                best = project;
                bestLen = projectDir.Length;
            }
        }

        return best;
    }

    // Full MSBuild reload — runs outside the lock, re-acquires it only to swap CurrentSolution.
    // Callers fire this on a background Task.Run after releasing the main lock.
    private async Task ReloadWorkspaceFromDiskAsync(CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        var slnPath = SolutionPath;
        if (string.IsNullOrEmpty(slnPath))
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Background MSBuild reload: {SlnPath}", slnPath);
        }

        // We create a new workspace instance to ensure no cached metadata remains.
        // Pass the same MSBuild properties used in LoadSolutionAsync so that
        // NuGet vulnerability audit (NU1901-NU1904) does not block project loading.
        var newWorkspace = MSBuildWorkspace.Create(new Dictionary<string, string>
        {
            { "NuGetAudit", "false" },
            { "NuGetAuditLevel", "critical" }
        });
        newWorkspace.RegisterWorkspaceFailedHandler(d =>
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Refresh error: {Message}", d.Diagnostic.Message);
            }
        });

        Solution newSolution;
        try
        {
            newSolution = await newWorkspace.OpenSolutionAsync(slnPath, null, ct);
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Background MSBuild reload failed for {SlnPath}", slnPath);
            }
            newWorkspace.Dispose();
            return;
        }

        // Brief re-acquisition of the lock only to swap the workspace and solution.
        await _solutionLock.WaitAsync(ct);
        try
        {
            var old = _workspace;
            _workspace = newWorkspace;
            CurrentSolution = newSolution;
            _lastLoadedAt = DateTime.UtcNow;
            Interlocked.Increment(ref _workspaceVersion);

            var cutoff = DateTime.UtcNow.AddSeconds(-5);
            foreach (var key in _internalChanges.Keys.ToList())
            {
                if (_internalChanges.TryGetValue(key, out var ts) && ts < cutoff)
                {
                    _internalChanges.TryRemove(key, out _);
                }
            }

            old?.Dispose();
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    /// <summary>
    /// Attempts to re-write files that failed in previous attempts using cached content.
    /// </summary>
    public async Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3)
    {
        var toRetry = new Dictionary<FilePath, string>();

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
            return new ApplyChangesResult(true, new List<string>(), new Dictionary<FilePath, string>(), $"No matching failed changes found in cache to retry. Valid staged change IDs: [{string.Join(", ", _stagedChanges.Keys)}]; Valid applied change IDs: [{string.Join(", ", _appliedChanges.Keys)}]; Valid reverted change IDs: [{string.Join(", ", _revertedChanges.Keys)}]");
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

    // ── Circuit breaker public API ────────────────────────────────────────────

    /// <summary>
    /// Records the outcome of a batch operation and advances the circuit breaker state.
    /// Call once per batch-first mutation tool after work completes.
    /// Rollbacks are weighted 2× against plain failures (skips are benign and do not advance the streak).
    /// </summary>
    public void RecordBatchOutcome(int succeeded, int failed, int rolledBack, int skipped)
    {
        lock (_breakerLock)
        {
            _totalAttempts += succeeded + failed + rolledBack + skipped;
            _totalFailures += failed + rolledBack;
            _weightedRollbackScore += (rolledBack * 2) + failed;

            if (succeeded > 0)
            {
                _consecutiveFailureStreak = 0;
            }
            else if (failed + rolledBack > 0)
            {
                _consecutiveFailureStreak++;
            }
            // skips only — streak unchanged

            if (!_breakerOpen)
            {
                double failureRate = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts : 0;
                bool streakTrip = _consecutiveFailureStreak >= BreakerStreakThreshold;
                bool rateTrip = _totalAttempts >= BreakerRateMinAttempts && failureRate > BreakerRateThreshold;
                bool rollbackTrip = _weightedRollbackScore > BreakerRollbackScoreThreshold;

                if (streakTrip || rateTrip || rollbackTrip)
                {
                    _breakerOpen = true;
                    _logger.LogWarning(
                        "Circuit breaker TRIPPED. streak={Streak}, attempts={Attempts}, " +
                        "failureRate={Rate:P1}, rollbackScore={Score}",
                        _consecutiveFailureStreak, _totalAttempts, failureRate, _weightedRollbackScore);
                }
            }
        }
    }

    /// <summary>
    /// Returns a halt BatchResultSummary if the breaker is open (call at the top of every mutating tool).
    /// Returns null when tools may proceed.
    /// </summary>
    public BatchResultSummary? CheckBreaker()
    {
        lock (_breakerLock)
        {
            if (!_breakerOpen)
            {
                return null;
            }

            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;

            return new BatchResultSummary
            {
                ChangeId = "",
                BlobName = "",
                Severity = "halt",
                BreakerOpen = true,
                Directive = $"Circuit breaker open. All mutating tools disabled until reset_breaker is called by the user. " +
                              $"(streak={_consecutiveFailureStreak}/{BreakerStreakThreshold}, " +
                              $"attempts={_totalAttempts}, " +
                              $"failureRate={failureRatePct:F1}%/{BreakerRateThreshold * 100:F0}%, " +
                              $"rollbackScore={_weightedRollbackScore}/{BreakerRollbackScoreThreshold})",
            };
        }
    }

    /// <summary>
    /// Clears all circuit breaker state and re-enables mutating tools.
    /// Manual only — never auto-reset by design.
    /// </summary>
    public void ResetBreaker()
    {
        lock (_breakerLock)
        {
            _breakerOpen = false;
            _consecutiveFailureStreak = 0;
            _totalAttempts = 0;
            _totalFailures = 0;
            _weightedRollbackScore = 0;
        }

        _logger.LogInformation("Circuit breaker manually reset.");
    }

    /// <summary>Returns the current severity tier for inclusion in BatchResultSummary.</summary>
    public string GetBreakerSeverity()
    {
        lock (_breakerLock)
        {
            return ComputeSeverityUnlocked();
        }
    }

    /// <summary>Returns the human-readable directive for inclusion in BatchResultSummary.</summary>
    public string GetBreakerDirective()
    {
        lock (_breakerLock)
        {
            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;
            return ComputeDirectiveUnlocked(ComputeSeverityUnlocked(), failureRatePct);
        }
    }

    /// <summary>Returns a full snapshot of circuit breaker state for the get_breaker_status tool.</summary>
    public BreakerStatusReport GetBreakerStatus()
    {
        lock (_breakerLock)
        {
            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;
            string severity = ComputeSeverityUnlocked();
            string directive = ComputeDirectiveUnlocked(severity, failureRatePct);

            return new BreakerStatusReport(
                Open: _breakerOpen,
                Severity: severity,
                Directive: directive,
                ConsecutiveFailureStreak: _consecutiveFailureStreak,
                TotalAttempts: _totalAttempts,
                TotalFailures: _totalFailures,
                FailureRatePct: Math.Round(failureRatePct, 1),
                WeightedRollbackScore: _weightedRollbackScore,
                StreakTripThreshold: BreakerStreakThreshold,
                RollbackScoreTripThreshold: BreakerRollbackScoreThreshold,
                RateTripThresholdPct: BreakerRateThreshold * 100,
                RateMinAttempts: BreakerRateMinAttempts
            );
        }
    }

    private string ComputeSeverityUnlocked()
    {
        if (_breakerOpen)
        {
            return "halt";
        }

        double failureRate = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts : 0;
        bool caution = _consecutiveFailureStreak >= CautionStreakThreshold
                          || (_totalAttempts >= CautionRateMinAttempts && failureRate >= CautionRateThreshold)
                          || _weightedRollbackScore >= CautionRollbackScoreThreshold;

        return caution ? "caution" : "ok";
    }

    private string ComputeDirectiveUnlocked(string severity, double failureRatePct)
    {
        return severity switch
        {
            "halt" => $"Circuit breaker open. All mutating tools disabled until reset_breaker is called by the user. " +
                      $"(streak={_consecutiveFailureStreak}/{BreakerStreakThreshold}, " +
                      $"attempts={_totalAttempts}, " +
                      $"failureRate={failureRatePct:F1}%/{BreakerRateThreshold * 100:F0}%, " +
                      $"rollbackScore={_weightedRollbackScore}/{BreakerRollbackScoreThreshold})",
            "caution" => $"Elevated failure indicators — proceeding but monitor for trip. " +
                         $"streak={_consecutiveFailureStreak}/{BreakerStreakThreshold}, " +
                         $"failureRate={failureRatePct:F1}%/{BreakerRateThreshold * 100:F0}%, " +
                         $"rollbackScore={_weightedRollbackScore}/{BreakerRollbackScoreThreshold}.",
            _ => "Operating within normal failure tolerance.",
        };
    }

    public FilePath SetFilePath(string? filepath)
    {
        FilePath filePath = null;
        string? solutionRoot = this.GetSolutionRoot();

        if (!string.IsNullOrWhiteSpace(filepath) && !string.IsNullOrWhiteSpace(solutionRoot))
        {
            filePath = FilePath.FromWire(filepath, solutionRoot);
        }

        return filePath;
    }

    // In PersistentWorkspaceManager
    private readonly Dictionary<string, SymbolHandle> _trackedSymbols = new();

    public void TrackSymbol(string agentHandle, SymbolHandle handle)
    {
        _trackedSymbols[agentHandle] = handle;
    }

    public async Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken ct)
    {
        var solution = await GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == handle.ProjectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(ct);
        ISymbol? resolved = DocumentationCommentId.GetFirstSymbolForDeclarationId(handle.DocCommentId, compilation);
        return resolved;
    }
    public async Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken ct = default)
    {
        var solution = await GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(ct);
        return DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
    }

    public bool IsCurrentSession(string sessionId)
    {
        // TODO: revisit after empirical agent testing
        return sessionId == this.SessionId.ToString();
    }

    // v1 — single integration point for all symbol-accepting tools
    public async Task<SymbolResolution> ResolveFromWireAsync(
        string sessionId,
        string projectName,
        string docCommentId,
        CancellationToken ct)
    {
        if (!this.IsCurrentSession(sessionId))
        {
            return new SymbolResolution
            {
                Error = new EngineError(
                    EngineErrorCode.StaleSession,
                    "Symbol handle is from a prior workspace session. Re-run locate_symbol.",
                    DataTag.SymbolHandle)
            };
        }

        SymbolHandle handle = new SymbolHandle(sessionId, projectName, docCommentId);
        ISymbol? symbol = await this.ResolveSymbolAsync(handle, ct);

        if (symbol is null)
        {
            return new SymbolResolution
            {
                Handle = handle,
                Error = new EngineError(
                    EngineErrorCode.SymbolNotResolved,
                    $"Symbol '{docCommentId}' no longer resolves — may have been renamed, moved, or removed. Re-run locate_symbol.",
                    DataTag.SymbolHandle)
            };
        }

        return new SymbolResolution { Symbol = symbol, Handle = handle };
    }
}
