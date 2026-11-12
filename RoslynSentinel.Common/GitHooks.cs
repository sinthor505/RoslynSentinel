using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// Git hooks for commits made in process (proposal_libgit2sharp_in_process_git.md, step 5c-2; no caller yet): where git would find a hook, whether a hook that blocks or rewrites a commit exists,
/// and an unawaited launch of a <c>post-*</c> hook. libgit2 runs no hooks, so the server must reproduce exactly what <c>git commit</c> would do or decline to the CLI; every result type
/// separates "git would run nothing" (NotFound / None) from "cannot tell" (Declined / Unknown), and no method here throws for a repository, config or process problem.
/// <para>
/// Resolution equals <c>git rev-parse --path-format=absolute --git-path hooks/&lt;name&gt;</c> run in the work tree root (pinned against it in GitHooksResolutionTests): <c>core.hooksPath</c> unset is
/// <c>&lt;commondir&gt;/hooks</c> (a linked worktree's hooks live in the COMMON directory); absolute is used as is; relative is resolved against the work tree root (where git runs hooks; a linked worktree
/// against its own root); <c>~</c>, <c>~/x</c> and <c>~\x</c> expand to the home directory (HOME, then HOMEDRIVE+HOMEPATH, then USERPROFILE: git for Windows' order). The value is the effective one by git's
/// precedence (local over global over system), read by <see cref="Libgit2GitReader.ReadHookLayout"/> through the reader's commondir-aware config helper. A hook exists when a FILE of that name does
/// (git ignores the executable bit on Windows; on other platforms a non-executable hook would be skipped by git and run by this check: out of scope, the server is Windows-only).
/// </para>
/// <para>
/// Declines (the git CLI answers): any variable in the reader's <c>OverridingEnvironmentVariables</c> (GIT_DIR, GIT_CONFIG_*, XDG_CONFIG_HOME, ...) in the ambient environment; the reader's repository declines
/// (<c>extensions.worktreeConfig</c> together with a <c>config.worktree</c>, which libgit2 does not read); no repository; a bare repository (git runs hooks in the git directory); an empty
/// <c>core.hooksPath</c>, <c>~user</c>, <c>%(prefix)/</c> or a drive-relative value; <c>hook.*</c> config entries (git 2.54+ config-defined hooks, not reproduced); for a launch, a name that is not
/// <c>post-*</c> and a hook whose shebang names an interpreter other than sh, bash or dash (git runs it with that interpreter; this runner only has sh.exe).
/// </para>
/// <para>
/// Launch: <c>sh.exe &lt;hook&gt; [args]</c> (<see cref="GitShellLocator"/>), working directory the work tree root, every <c>GIT_*</c> variable removed from the child's environment, and returns as soon as the
/// process has started. git sets GIT_DIR (and for some hooks GIT_INDEX_FILE and others) for a hook; the repository's own post-commit starts <c>scripts/Start-ServerPrebuild.ps1</c>, which expects
/// plain cwd-based discovery, and the 5b spike ran with the scrubbed environment, so it is scrubbed here too; the remaining environment (HOME, ...) is the server's, and PATH is the server's behind git's
/// own tool directories (<c>mingw64\libexec\git-core</c>, <c>mingw64\bin</c>, <c>usr\bin</c> of the install that holds sh.exe), as git prepends them for a hook. Stdin is redirected and closed
/// at once (the child never inherits the MCP JSON-RPC pipe); stdout and stderr are redirected and drained by no-op async readers, so a hook that prints a lot cannot block on a full pipe.
/// The <see cref="Process"/> object is NOT disposed right after start: disposing cancels the readers and closes the pipes, and a hook still writing would get a broken pipe. It is kept in
/// <see cref="Running"/> and disposed by its Exited event (which .NET raises after the child exited AND both pipes reached EOF, so a hook's detached grandchild that inherited the pipes keeps the
/// object alive until it ends: one small object per such hook). Nothing waits for the hook and its output is not logged.
/// </para>
/// </summary>
internal static class GitHooks
{
    /// <summary>The hooks that block or rewrite a commit (<c>pre-commit</c>, <c>prepare-commit-msg</c>, <c>commit-msg</c>); <c>pre-merge-commit</c> and <c>applypatch-msg</c> do not apply to a plain commit.</summary>
    internal static readonly string[] BlockingHookNames = ["pre-commit", "prepare-commit-msg", "commit-msg"];

