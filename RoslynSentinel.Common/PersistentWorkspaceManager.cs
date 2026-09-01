using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

using ModelContextProtocol;

namespace RoslynSentinel.Common;

/// <summary>
/// Production <see cref="IWorkspaceManager"/>: loads and mutates a real MSBuild-backed
/// <see cref="Microsoft.CodeAnalysis.Solution"/> on disk. For tests, prefer
/// <c>RoslynSentinel.Tests.Fakes.FakeWorkspaceManager</c> (lightweight, in-memory, throws
/// NotImplementedException on unstubbed members) unless the test specifically needs this class's
/// real load/write/watch behavior against actual files — in that case use
/// <c>RoslynSentinel.Tests.TestSolutionFixture</c>, which stands up a disposable on-disk copy of
/// the Samples/ContosoOrders scenario and loads it through this class.
/// </summary>
public partial class PersistentWorkspaceManager : IDisposable, IWorkspaceManager, ISolutionProvider, IManualCircuitBreaker, IAutomaticCircuitBreaker, IWorkspaceHealthReporter, IWorkspaceMutator, IRateLimiter, ISymbolResolver
{
    private readonly ILogger<IWorkspaceManager> _logger;
    private MSBuildWorkspace? _workspace;
    private readonly SemaphoreSlim _solutionLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private readonly List<FileSystemWatcher> _outOfTreeWatchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingChanges = new();
    private readonly List<string> _workspaceLoadErrors = new();
    private readonly ConcurrentBag<string> _externalChanges = new();
    private volatile bool _watcherOverflowed;
    // Session-wide fatal latch: set once by ApplyProposedChangesAsync on a confirmed hash-backed
    // drift hit (see docs/current/ideas/external-drift-hard-blocker.md proposal item 2). While
    // true, every mutating call fails immediately via SessionHaltedException, regardless of which
    // file it targets. Cleared only out-of-band via ClearSessionHalt (SentinelAdminTools), never
    // by the exception's own throw path.
    private volatile bool _sessionHalted;
    private volatile bool _disposed = false;
    private readonly ConcurrentDictionary<FilePath, string> _failedChangesCache = new();
    // _internalChanges/_externalChanges are the older path-key + ~5s-freshness-window
    // self-write-suppression mechanism (see OnFileSystemChanged). _knownFileHashes (below) is a
    // newer, content-based check layered IN FRONT of this one, not a replacement — deliberately,
    // per docs/current/ideas/external-drift-hard-blocker.md's "Decisions" section. The hash check
    // is the authoritative signal for "did this file's content actually change"; these older
    // fields remain unreplaced behind it for now. Do not read the coexistence of both mechanisms
    // as an unintentional half-finished migration — it is a deliberate staged rollout, and
    // _internalChanges/_externalChanges are an intentional future-removal candidate once the
    // hash-based gate has proven itself in production, not a bug to "clean up" reflexively.
    private readonly ConcurrentDictionary<string, (DateTime Timestamp, string Content)> _internalChanges = new();
    // Content-hash baseline: path (normalized via FilePath's own case-insensitive, separator-
    // canonicalized equality/hashing) → SHA-256 of the last content RoslynSentinel itself wrote or
    // loaded for that file. Populated wholesale on LoadSolutionAsync, updated per-file on a
    // successful ApplyProposedChangesAsync write, and consulted first in OnFileSystemChanged: a
    // watcher event whose on-disk hash still matches the recorded hash is provably our own echo or
    // a no-op, regardless of path-key formatting or timing — see the hard-blocker doc for why this
    // closes a whole class of false positive, not just today's two known bugs.
    private readonly ConcurrentDictionary<FilePath, string> _knownFileHashes = new();

    /// <summary>
    /// Changesets rejected by <c>ApplyDiff</c>'s whole-file-rewrite size guard, keyed by a
    /// randomly generated confirmation code. A caller that intended the large rewrite replays
    /// <c>ApplyDiff(action: confirmationCode, confirmationCode: "...")</c> to apply the exact
    /// changeset that was rejected, without resending file content. Entries expire after
    /// <see cref="PendingConfirmationTtl"/> and are swept lazily on each cache/take call.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingChangeset> _pendingConfirmations = new();
    private static readonly TimeSpan PendingConfirmationTtl = TimeSpan.FromMinutes(10);

