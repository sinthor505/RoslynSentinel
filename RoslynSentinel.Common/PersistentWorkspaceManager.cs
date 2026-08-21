using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;

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
    private volatile bool _watcherOverflowed;
    private volatile bool _disposed = false;
    private readonly ConcurrentDictionary<FilePath, string> _failedChangesCache = new();
    private readonly ConcurrentDictionary<string, DateTime> _internalChanges = new();
    private volatile int _workspaceVersion = 0;
    private DateTime _lastLoadedAt = DateTime.MinValue;
    private readonly Timer _debounceTimer;
    public readonly Guid SessionId = Guid.NewGuid();

    /// <summary>
    /// Base repository directory used to resolve relative solution paths passed to <see cref="LoadSolutionAsync"/>.
    /// Defaults to <see cref="AppDomain.CurrentDomain"/>'s base directory when not explicitly set
    /// (e.g. via the server's --base-repo-dir startup argument).
    /// </summary>
    public string? BaseRepoDirectory
    {
        get; set;
    }
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

    /// <summary>
    /// Result summary returned by the write-through refactoring tools (ValidateAndApplyAsync).
    /// The change is already written to disk (or, when <see cref="DryRun"/> is true, validated
    /// but deliberately not written) — there is no separate apply step.
    /// </summary>
    public record AppliedChangeSummary(
        string? ChangeId,
        List<FilePath> AffectedFiles,
        string Description,
        bool DryRun,
        string? Diff = null,
        int? WorkspaceVersion = null
    )
    {
        /// <summary>Machine-parseable outcome — "applied" once written to disk, "dry_run_ok" when validated but not written.</summary>
        public string Status => DryRun ? "dry_run_ok" : "applied";

        public string Note => DryRun
            ? "Validated — introduces no new compiler errors. Not written to disk (dryRun=true). Re-call with dryRun=false to apply."
            : $"Written to disk. Call UndoLastApply(changeId: \"{ChangeId}\") to revert if needed.";
    }

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

    // Delegates to FilePath.NormalizeWirePath — the same sanitization every other tool's path
    // argument gets via FilePath.FromWire — so LoadSolution doesn't drift from that behavior.
    private static string? SanitizePathArgument(string? path)
    {
        return string.IsNullOrEmpty(path) ? path : FilePath.NormalizeWirePath(path);
    }

    /// <summary>
    /// Resolves a solution path that may be absolute or relative. Relative paths are checked,
    /// in order, against: the current working directory, <paramref name="baseRepoDirOverride"/>
    /// (if supplied for this call), <see cref="BaseRepoDirectory"/> (the server-wide default set
    /// via --base-repo-dir, if any), and the server's <see cref="AppDomain.CurrentDomain"/> base directory.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="baseRepoDirOverride"/> is supplied but does not exist as a
    /// directory on this host. A caller-supplied override that doesn't exist is treated as a
    /// fabricated/guessed value (e.g. an agent inventing a plausible-looking path instead of
    /// omitting the argument as the tool description recommends for relative paths) rather than
    /// silently discarded — discarding it would fall through to other candidates and could
    /// resolve to an unintended solution that happens to share the same relative path under a
    /// different base directory, with no error to signal the mismatch.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// Thrown when a relative <paramref name="solutionPath"/> does not exist under any of the
    /// candidate directories. Lists every candidate tried so the caller can diagnose a missing
    /// or misconfigured base repo directory.
    /// </exception>
    private string ResolveSolutionPath(string solutionPath, string? baseRepoDirOverride = null)
    {
        solutionPath = SanitizePathArgument(solutionPath) ?? solutionPath;
        baseRepoDirOverride = SanitizePathArgument(baseRepoDirOverride);

        if (string.IsNullOrWhiteSpace(solutionPath) || Path.IsPathRooted(solutionPath))
        {
            return solutionPath;
        }

        if (!string.IsNullOrWhiteSpace(baseRepoDirOverride) && !Directory.Exists(baseRepoDirOverride))
        {
            throw new ArgumentException(
                $"baseRepoDir '{baseRepoDirOverride}' does not exist on this host. If you don't know " +
                "the exact repo root, omit baseRepoDir entirely — LoadSolution resolves a relative " +
                "solutionPath against the server's configured base directory automatically. Do not " +
                "guess or fabricate a path.", nameof(baseRepoDirOverride));
        }

        var candidates = new List<string> { Path.GetFullPath(solutionPath) };

        if (!string.IsNullOrWhiteSpace(baseRepoDirOverride))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(baseRepoDirOverride, solutionPath)));
        }

        if (!string.IsNullOrWhiteSpace(BaseRepoDirectory))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(BaseRepoDirectory, solutionPath)));
        }

        candidates.Add(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, solutionPath)));

        foreach (var candidate in candidates.Distinct())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ToolNotFoundException(
            $"Could not resolve relative solution path '{solutionPath}'. Tried: {string.Join(", ", candidates.Distinct())}. " +
            $"Pass an absolute path, or supply baseRepoDir to LoadSolution (or set --base-repo-dir at server startup).");
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

    /// <summary>
    /// Compares every tracked document's in-memory text against the bytes currently on disk.
    /// Unlike <see cref="GetExternalDrift"/> (which relies on the FileSystemWatcher and can miss
    /// events under overflow), this reads disk directly, so it also catches drift the watcher
    /// never reported.
    /// </summary>
    public async Task<List<string>> GetContentDriftAsync(CancellationToken cancellationToken = default)
    {
        var solution = CurrentSolution;
        if (solution is null)
        {
            return [];
        }

        var driftedFiles = new List<string>();
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is null || !File.Exists(document.FilePath))
            {
                continue;
            }

            string onDiskText;
            try
            {
                onDiskText = await File.ReadAllTextAsync(document.FilePath, cancellationToken);
            }
            catch (IOException)
            {
                // File locked/being written concurrently — skip rather than false-positive.
                continue;
            }

            var inMemoryText = (await document.GetTextAsync(cancellationToken)).ToString();
            if (!string.Equals(inMemoryText, onDiskText, StringComparison.Ordinal))
            {
                driftedFiles.Add(document.FilePath);
            }
        }

        return driftedFiles;
    }

    public async Task LoadSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
        => await LoadSolutionAsync(solutionPath, baseRepoDir: null, cancellationToken);

    /// <param name="baseRepoDir">
    /// Optional per-call base directory used to resolve a relative <paramref name="solutionPath"/>.
    /// Takes precedence over the server-wide <see cref="BaseRepoDirectory"/>.
    /// </param>
    public async Task LoadSolutionAsync(string solutionPath, string? baseRepoDir, CancellationToken cancellationToken = default)
    {
        solutionPath = ResolveSolutionPath(solutionPath, baseRepoDir);

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
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    // FileSystemWatcher has a fixed-size internal buffer; a burst of changes arriving faster
    // than we can drain them (bulk git checkout, a codemod touching hundreds of files, etc.)
    // overflows it and the OS silently drops the events — Changed/Created/Deleted/Renamed
    // simply never fire for them. Without this handler that loss was invisible: CurrentSolution
    // would keep serving whatever it last had, forever, with no drift recorded and nothing to
    // reconcile. We don't know which files were affected, so — same as a .sln change — force a
    // full reload rather than silently continuing on stale content.
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(e.GetException(), "FileSystemWatcher error — file change notifications may have been lost. Forcing a full solution reload.");
        }

        _watcherOverflowed = true;
        _debounceTimer.Change(500, Timeout.Infinite);
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
            bool overflowed = _watcherOverflowed;
            _watcherOverflowed = false;

            if (_workspace == null || CurrentSolution == null)
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Processing {Count} file system changes and reloading solution if necessary...", changes.Count);
            }

            bool solutionNeedsReload = overflowed;
            var projectsToReload = new HashSet<ProjectId>();

            if (!solutionNeedsReload)
            {
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
            }

            if (solutionNeedsReload)
            {
                _logger.LogInformation(overflowed ? "Reloading entire solution (watcher overflow — change set unknown)..." : "Reloading entire solution...");
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
                // No .csproj/.sln involved — resetting to the workspace's own CurrentSolution
                // here would discard every edit accumulated in-memory since the last full
                // load/reload (WithDocumentText/AddDocument only ever update the branched
                // CurrentSolution property, never the underlying _workspace). Fold just the
                // changed .cs files into the existing CurrentSolution instead.
                bool needsReloadAfterAll = await ApplyInMemoryDocumentUpdatesAsync(changes, CancellationToken.None);
                if (needsReloadAfterAll)
                {
                    var slnPath = _workspace.CurrentSolution.FilePath;
                    if (!string.IsNullOrEmpty(slnPath))
                    {
                        CurrentSolution = await _workspace.OpenSolutionAsync(slnPath);
                    }
                }
                else
                {
                    Interlocked.Increment(ref _workspaceVersion);
                }
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

    /// <summary>
    /// Monotonically increasing counter bumped on every successful in-memory workspace update.
    /// Tools surface this so a caller can tell whether the workspace changed between two calls
    /// (e.g. a cached line number from an earlier response may no longer be valid) without
    /// having to diff file contents itself.
    /// </summary>
    public int WorkspaceVersion => _workspaceVersion;

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
    /// Relative paths of files attached via the .sln's Solution Folders (i.e. lines inside a
    /// <c>ProjectSection(SolutionItems)</c> block), paired with the enclosing folder's name.
    /// MSBuildWorkspace never represents these as Roslyn Projects/Documents — <c>Solution.Projects</c>
    /// only contains real buildable projects — so tools built on top of it (SearchSolutionText,
    /// ListSolutionItems(kind: files)) can never see them. This reads the raw .sln text directly
    /// to surface them instead. Classic .sln format only; .slnx (XML) solutions and in-memory/test
    /// solutions (no real .sln backing) return an empty list.
    /// </summary>
    public List<(string RelativePath, string SolutionFolder)> GetSolutionFolderItems()
    {
        var slnPath = CurrentSolution?.FilePath ?? SolutionPath;
        if (string.IsNullOrEmpty(slnPath) || !slnPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || !File.Exists(slnPath))
        {
            return [];
        }

        var items = new List<(string, string)>();
        var currentFolderName = "";
        var inSolutionItemsSection = false;

        foreach (var rawLine in File.ReadLines(slnPath))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("Project(", StringComparison.Ordinal))
            {
                // Project("{TYPE-GUID}") = "Name", "Path-or-Name", "{PROJECT-GUID}"
                var eq = line.IndexOf('=');
                if (eq >= 0)
                {
                    var firstField = line[(eq + 1)..].Split(',').FirstOrDefault();
                    currentFolderName = firstField?.Trim().Trim('"') ?? "";
                }
                continue;
            }

            if (line.StartsWith("ProjectSection(SolutionItems)", StringComparison.OrdinalIgnoreCase))
            {
                inSolutionItemsSection = true;
                continue;
            }

            if (line.StartsWith("EndProjectSection", StringComparison.OrdinalIgnoreCase))
            {
                inSolutionItemsSection = false;
                continue;
            }

            if (inSolutionItemsSection)
            {
                // Each line is "relative\path = relative\path" (the two sides are always identical).
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    var relativePath = line[..eq].Trim();
                    if (!string.IsNullOrEmpty(relativePath))
                    {
                        items.Add((relativePath, currentFolderName));
                    }
                }
            }
        }

        return items;
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

    /// <summary>
    /// Removes a document's in-memory tracking from CurrentSolution after its underlying file
    /// has been deleted from disk directly (e.g. as the old half of a file rename). Without this,
    /// the deleted file's Document stays tracked — and if a new Document was added at a different
    /// path with the same type declaration (as SyncTypeAndFilename does), the two coexist as a
    /// duplicate type in the compilation, corrupting symbol resolution for everything downstream.
    /// No-op if the path isn't currently tracked.
    /// </summary>
    public async Task RemoveDocumentByPathAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        await _solutionLock.WaitAsync(cancellationToken);
        try
        {
            var docId = CurrentSolution?.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (docId != null)
            {
                CurrentSolution = CurrentSolution!.RemoveDocument(docId);
            }
        }
        finally
        {
            _solutionLock.Release();
        }
    }

    public async Task<Solution> GetBranchedSolutionAsync(CancellationToken cancellationToken)
    {
        await _solutionLock.WaitAsync(cancellationToken);
        try
        {
            return CurrentSolution ?? throw new SolutionNotLoadedException(
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
        IReadOnlyDictionary<string, string?>? PreImages = null,
        DiagnosticReport? ValidationResult = null,
        List<string>? RolledBackFiles = null
    );

    /// <summary>
    /// Writes proposed file changes to disk and updates the in-memory workspace.
    /// Captures a pre-image of every file before writing so callers can populate
    /// BeforeSource on OperationItemRecords for undo support.
    /// Retries on IOExceptions (e.g. file locks).
    /// </summary>
    public async Task<ApplyChangesResult> ApplyProposedChangesAsync(
        Dictionary<FilePath, string> changes,
        int retryCount = 3,
        bool validateChanges = false,
        bool rollbackOnPartialFailure = false,
        IProgress<ProgressNotificationValue>? progress = default,
        CancellationToken cancellationToken = default)
    {
        // Refuse to write through unacknowledged external drift. A proposed change is always
        // computed against CurrentSolution's in-memory text; if the target file was touched on
        // disk after that (a human editing alongside the agent, git, a build step) and the drift
        // hasn't been acknowledged, the proposed content is stale and writing it would silently
        // clobber whatever changed it externally. Fail loud instead — same "no silent overwrites"
        // rule the rest of this write path already follows for no-op/whitespace-only writes.
        var drift = new HashSet<string>(GetExternalDrift(), StringComparer.OrdinalIgnoreCase);
        var driftedTargets = changes.Keys.Where(k => drift.Contains(k)).Distinct().ToList();
        if (driftedTargets.Count > 0)
        {
            return new ApplyChangesResult(
                Success: false,
                SucceededFiles: [],
                FailedFiles: driftedTargets.ToDictionary(f => f, _ => "Modified externally since last sync."),
                Summary: $"Refused to write — {driftedTargets.Count} target file(s) were modified externally since the " +
                         $"last sync: {string.Join(", ", driftedTargets.Select(f => Path.GetFileName(f)))}. Call " +
                         "ListExternalDiskChanges to review what changed, then either re-derive the proposed change " +
                         "against the current content, or call ClearExternalDrift to acknowledge and overwrite anyway.");
        }

        // Pre-lock validation: compiles an in-memory fork without holding the write lock,
        // consistent with the existing external validate-then-apply pattern.
        DiagnosticReport? validationReport = null;
        if (validateChanges && CurrentSolution != null)
        {
            validationReport = await ValidationEngine.ValidateChangesAsync(CurrentSolution, changes, cancellationToken);
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
                throw new SolutionNotLoadedException("Solution not loaded.");
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

                // For C# files, suppress writes where the only difference is whitespace
                // normalization — e.g. NormalizeWhitespace() adding blank lines between methods
                // that were already present in the original. Parsing both sides and comparing
                // their normalized forms catches any engine that forgot to preserve formatting.
                if (preImage != null &&
                    string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var normalizedNew = CSharpSyntaxTree.ParseText(newContent).GetRoot().NormalizeWhitespace().ToFullString();
                        var normalizedExisting = CSharpSyntaxTree.ParseText(preImage).GetRoot().NormalizeWhitespace().ToFullString();
                        if (normalizedNew == normalizedExisting)
                        {
                            _logger.LogInformation("Skipping whitespace-only write for {FilePath}: content is semantically identical after normalization.", filePath);
                            succeeded.Add(filePath);
                            continue;
                        }
                    }
                    catch
                    {
                        // Parse failure (e.g. malformed generated code) — fall through to normal write.
                    }
                }

                // Mark as internal change before writing to avoid FileSystemWatcher loop
                _internalChanges[filePath] = DateTime.UtcNow;

                for (int attempt = 0; attempt <= retryCount; attempt++)
                {
                    try
                    {
                        var directory = Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
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

            // ── Rollback on partial failure ─────────────────────────────────────
            // A multi-file change (e.g. a rename touching 5 files) is not atomic across the
            // per-file write loop above. If some files failed after others already succeeded,
            // restore the succeeded files to their pre-images so the change doesn't land
            // half-applied. Best-effort: a rollback write failure is logged, not thrown — the
            // caller already sees Success=false and can inspect Summary/FailedFiles.
            var rolledBack = new List<string>();
            if (rollbackOnPartialFailure && failed.Count > 0 && succeeded.Count > 0)
            {
                foreach (var filePath in succeeded)
                {
                    try
                    {
                        preImages.TryGetValue(filePath, out var original);
                        _internalChanges[filePath] = DateTime.UtcNow;
                        if (original is null)
                        {
                            if (File.Exists(filePath))
                            {
                                File.Delete(filePath);
                            }
                        }
                        else
                        {
                            await File.WriteAllTextAsync(filePath, original, CancellationToken.None);
                        }
                        rolledBack.Add(filePath);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled(LogLevel.Error))
                        {
                            _logger.LogError(ex, "Rollback failed for {FilePath} after partial-apply failure — file may be left in a partially-applied state.", filePath);
                        }
                    }
                }
                succeeded.Clear();
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

            var summary = rolledBack.Count > 0
                ? $"Partial write failure — {rolledBack.Count} already-written file(s) rolled back to keep the change atomic. {failed.Count} file(s) failed: {string.Join(", ", failed.Keys.Select(f => Path.GetFileName(f)))}."
                : $"Applied {succeeded.Count} changes successfully. {failed.Count} failures.";
            return new ApplyChangesResult(failed.Count == 0, succeeded, failed, summary,
                workspaceInSync, _workspaceVersion, preImages, validationReport,
                rolledBack.Count > 0 ? rolledBack : null);
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
    private async Task<bool> ApplyInMemoryDocumentUpdatesAsync(List<string> affectedFiles, CancellationToken cancellationToken)
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

            if (!File.Exists(filePath))
            {
                // File is gone (e.g. the old half of a rename) — drop its tracked Document so
                // the type it declared doesn't keep existing twice in the compilation.
                var deletedDocId = CurrentSolution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
                if (deletedDocId != null)
                {
                    CurrentSolution = CurrentSolution.RemoveDocument(deletedDocId);
                }
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, cancellationToken);
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
    private async Task ReloadWorkspaceFromDiskAsync(CancellationToken cancellationToken)
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
            newSolution = await newWorkspace.OpenSolutionAsync(slnPath, null, cancellationToken);
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
        await _solutionLock.WaitAsync(cancellationToken);
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
            return new ApplyChangesResult(true, new List<string>(), new Dictionary<FilePath, string>(), "No matching failed changes found in cache to retry.");
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

    public async Task<ISymbol?> ResolveSymbolAsync(SymbolHandle handle, CancellationToken cancellationToken)
    {
        var solution = await GetBranchedSolutionAsync(cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name == handle.ProjectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(cancellationToken);
        ISymbol? resolved = DocumentationCommentId.GetFirstSymbolForDeclarationId(handle.DocCommentId, compilation);
        return resolved;
    }
    public async Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await GetBranchedSolutionAsync(cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(cancellationToken);
        return DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
    }

    public bool IsCurrentSession(string sessionId)
    {
        // An absent sessionId means the caller isn't tracking sessions — nothing to compare
        // against, so it can't be stale. Only a non-empty sessionId that doesn't match the
        // current workspace session counts as stale.
        return string.IsNullOrEmpty(sessionId) || sessionId == this.SessionId.ToString();
    }

    // v1 — single integration point for all symbol-accepting tools
    public async Task<SymbolResolution> ResolveFromWireAsync(
        string sessionId,
        string projectName,
        string docCommentId,
        CancellationToken cancellationToken)
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
        ISymbol? symbol = await this.ResolveSymbolAsync(handle, cancellationToken);

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