    private const string PostHookPrefix = "post-";

    // Interpreters a hook's shebang may name for sh.exe to be a faithful stand-in (Git for Windows' sh.exe is bash).
    private static readonly string[] ShellInterpreters = ["sh", "bash", "dash"];

    private static readonly ConcurrentDictionary<int, Process> Running = new();

    /// <summary>Where git would find hook <paramref name="hookName"/> for the repository containing <paramref name="workingDirectory"/>; see the class comment for the rules and declines.</summary>
    internal static HookPathResult ResolveHookPath(string workingDirectory, string hookName, GitHookOptions? options = null)
    {
        if (!IsPlainHookName(hookName))
        {
            return HookPathResult.Declined(hookName, $"'{hookName}' is not a plain hook name.");
        }

        var (layout, hooksDirectory, decline) = ResolveDirectory(workingDirectory, options ?? GitHookOptions.Default);
        if (decline is not null)
        {
            return HookPathResult.Declined(hookName, decline);
        }

        var file = Path.Combine(hooksDirectory!, hookName);
        return new HookPathResult(
            File.Exists(file) ? HookPathOutcome.Found : HookPathOutcome.NotFound, hookName, file, hooksDirectory, layout!.WorkTreeRoot, layout.ConfigHooksPresent);
    }

    /// <summary>
    /// Whether a hook that blocks or rewrites a commit (<see cref="BlockingHookNames"/>) exists: Found names the first, None means certainly none, Unknown (unresolvable directory, or config-defined
    /// hooks present) means a caller must decline to the CLI.
    /// </summary>
    internal static BlockingHookResult HasBlockingHook(string workingDirectory, GitHookOptions? options = null)
    {
        var (layout, hooksDirectory, decline) = ResolveDirectory(workingDirectory, options ?? GitHookOptions.Default);
        if (decline is not null)
        {
            return new BlockingHookResult(BlockingHookOutcome.Unknown, Detail: decline);
        }

        foreach (var name in BlockingHookNames)
        {
            var file = Path.Combine(hooksDirectory!, name);
            if (File.Exists(file))
            {
                return new BlockingHookResult(BlockingHookOutcome.Found, name, file);
            }
        }

        return layout!.ConfigHooksPresent
            ? new BlockingHookResult(BlockingHookOutcome.Unknown, Detail: "The config has hook.* entries (config-defined hooks), which this runner does not evaluate.")
            : new BlockingHookResult(BlockingHookOutcome.None);
    }