    private sealed record PendingChangeset(
        Dictionary<FilePath, string> Changes, int RetryCount, bool ValidateOnApply, DateTime ExpiresAtUtc);

    /// <summary>
    /// Caches <paramref name="changes"/> under a fresh confirmation code and returns the code.
    /// Also opportunistically sweeps expired entries so the cache doesn't grow unbounded across
    /// a long-running server session.
    /// </summary>
    public string CachePendingChangeset(Dictionary<FilePath, string> changes, int retryCount, bool validateOnApply)
    {
        foreach (var kvp in _pendingConfirmations)
        {
            if (kvp.Value.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _pendingConfirmations.TryRemove(kvp.Key, out _);
            }
        }

        var code = Guid.NewGuid().ToString("N")[..8];
        _pendingConfirmations[code] = new PendingChangeset(changes, retryCount, validateOnApply, DateTime.UtcNow.Add(PendingConfirmationTtl));
        return code;
    }

    /// <summary>
    /// Retrieves and removes the changeset cached under <paramref name="confirmationCode"/>.
    /// Returns null if the code is unrecognized or has expired (one-time use).
    /// </summary>
    public (Dictionary<FilePath, string> Changes, int RetryCount, bool ValidateOnApply)? TakePendingChangeset(string confirmationCode)
    {
        if (!_pendingConfirmations.TryRemove(confirmationCode, out var pending))
        {
            return null;
        }

        if (pending.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return (pending.Changes, pending.RetryCount, pending.ValidateOnApply);
    }
    private volatile int _workspaceVersion = 0;
    private DateTime _lastLoadedAt = DateTime.MinValue;
    private readonly Timer _debounceTimer;

    public Guid SessionId { get; } = Guid.NewGuid();

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

    private readonly Lock _breakerLock = new();
    private bool _breakerOpen;
    private int _consecutiveFailureStreak;
    private int _totalAttempts;
    private int _totalFailures;
    private int _weightedRollbackScore;

    // ── Orientation breaker state ─────────────────────────────────────────────
    // Independent from the mutating-tools breaker above: trips after repeated zero-match
    // SearchSolutionText calls, auto-resets on the next successful allowlisted call. See
    // IAutomaticCircuitBreaker.
    private const int OrientationBreakerTripThreshold = 3;
    private readonly Lock _orientationBreakerLock = new();
    private bool _orientationBreakerOpen;
    private int _consecutiveZeroMatchSearches;

    public PersistentWorkspaceManager(ILogger<IWorkspaceManager> logger)
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
    public List<string> GetExternalFileChanges()
    {
        return _externalChanges.Distinct().ToList();
    }

    /// <summary>
    /// Clears the drift list, indicating the AI has acknowledged and synced with disk.
    /// </summary>
    public void ClearExternalFileChanges()
    {
        // ConcurrentBag has no Clear(); swap to a new instance atomically is not possible,
        // so drain it with TryTake instead.
        while (_externalChanges.TryTake(out _)) { }
    }

    /// <summary>
    /// True once a confirmed drift hit has tripped the session-wide halt latch. See
    /// <see cref="SessionHaltedException"/> and docs/current/ideas/external-drift-hard-blocker.md.
    /// </summary>
    public bool IsSessionHalted() => _sessionHalted;

    /// <summary>
    /// Out-of-band recovery: clears the session-wide halt latch after a human/operator has
    /// reviewed the drift that tripped it. Deliberately not reachable from the model's normal
    /// tool surface — only via the Admin-gated SentinelAdminTools.
    /// </summary>
    public void ClearSessionHalt()
    {
        _sessionHalted = false;
    }

    // SHA-256 of the given content, hex-encoded lowercase. Cheap relative to the I/O already
    // being done on both the write side (content is already in memory) and the watcher side
    // (which already reads the file to do an equivalent content comparison — see
    // OnFileSystemChanged). Not a security boundary — just a fast, collision-safe-enough content
    // fingerprint — so SHA-256 is used for its BCL support and zero extra dependency, not because
    // cryptographic strength matters here.
    private static string ComputeContentHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    // Rebuilds _knownFileHashes wholesale from CurrentSolution's just-loaded documents. Must fully
    // reset (clear then repopulate), not merge, so a hash left over from a previous load can never
    // survive a reload and be compared against unrelated new content — the same "stale state
    // survives reload" shape ClearExternalFileChanges already guards against for _externalChanges.
    // Called with _solutionLock already held (from within LoadSolutionAsync).
    private void PopulateKnownFileHashes()
    {
        _knownFileHashes.Clear();
        if (CurrentSolution is null)
        {
            return;
        }

        foreach (var document in CurrentSolution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is null || !File.Exists(document.FilePath))
            {
                continue;
            }

            try
            {
                var content = File.ReadAllText(document.FilePath);
                _knownFileHashes[new FilePath(document.FilePath)] = ComputeContentHash(content);
            }
            catch (IOException)
            {
                // Locked/mid-write during load — leave unhashed; the next write or watcher event
                // that touches this path will populate it then.
            }
        }
    }

