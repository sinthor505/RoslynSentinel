namespace RoslynSentinel.Common;

/// <summary>What <see cref="GitHooks.ResolveHookPath"/> found.</summary>
internal enum HookPathOutcome
{
    /// <summary>The hooks directory was resolved as git resolves it and the hook file exists in it.</summary>
    Found,

    /// <summary>The hooks directory was resolved and holds no file of that name: git runs nothing for the hook.</summary>
    NotFound,

    /// <summary>The directory could not be resolved faithfully (environment variable, config libgit2 does not read, no repository, a path form git expands and this does not): the git CLI decides.</summary>
    Declined,
}

/// <summary>
/// Where git would look for one hook. <see cref="HooksDirectory"/> and <see cref="Path"/> are set for Found and NotFound (Path is the file git would run, whether or not it exists);
/// <see cref="WorkTreeRoot"/> is the directory git runs hooks in. <see cref="ConfigHooksPresent"/> is true when the effective config has a <c>hook.*</c> entry (git 2.54+ config-defined hooks,
/// which this runner does not reproduce). <see cref="Detail"/> holds the reason for Declined.
/// </summary>
internal sealed record HookPathResult(
    HookPathOutcome Outcome, string HookName, string? Path = null, string? HooksDirectory = null, string? WorkTreeRoot = null, bool ConfigHooksPresent = false, string? Detail = null)
{
    internal bool Found => Outcome == HookPathOutcome.Found;

    internal static HookPathResult Declined(string hookName, string detail) => new(HookPathOutcome.Declined, hookName, Detail: detail);
}

/// <summary>How an unawaited hook launch ended.</summary>
internal enum HookLaunchOutcome
{
    /// <summary>The hook process was started (<see cref="HookLaunchResult.ProcessId"/>); it is not waited for and its output is discarded.</summary>
    Launched,

    /// <summary>The repository has no such hook: git runs nothing, so neither does the caller.</summary>
    NotFound,

    /// <summary>The hook exists but no Git for Windows <c>sh.exe</c> could be located: the git CLI must run it.</summary>
    ShNotFound,

    /// <summary>The hook cannot be reproduced faithfully (see <see cref="HookPathOutcome.Declined"/>, a non-shell shebang, config-defined hooks, a name that is not post-*): the git CLI must run it.</summary>
    Declined,

    /// <summary>The process could not be started; <see cref="HookLaunchResult.Detail"/> carries the exception text.</summary>
    LaunchFailed,
}

/// <summary>Result of <see cref="GitHooks.LaunchPostHook"/>; <see cref="ProcessId"/> is set for Launched, <see cref="Detail"/> for every other outcome except NotFound.</summary>
internal sealed record HookLaunchResult(HookLaunchOutcome Outcome, int? ProcessId = null, string? Detail = null)
{
    internal bool Launched => Outcome == HookLaunchOutcome.Launched;
}

/// <summary>Whether a hook that blocks or rewrites a commit exists.</summary>
internal enum BlockingHookOutcome
{
    /// <summary>None of pre-commit, prepare-commit-msg and commit-msg exists (and no config-defined hooks): a commit made in process runs the same hooks git would.</summary>
    None,

    /// <summary>One exists; <see cref="BlockingHookResult.HookName"/> names the first found.</summary>
    Found,

    /// <summary>The hooks directory could not be resolved faithfully: callers must treat this as "a hook may exist" and decline to the CLI.</summary>
    Unknown,
}

/// <summary>Result of <see cref="GitHooks.HasBlockingHook"/>; <see cref="HookName"/> and <see cref="HookPath"/> are set for Found, <see cref="Detail"/> for Unknown (and for Found through a config-defined hook).</summary>
internal sealed record BlockingHookResult(BlockingHookOutcome Outcome, string? HookName = null, string? HookPath = null, string? Detail = null)
{
    /// <summary>True only when it is certain no blocking hook exists; Found and Unknown both mean the caller declines.</summary>
    internal bool IsCertainlyAbsent => Outcome == BlockingHookOutcome.None;
}

/// <summary>Result of <see cref="GitShellLocator.Locate"/>: <see cref="Path"/> of <c>sh.exe</c>, or null with <see cref="Detail"/> naming what was probed.</summary>
internal sealed record ShLocateResult(string? Path, string? Detail = null)
{
    internal bool Found => Path is not null;
}

/// <summary>What the hook resolver needs from the repository, read once: see <see cref="Libgit2GitReader.ReadHookLayout"/>. <see cref="WorkTreeRoot"/> is null for a bare repository.</summary>
internal sealed record HookRepositoryLayout(string? WorkTreeRoot, string CommonDirectory, string? HooksPathConfig, bool ConfigHooksPresent);

/// <summary>
/// Seams of <see cref="GitHooks"/> and <see cref="GitShellLocator"/> for tests; null properties mean the real process, repository and git installation.
/// <see cref="AmbientEnvironment"/> replaces the environment both the GIT_* decline check and the child's environment come from (the test injects it instead of
/// changing this process's environment, which parallel tests would see).
/// </summary>
internal sealed record GitHookOptions
{
    internal static readonly GitHookOptions Default = new();

    /// <summary>The environment to behave as if this process had (names compared case-insensitively); null reads the real one.</summary>
    internal IReadOnlyDictionary<string, string>? AmbientEnvironment { get; init; }

    /// <summary>The home directory <c>~</c> expands to; null uses HOME, then HOMEDRIVE+HOMEPATH, then USERPROFILE (git for Windows' order).</summary>
    internal string? HomeDirectory { get; init; }

    /// <summary>Reads the repository facts; null uses <see cref="Libgit2GitReader.ReadHookLayout"/>. Returns null for "no repository"; a NotSupportedException is a decline.</summary>
    internal Func<string, HookRepositoryLayout?>? ReadLayout { get; init; }

    /// <summary>Locates <c>sh.exe</c>; null uses <see cref="GitShellLocator.Locate"/> for the git on PATH.</summary>
    internal Func<ShLocateResult>? LocateShell { get; init; }
}