    /// <summary>
    /// Starts <c>post-*</c> hook <paramref name="hookName"/> unawaited (see the class comment) with <paramref name="arguments"/> (post-commit takes none; post-rewrite and post-merge take some) and returns
    /// at once. Never throws: every problem is a typed outcome, after which the caller must run the hook through the git CLI (Declined, ShNotFound, LaunchFailed) or has nothing to run (NotFound).
    /// </summary>
    internal static HookLaunchResult LaunchPostHook(string workingDirectory, string hookName, IReadOnlyList<string>? arguments = null, GitHookOptions? options = null)
    {
        options ??= GitHookOptions.Default;
        if (!hookName.StartsWith(PostHookPrefix, StringComparison.Ordinal))
        {
            return new HookLaunchResult(HookLaunchOutcome.Declined, Detail: $"'{hookName}' is not a post-* hook; only those are launched unawaited.");
        }

        var path = ResolveHookPath(workingDirectory, hookName, options);
        switch (path.Outcome)
        {
            case HookPathOutcome.Declined:
                return new HookLaunchResult(HookLaunchOutcome.Declined, Detail: path.Detail);
            case HookPathOutcome.NotFound:
                return new HookLaunchResult(HookLaunchOutcome.NotFound);
        }

        if (path.ConfigHooksPresent)
        {
            return new HookLaunchResult(HookLaunchOutcome.Declined, Detail: "The config has hook.* entries (config-defined hooks), which this runner does not evaluate.");
        }

        try
        {
            if (UnsupportedShebang(path.Path!) is { } unsupported)
            {
                return new HookLaunchResult(HookLaunchOutcome.Declined, Detail: unsupported);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HookLaunchResult(HookLaunchOutcome.LaunchFailed, Detail: $"{ex.GetType().Name}: {ex.Message}");
        }

        var shell = (options.LocateShell ?? (() => GitShellLocator.Locate()))();
        return shell.Path is null
            ? new HookLaunchResult(HookLaunchOutcome.ShNotFound, Detail: shell.Detail)
            : Start(shell.Path, path.Path!, path.WorkTreeRoot!, arguments ?? [], options.AmbientEnvironment);
    }

    /// <summary>
    /// The reason <see cref="LaunchPostHook"/> would not start hook <paramref name="hookName"/> (a hook that exists but cannot be launched faithfully: declined resolution, config-defined hooks, a non-shell
    /// shebang, no sh.exe, an unreadable file), or null when it would start it or the repository has no such hook (git runs nothing). A caller that has already committed cannot hand the hook to the CLI,
    /// so it asks BEFORE committing and declines to the CLI when this is not null. Never throws for a repository, config or file problem.
    /// </summary>
    internal static string? PostHookLaunchBlocker(string workingDirectory, string hookName, GitHookOptions? options = null)
    {
        options ??= GitHookOptions.Default;
        var path = ResolveHookPath(workingDirectory, hookName, options);
        switch (path.Outcome)
        {
            case HookPathOutcome.Declined:
                return path.Detail ?? $"the {hookName} hook could not be resolved faithfully";
            case HookPathOutcome.NotFound:
                return null;
        }

        if (path.ConfigHooksPresent)
        {
            return "The config has hook.* entries (config-defined hooks), which this runner does not evaluate.";
        }

        try
        {
            if (UnsupportedShebang(path.Path!) is { } unsupported)
            {
                return unsupported;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the {hookName} hook could not be read ({ex.GetType().Name})";
        }

        var shell = (options.LocateShell ?? (() => GitShellLocator.Locate()))();
        return shell.Path is null ? $"no sh.exe to run the {hookName} hook: {shell.Detail}" : null;
    }

    /// <summary>
    /// The hooks directory for a repository layout, by the rules of the class comment.
    /// </summary>
    /// <exception cref="NotSupportedException">A <c>core.hooksPath</c> form this does not reproduce.</exception>
    internal static string ResolveHooksDirectory(HookRepositoryLayout layout, string? homeDirectory)
    {
        var configured = layout.HooksPathConfig;
        if (configured is null)
        {
            return Path.GetFullPath(Path.Combine(layout.CommonDirectory, "hooks"));
        }

        if (configured.Length == 0)
        {
            throw new NotSupportedException("core.hooksPath is empty.");
        }

        if (configured.StartsWith("%(prefix)/", StringComparison.Ordinal))
        {
            throw new NotSupportedException("core.hooksPath starts with %(prefix)/, which depends on git's install location.");
        }

        var path = configured;
        if (path[0] == '~')
        {
            if (path.Length > 1 && path[1] is not ('/' or '\\'))
            {
                throw new NotSupportedException("core.hooksPath uses ~user, which is not expanded here.");
            }

            if (homeDirectory is null || !Path.IsPathFullyQualified(homeDirectory))
            {
                throw new NotSupportedException("core.hooksPath starts with ~ and no usable home directory is known.");
            }

            path = Path.Combine(homeDirectory, path.Length > 1 ? path[2..].TrimStart('/', '\\') : string.Empty);
        }

        if (Path.IsPathFullyQualified(path))
        {
            return Path.GetFullPath(path);
        }

        if (Path.IsPathRooted(path))
        {
            throw new NotSupportedException($"core.hooksPath '{configured}' is relative to a drive or to the current drive's root.");
        }

        return layout.WorkTreeRoot is { } root
            ? Path.GetFullPath(path, root)
            : throw new NotSupportedException("A relative core.hooksPath in a repository without a work tree.");
    }

    private static bool IsPlainHookName(string hookName) =>
        hookName.Length > 0 && hookName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static Func<string, string?> EnvironmentLookup(GitHookOptions options)
    {
        if (options.AmbientEnvironment is null)
        {
            return Environment.GetEnvironmentVariable;
        }

        var copy = new Dictionary<string, string>(options.AmbientEnvironment, StringComparer.OrdinalIgnoreCase);
        return name => copy.GetValueOrDefault(name);
    }

    // The directory every hook of the repository lives in, or the reason it cannot be determined faithfully.
    private static (HookRepositoryLayout? Layout, string? HooksDirectory, string? Decline) ResolveDirectory(string workingDirectory, GitHookOptions options)
    {
        var getEnvironmentVariable = EnvironmentLookup(options);
        if (Libgit2GitReader.FirstOverridingEnvironmentVariable(getEnvironmentVariable) is { } variable)
        {
            return (null, null, $"{variable} is set in the environment; git honours it when it looks for hooks and this runner does not.");
        }

        HookRepositoryLayout? layout;
        try
        {
            layout = (options.ReadLayout ?? Libgit2GitReader.ReadHookLayout)(workingDirectory);
        }
        catch (Exception ex) when (ex is NotSupportedException or LibGit2SharpException or IOException or UnauthorizedAccessException or ArgumentException
            or DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            return (null, null, $"The repository facts could not be read faithfully ({ex.GetType().Name}: {ex.Message}).");
        }

        if (layout is null)
        {
            return (null, null, $"'{workingDirectory}' is not inside a git repository.");
        }

        if (layout.WorkTreeRoot is null)
        {
            return (null, null, "The repository is bare; git runs its hooks in the git directory.");
        }

        try
        {
            return (layout, ResolveHooksDirectory(layout, options.HomeDirectory ?? HomeDirectory(getEnvironmentVariable)), null);
        }
        catch (NotSupportedException ex)
        {
            return (null, null, ex.Message);
        }
    }

    // git for Windows' HOME: the HOME variable, else HOMEDRIVE+HOMEPATH when that directory exists, else USERPROFILE.
    private static string? HomeDirectory(Func<string, string?> getEnvironmentVariable)
    {
        if (getEnvironmentVariable("HOME") is { Length: > 0 } home)
        {
            return home;
        }

        if (getEnvironmentVariable("HOMEDRIVE") is { Length: > 0 } drive && getEnvironmentVariable("HOMEPATH") is { Length: > 0 } homePath && Directory.Exists(drive + homePath))
        {
            return drive + homePath;
        }

        return getEnvironmentVariable("USERPROFILE") is { Length: > 0 } profile ? profile : null;
    }

    // Null when the hook has no shebang or one naming a shell; otherwise why sh.exe is not a faithful stand-in.
    private static string? UnsupportedShebang(string hookFile)
    {
        var buffer = new byte[256];
        int read;
        using (var stream = new FileStream(hookFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.Read(buffer, 0, buffer.Length);
        }

        if (read < 2 || buffer[0] != (byte)'#' || buffer[1] != (byte)'!')
        {
            return null;
        }

        var line = Encoding.ASCII.GetString(buffer, 2, read - 2);
        var end = line.IndexOfAny(['\r', '\n']);
        line = (end >= 0 ? line[..end] : line).Trim();
        var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var interpreter = tokens.Length > 0 ? BaseName(tokens[0]) : string.Empty;
        if (interpreter == "env")
        {
            interpreter = tokens.Skip(1).Where(token => !token.StartsWith('-')).Select(BaseName).FirstOrDefault() ?? string.Empty;
        }

        return ShellInterpreters.Contains(interpreter, StringComparer.OrdinalIgnoreCase)
            ? null
            : $"The hook's shebang '#!{line}' names an interpreter other than sh, bash or dash; git runs it with that interpreter and this runner only has sh.exe.";
    }

    private static string BaseName(string interpreterPath)
    {
        var name = interpreterPath[(interpreterPath.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>
    /// Removes every <c>GIT_*</c> variable from <paramref name="environment"/>, after replacing its content with <paramref name="ambient"/> when that is given (a test injecting the environment),
    /// and puts git's tool directories in front of PATH the way git does for a hook (measured 2026-11-11: <c>/mingw64/libexec/git-core:/mingw64/bin:/usr/bin:</c> first): a bare sh.exe has only
    /// its builtins and the Windows PATH, so a hook calling <c>cat</c>, <c>sed</c> or <c>env</c> would fail where git's own run succeeds.
    /// </summary>
    private static void PrepareChildEnvironment(IDictionary<string, string?> environment, IReadOnlyDictionary<string, string>? ambient, string shell)
    {
        if (ambient is not null)
        {
            environment.Clear();
            foreach (var (name, value) in ambient)
            {
                environment[name] = value;
            }
        }

        foreach (var name in environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(name);
        }

        var toolDirectories = GitToolDirectories(shell);
        if (toolDirectories.Count > 0)
        {
            environment.TryGetValue("PATH", out var path);
            environment["PATH"] = string.Join(Path.PathSeparator, toolDirectories.Append(path).Where(part => !string.IsNullOrEmpty(part)));
        }
    }

    // The install root is two levels above usr\bin\sh.exe (one above bin\sh.exe); only directories that exist are returned, in git's order.
    private static List<string> GitToolDirectories(string shell)
    {
        var shellDirectory = Path.GetDirectoryName(shell);
        var root = shellDirectory is null ? null : Path.GetDirectoryName(shellDirectory);
        if (root is not null && Path.GetFileName(root).Equals("usr", StringComparison.OrdinalIgnoreCase))
        {
            root = Path.GetDirectoryName(root);
        }

        if (root is null)
        {
            return [];
        }

        string[] candidates =
        [
            Path.Combine(root, "mingw64", "libexec", "git-core"), Path.Combine(root, "mingw64", "bin"),
            Path.Combine(root, "mingw32", "libexec", "git-core"), Path.Combine(root, "mingw32", "bin"),
            Path.Combine(root, "usr", "bin"),
        ];
        return candidates.Where(Directory.Exists).ToList();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Kept in Running and disposed by OnExited; disposing earlier would close the output pipes under a running hook.")]
    private static HookLaunchResult Start(string shell, string hookFile, string workTreeRoot, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? ambient)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(shell)
            {
                WorkingDirectory = workTreeRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(hookFile.Replace('\\', '/'));
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            PrepareChildEnvironment(startInfo.Environment, ambient, shell);
            process = Process.Start(startInfo);
            if (process is null)
            {
                return new HookLaunchResult(HookLaunchOutcome.LaunchFailed, Detail: "Process.Start returned null");
            }

            var processId = process.Id;
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.StandardInput.Close();
            Running[processId] = process;
            process.EnableRaisingEvents = true;
            process.Exited += OnExited;
            return new HookLaunchResult(HookLaunchOutcome.Launched, processId);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AbandonProcess(process);
            return new HookLaunchResult(HookLaunchOutcome.LaunchFailed, Detail: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            AbandonProcess(process);
        }
    }

    // Best effort: forget the process, kill it if a failed launch left it running, release its handles.
    private static void AbandonProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            Running.TryRemove(process.Id, out _);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or never fully started: nothing more to release here.
        }
        finally
        {
            process.Dispose();
        }
    }
}