    /// <summary>
    /// Compares every tracked document's in-memory text against the bytes currently on disk.
    /// Unlike <see cref="GetExternalFileChanges"/> (which relies on the FileSystemWatcher and can miss
    /// events under overflow), this reads disk directly, so it also catches drift the watcher
    /// never reported.
    /// </summary>
    public async Task<List<string>> GetContentExternalFileChangesAsync(CancellationToken cancellationToken = default)
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
            var solutionDirectory = Path.GetDirectoryName(solutionPath)!;
            SetupWatcher(solutionDirectory);
            SetupOutOfTreeWatchers(solutionDirectory);

            // A full reload re-derives CurrentSolution from disk, so any drift flagged against the
            // previous in-memory state is stale by construction — otherwise a flag raised before
            // the reload permanently blocks writes to that file for the rest of the session, since
            // only ClearExternalFileChanges (not a reload) ever drains _externalChanges.
            ClearExternalFileChanges();

            // Rebuild the content-hash baseline from what was just loaded — same "full reset, not
            // just added-to" requirement as ClearExternalFileChanges just above, so a stale hash
            // from a previous load never survives a reload and is compared against genuinely new
            // content.
            PopulateKnownFileHashes();

            // OpenSolutionAsync throwing (e.g. solutionPath doesn't exist on disk) previously left
            // _workspaceLoadErrors populated but returned normally, so a bad path silently reported
            // success with an empty CurrentSolution. Surface it as a real failure instead — the
            // LoadSolution tool wrapper's catch block already turns a thrown ToolException into a
            // correct Success=false ToolResult.
            if (CurrentSolution == null || CurrentSolution.ProjectIds.Count == 0)
            {
                var detail = _workspaceLoadErrors.Count > 0
                    ? string.Join(" ", _workspaceLoadErrors)
                    : "no projects were found.";
                throw new ToolNotFoundException($"Solution '{solutionPath}' failed to load: {detail}");
            }
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

    // The main watcher only covers the solution directory's own subtree
    // (SetupWatcher(Path.GetDirectoryName(solutionPath))). A project or document referenced from
    // outside that tree — a linked file, a shared project pulled in via a relative "..\" path, a
    // solution folder pointing elsewhere — is invisible to it: edits there never populate
    // _externalChanges and the drift write-guard in ApplyProposedChangesAsync never fires for
    // them either, so reads would silently serve stale content indefinitely. This sets up one
    // extra watcher per distinct out-of-tree project directory found in the freshly loaded
    // solution, deduped to the shortest common ancestor so a project nested inside another
    // watched project's directory doesn't get its own redundant watcher.
    private void SetupOutOfTreeWatchers(string solutionDirectory)
    {
        foreach (var watcher in _outOfTreeWatchers)
        {
            watcher.Dispose();
        }
        _outOfTreeWatchers.Clear();

        if (CurrentSolution is null)
        {
            return;
        }

        var solutionDirFull = Path.GetFullPath(solutionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var outOfTreeDirs = CurrentSolution.Projects
            .Select(p => p.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetDirectoryName(p!))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => Path.GetFullPath(d!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(d => !IsUnderDirectory(d, solutionDirFull))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Drop any directory that is itself a subdirectory of another candidate — the parent's
        // recursive watcher already covers it.
        var roots = outOfTreeDirs
            .Where(d => !outOfTreeDirs.Any(other =>
                !string.Equals(other, d, StringComparison.OrdinalIgnoreCase) && IsUnderDirectory(d, other)))
            .ToList();

        foreach (var dir in roots)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    Filter = "*.*"
                };

                watcher.Changed += OnFileSystemChanged;
                watcher.Created += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _outOfTreeWatchers.Add(watcher);

                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Watching out-of-tree project directory: {Directory}", dir);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(ex, "Failed to set up watcher for out-of-tree directory {Directory}", dir);
                }
            }
        }
    }

    private static bool IsUnderDirectory(string candidate, string root)
    {
        var rootWithSep = root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
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

        // ── Content-hash baseline gate (layered in FRONT of the older path-key/timestamp check
        // below — see _knownFileHashes's declaration-site comment for why both exist) ──────────
        // Only meaningful for Changed/Created, which have real on-disk content to compare;
        // Renamed/Deleted fall through to the older check unchanged, same as that check already
        // treats them specially.
        if (e.ChangeType is WatcherChangeTypes.Changed or WatcherChangeTypes.Created)
        {
            var hashKey = new FilePath(e.FullPath);
            if (_knownFileHashes.TryGetValue(hashKey, out var recordedHash))
            {
                try
                {
                    if (FilePathLock.IsLocked(e.FullPath))
                    {
                        // Write in flight for this exact path — same race the older check guards
                        // against below; skip the verification read rather than race the writer's
                        // open handle.
                        return;
                    }

                    var onDiskContent = File.Exists(e.FullPath) ? File.ReadAllText(e.FullPath) : null;
                    var onDiskHash = onDiskContent is null ? null : ComputeContentHash(onDiskContent);
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Drift hash check for {PathKey}: recorded={RecordedHash} onDisk={OnDiskHash} match={Match}",
                            hashKey, recordedHash, onDiskHash ?? "(missing)", onDiskHash == recordedHash);
                    }
                    if (onDiskHash == recordedHash)
                    {
                        // Hash-confirmed no-op or echo of our own write — never flag as drift,
                        // regardless of what the older path-key/timestamp check below would decide.
                        return;
                    }
                }
                catch (IOException)
                {
                    // Locked/being written concurrently — can't verify; assume it's our own write
                    // in progress rather than false-positive an external edit, matching the older
                    // check's identical fallback below.
                    return;
                }
            }
        }

        // Ignore files written by ApplyProposedChangesAsync — they are already reflected in
        // the in-memory workspace and a redundant reload would hold _solutionLock for tens of
        // seconds, starving every other caller. Path+timing alone isn't enough to tell our own
        // write apart from a human editing the same file within the suppression window, so we
        // also verify the on-disk content still matches what we wrote (Changed/Created events);
        // Renamed always falls through as real (no content to compare, and always someone else's
        // action — nothing in this codebase renames a tracked file directly outside a rename-shaped
        // multi-file apply that writes both paths' *content*, not a rename). Deleted gets its own
        // narrower check just below, for a genuine tracked delete via deletePaths.
        if (_internalChanges.TryGetValue(e.FullPath, out var recordedChange) &&
            (DateTime.UtcNow - recordedChange.Timestamp).TotalSeconds < 5)
        {
            if (e.ChangeType is WatcherChangeTypes.Deleted && recordedChange.Content.Length == 0 && !File.Exists(e.FullPath))
            {
                // Our own tracked delete (ApplyProposedChangesAsync's deletePaths branch records
                // an empty-string sentinel before deleting — see its call site). Content can't be
                // compared the way Changed/Created is, but "we just deleted this path ourselves,
                // recently, and it's still gone" is unambiguous enough to suppress without a false
                // positive risk that matters here — unlike Changed/Created, nothing else could have
                // legitimately produced a content match to check against for a delete.
                return;
            }
            else if (e.ChangeType is WatcherChangeTypes.Renamed or WatcherChangeTypes.Deleted)
            {
                // Fall through — treat as a real external change.
            }
            else if (FilePathLock.IsLocked(e.FullPath))
            {
                // A write to this exact path is still in flight — reading now would race the
                // writer's open handle. Skip the verification read entirely rather than let it
                // throw; this is our own write in progress, not a false-positive external edit.
                return;
            }
            else
            {
                try
                {
                    var onDiskContent = File.Exists(e.FullPath) ? File.ReadAllText(e.FullPath) : null;
                    if (onDiskContent == recordedChange.Content)
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // File locked/being written concurrently — can't verify, assume it's our
                    // own write in progress rather than false-positive an external edit.
                    return;
                }
            }
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

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Flagging external drift for {PathKey} (ChangeType={ChangeType}) — hash gate found no recorded baseline or a mismatch, falling through to path-key/timestamp tracking.",
                e.FullPath, e.ChangeType);
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
            }

            if (solutionNeedsReload)
            {
                _logger.LogInformation(overflowed ? "Reloading entire solution (watcher overflow — change set unknown)..." : "Reloading entire solution...");
                var slnPath = _workspace.CurrentSolution.FilePath;
                if (!string.IsNullOrEmpty(slnPath))
                {
                    // Reload into a fresh MSBuildWorkspace rather than reusing _workspace: if
                    // OpenSolutionAsync throws partway through (e.g. the .sln transiently
                    // disappears mid-write during a concurrent git checkout), a reused workspace
                    // is left in a half-reloaded state whose projects resolve with broken
                    // references — every subsequent build/diagnostic call then reports mass
                    // CS0234/CS0246 errors until the next explicit LoadSolution. Building a new
                    // instance and only swapping it in on success keeps the existing _workspace
                    // (and CurrentSolution) untouched and valid on failure.
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

                    try
                    {
                        var newSolution = await newWorkspace.OpenSolutionAsync(slnPath);
                        var old = _workspace;
                        _workspace = newWorkspace;
                        CurrentSolution = newSolution;
                        old?.Dispose();
                    }
                    catch
                    {
                        newWorkspace.Dispose();
                        throw;
                    }
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
                // load/reload (WithDocumentText/AddDocument only ever update this manager's
                // in-memory CurrentSolution property, never the underlying _workspace). Fold just
                // the changed .cs files into the existing CurrentSolution instead.
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
        {
            queue.TryDequeue(out _);
        }

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
                    {
                        defaults[key] = value;
                    }
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

    public async Task<Solution> GetCurrentSolutionAsync(CancellationToken cancellationToken)
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
    /// Writes proposed file changes to disk and updates the in-memory workspace.
    /// Captures a pre-image of every file before writing so callers can populate
    /// BeforeSource on OperationItemRecords for undo support.
    /// Retries on IOExceptions (e.g. file locks).
    /// </summary>
    /// <remarks>
    /// This is the shared chokepoint for writing modified source (.cs) file content to disk —
    /// every tool/engine that persists an edit to a workspace file should route through this
    /// method (directly or via a caller that does), rather than calling File.WriteAllText*
    /// itself. Bypassing it loses external-drift refusal, pre-image capture for undo,
    /// no-op/whitespace-only-write skipping, FileSystemWatcher loop suppression, retry-on-lock,
    /// and rollback-on-partial-failure — see docs/reference-code-file-write-paths-v1.md for the
    /// full inventory of callers and the divergent paths found (and fixed) by bypassing this.
    /// There is no IWorkspaceManager interface enforcing this, so it is a convention, not a
    /// compiler-checked constraint — keep it in mind when adding a new write path.
    /// </remarks>
    public async Task<ApplyChangesResult> ApplyProposedChangesAsync(
        Dictionary<FilePath, string> changes,
        int retryCount = 3,
        bool validateChanges = false,
        bool rollbackOnPartialFailure = false,
        IProgress<ProgressNotificationValue>? progress = default,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<FilePath>? deletePaths = null)
    {
        deletePaths ??= [];

        // Session-wide fatal latch (see docs/current/ideas/external-drift-hard-blocker.md
        // proposal item 2): once tripped by a confirmed drift hit below, every subsequent
        // mutating call fails immediately and unconditionally, regardless of which file it
        // targets — checked first, ahead of every other validation in this method.
        if (_sessionHalted)
        {
            throw new SessionHaltedException(
                "Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator.");
        }
        if (deletePaths.Count > 0 && changes.Keys.Any(deletePaths.Contains))
        {
            var overlap = changes.Keys.Where(deletePaths.Contains).ToList();
            return new ApplyChangesResult(
                Success: false,
                SucceededFiles: [],
                FailedFiles: overlap.ToDictionary(f => f, _ => "Path given as both a write and a delete target."),
                Summary: $"Refused — {overlap.Count} path(s) appear in both changes and deletePaths: " +
                         $"{string.Join(", ", overlap.Select(f => Path.GetFileName(f)))}.");
        }

        // Confirmed external drift: a proposed change is always computed against
        // CurrentSolution's in-memory text; if the target file was touched on disk after that
        // and the hash-baseline gate (OnFileSystemChanged) still flagged it as real drift, the
        // proposed content is stale and writing it would silently clobber whatever changed it
        // externally. Under the single-session/no-concurrent-actors assumption this is an
        // anomaly, not something for the in-task model to reconcile — trip the session-wide
        // latch and fail terminally rather than returning a soft, retryable result.
        var drift = new HashSet<string>(GetExternalFileChanges(), StringComparer.OrdinalIgnoreCase);
        var driftedTargets = changes.Keys.Concat(deletePaths).Where(k => drift.Contains(k)).Distinct().ToList();
        if (driftedTargets.Count > 0)
        {
            _sessionHalted = true;
            throw new SessionHaltedException(
                "Session halted: external file drift was detected on a tracked file. This session cannot safely continue. Stop and report to the user/operator.");
        }

        // Pre-lock validation: compiles an in-memory fork without holding the write lock,
        // consistent with the existing external validate-then-apply pattern.
        DiagnosticReport? validationReport = null;
        if (validateChanges && CurrentSolution != null)
        {
            validationReport = await ValidationEngine.ValidateChangesAsync(CurrentSolution, changes, cancellationToken: cancellationToken);
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

        await _solutionLock.WaitAsync(cancellationToken);
        var succeeded = new List<string>();
        var failed = new Dictionary<FilePath, string>();
        bool needsFullReload = false;

        // Clear retry cache for this specific batch
        foreach (var key in changes.Keys.Concat(deletePaths))
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
            foreach (var key in changes.Keys.Concat(deletePaths))
            {
                try
                {
                    preImages[key] = await FileIoHelper.ReadAllTextIfExistsAsync(key, cancellationToken);
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

            // ── Deletes ──────────────────────────────────────────────────────
            // Handled as their own pass, before the write loop below, so a delete failure is
            // tracked the same way a write failure is (failed/succeeded/rollback), and so a
            // deleted path ends up in `succeeded` for ApplyInMemoryDocumentUpdatesAsync — which
            // already removes the tracked Document for any affected file that no longer exists
            // on disk (originally written for the old half of a rename, but generically correct
            // here too).
            foreach (var filePath in deletePaths)
            {
                _internalChanges[filePath] = (DateTime.UtcNow, string.Empty);
                try
                {
                    await FileIoHelper.DeleteAsync(filePath, cancellationToken);
                    succeeded.Add(filePath);
                    _knownFileHashes.TryRemove(filePath, out _);
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Deleted {FilePath}", filePath);
                    }
                }
                catch (Exception ex)
                {
                    failed[filePath] = ex.Message;
                    if (_logger.IsEnabled(LogLevel.Error))
                    {
                        _logger.LogError(ex, "Failed to delete {FilePath}", filePath);
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

                // Dev diagnostic only: measure how far this write's content diverges from what
                // Roslyn's own formatter would produce, purely for observability — never mutates
                // newContent or affects what gets written below. See
                // docs/reference-code-file-write-paths-v1.md ("Format-and-log diagnostic").
                if (_logger.IsEnabled(LogLevel.Debug) &&
                    string.Equals(Path.GetExtension(filePath), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var tree = CSharpSyntaxTree.ParseText(newContent, cancellationToken: cancellationToken);
                        using var formattingWorkspace = new AdhocWorkspace();
                        var formattedRoot = Formatter.Format(await tree.GetRootAsync(cancellationToken), formattingWorkspace, cancellationToken: cancellationToken);
                        var formattedText = formattedRoot.ToFullString();
                        if (formattedText != newContent)
                        {
                            var lineDelta = CountLines(formattedText) - CountLines(newContent);
                            _logger.LogDebug("Formatter divergence for {FilePath}: written content differs from Formatter.Format output (line delta {LineDelta}).", filePath, lineDelta);
                        }
                    }
                    catch
                    {
                        // Diagnostic-only — a parse/format failure here must never block the real write.
                    }
                }

                // Mark as internal change before writing to avoid FileSystemWatcher loop.
                // Content is recorded alongside the timestamp so the watcher handler can verify
                // an incoming event actually matches what we wrote, rather than suppressing by
                // path+timing alone (see OnFileSystemChanged).
                _internalChanges[filePath] = (DateTime.UtcNow, newContent);

                for (int attempt = 0; attempt <= retryCount; attempt++)
                {
                    try
                    {
                        // FileIoHelper holds the per-path lock for the duration of the write, so
                        // OnFileSystemChanged can check FilePathLock.IsLocked and skip its
                        // verification read instead of racing the open handle (see the
                        // Changed-event branch above).
                        await FileIoHelper.WriteAllTextAsync(filePath, newContent, cancellationToken);
                        success = true;
                        succeeded.Add(filePath);
                        // Update the hash baseline with what we just wrote — no extra I/O, newContent
                        // is already in memory. See _knownFileHashes's declaration-site comment.
                        _knownFileHashes[filePath] = ComputeContentHash(newContent);
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
                        _internalChanges[filePath] = (DateTime.UtcNow, original ?? "");
                        if (original is null)
                        {
                            await FileIoHelper.DeleteAsync(filePath, CancellationToken.None);
                            _knownFileHashes.TryRemove(filePath, out _);
                        }
                        else
                        {
                            await FileIoHelper.WriteAllTextAsync(filePath, original, CancellationToken.None);
                            _knownFileHashes[filePath] = ComputeContentHash(original);
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
                ? $"Partial write failure — {rolledBack.Count} already-written/deleted file(s) rolled back to keep the change atomic. {failed.Count} file(s) failed: {string.Join(", ", failed.Keys.Select(f => Path.GetFileName(f)))}."
                : $"Applied {succeeded.Count} changes successfully ({deletePaths.Count} delete(s)). {failed.Count} failures.";
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
                content = await FileIoHelper.ReadAllTextAsync(filePath, cancellationToken);
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
                var project = SolutionProjectLocator.FindContainingProject(CurrentSolution, filePath);
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
            if (_internalChanges.TryGetValue(key, out var entry) && entry.Timestamp < cutoff)
            {
                _internalChanges.TryRemove(key, out _);
            }
        }

        return needsFullReload;
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
                if (_internalChanges.TryGetValue(key, out var entry) && entry.Timestamp < cutoff)
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
    public async Task<ApplyChangesResult> RetryFailedChangesAsync(List<string>? specificFiles = null, int retryCount = 3, CancellationToken cancellationToken = default)
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
        foreach (var watcher in _outOfTreeWatchers)
        {
            watcher.Dispose();
        }
        _outOfTreeWatchers.Clear();
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
    void IManualCircuitBreaker.Reset()
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

    // This class implements two distinct breakers (IManualCircuitBreaker, IAutomaticCircuitBreaker)
    // that both redeclare ICircuitBreaker's members with their own meaning — there is no single
    // correct answer for "IsTripped()" on the bare ICircuitBreaker view, so it isn't meant to be
    // called through that type. Cast to IManualCircuitBreaker or IAutomaticCircuitBreaker instead.
    bool ICircuitBreaker.IsTripped() => throw new NotSupportedException($"Ambiguous: cast to {nameof(IManualCircuitBreaker)} or {nameof(IAutomaticCircuitBreaker)} instead of calling through the base {nameof(ICircuitBreaker)}.");
    string? ICircuitBreaker.StateMessage() => throw new NotSupportedException($"Ambiguous: cast to {nameof(IManualCircuitBreaker)} or {nameof(IAutomaticCircuitBreaker)} instead of calling through the base {nameof(ICircuitBreaker)}.");
    void ICircuitBreaker.Reset() => throw new NotSupportedException($"Ambiguous: cast to {nameof(IManualCircuitBreaker)} or {nameof(IAutomaticCircuitBreaker)} instead of calling through the base {nameof(ICircuitBreaker)}.");

    /// <summary>True when the mutating-tools breaker is currently open.</summary>
    bool IManualCircuitBreaker.IsTripped()
    {
        lock (_breakerLock)
        {
            return _breakerOpen;
        }
    }

    /// <summary>Same directive text as CheckBreaker()/GetBreakerStatus(); null when not tripped.</summary>
    string? IManualCircuitBreaker.StateMessage()
    {
        lock (_breakerLock)
        {
            if (!_breakerOpen)
            {
                return null;
            }

            double failureRatePct = _totalAttempts > 0 ? (double)_totalFailures / _totalAttempts * 100 : 0;
            return ComputeDirectiveUnlocked("halt", failureRatePct);
        }
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

    // ── Orientation breaker public API ────────────────────────────────────────

    /// <summary>Records a SearchSolutionText outcome; trips after OrientationBreakerTripThreshold consecutive zero-match calls.</summary>
    public void RecordSearchOutcome(int matchCount)
    {
        lock (_orientationBreakerLock)
        {
            if (matchCount > 0)
            {
                _consecutiveZeroMatchSearches = 0;
                return;
            }

            _consecutiveZeroMatchSearches++;
            if (_consecutiveZeroMatchSearches >= OrientationBreakerTripThreshold && !_orientationBreakerOpen)
            {
                _orientationBreakerOpen = true;
                _logger.LogWarning(
                    "Orientation breaker TRIPPED after {Count} consecutive zero-match SearchSolutionText calls.",
                    _consecutiveZeroMatchSearches);
            }
        }
    }

    /// <summary>True when the orientation breaker is currently restricting tool calls to the orienting allowlist.</summary>
    bool IAutomaticCircuitBreaker.IsTripped()
    {
        lock (_orientationBreakerLock)
        {
            return _orientationBreakerOpen;
        }
    }

    /// <summary>Directive describing the orientation breaker's tripped state; null when not tripped.</summary>
    string? IAutomaticCircuitBreaker.StateMessage()
    {
        lock (_orientationBreakerLock)
        {
            if (!_orientationBreakerOpen)
            {
                return null;
            }

            return $"Orientation breaker tripped: {_consecutiveZeroMatchSearches} consecutive SearchSolutionText " +
                   "calls returned no matches. Only ListAll, ListSolutionItems, GetFileOutline, and ReadFile are " +
                   "available until one of them succeeds. Call ListAll(kind: all) or ListSolutionItems(kind: all) " +
                   "to find what you're looking for by browsing instead of guessing.";
        }
    }

    /// <summary>Clears the orientation breaker and its zero-match streak. Called automatically by the request filter — no manual reset tool.</summary>
    void IAutomaticCircuitBreaker.Reset()
    {
        lock (_orientationBreakerLock)
        {
            _orientationBreakerOpen = false;
            _consecutiveZeroMatchSearches = 0;
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
        FilePath filePath = default;
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
        var solution = await GetCurrentSolutionAsync(cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name == handle.ProjectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null) { return null; }
        ISymbol? resolved = DocumentationCommentId.GetFirstSymbolForDeclarationId(handle.DocCommentId, compilation);
        return resolved;
    }
    public async Task<ISymbol?> ResolveByDocCommentIdAsync(string symbolId, string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await GetCurrentSolutionAsync(cancellationToken);
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project is null) { return null; }
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation is null) { return null; }
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

    private static int CountLines(string text)
    {
        int count = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }
        return count;
    }
}
