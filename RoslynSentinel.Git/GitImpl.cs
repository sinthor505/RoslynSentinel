using System.Text;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tools.Basic;

// ── Operation implementations ─────────────────────────────────────────────

public class GitImpl : IGitOperations
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<GitImpl> _logger;

    /// <summary>
    /// Bound on how long a single git subprocess invocation may run. Applied whenever no caller-
    /// supplied CancellationToken already carries a shorter deadline -> MCP tool calls that omit
    /// cancellationToken get CancellationToken.None here, which would otherwise let a hung git
    /// process (see docs/TODO.md's "GitTool(operation: status) hung indefinitely" entry) block forever.
    /// </summary>
    private static readonly TimeSpan GitProcessTimeout = TimeSpan.FromSeconds(30);

    // Resolved once and reused: repeatedly letting CreateProcess re-resolve the bare "git" name
    // through PATH on every call is themselves a plausible source of the intermittent 30s stalls
    // documented in docs/current/blockers/blocking_error_git_status_timeout.md (first hang is always
    // the process-spawn itself, not anything git does once running). Falls back to "git" if PATH
    // resolution fails here, preserving prior behavior.
    private static readonly string GitExecutablePath = GitProcess.ResolveGitExecutablePath();

    // Belt-and-braces on top of the UTF-8 pipe decoding GitProcess applies: tell git itself to treat commit/log
    // text as UTF-8, in case a repo-level i18n.* config otherwise changes how git encodes that text before it
    // ever reaches the pipe (docs/current/blockers/blocking_error_git_diff_mojibake_display.md).
    private static readonly string[] GitEncodingConfigArgs =
        ["-c", "i18n.logOutputEncoding=utf-8", "-c", "i18n.commitEncoding=utf-8"];

    private static readonly GitRunOptions GitTextRunOptions = new()
    {
        GitExecutable = GitExecutablePath,
        PrefixArgs = GitEncodingConfigArgs,
    };

    private static readonly GitRunOptions GitBytesRunOptions = new()
    {
        GitExecutable = GitExecutablePath,
        PrefixArgs = GitEncodingConfigArgs,
        StdOutAsBytes = true,
    };

    // The typed git seam (libgit2 first, git CLI fallback): the read operations moved onto it one at a time, and whole-file stage and unstage are its only writes
    // (RunIndexStageAsync, RunIndexUnstageAsync). Hunk staging, commit, revert, reset, abort and the rest still run the CLI through RunGitAsync.
    // Injectable (internal constructor) so a test can force one backend; production always uses the shared GitReaders.Default.
    private readonly IGitReader _gitReader;

    // The default-on in-process commit route (git config roslynsentinel.inProcessCommit=false turns it off; see CommitAsync and InProcessCommitRoute). Injectable so a test sees its own counters and can move the branch mid-commit; production uses the shared one.
    private readonly InProcessCommitRoute _commitRoute;

    public GitImpl(ISolutionProvider workspaceManager)
        : this(workspaceManager, new NullLogger<GitImpl>(), GitReaders.Default)
    {
    }

    public GitImpl(ISolutionProvider workspaceManager, ILogger<GitImpl> logger)
        : this(workspaceManager, logger, GitReaders.Default)
    {
    }

    // Test seam, null in production: StageHunksAsync calls it with the git root after it has read the diff and written the patch and
    // before it re-reads the file's index entry, so a test can stage the same file as "another session" at exactly that moment.
    private readonly Action<string>? _afterHunkDiffLoaded;

    internal GitImpl(ISolutionProvider workspaceManager, ILogger<GitImpl> logger, IGitReader gitReader, InProcessCommitRoute? commitRoute = null,
        Action<string>? afterHunkDiffLoaded = null)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _gitReader = gitReader;
        _commitRoute = commitRoute ?? InProcessCommitRoute.Shared;
        _afterHunkDiffLoaded = afterHunkDiffLoaded;
    }

    /// <summary>
    /// Refusal for a tag, stash or worktree call whose action is missing or not valid for that
    /// operation. Runs nothing, so the message says nothing was changed.
    /// </summary>
    internal static T ActionRefused<T>(string operation, GitAction? action, string validList) where T : GitResult, new()
    {
        var got = action is null ? "none" : action.Value.ToString();
        return new T
        {
            IsError = true,
            ErrorKind = GitErrorCodes.ActionRequired,
            Hints = [new GitHint(GitHintKey.PassActionExample, new GitHintFacts { Operation = operation })],
            Error = $"operation={operation} needs action to be one of: {validList} (got {got}). Nothing was changed."
        };
    }

    /// <summary>Appends one more hint to the hints a failure helper already set (for example an Inline token hint beside the ErrorDetail key) and returns the same result.</summary>
    private static T WithHint<T>(T result, GitHint hint) where T : GitResult
    {
        result.Hints = [.. result.Hints ?? Array.Empty<GitHint>(), hint];
        return result;
    }

    /// <summary>
    /// Catch-all for an unexpected exception in a new GitTool operation. Logs the full exception and
    /// returns a fixed message, so no exception text or internal path reaches the caller.
    /// </summary>
    internal T UnexpectedFailure<T>(Exception ex, string what) where T : GitResult, new()
    {
        _logger.LogError(ex, "Git {What} failed unexpectedly", what);
        return new T
        {
            IsError = true,
            Error = $"Git {what} failed unexpectedly; the server log has details. Nothing further was changed."
        };
    }

    /// <summary>Tag failure whose next-step text is a hint key rendered at the tool boundary; ErrorDetail stays null.</summary>
    private static GitTagResult TagFailure(string action, string kind, string error, GitHintKey key, GitHintFacts? facts = null) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        Hints = [new GitHint(key, facts)]
    };

    /// <summary>
    /// Lists, creates or deletes a local tag. Never moves or overwrites an existing tag, never signs,
    /// never pushes, and deletes exactly one named tag (no patterns, no -f).
    /// </summary>
    public async Task<GitTagResult> TagAsync(
        string gitRoot, GitAction? action, string? tagName, string? refName, string? message, int count, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.list:
                    return await TagListAsync(gitRoot, count, cancellationToken);
                case GitAction.create:
                    return await TagCreateAsync(gitRoot, tagName, refName, message, cancellationToken);
                case GitAction.delete:
                    return await TagDeleteAsync(gitRoot, tagName, cancellationToken);
                default:
                    return ActionRefused<GitTagResult>("tag", action, "list, create, delete");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitTagResult>(ex, "tag");
        }
    }

    /// <summary>
    /// Reads one git config value (action=get), or sets or unsets one writable key in the repository's local
    /// config (action=set or unset). Keys that can hold credentials, and URL values that carry userinfo, are
    /// refused without returning the value.
    /// </summary>
    public async Task<GitConfigResult> ConfigAsync(
        string gitRoot, GitAction? action, string? key, string? value, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.get:
                    break;
                case GitAction.set:
                case GitAction.unset:
                    // ConfigWriteAsync and the result's Action property use the plain words "set" and "unset".
                    return await ConfigWriteAsync(gitRoot, action == GitAction.set ? "set" : "unset", key, value, cancellationToken);
                default:
                    return ConfigFailure(action?.ToString() ?? "", GitErrorCodes.ActionRequired,
                        $"operation=config needs action to be one of: get, set, unset (got {action?.ToString() ?? "none"}). Nothing was changed.",
                        GitHintKey.ConfigActionUsage);
            }

            var keyError = ValidateConfigKey(key);
            if (keyError is not null)
            {
                return ConfigFailureText("get", GitErrorCodes.InvalidName, keyError,
                    "Valid forms by example: user.name, branch.main.remote.");
            }

            var configKey = key!;
            if (IsDeniedConfigKey(configKey))
            {
                return ConfigFailureText("get", GitErrorCodes.Refused,
                    $"Config key '{configKey}' can hold credentials, so its value is not returned. Nothing was read.",
                    "Other keys can be read, for example key=user.name.");
            }

            // The typed seam reads in-process where that reproduces git config and runs the git CLI otherwise. It blocks, so it runs
            // off the caller's thread; the token only stops a call that has not started.
            var configValue = await Task.Run(() => _gitReader.GetConfigValue(gitRoot, configKey, GitProcessTimeout), cancellationToken);

            if (configValue is not null
                && string.Equals(configKey.Split('.')[0], "url", StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(configValue, @"://[^/\s@]+@"))
            {
                return ConfigFailureText("get", GitErrorCodes.Refused,
                    $"Config key '{configKey}' has a value that carries credentials (userinfo), so it is not returned.",
                    "Other keys can be read, for example key=user.name.");
            }

            return new GitConfigResult
            {
                Action = "get",
                Key = configKey,
                Value = configValue,
                Note = configValue is null
                    ? $"Key '{configKey}' is not set in any config scope visible from this repository."
                    : null,
                Changed = false
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitConfigResult>(ex, "config");
        }
    }

    /// <summary>
    /// Sets or unsets one writable key in the repository's local config. Reads the effective value before and
    /// after the write, so the result reports what git reads back rather than the requested value.
    /// </summary>
    private async Task<GitConfigResult> ConfigWriteAsync(
        string gitRoot, string action, string? key, string? value, CancellationToken cancellationToken)
    {
        var keyError = ValidateConfigKey(key);
        if (keyError is not null)
            return ConfigFailureText(action, GitErrorCodes.InvalidName, keyError.Replace("Nothing was read.", "Nothing was changed."),
                "Valid forms by example: user.name, branch.main.remote.");

        var writeKey = key!;
        if (!IsWritableConfigKey(writeKey))
            return ConfigFailureText(action, GitErrorCodes.Refused,
                $"Config key '{writeKey}' is not one this operation writes. Nothing was changed.",
                "Writable keys: roslynsentinel.*, user.name, user.email, core.autocrlf, core.eol, core.safecrlf, core.longpaths, pull.rebase, push.default, init.defaultBranch, branch.<name>.remote, branch.<name>.merge, branch.<name>.rebase. The value of any other key can be read with action=get.");

        if (action == "set")
        {
            if (value is null)
                return ConfigFailure(action, GitErrorCodes.ActionRequired, "action=set needs configValue (got none). Nothing was changed.",
                    GitHintKey.ConfigValueRequired);
            if (value.Contains('\0'))
                return ConfigFailureText(action, GitErrorCodes.InvalidName,
                    "The config value contains a NUL character, which git cannot store. Nothing was changed.",
                    "Pass the value without the NUL character.");
        }

        var before = await Task.Run(() => _gitReader.GetConfigValue(gitRoot, writeKey, GitProcessTimeout), cancellationToken);

        if (action == "unset")
        {
            if (before is null)
                return new GitConfigResult
                {
                    Action = action,
                    Key = writeKey,
                    Value = null,
                    PreviousValue = null,
                    Changed = false,
                    Note = $"Key '{writeKey}' is not set. Nothing was changed.",
                };

            var unset = await RunGitAsync(gitRoot, ["config", "--local", "--unset", "--", writeKey], cancellationToken);
            // Exit 5: the key is not in this repository's local file; the value comes from the global or system file.
            if (unset.ExitCode == 5)
                return new GitConfigResult
                {
                    Action = action,
                    Key = writeKey,
                    Value = before,
                    PreviousValue = before,
                    Changed = false,
                    Note = $"Key '{writeKey}' is set in another scope (global or system), not in this repository's local config. Nothing was changed.",
                };
            if (unset.ExitCode != 0)
                return ConfigFailureText(action, GitErrorCodes.Fallback,
                    $"git config exited with code {unset.ExitCode}. Nothing further was changed.",
                    "The key and its current value can be read with action=get.");
        }
        else
        {
            // The -- guards a value that starts with '-'.
            var set = await RunGitAsync(gitRoot, ["config", "--local", "--", writeKey, value!], cancellationToken);
            if (set.ExitCode != 0)
                return ConfigFailureText(action, GitErrorCodes.Fallback,
                    $"git config exited with code {set.ExitCode}. Nothing further was changed.",
                    "The key and its current value can be read with action=get.");
        }

        var after = await Task.Run(() => _gitReader.GetConfigValue(gitRoot, writeKey, GitProcessTimeout), cancellationToken);
        string? note = null;
        if (action == "set" && !string.Equals(after, value, StringComparison.Ordinal))
            note = $"git reads back '{after}' for key '{writeKey}'.";
        else if (action == "unset" && after is not null)
            note = $"Key '{writeKey}' is still set in another scope (global or system); the local entry was removed.";

        return new GitConfigResult
        {
            Action = action,
            Key = writeKey,
            Value = after,
            PreviousValue = before,
            Changed = !string.Equals(before, after, StringComparison.Ordinal),
            Note = note,
        };
    }

    /// <summary>True when the validated key is one the config write operation may set or unset.</summary>
    private static bool IsWritableConfigKey(string key)
    {
        var first = key.IndexOf('.');
        var last = key.LastIndexOf('.');
        var section = key[..first];
        var subsection = last > first ? key[(first + 1)..last] : "";
        var name = key[(last + 1)..];
        if (string.Equals(section, "roslynsentinel", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(section, "branch", StringComparison.OrdinalIgnoreCase))
            return subsection.Length > 0
                && (string.Equals(name, "remote", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "merge", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "rebase", StringComparison.OrdinalIgnoreCase));
        if (subsection.Length > 0)
            return false;
        string[] writable = ["user.name", "user.email", "core.autocrlf", "core.eol", "core.safecrlf", "core.longpaths", "pull.rebase", "push.default", "init.defaultbranch"];
        return writable.Contains($"{section}.{name}", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Config failure whose next-step text names no tool, so it stays plain engine text in ErrorDetail.</summary>
    private static GitConfigResult ConfigFailureText(string action, string kind, string error, string detail) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    /// <summary>Config failure whose next-step text is a hint key rendered at the tool boundary; ErrorDetail stays null.</summary>
    private static GitConfigResult ConfigFailure(string action, string kind, string error, GitHintKey key, GitHintFacts? facts = null) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        Hints = [new GitHint(key, facts)]
    };

    /// <summary>Returns the fact-only error for a key that is not section.name or section.subsection.name, or null when the shape is valid.</summary>
    private static string? ValidateConfigKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "Config key is empty. Nothing was read.";
        var malformed = $"Config key '{key}' is not of the form section.name or section.subsection.name. Nothing was read.";
        foreach (var c in key)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return malformed;
        }
        if (key[0] == '-')
            return malformed;
        var first = key.IndexOf('.');
        var last = key.LastIndexOf('.');
        if (first < 0 || last - first == 1)
            return malformed;
        var section = key[..first];
        var name = key[(last + 1)..];
        if (!System.Text.RegularExpressions.Regex.IsMatch(section, @"^[A-Za-z0-9-]+\z")
            || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z][A-Za-z0-9-]*\z"))
            return malformed;
        return null;
    }

    /// <summary>True when the validated key names a credential-carrying setting whose value must not be returned.</summary>
    private static bool IsDeniedConfigKey(string key)
    {
        var section = key[..key.IndexOf('.')];
        var name = key[(key.LastIndexOf('.') + 1)..];
        return string.Equals(section, "credential", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "password", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "token", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(section, "http", StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, "extraheader", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<GitTagResult> TagListAsync(string gitRoot, int count, CancellationToken cancellationToken)
    {
        // Annotated tags have objecttype=tag and a peeled *objectname; lightweight tags have an empty
        // *objectname and their objectname is the commit itself.
        var raw = await RunGitAsync(gitRoot,
            ["for-each-ref", "--sort=-creatordate",
             "--format=%(refname:short)%09%(objecttype)%09%(*objectname:short)%09%(objectname:short)%09%(contents:subject)",
             "refs/tags"], cancellationToken);
        if (raw.ExitCode != 0)
            return new GitTagResult { Action = "list", IsError = true, Error = $"git tag list failed: {CleanGitStderr(raw.Stderr)}" };

        var all = new List<GitTagEntry>();
        foreach (var rawLine in raw.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t', 5);
            if (parts.Length < 4) continue;
            all.Add(new GitTagEntry
            {
                Name = parts[0],
                Kind = parts[1] == "tag" ? "annotated" : "lightweight",
                TargetHash = parts[2].Length > 0 ? parts[2] : parts[3],
                Subject = parts.Length > 4 ? parts[4] : "",
            });
        }

        var take = Math.Clamp(count, 1, 100);
        return new GitTagResult
        {
            Action = "list",
            IsError = false,
            Tags = all.Take(take).ToList(),
            TotalCount = all.Count,
            IsTruncated = all.Count > take,
        };
    }

    private async Task<GitTagResult> TagCreateAsync(
        string gitRoot, string? tagName, string? refName, string? message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return TagFailure("create", GitErrorCodes.RefRequired,
                "Git tag create needs tagName. Nothing was changed.", GitHintKey.RefRequiredTag);
        var name = tagName.Trim();

        // A leading '-' passes check-ref-format but would be read as an option by git tag.
        var formatOk = !name.StartsWith('-');
        if (formatOk)
        {
            var fmt = await RunGitAsync(gitRoot, ["check-ref-format", $"refs/tags/{name}"], cancellationToken);
            formatOk = fmt.ExitCode == 0;
        }
        if (!formatOk)
            return TagFailure("create", GitErrorCodes.InvalidName,
                $"'{name}' is not a valid tag name (no spaces, '..', '~', '^', ':', leading '-' or trailing '.lock'). Nothing was changed.",
                GitHintKey.InvalidName);

        var existing = await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"refs/tags/{name}"], cancellationToken);
        if (existing.ExitCode == 0)
        {
            var existingShort = await RunGitAsync(gitRoot, ["rev-parse", "--short", "--verify", "--quiet", $"refs/tags/{name}^{{commit}}"], cancellationToken);
            var existingHash = existingShort.ExitCode == 0 ? existingShort.Stdout.Trim() : existing.Stdout.Trim();
            return WithHint(TagFailure("create", GitErrorCodes.AlreadyExists,
                $"Tag '{name}' already exists (points at {existingHash}). This tool never overwrites or moves a tag. Use a new name, or delete it first ({GitHintTokens.Inline(GitHintKey.TagDeleteCall)}) and recreate. Nothing was changed.",
                GitHintKey.AlreadyExists), new GitHint(GitHintKey.TagDeleteCall, new GitHintFacts { Name = name }));
        }

        var target = string.IsNullOrWhiteSpace(refName) ? "HEAD" : refName.Trim();
        // A ref starting with '-' could be read as an option, so it is treated as unresolvable.
        var resolved = target.StartsWith('-')
            ? new GitRawResult { ExitCode = 1 }
            : await RunGitAsync(gitRoot, ["rev-parse", "--short", "--verify", "--quiet", $"{target}^{{commit}}"], cancellationToken);
        var targetHash = resolved.Stdout.Trim();
        if (resolved.ExitCode != 0 || targetHash.Length == 0)
            return TagFailure("create", GitErrorCodes.TargetNotFound,
                $"Ref '{target}' does not resolve to a commit. Nothing was changed.",
                GitHintKey.TargetNotFound);

        var annotated = !string.IsNullOrWhiteSpace(message);
        string[] createArgs = annotated
            ? ["tag", "-a", "-m", message!, name, targetHash]
            : ["tag", name, targetHash];
        var created = await RunGitAsync(gitRoot, createArgs, cancellationToken);
        if (created.ExitCode != 0)
            return new GitTagResult
            {
                Action = "create",
                IsError = true,
                Error = $"git tag failed: {CleanGitStderr(created.Stderr)}. Nothing was changed."
            };

        return new GitTagResult
        {
            Action = "create",
            IsError = false,
            Created = name,
            TargetHash = targetHash,
            Annotated = annotated,
        };
    }

    private async Task<GitTagResult> TagDeleteAsync(string gitRoot, string? tagName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            return TagFailure("delete", GitErrorCodes.RefRequired,
                "Git tag delete needs tagName. Nothing was changed.", GitHintKey.RefRequiredTag);
        var name = tagName.Trim();

        var existing = await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"refs/tags/{name}"], cancellationToken);
        if (existing.ExitCode != 0)
        {
            var names = await RunGitAsync(gitRoot, ["for-each-ref", "--format=%(refname:short)", "refs/tags"], cancellationToken);
            var known = names.ExitCode == 0
                ? names.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => n.TrimEnd('\r')).Where(n => n.Length > 0).ToList()
                : [];
            var listed = known.Count == 0
                ? "The repository has no tags."
                : $"Existing tags: {string.Join(", ", known.Take(10))}{(known.Count > 10 ? $" and {known.Count - 10} more" : "")}.";
            return TagFailure("delete", GitErrorCodes.TargetNotFound,
                $"Tag '{name}' does not exist. {listed} Nothing was changed.",
                GitHintKey.TargetNotFound);
        }

        var kindRaw = await RunGitAsync(gitRoot, ["for-each-ref", "--format=%(objecttype)", $"refs/tags/{name}"], cancellationToken);
        var wasAnnotated = kindRaw.ExitCode == 0 && kindRaw.Stdout.Trim() == "tag";
        var prevRaw = await RunGitAsync(gitRoot, ["rev-parse", "--short", "--verify", "--quiet", $"refs/tags/{name}^{{commit}}"], cancellationToken);
        var previous = prevRaw.ExitCode == 0 ? prevRaw.Stdout.Trim() : "";

        // Exactly one literal name: never -f and never a pattern.
        var deleted = await RunGitAsync(gitRoot, ["tag", "-d", name], cancellationToken);
        if (deleted.ExitCode != 0)
            return new GitTagResult
            {
                Action = "delete",
                IsError = true,
                Error = $"git tag -d failed: {CleanGitStderr(deleted.Stderr)}. Nothing was changed."
            };

        var note = $"Deleted locally only. To undo: {GitHintTokens.Inline(GitHintKey.TagCreateUndoCall)}.";
        if (wasAnnotated)
            note += " The tag was annotated; its message and tagger are not restored by that call (pass message to make the recreated tag annotated).";
        return new GitTagResult
        {
            Action = "delete",
            IsError = false,
            Deleted = name,
            PreviousTargetHash = previous,
            Note = note,
            Hints = [new GitHint(GitHintKey.TagCreateUndoCall, new GitHintFacts { Name = name, Hash = previous })],
        };
    }

    /// <summary>
    /// Lists, pushes, applies or pops a stash entry. Never drops, clears, uses --index, --keep-index or
    /// --all, and never stashes patches. A conflicted apply/pop is rolled back (see StashRollbackConflictAsync).
    /// </summary>
    public async Task<GitStashResult> StashAsync(
        string gitRoot, GitAction? action, string? message, bool includeUntracked, string? paths, int? stashIndex, int count, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.list:
                    return await StashListAsync(gitRoot, count, cancellationToken);
                case GitAction.push:
                    return await StashPushAsync(gitRoot, message, includeUntracked, paths, cancellationToken);
                case GitAction.apply:
                    return await StashRestoreAsync(gitRoot, false, stashIndex, cancellationToken);
                case GitAction.pop:
                    return await StashRestoreAsync(gitRoot, true, stashIndex, cancellationToken);
                default:
                    return ActionRefused<GitStashResult>("stash", action, "list, push, apply, pop");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStashResult>(ex, "stash");
        }
    }

    /// <summary>Stash failure whose next-step text is a hint key rendered at the tool boundary; ErrorDetail stays null.</summary>
    private static GitStashResult StashFailure(string action, string kind, string error, GitHintKey key, GitHintFacts? facts = null) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        Hints = [new GitHint(key, facts)]
    };

    private async Task<GitStashResult> StashRestoreAsync(
    string gitRoot, bool pop, int? stashIndex, CancellationToken cancellationToken)
    {
        var action = pop ? "pop" : "apply";
        if (stashIndex is null)
            return StashFailure(action, GitErrorCodes.RefRequired,
                $"Git stash {action} needs stashIndex (0 = newest). Nothing was changed.", GitHintKey.RefRequiredStash);
        var index = stashIndex.Value;

        var entriesBefore = await StashCountAsync(gitRoot, cancellationToken);
        if (entriesBefore < 0)
            return new GitStashResult { Action = action, IsError = true, Error = "Could not read the stash list. Nothing was changed." };
        if (index < 0 || index >= entriesBefore)
            return StashFailure(action, GitErrorCodes.TargetNotFound,
                $"stash@{{{index}}} does not exist: the stash has {entriesBefore} {(entriesBefore == 1 ? "entry" : "entries")}. Nothing was changed.",
                GitHintKey.TargetNotFound);

        // Precondition: no tracked changes, staged or unstaged. The D-18 rollback below relies on it.
        var dirty = await TrackedChangePathsAsync(gitRoot, cancellationToken);
        if (dirty is null)
            return new GitStashResult { Action = action, IsError = true, Error = "Could not read the working tree status. Nothing was changed." };
        if (dirty.Count > 0)
            return StashFailure(action, GitErrorCodes.WorkingTreeDirty,
                $"Cannot {action} stash@{{{index}}}: the tracked tree has uncommitted changes in: {FormatPathSample(dirty)}. Nothing was changed.",
                GitHintKey.WorkingTreeDirty);

        // Deliberately no --index: staged state is not restored.
        var raw = await RunGitAsync(gitRoot, ["stash", action, $"stash@{{{index}}}"], cancellationToken);
        if (raw.ExitCode == 0)
        {
            var entriesAfter = await StashCountAsync(gitRoot, cancellationToken);
            var expected = pop ? entriesBefore - 1 : entriesBefore;
            var note = "Staged state is not restored (--index is not used).";
            note += entriesAfter == expected
                ? (pop ? $" stash@{{{index}}} was applied and dropped; {entriesAfter} stash {(entriesAfter == 1 ? "entry" : "entries")} left."
                       : $" stash@{{{index}}} was applied and kept; {entriesAfter} stash {(entriesAfter == 1 ? "entry" : "entries")} total.")
                : $" Expected {expected} stash entries afterwards but found {entriesAfter}; check {GitHintTokens.Inline(GitHintKey.StashListCall)}.";
            return new GitStashResult
            {
                Action = action,
                IsError = false,
                AppliedIndex = index,
                Note = note,
                Hints = entriesAfter == expected
                    ? new[] { new GitHint(GitHintKey.StashDiskNote) }
                    : new[] { new GitHint(GitHintKey.StashDiskNote), new GitHint(GitHintKey.StashListCall) },
                Status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken),
            };
        }

        var unmerged = await UnmergedPathsAsync(gitRoot, cancellationToken);
        if (unmerged is { Count: > 0 })
            return await StashRollbackConflictAsync(gitRoot, action, index, entriesBefore, unmerged, cancellationToken);

        // Non-zero exit without unmerged paths (for example an untracked file in the way). git may
        // still have applied part of the stash, so claim "Nothing was changed" only after verifying.
        var trackedNow = await TrackedChangePathsAsync(gitRoot, cancellationToken);
        var entriesNow = await StashCountAsync(gitRoot, cancellationToken);
        var stderr = CleanGitStderr(raw.Stderr);
        GitStashResult failure;
        if (unmerged is not null && trackedNow is { Count: 0 } && entriesNow == entriesBefore)
            failure = new GitStashResult
            {
                Action = action,
                IsError = true,
                Error = $"git stash {action} failed: {stderr}. Nothing was changed."
            };
        else
            failure = new GitStashResult
            {
                Action = action,
                IsError = true,
                Error = $"git stash {action} failed: {stderr}. The tree could not be verified clean afterwards" +
                        (trackedNow is { Count: > 0 } ? $" (tracked changes in: {FormatPathSample(trackedNow)})" : "") +
                        $"; stash entries: {entriesNow} (was {entriesBefore}). The stash entry was kept. Run {GitHintTokens.Inline(GitHintKey.GitStatusCall)} before editing.",
                Hints = [new GitHint(GitHintKey.GitStatusCall)]
            };
        return await AppendInProgressAsync(failure, gitRoot, cancellationToken);
    }

    /// <summary>
    /// D-18: rolls back a conflicted stash apply/pop and verifies the result structurally. Reports a
    /// clean rollback only when the tracked tree is clean, no path is unmerged and the stash entry
    /// count is unchanged; otherwise says exactly which check failed.
    /// </summary>
    private async Task<GitStashResult> StashRollbackConflictAsync(
        string gitRoot, string action, int stashIndex, int entriesBefore, List<string> conflicted, CancellationToken cancellationToken)
    {
        // Safe only because the precondition in the caller guarantees a clean tracked tree: reset --merge
        // removes the conflicted stash application and nothing else. This is the only reset form the
        // tool runs internally; it is not exposed as an operation.
        var reset = await RunGitAsync(gitRoot, ["reset", "--merge"], cancellationToken);

        var failed = new List<string>();
        if (reset.ExitCode != 0)
            failed.Add($"git reset --merge exited {reset.ExitCode}");
        var trackedAfter = await TrackedChangePathsAsync(gitRoot, cancellationToken);
        if (trackedAfter is null)
            failed.Add("git status could not be read");
        else if (trackedAfter.Count > 0)
            failed.Add($"tracked changes remain in: {FormatPathSample(trackedAfter)}");
        var unmergedAfter = await UnmergedPathsAsync(gitRoot, cancellationToken);
        if (unmergedAfter is null)
            failed.Add("unmerged paths could not be read");
        else if (unmergedAfter.Count > 0)
            failed.Add($"unmerged paths remain: {FormatPathSample(unmergedAfter)}");
        var entriesAfter = await StashCountAsync(gitRoot, cancellationToken);
        if (entriesAfter != entriesBefore)
            failed.Add($"stash entry count is {entriesAfter}, expected {entriesBefore}");

        if (failed.Count == 0)
            return StashFailure(action, GitErrorCodes.StashConflict,
                $"Applying stash@{{{stashIndex}}} conflicts with the current tree in: {FormatPathSample(conflicted)}. " +
                $"The attempt was rolled back (git reset --merge): the tracked tree is clean again and stash@{{{stashIndex}}} was kept. Nothing was changed.",
                GitHintKey.StashConflict);

        return WithHint(StashFailure(action, GitErrorCodes.StashConflict,
            $"Applying stash@{{{stashIndex}}} conflicts, and the rollback could not be verified ({string.Join("; ", failed)}). " +
            $"The stash entry was kept. Run {GitHintTokens.Inline(GitHintKey.GitStatusCall)} before editing.",
            GitHintKey.StashRollbackUnverified), new GitHint(GitHintKey.GitStatusCall));
    }

    private async Task<GitStashResult> StashPushAsync(
    string gitRoot, string? message, bool includeUntracked, string? paths, CancellationToken cancellationToken)
    {
        var pathList = new List<string>();
        if (!string.IsNullOrWhiteSpace(paths))
        {
            var parsed = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
            if (pathsError != null)
                return new GitStashResult { Action = "push", IsError = true, Error = $"{pathsError}. Nothing was changed." };
            foreach (var p in parsed!)
            {
                var pathError = ValidateRepoRoot(gitRoot, p);
                if (pathError != null)
                    return StashFailure("push", GitErrorCodes.PathNotFound, $"{pathError}. Nothing was changed.", GitHintKey.PathNotFound);
                pathList.Add(p);
            }
        }

        // Structural precondition. git itself exits 0 with "No local changes to save", so its exit
        // code cannot be trusted to say whether an entry was created.
        string[] statusArgs = pathList.Count > 0
            ? ["--literal-pathspecs", "-c", "core.quotePath=false", "status", "--porcelain",
               includeUntracked ? "--untracked-files=normal" : "--untracked-files=no", "--", .. pathList]
            : ["-c", "core.quotePath=false", "status", "--porcelain",
               includeUntracked ? "--untracked-files=normal" : "--untracked-files=no"];
        var statusRaw = await RunGitAsync(gitRoot, statusArgs, cancellationToken);
        if (statusRaw.ExitCode != 0)
            return await AppendInProgressAsync(new GitStashResult
            {
                Action = "push",
                IsError = true,
                Error = $"git status failed: {CleanGitStderr(statusRaw.Stderr)}. Nothing was changed."
            }, gitRoot, cancellationToken);
        if (statusRaw.Stdout.Trim().Length == 0)
            return StashFailure("push", GitErrorCodes.NoChanges,
                $"Nothing to stash: no modified tracked files{(includeUntracked ? ", and no untracked files" : "")}{(pathList.Count > 0 ? " among the named paths" : "")}. Nothing was changed.",
                GitHintKey.NoChanges);

        var before = (await RunGitAsync(gitRoot, ["rev-parse", "-q", "--verify", "refs/stash"], cancellationToken)).Stdout.Trim();

        // --literal-pathspecs only when the caller named paths: git (2.55, verified) runs stash -u's internal
        // clean of the untracked files with a magic pathspec of its own, and with the flag set and no caller
        // paths that clean removes nothing, leaving the files both in the stash and in the working tree.
        var pushArgs = pathList.Count > 0
            ? new List<string> { "--literal-pathspecs", "stash", "push" }
            : new List<string> { "stash", "push" };
        if (includeUntracked) pushArgs.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) { pushArgs.Add("-m"); pushArgs.Add(message); }
        if (pathList.Count > 0) { pushArgs.Add("--"); pushArgs.AddRange(pathList); }
        var pushed = await RunGitAsync(gitRoot, [.. pushArgs], cancellationToken);
        if (pushed.ExitCode != 0)
        {
            if (pushed.Stderr.Contains("did not match any file(s)"))
                return StashFailure("push", GitErrorCodes.PathNotFound,
                    $"{CleanGitStderr(pushed.Stderr)}. Nothing was changed.", GitHintKey.PathNotFound);
            return await AppendInProgressAsync(new GitStashResult
            {
                Action = "push",
                IsError = true,
                Error = $"git stash push failed: {CleanGitStderr(pushed.Stderr)}. Nothing was changed."
            }, gitRoot, cancellationToken);
        }

        var after = (await RunGitAsync(gitRoot, ["rev-parse", "-q", "--verify", "refs/stash"], cancellationToken)).Stdout.Trim();
        if (after.Length == 0 || after == before)
            return StashFailure("push", GitErrorCodes.NoChanges,
                "Nothing was stashed: git reported success but no stash entry was created. Nothing was changed.",
                GitHintKey.NoChanges);

        var top = await StashListAsync(gitRoot, 1, cancellationToken);
        return new GitStashResult
        {
            Action = "push",
            IsError = false,
            Created = true,
            Message = top.Entries.FirstOrDefault()?.Message ?? message,
            Note = "Staged changes come back unstaged on pop/apply (--index is not used).",
            Hints = [new GitHint(GitHintKey.StashDiskNote)],
            Status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken),
        };
    }

    private async Task<GitStashResult> StashListAsync(string gitRoot, int count, CancellationToken cancellationToken)
    {
        // The typed seam lists in-process (libgit2) where that reproduces git stash list and runs the git CLI otherwise. It blocks, so it runs
        // off the caller's thread; the token only stops a call that has not started.
        var listed = await Task.Run(() => _gitReader.ListStashes(gitRoot, GitProcessTimeout), cancellationToken);
        if (listed is not null)
            return BuildStashListResult(listed, count);

        // git could not answer: run it once more for its own error text.
        // %gd = stash@{N}, %gs = reflog subject ("On <branch>: <label>" or "WIP on <branch>: <hash> <subject>").
        var raw = await RunGitAsync(gitRoot,
            ["stash", "list", "--format=%gd%x09%h%x09%cI%x09%gs"], cancellationToken);
        if (raw.ExitCode != 0)
            return new GitStashResult { Action = "list", IsError = true, Error = $"git stash list failed: {CleanGitStderr(raw.Stderr)}" };

        return BuildStashListResult(CliGitReader.ParseStashList(raw.Stdout), count);
    }

    private static GitStashResult BuildStashListResult(IReadOnlyList<StashRef> stashes, int count)
    {
        var all = new List<GitStashEntry>();
        foreach (var stash in stashes)
        {
            string? branch = null;
            var message = stash.Subject;
            var subject = System.Text.RegularExpressions.Regex.Match(message, @"^(?:WIP on|On) ([^:]+): (.*)$");
            if (subject.Success)
            {
                branch = subject.Groups[1].Value;
                message = subject.Groups[2].Value;
            }
            all.Add(new GitStashEntry
            {
                Index = stash.Index,
                Hash = stash.ShortSha,
                Date = stash.CommitterDate,
                Branch = branch,
                Message = message,
            });
        }

        var take = Math.Clamp(count, 1, 100);
        return new GitStashResult
        {
            Action = "list",
            IsError = false,
            Entries = all.Take(take).ToList(),
            TotalCount = all.Count,
            IsTruncated = all.Count > take,
        };
    }

    /// <summary>Up to 10 paths, comma-separated, with a "(+N more)" suffix when there are more.</summary>
    private static string FormatPathSample(IReadOnlyList<string> paths)
    {
        var shown = string.Join(", ", paths.Take(10));
        return paths.Count > 10 ? $"{shown} (+{paths.Count - 10} more)" : shown;
    }

    /// <summary>Paths with unresolved merge conflicts (git diff --diff-filter=U), or null when git could not be asked.</summary>
    private async Task<List<string>?> UnmergedPathsAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot,
            ["-c", "core.quotePath=false", "diff", "--name-only", "--diff-filter=U"], cancellationToken);
        if (raw.ExitCode != 0)
            return null;
        return raw.Stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
    }

    /// <summary>Paths with staged or unstaged changes to tracked files (untracked files ignored), or null when
    /// git could not be asked. Empty means the tracked tree is clean.</summary>
    private async Task<List<string>?> TrackedChangePathsAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot,
            ["-c", "core.quotePath=false", "status", "--porcelain", "--untracked-files=no"], cancellationToken);
        if (raw.ExitCode != 0)
            return null;
        return raw.Stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 3)
            .Select(l => l[3..])
            .ToList();
    }

    /// <summary>Number of stash entries, or -1 when git could not be asked.</summary>
    private async Task<int> StashCountAsync(string gitRoot, CancellationToken cancellationToken)
    {
        // The seam counts in-process (libgit2) with the git CLI as fallback; null means git could not be asked.
        var count = await Task.Run(() => _gitReader.CountStashes(gitRoot, GitProcessTimeout), cancellationToken);
        return count ?? -1;
    }

    /// <summary>
    /// Lists, adds or removes a linked worktree. Never prunes, locks, unlocks or moves, never detaches HEAD,
    /// and removes a dirty worktree only behind the explicit discardUncommittedChanges flag (D-20).
    /// </summary>
    public async Task<GitWorktreeResult> WorktreeAsync(
        string gitRoot, GitAction? action, string? worktreePath, string? branchName, bool createBranch, string? startPoint,
        bool discardUncommittedChanges, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case GitAction.list:
                    var (entries, listError) = await ListWorktreesAsync(gitRoot, cancellationToken);
                    return entries is null
                        ? new GitWorktreeResult { Action = "list", IsError = true, Error = $"git worktree list failed: {listError}" }
                        : new GitWorktreeResult { Action = "list", IsError = false, Worktrees = entries };
                case GitAction.add:
                    return await WorktreeAddAsync(gitRoot, worktreePath, branchName, createBranch, startPoint, cancellationToken);
                case GitAction.remove:
                    return await WorktreeRemoveAsync(gitRoot, worktreePath, discardUncommittedChanges, cancellationToken);
                default:
                    return ActionRefused<GitWorktreeResult>("worktree", action, "list, add, remove");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitWorktreeResult>(ex, "worktree");
        }
    }

    /// <summary>Worktree failure whose next-step text names no tool, so it stays plain engine text in ErrorDetail.</summary>
    private static GitWorktreeResult WorktreeFailureText(string action, string kind, string error, string detail) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        ErrorDetail = detail
    };

    /// <summary>Worktree failure whose next-step text is a hint key rendered at the tool boundary; ErrorDetail stays null.</summary>
    private static GitWorktreeResult WorktreeFailure(string action, string kind, string error, GitHintKey key, GitHintFacts? facts = null) => new()
    {
        Action = action,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        Hints = [new GitHint(key, facts)]
    };

    private static class WorktreeNativePaths
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
        internal static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);
    }

    /// <summary>
    /// Expands Windows 8.3 short names (for example C:\Users\ADMINI~1) in the part of the path that exists.
    /// git reports worktree paths in long form, so a path built from a short-form temp directory would
    /// otherwise never compare equal to git's. Returns the input unchanged off Windows or on any failure.
    /// </summary>
    private static string ExpandShortNames(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return fullPath;
        try
        {
            var existing = fullPath;
            var tail = "";
            while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent))
                    return fullPath;
                tail = Path.Combine(Path.GetFileName(existing), tail);
                existing = parent;
            }
            if (string.IsNullOrEmpty(existing))
                return fullPath;
            var buffer = new StringBuilder(1024);
            var length = WorktreeNativePaths.GetLongPathName(existing, buffer, (uint)buffer.Capacity);
            if (length == 0 || length >= buffer.Capacity)
                return fullPath;
            var expanded = buffer.ToString();
            return tail.Length == 0 ? expanded : Path.Combine(expanded, tail);
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>
    /// Canonical form used to compare worktree paths: full path, platform separator, 8.3 short names
    /// expanded, no trailing separator. Compare results with OrdinalIgnoreCase.
    /// </summary>
    private static string NormalizeWorktreePath(string path)
    {
        var full = Path.GetFullPath(path.Trim()).Replace('/', Path.DirectorySeparatorChar);
        full = ExpandShortNames(full);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    /// <summary>
    /// Parses git worktree list --porcelain: blocks separated by a blank line, each starting with
    /// "worktree &lt;path&gt;" followed by HEAD, branch | detached, and optional locked / prunable lines.
    /// The first block is the main worktree. Parsed structurally; git's localizable messages are never matched.
    /// Returns (null, stderr) when git could not be asked.
    /// </summary>
    private async Task<(List<GitWorktreeEntry>? Entries, string? Error)> ListWorktreesAsync(
        string gitRoot, CancellationToken cancellationToken)
    {
        var raw = await RunGitAsync(gitRoot, ["worktree", "list", "--porcelain"], cancellationToken);
        if (raw.ExitCode != 0)
            return (null, CleanGitStderr(raw.Stderr));

        var current = NormalizeWorktreePath(gitRoot);
        var entries = new List<GitWorktreeEntry>();
        GitWorktreeEntry? block = null;
        foreach (var rawLine in raw.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                block = null;
                continue;
            }
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                var path = NormalizeWorktreePath(line["worktree ".Length..]);
                block = new GitWorktreeEntry
                {
                    Path = path,
                    IsMain = entries.Count == 0,
                    IsCurrent = path.Equals(current, StringComparison.OrdinalIgnoreCase),
                };
                entries.Add(block);
            }
            else if (block is null)
            {
                continue;
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                var head = line["HEAD ".Length..];
                block.Head = head.Length > 8 ? head[..8] : head;
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var branch = line["branch ".Length..];
                block.Branch = branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch["refs/heads/".Length..] : branch;
            }
            else if (line == "detached")
            {
                block.IsDetached = true;
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                block.IsLocked = true;
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                block.IsPrunable = true;
            }
        }
        return (entries, null);
    }

    private async Task<GitWorktreeResult> WorktreeAddAsync(
    string gitRoot, string? worktreePath, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath) || string.IsNullOrWhiteSpace(branchName))
        {
            var missing = string.IsNullOrWhiteSpace(worktreePath) && string.IsNullOrWhiteSpace(branchName) ? "worktreePath and branchName"
                : string.IsNullOrWhiteSpace(worktreePath) ? "worktreePath" : "branchName";
            return WorktreeFailure("add", GitErrorCodes.RefRequired,
                $"Git worktree add needs {missing}. Nothing was changed.", GitHintKey.RefRequiredWorktree);
        }
        var path = worktreePath.Trim();
        var branch = branchName.Trim();

        // (a) absolute path. Fully qualified, so "\foo" or "C:foo" cannot resolve against the server's cwd.
        if (!Path.IsPathFullyQualified(path))
            return WorktreeFailureText("add", GitErrorCodes.Refused,
                $"worktreePath must be an absolute path (got '{path}'). Nothing was changed.",
                "Pass a full path outside the repository, for example a sibling directory of the repository root.");

        var repoRoot = NormalizeWorktreePath(gitRoot);
        var target = NormalizeWorktreePath(path);

        // (b) outside the repository root. ValidateRepoRoot returns null when the path is inside (or equal to) the root.
        if (ValidateRepoRoot(repoRoot, target) is null)
        {
            var parent = Path.GetDirectoryName(repoRoot);
            var example = parent is null
                ? ""
                : $" e.g. '{Path.Combine(parent, $"{Path.GetFileName(repoRoot)}-{branch.Replace('/', '-')}")}'";
            return WorktreeFailure("add", GitErrorCodes.Refused,
                "A worktree inside the repository would be compiled into the loaded solution and shows up in this repo's diffs (the Worktree/ trap). " +
                $"Use a sibling directory,{(example.Length > 0 ? example : " outside the repository root")}. Nothing was changed.",
                GitHintKey.Refused);
        }

        // startPoint only means something when a branch is being created; dropping it silently would
        // leave the caller believing the worktree was based on it (same rule as CheckoutAsync).
        if (!createBranch && !string.IsNullOrWhiteSpace(startPoint))
            return WorktreeFailure("add", GitErrorCodes.Refused,
                $"startPoint '{startPoint}' was supplied without createBranch=true, so it would be ignored. " +
                "Pass createBranch=true to create the branch from it, or omit startPoint to add a worktree for the existing branch. Nothing was changed.",
                GitHintKey.Refused);

        // A leading '-' would be read by git as an option.
        var nameOk = !branch.StartsWith('-');
        if (nameOk && createBranch)
        {
            var fmt = await RunGitAsync(gitRoot, ["check-ref-format", $"refs/heads/{branch}"], cancellationToken);
            nameOk = fmt.ExitCode == 0;
        }
        if (!nameOk)
            return WorktreeFailure("add", GitErrorCodes.InvalidName,
                $"'{branch}' is not a valid branch name (no spaces, '..', '~', '^', ':', leading '-' or trailing '.lock'). Nothing was changed.",
                GitHintKey.InvalidName);

        // (c) the target path must be free: absent, or an existing empty directory.
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            return WorktreeFailure("add", GitErrorCodes.AlreadyExists,
                $"Path '{path}' already exists and is not empty; a worktree cannot be added there. Nothing was changed.",
                GitHintKey.AlreadyExists);

        var exists = (await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken)).ExitCode == 0;
        if (!createBranch && !exists)
            return WorktreeFailure("add", GitErrorCodes.TargetNotFound,
                $"Branch '{branch}' does not exist. Nothing was changed.",
                GitHintKey.WorktreeBranchNotFoundOptions);
        if (createBranch && exists)
            return WorktreeFailure("add", GitErrorCodes.AlreadyExists,
                $"Branch '{branch}' already exists, and this tool does not silently reuse it for a new worktree. " +
                "Pass createBranch=false to check the existing branch out in the new worktree, or choose a new branch name. Nothing was changed.",
                GitHintKey.AlreadyExists);

        string? baseRef = null;
        if (createBranch)
        {
            baseRef = string.IsNullOrWhiteSpace(startPoint) ? "HEAD" : startPoint.Trim();
            // A start point starting with '-' could be read as an option, so it is treated as unresolvable.
            var resolved = baseRef.StartsWith('-')
                ? new GitRawResult { ExitCode = 1 }
                : await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}"], cancellationToken);
            if (resolved.ExitCode != 0)
                return WorktreeFailure("add", GitErrorCodes.TargetNotFound,
                    $"startPoint '{baseRef}' does not resolve to a commit. Nothing was changed.", GitHintKey.TargetNotFound);
        }

        var (entries, listError) = await ListWorktreesAsync(gitRoot, cancellationToken);
        if (entries is null)
            return new GitWorktreeResult { Action = "add", IsError = true, Error = $"git worktree list failed: {listError}. Nothing was changed." };

        // (f) git allows a branch to be checked out in one worktree only.
        if (!createBranch)
        {
            var holder = entries.FirstOrDefault(e => string.Equals(e.Branch, branch, StringComparison.Ordinal));
            if (holder is not null)
                return WorktreeFailure("add", GitErrorCodes.AlreadyExists,
                    $"Branch '{branch}' is already checked out in the worktree at '{holder.Path}'; a branch can be checked out in only one worktree. Nothing was changed.",
                    GitHintKey.AlreadyExists);
        }

        string[] args = createBranch
            ? ["-c", "core.longpaths=true", "worktree", "add", "-b", branch, target, baseRef!]
            : ["-c", "core.longpaths=true", "worktree", "add", target, branch];
        var added = await RunGitAsync(gitRoot, args, cancellationToken);
        if (added.ExitCode != 0)
        {
            // Claim "Nothing was changed" only when git did not register the path.
            var (after, _) = await ListWorktreesAsync(gitRoot, cancellationToken);
            var registered = after is null || after.Any(e => string.Equals(e.Path, target, StringComparison.OrdinalIgnoreCase));
            return new GitWorktreeResult
            {
                Action = "add",
                IsError = true,
                Error = $"git worktree add failed: {CleanGitStderr(added.Stderr)}. " +
                        (registered ? $"The worktree may have been partly created; check {GitHintTokens.Inline(GitHintKey.WorktreeListCall)}." : "Nothing was changed."),
                Hints = registered ? new[] { new GitHint(GitHintKey.WorktreeListCall) } : null
            };
        }

        return new GitWorktreeResult
        {
            Action = "add",
            IsError = false,
            Added = path,
            Branch = branch,
            Hints = [new GitHint(GitHintKey.WorktreeAddNote, new GitHintFacts { Path = path })],
        };
    }

    /// <summary>Up to 10 paths, comma-separated, then "and N more" when there are more.</summary>
    private static string FormatWorktreePaths(IReadOnlyList<string> paths)
    {
        var shown = string.Join(", ", paths.Take(10));
        return paths.Count > 10 ? $"{shown} and {paths.Count - 10} more" : shown;
    }

    /// <summary>
    /// D-20: removes a linked worktree. The main, current and locked worktrees are always refused. A
    /// worktree with modified or untracked files is removed (git worktree remove --force) only when
    /// discardUncommittedChanges is true; otherwise the changed paths are listed and nothing is removed.
    /// --force is never used on a clean worktree.
    /// </summary>
    private async Task<GitWorktreeResult> WorktreeRemoveAsync(
        string gitRoot, string? worktreePath, bool discardUncommittedChanges, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            return WorktreeFailure("remove", GitErrorCodes.RefRequired,
                "Git worktree remove needs worktreePath. Nothing was removed.", GitHintKey.RefRequiredWorktree);
        var path = worktreePath.Trim();

        var (entries, listError) = await ListWorktreesAsync(gitRoot, cancellationToken);
        if (entries is null)
            return new GitWorktreeResult { Action = "remove", IsError = true, Error = $"git worktree list failed: {listError}. Nothing was removed." };

        // A relative path is never resolved against the server's working directory.
        var entry = Path.IsPathFullyQualified(path)
            ? entries.FirstOrDefault(e => string.Equals(e.Path, NormalizeWorktreePath(path), StringComparison.OrdinalIgnoreCase))
            : null;
        if (entry is null)
            return WorktreeFailure("remove", GitErrorCodes.TargetNotFound,
                $"No worktree at '{path}'. Known worktrees: {FormatWorktreePaths(entries.Select(e => e.Path).ToList())}. Nothing was removed.",
                GitHintKey.TargetNotFound);

        if (entry.IsMain)
            return WorktreeFailure("remove", GitErrorCodes.Refused,
                $"Worktree '{path}' is the main worktree; this tool never removes it. Nothing was removed.",
                GitHintKey.WorktreeOnlyLinkedRemovable);
        if (entry.IsCurrent)
            return WorktreeFailure("remove", GitErrorCodes.Refused,
                $"Worktree '{path}' is the worktree this Git tool is operating in (the loaded solution's repository); removing it would delete the directory the server is working in. Nothing was removed.",
                GitHintKey.WorktreeRemoveCurrentSwitch);
        if (entry.IsLocked)
            return WorktreeFailureText("remove", GitErrorCodes.Refused,
                $"Worktree '{path}' is locked. Nothing was removed.",
                "unlock it first with git worktree unlock in a shell; this tool does not unlock");

        // Dirty check, run inside that worktree. A missing directory (prunable entry) has nothing to lose.
        var changed = new List<string>();
        if (Directory.Exists(entry.Path))
        {
            var status = await RunGitAsync(entry.Path,
                ["-c", "core.quotePath=false", "status", "--porcelain", "--untracked-files=all"], cancellationToken);
            if (status.ExitCode != 0)
                return new GitWorktreeResult
                {
                    Action = "remove",
                    IsError = true,
                    Error = $"Could not read the status of worktree '{path}': {CleanGitStderr(status.Stderr)}. Nothing was removed."
                };
            changed = status.Stdout.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 3)
                .Select(l => l[3..])
                .ToList();
        }

        if (changed.Count > 0 && !discardUncommittedChanges)
            return WithHint(WorktreeFailureText("remove", GitErrorCodes.WorkingTreeDirty,
                $"Worktree '{path}' has uncommitted changes ({changed.Count} path(s): {FormatWorktreePaths(changed)}). Nothing was removed. " +
                $"To delete it anyway and permanently lose those changes, call {GitHintTokens.Inline(GitHintKey.WorktreeRemoveDiscardCall)}. " +
                "To keep them, commit or stash inside that worktree first.",
                "discardUncommittedChanges: true deletes modified and untracked files in that worktree; they cannot be recovered. Committed work and the branch are not affected."),
                new GitHint(GitHintKey.WorktreeRemoveDiscardCall, new GitHintFacts { Path = path }));

        // --force only for a worktree verified dirty above; a clean worktree never gets it.
        var force = changed.Count > 0;
        string[] removeArgs = force
            ? ["-c", "core.longpaths=true", "worktree", "remove", "--force", entry.Path]
            : ["-c", "core.longpaths=true", "worktree", "remove", entry.Path];
        var removed = await RunGitAsync(gitRoot, removeArgs, cancellationToken);
        if (removed.ExitCode != 0)
        {
            var (after, _) = await ListWorktreesAsync(gitRoot, cancellationToken);
            var stillThere = after is not null && after.Any(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                             && Directory.Exists(entry.Path);
            return new GitWorktreeResult
            {
                Action = "remove",
                IsError = true,
                Error = $"git worktree remove failed: {CleanGitStderr(removed.Stderr)}. " +
                        (stillThere ? "The worktree was not removed." : $"Check its state with {GitHintTokens.Inline(GitHintKey.WorktreeListCall)}."),
                Hints = stillThere ? null : new[] { new GitHint(GitHintKey.WorktreeListCall) }
            };
        }

        var kept = entry.Branch is null
            ? "The worktree was on a detached HEAD, so there is no branch to delete."
            : $"The branch '{entry.Branch}' was kept; delete it with {GitHintTokens.Inline(GitHintKey.GitRemoteBranchDeleteCall)} once merged.";
        return new GitWorktreeResult
        {
            Action = "remove",
            IsError = false,
            Removed = path,
            Branch = entry.Branch,
            DiscardedPaths = force ? changed : null,
            Note = force
                ? $"Removed '{path}' and discarded uncommitted changes in {changed.Count} path(s). They are not recoverable. {kept}"
                : kept,
            Hints = entry.Branch is null ? null : [new GitHint(GitHintKey.GitRemoteBranchDeleteCall)],
        };
    }

    /// <summary>
    /// Finds the git repository root and reports why it could not, as a <see cref="GitRootFailure"/>; <paramref name="error"/>
    /// carries only the facts, and the tool appends the advice (<c>GitHintText.RenderRootFailure</c>). Resolution order: explicit
    /// repoPath, then the loaded solution's directory (authoritative when loaded), then the server's base directory, then its
    /// working directory.
    /// GitTool operations read the working tree, not the Roslyn
    /// compilation, so this deliberately does NOT require a loaded solution: it falls back to
    /// walking up from the server's own base/working directory when nothing is loaded. When a
    /// solution IS loaded, its directory is tried FIRST -> a loaded solution reflects the
    /// worktree/repo the caller most recently pointed the server at via LoadSolutionTool, whereas the // hint-ok: prose names the LoadSolutionTool class, not a callable tool form
    /// server's base directory and process working directory are fixed at process launch and
    /// never change afterward. Trying those first meant that once LoadSolutionTool pointed the // hint-ok: prose names the LoadSolutionTool class, not a callable tool form
    /// workspace at a git worktree, status/diff/commit still silently resolved against whatever
    /// repo the server binary happened to be launched from/inside (almost always the primary
    /// checkout) instead of the worktree -> a plausible, well-formed, wrong answer with no error.
    /// See docs/current/blockers/blocking_error_git_tool_commit_reports_clean_tree_worktree.md.
    /// </summary>
    public string? TryGetGitRoot(out string error, out GitRootFailure failure, string? repoPath = null)
    {
        failure = GitRootFailure.None;

        // 0. An explicit repoPath override: takes priority over everything else.
        if (!string.IsNullOrWhiteSpace(repoPath))
        {
            var fromRepoPath = FindRepositoryRoot(repoPath);
            if (fromRepoPath is not null)
            {
                error = "";
                return fromRepoPath;
            }

            error = $"repoPath '{repoPath}' does not resolve to a git repository or worktree (no .git entry found walking up from it). Double-check the path - it must be a real directory that is itself inside a git working tree.";
            failure = GitRootFailure.RepoPathNotARepository;
            return null;
        }

        // 1. The loaded solution's directory, if there is one (the caller's most recent explicit signal of which repo they mean).
        var solutionRoot = _workspaceManager.GetSolutionRoot();
        if (solutionRoot is not null)
        {
            var fromSolution = FindRepositoryRoot(solutionRoot);
            if (fromSolution is not null)
            {
                error = "";
                return fromSolution;
            }

            // A loaded solution is authoritative: do not fall through to the base or working directory.
            error = $"The loaded solution's directory ('{solutionRoot}') is not inside a git repository, so there is nothing for Git to operate on. Git does not fall back to the server's own repository when a solution is loaded. Pass repoPath (status/log/diff/show) to inspect another repository."; // hint-ok: names the Git tool's own repoPath parameter, not a call to another tool
            failure = GitRootFailure.SolutionDirectoryNotInRepository;
            return null;
        }

        // 2. Walk up from the server's base directory (the server runs from a bin/ folder inside the repo it operates on).
        var fromBaseDirectory = FindRepositoryRoot(AppContext.BaseDirectory);
        if (fromBaseDirectory is not null)
        {
            error = "";
            return fromBaseDirectory;
        }

        // 3. Walk up from the current working directory (binaries deployed outside the repo but launched from inside it).
        var fromCurrentDirectory = FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (fromCurrentDirectory is not null)
        {
            error = "";
            return fromCurrentDirectory;
        }

        error = "No git repository found. Searched upward from the server's base directory and working directory without finding a .git entry, and no solution is loaded to search from either.";
        failure = GitRootFailure.NoRepositoryFound;
        return null;
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a <c>.git</c> entry. Accepts a
    /// <c>.git</c> <b>file</b> as well as a directory: linked worktrees and submodules use a
    /// <c>.git</c> file holding a gitdir pointer, and a directory-only check silently walks past
    /// them into the parent repository -> which is exactly how a PlanStepRunner harness clone would
    /// end up having git operations aimed at the wrong repo.
    /// </summary>
    private static string? FindRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        string? dir;
        try
        {
            dir = Path.GetFullPath(startDirectory);
        }
        catch (Exception)
        {
            // A malformed or over-long path is not something the caller can act on -> treat it as
            // "nothing found here" so the next candidate directory still gets tried.
            return null;
        }

        while (!string.IsNullOrEmpty(dir))
        {
            var gitEntry = Path.Combine(dir, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                return dir;

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }

        return null;
    }

    // Runs git through the shared GitProcess runner and keeps GitImpl's old contract: a git that does not exit
    // within GitProcessTimeout is killed and surfaces as TimeoutException (a caller-supplied token still cancels
    // earlier and surfaces as OperationCanceledException). The timeout covers MCP calls that omit cancellationToken,
    // which would otherwise wait on a hung git process forever.
    private async Task<GitRunResult> RunGitCoreAsync(
        string gitRoot, string[] args, GitRunOptions options, CancellationToken cancellationToken)
    {
        var run = await GitProcess.RunAsync(gitRoot, args, GitProcessTimeout, cancellationToken, options);
        return EnsureCompleted(run, args);
    }

    // The one place a git run that did not complete becomes an exception (see RunGitCoreAsync); shared with the index writes that go through the typed seam.
    private GitRunResult EnsureCompleted(GitRunResult run, string[] args)
    {
        switch (run.Outcome)
        {
            case GitRunOutcome.Completed:
                return run;
            case GitRunOutcome.Failed:
                throw new InvalidOperationException($"Git could not be run: {run.FailureDetail}");
            default:
                // Full detail (args, timeout value, TODO cross-reference) is server-side only, via the logger
                // call below -> every caller's catch block folds the thrown exception's Message straight into
                // the ResultError text returned to the agent, so that Message must stay a plain, self-contained
                // statement with no internal paths/docs the agent can't open.
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(
                        "git {Args} did not finish within {TimeoutSeconds}s ({Outcome}: {Detail}) and was killed. Not a normal git " +
                        "failure - see docs/TODO.md's 'Git(operation: status) hung indefinitely' entry.",
                        string.Join(' ', args), GitProcessTimeout.TotalSeconds, run.Outcome, run.FailureDetail); // hint-ok: logger message text, not a callable tool response
                }

                throw new TimeoutException($"Git operation timed out after {GitProcessTimeout.TotalSeconds:0}s and was cancelled.");
        }
    }

    // Reproduces what Process.OutputDataReceived + StringBuilder.AppendLine produced: text split at CR, LF or CRLF,
    // every line (empty ones included) re-terminated with Environment.NewLine, a missing final newline added, and
    // empty text staying empty. Removing this changes the text of every Git tool result (CRLF becomes whatever git
    // wrote), so it stays until the callers are audited (see the TODO in RunGitAsync).
    private static string RejoinLines(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var joined = new StringBuilder(text.Length + 16);
        var lineStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                joined.Append(text, lineStart, i - lineStart).Append(Environment.NewLine);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                i++;
                lineStart = i;
            }
            else
            {
                i++;
            }
        }

        if (lineStart < text.Length)
        {
            joined.Append(text, lineStart, text.Length - lineStart).Append(Environment.NewLine);
        }

        return joined.ToString();
    }

    private async Task<GitRawResult> RunGitAsync(
        string gitRoot, string[] args, CancellationToken cancellationToken)
    {
        var run = await RunGitCoreAsync(gitRoot, args, GitTextRunOptions, cancellationToken);
        // TODO the line re-join below reproduces the old BeginOutputReadLine/AppendLine output (every line ends in
        // Environment.NewLine, a missing final newline is added); drop it and return run.StdOut/StdErr verbatim once
        // the ~69 callers and their tests are audited for CRLF-vs-LF and trailing-newline assumptions.
        return new GitRawResult
        {
            ExitCode = run.ExitCode,
            Stdout = RejoinLines(run.StdOut),
            Stderr = RejoinLines(run.StdErr)
        };
    }

    // Whole-file stage goes through the typed seam: libgit2 in-process for ordinary text files, the same git add command as before for everything libgit2
    // declines (see Libgit2GitReader.IndexWrites.cs). The seam hands back a GitRunResult, and a run that did not complete maps to the same exceptions as
    // RunGitAsync, so the result and every error text stay what they were. The call blocks, so it runs off the caller's thread; the token cancels a running CLI process.
    private async Task<GitRawResult> RunIndexStageAsync(
        string gitRoot, IndexStageMode mode, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var run = await Task.Run(() => _gitReader.StageWholeFiles(gitRoot, mode, paths, GitProcessTimeout, cancellationToken), cancellationToken);
        return ToRawResult(EnsureCompleted(run, ["add", $"({mode})", .. paths]));
    }

    // The unstage twin of RunIndexStageAsync: git reset -- paths (a plain git reset for no paths), libgit2 in-process where it can reproduce that.
    private async Task<GitRawResult> RunIndexUnstageAsync(
        string gitRoot, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var run = await Task.Run(() => _gitReader.UnstagePaths(gitRoot, paths, GitProcessTimeout, cancellationToken), cancellationToken);
        return ToRawResult(EnsureCompleted(run, ["reset", .. paths]));
    }

    // The same line re-join RunGitAsync applies, so CleanGitStderr sees the text it always saw.
    private static GitRawResult ToRawResult(GitRunResult run) => new()
    {
        ExitCode = run.ExitCode,
        Stdout = RejoinLines(run.StdOut),
        Stderr = RejoinLines(run.StdErr)
    };

    // Byte-exact sibling of RunGitAsync, for output whose line endings must survive untouched (a diff that will be
    // re-applied as a patch). Stdout comes back raw from GitProcess (no line re-join, no decoding); callers decode
    // with Encoding.Latin1 so one char is one byte. Stderr is line-rejoined exactly as RunGitAsync does.
    private async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunGitBytesAsync(
        string gitRoot, string[] args, CancellationToken cancellationToken)
    {
        var run = await RunGitCoreAsync(gitRoot, args, GitBytesRunOptions, cancellationToken);
        return (run.ExitCode, run.StdOutBytes, RejoinLines(run.StdErr));
    }

    private static FileDiffLoad NoUnstagedChangesLoad(string path) => new(path, null, GitErrorCodes.NoChanges,
    $"No unstaged changes in '{path}'. Already staged? {GitHintTokens.Inline(GitHintKey.GitDiffStagedAnyPathsCall)}. Nothing was changed.",
    null, [new GitHint(GitHintKey.NoChanges), new GitHint(GitHintKey.GitDiffStagedAnyPathsCall)]);

    /// <summary>Outcome of loading one file's unstaged diff: Diff is set on success; otherwise ErrorKind, Error and
    /// ErrorDetail describe the classified failure. Shared by operation=hunks and the hunk stage.</summary>
    private sealed record FileDiffLoad(
        string Path, GitFileDiff? Diff, string? ErrorKind = null, string? Error = null, string? ErrorDetail = null,
        IReadOnlyList<GitHint>? Hints = null);

    // Runs the zero-context unstaged diff of one already-classified tracked path through the byte-exact runner,
    // parses it and applies the kind refusals. Success carries the parsed diff; every failure is classified.
    private async Task<FileDiffLoad> LoadDiffForPathAsync(string gitRoot, string path, CancellationToken cancellationToken)
    {
        // Byte-exact runner: the fingerprint and any patch later built from this text must see the file's real line endings.
        var raw = await RunGitBytesAsync(gitRoot,
            ["-c", "core.quotePath=false", "--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "--no-renames", "-U0", "--", path],
            cancellationToken);
        if (raw.ExitCode != 0)
        {
            return new FileDiffLoad(path, null, GitErrorCodes.Fallback,
                $"git diff failed: {CleanGitStderr(raw.Stderr)}. Nothing was changed.", null, [new GitHint(GitHintKey.GitDiffFailedRetry)]);
        }
        if (raw.Stdout.Length == 0)
            return NoUnstagedChangesLoad(path);

        var diff = GitHunkParser.Parse(Encoding.Latin1.GetString(raw.Stdout));
        // Kind checks come before IsRenameOrModeOnly: that flag is also true for a binary or new-file diff with no @@ lines.
        string? unsupported = diff switch
        {
            { IsBinary: true } => "is a binary file",
            { IsNewFile: true } => "is a new file (added with intent-to-add)",
            { IsDeletedFile: true } => "is deleted in the working tree",
            { IsRenameOrModeOnly: true } => "has only a mode or rename change",
            _ => null
        };
        if (unsupported is not null)
        {
            return new FileDiffLoad(path, null, GitErrorCodes.HunkUnsupported,
                $"'{path}' {unsupported}, so it has no text hunks to choose from. Nothing was changed.", null, [new GitHint(GitHintKey.HunkUnsupported)]);
        }
        if (diff.Hunks.Count == 0)
            return NoUnstagedChangesLoad(path);

        return new FileDiffLoad(path, diff);
    }

    // Shared by operation=hunks and the hunk stage: validates that paths names exactly one tracked, existing,
    // non-deleted file, then loads and parses its unstaged diff. Never throws for a classified refusal.
    private async Task<FileDiffLoad> LoadFileDiffAsync(string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        var list = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var listError);
        if (list is null)
            return new FileDiffLoad("", null, GitErrorCodes.HunkSelection, $"{listError} Nothing was changed.", null, [new GitHint(GitHintKey.HunkSelection)]);
        if (list.Length == 0)
        {
            return new FileDiffLoad("", null, GitErrorCodes.RefRequired,
                "Hunk selection needs paths naming exactly one file. Nothing was changed.",
                "Pass paths: '<one repo-relative path>'");
        }
        if (list.Length > 1)
        {
            return new FileDiffLoad("", null, GitErrorCodes.HunkSelection,
                $"hunks works on one file at a time; got {list.Length}. Nothing was changed.", null, [new GitHint(GitHintKey.HunkSelection)]);
        }

        var pathError = ValidateRepoRoot(gitRoot, list[0]);
        if (pathError is not null)
            return new FileDiffLoad(list[0], null, GitErrorCodes.PathNotFound, $"{pathError}. Nothing was changed.", null, [new GitHint(GitHintKey.PathNotFound)]);

        var classified = (await ClassifyPathsAsync(gitRoot, [list[0]], cancellationToken))[0];
        var path = classified.Path;
        switch (classified.Classification)
        {
            case PathClassification.Untracked:
                var isDirectory = Directory.Exists(Path.Combine(gitRoot, path));
                return new FileDiffLoad(path, null, GitErrorCodes.HunkUnsupported,
                    isDirectory
                        ? $"'{path}' is a directory; hunks works on one tracked file at a time. Nothing was changed."
                        : $"'{path}' is untracked: untracked files have no hunks to choose from; stage the whole file with {GitHintTokens.Inline(GitHintKey.GitStageAnyPathsCall)}. Nothing was changed.",
                    null, isDirectory
                        ? [new GitHint(GitHintKey.HunkUnsupported)]
                        : [new GitHint(GitHintKey.HunkUnsupported), new GitHint(GitHintKey.GitStageAnyPathsCall)]);
            case PathClassification.Missing:
                return new FileDiffLoad(path, null, GitErrorCodes.PathNotFound,
                    $"'{path}' is not tracked and does not exist on disk. Nothing was changed.", null, [new GitHint(GitHintKey.PathNotFound)]);
            case PathClassification.HeadOnly:
                return new FileDiffLoad(path, null, GitErrorCodes.HunkUnsupported,
                    $"'{path}' is deleted (tracked in HEAD, gone from the index), so it has no hunks to choose from. Nothing was changed.",
                    null, [new GitHint(GitHintKey.HunkUnsupported)]);
        }

        return await LoadDiffForPathAsync(gitRoot, path, cancellationToken);
    }

    private static GitHunkEntry ToHunkEntry(GitHunk h) => new()
    {
        Index = h.Index,
        OldStart = h.OldStart,
        OldCount = h.OldCount,
        NewStart = h.NewStart,
        NewCount = h.NewCount,
        Header = h.Header,
        Added = h.NewCount,
        Removed = h.OldCount,
        Preview = GitHunkParser.PreviewLines(h, 6, 120)
    };

    private static GitHunksResult BuildHunksResult(string path, GitFileDiff diff, int count)
    {
        var limit = Math.Clamp(count, 1, 100);
        return new GitHunksResult
        {
            Path = path,
            Fingerprint = diff.Fingerprint,
            TotalHunks = diff.Hunks.Count,
            IsTruncated = diff.Hunks.Count > limit,
            Hunks = diff.Hunks.Take(limit).Select(ToHunkEntry).ToList(),
            Note = $"Pass hunkIds with this hunkFingerprint to {GitHintTokens.Inline(GitHintKey.GitStagePathCall)}, or lineRange. " +
                   "Hunks are zero-context: adjacent edits are separate hunks.",
            Hints = [new GitHint(GitHintKey.GitStagePathCall, new GitHintFacts { Path = path })]
        };
    }

    // The new-file lines a hunk occupies, using the same rule as GitHunkParser.SelectByNewLineRange.
    private static (int Start, int End) HunkSpan(GitHunk h)
    {
        var start = Math.Max(h.NewStart, 1);
        return (start, start + Math.Max(h.NewCount, 1) - 1);
    }

    private static string SpanText(int start, int end) => start == end ? start.ToString() : $"{start}-{end}";

    private static string DescribeHunkSpans(GitFileDiff diff) =>
    "Hunks (id: new-file lines): " + string.Join("; ", diff.Hunks.Select(h =>
    {
        var (start, end) = HunkSpan(h);
        return $"{h.Index}: {SpanText(start, end)}";
    })) + ".";

    // Resolves hunkIds (guarded by the fingerprint) or lineRange to the selection to stage. Returns null on success with
    // the selection in `selection`; otherwise the classified refusal. Exactly one of the two is non-blank. A lineRange may cut
    // a hunk (a sub-hunk) whose body is a run of removed lines followed by a run of added lines; a partly covered hunk of any other shape is refused.
    private static (string Kind, string Error, string? Detail, IReadOnlyList<GitHint>? Hints)? SelectHunks(
        GitFileDiff diff, string path, string? hunkIds, string? hunkFingerprint, string? lineRange, out GitLineSelectionResult selection)
    {
        selection = new GitLineSelectionResult([], [], []);
        if (!string.IsNullOrWhiteSpace(hunkIds))
        {
            if (string.IsNullOrWhiteSpace(hunkFingerprint))
            {
                return (GitErrorCodes.HunkSelection,
                    $"hunkIds needs the hunkFingerprint returned by {GitHintTokens.Inline(GitHintKey.GitHunksCall)}. Nothing was staged.", null,
                    [new GitHint(GitHintKey.HunkSelection), new GitHint(GitHintKey.GitHunksCall)]);
            }
            var given = hunkFingerprint.Trim();
            if (!string.Equals(given, diff.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return (GitErrorCodes.HunkStale,
                    $"'{path}' changed since the hunks were listed (fingerprint {given} vs {diff.Fingerprint}). " +
                    $"Re-run {GitHintTokens.Inline(GitHintKey.GitHunksPathCall)} and pick again. Nothing was staged.", null,
                    [new GitHint(GitHintKey.HunkStale), new GitHint(GitHintKey.GitHunksPathCall, new GitHintFacts { Path = path })]);
            }
            if (!GitHunkParser.TryParseHunkIds(hunkIds, diff.Hunks.Count, out var ids, out var idError))
                return (GitErrorCodes.HunkSelection, $"{idError} {DescribeHunkSpans(diff)} Nothing was staged.", null, [new GitHint(GitHintKey.HunkSelection)]);
            selection = new GitLineSelectionResult(ids, [], []);
            return null;
        }

        if (!GitHunkParser.TryParseLineRange(lineRange, out var from, out var to, out var rangeError))
            return (GitErrorCodes.HunkSelection, $"{rangeError} {DescribeHunkSpans(diff)} Nothing was staged.", null, [new GitHint(GitHintKey.HunkSelection)]);

        var picked = GitHunkParser.SelectLines(diff, from, to);
        if (picked.Unsupported.Count > 0)
        {
            var spans = picked.Unsupported.Select(HunkSpan).ToList();
            var lo = Math.Min(from, spans.Min(s => s.Start));
            var hi = Math.Max(to, spans.Max(s => s.End));
            var parts = picked.Unsupported.Select(h =>
            {
                var (start, end) = HunkSpan(h);
                return $"Hunk {h.Index} spans lines {SpanText(start, end)} but only partly lies in {SpanText(from, to)}.";
            });
            var unsupportedIds = string.Join(", ", picked.Unsupported.Select(h => h.Index));
            return (GitErrorCodes.HunkSelection,
                $"{string.Join(' ', parts)} The body of hunk {unsupportedIds} is not a run of removed lines followed by a run of added lines (or its cut would join text onto a final line without a newline), so it cannot be cut at that range. " +
                $"Widen the range to {SpanText(lo, hi)} to take the whole hunk, pass hunkIds from {GitHintTokens.Inline(GitHintKey.GitHunksPathCall)}, " +
                $"or stage the whole file with {GitHintTokens.Inline(GitHintKey.GitStagePathCall)}. {DescribeHunkSpans(diff)} Nothing was staged.",
                null,
                [new GitHint(GitHintKey.HunkSelection), new GitHint(GitHintKey.GitHunksPathCall, new GitHintFacts { Path = path }), new GitHint(GitHintKey.GitStagePathCall, new GitHintFacts { Path = path })]);
        }
        if (picked.WholeHunks.Count == 0 && picked.SubHunks.Count == 0)
        {
            return (GitErrorCodes.HunkSelection,
                $"No hunk overlaps lines {SpanText(from, to)}. {DescribeHunkSpans(diff)} Nothing was staged.",
                null, [new GitHint(GitHintKey.HunkSelection)]);
        }
        selection = picked;
        return null;
    }

    // Hunks left in the file after staging `selection`: a whole hunk is gone; a sub-hunk leaves the lines of its hunk it did not take (a head
    // before it and a tail after it are separate zero-context hunks); every other hunk stays. For a mixed hunk cut with its removed lines left unstaged
    // (sub.NewStart > original.NewStart), the head is the unstaged '-' lines plus the head '+' lines: one hunk, which the same test counts.
    private static int ExpectedRemainingHunks(GitFileDiff before, GitLineSelectionResult selection)
    {
        var remaining = before.Hunks.Count - selection.WholeHunks.Count - selection.SubHunks.Count;
        foreach (var sub in selection.SubHunks)
        {
            var original = before.Hunks[sub.Index - 1];
            if (sub.NewStart > original.NewStart)
                remaining++;
            if (sub.NewStart + sub.NewCount < original.NewStart + original.NewCount)
                remaining++;
        }
        return remaining;
    }

    // The staged hunks in listing order; a sub-hunk keeps the id of the hunk it was cut from, its new-file line numbers and only the lines taken.
    private static List<GitHunkEntry> StagedEntries(GitFileDiff before, GitLineSelectionResult selection) =>
        selection.WholeHunks.Select(i => before.Hunks[i - 1]).Concat(selection.SubHunks).OrderBy(h => h.Index).Select(ToHunkEntry).ToList();

    // One fact per hunk that was staged only in part: which lines went in and that the rest of the hunk stays unstaged.
    private static List<string> PartialHunkNotes(GitFileDiff before, GitLineSelectionResult selection)
    {
        var notes = new List<string>();
        foreach (var sub in selection.SubHunks)
        {
            var original = before.Hunks[sub.Index - 1];
            var counts = $"{sub.NewCount} of {original.NewCount} added lines";
            if (sub.OldCount > 0)
                counts += $", plus its {sub.OldCount} removed lines";
            var rest = original.OldCount > 0 && sub.OldCount == 0
                ? $"The other lines of that hunk stay unstaged, including its {original.OldCount} removed lines."
                : "The other lines of that hunk stay unstaged.";
            notes.Add($"Hunk {sub.Index} (new-file lines {SpanText(original.NewStart, original.NewStart + original.NewCount - 1)}) was staged only in part: " +
                      $"lines {SpanText(sub.NewStart, sub.NewStart + sub.NewCount - 1)} ({counts}). {rest}");
        }
        return notes;
    }

    // The index blob id the diff was read against: the old side of its "index <old>..<new>" line (an abbreviation), or null if the header has none.
    private static string? DiffIndexBlob(GitFileDiff diff)
    {
        foreach (var line in diff.HeaderText.Split('\n'))
        {
            if (!line.StartsWith("index ", StringComparison.Ordinal))
                continue;
            var rest = line[6..];
            var dots = rest.IndexOf("..", StringComparison.Ordinal);
            return dots > 0 ? rest[..dots] : null;
        }
        return null;
    }

    private static string ShortBlob(string id) => id.Length > 7 && id[0] != '(' ? id[..7] : id;

    // The blob id of the file's index entry now: "(absent)" when it has none, "(unmerged)" when it has several stages, null when git could not be asked.
    private async Task<string?> ReadIndexBlobAsync(string gitRoot, string path, CancellationToken cancellationToken)
    {
        var run = await RunGitAsync(gitRoot, ["--literal-pathspecs", "ls-files", "-s", "--", path], cancellationToken);
        if (run.ExitCode != 0)
            return null;
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return "(absent)";
        if (lines.Length > 1)
            return "(unmerged)";
        var fields = lines[0].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 ? fields[1] : null;
    }

    // Returns the refusal text when the file's index entry is no longer the one the diff was read against (another session staged or reset
    // it). The patch is positioned for the old index content, so applying it over that change could put lines in the wrong place. git apply
    // holds index.lock only while it runs, so a change landing between this re-read and that lock is a residual window this check cannot close.
    // Returns null when unchanged, or when the diff header or git cannot say (git apply then judges the patch itself).
    private async Task<string?> IndexEntryChangedMessageAsync(string gitRoot, string path, GitFileDiff diff, CancellationToken cancellationToken)
    {
        var expected = DiffIndexBlob(diff);
        if (expected is null)
            return null;
        var now = await ReadIndexBlobAsync(gitRoot, path, cancellationToken);
        if (now is null || now.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
            return null;
        return $"The index entry for '{path}' changed between reading the diff and applying it (was {ShortBlob(expected)}, now {ShortBlob(now)}): " +
               $"another session changed what is staged for this file. Re-run {GitHintTokens.Inline(GitHintKey.GitHunksPathCall)} " +
               $"for the current hunks and select again, or stage the whole file with {GitHintTokens.Inline(GitHintKey.GitStagePathCall)}. Nothing was staged.";
    }

    private static GitStageHunksResult StageHunksFailure(string path, string kind, string error, string detail,
        IReadOnlyList<GitHint>? hints = null) => new()
        {
            Path = path,
            IsError = true,
            ErrorKind = kind,
            Error = error,
            ErrorDetail = detail,
            Hints = hints
        };

    /// <summary>Stage-hunks failure whose next-step text is a hint key rendered at the tool boundary; ErrorDetail stays null.</summary>
    private static GitStageHunksResult StageHunksFailure(string path, string kind, string error, GitHintKey key, GitHintFacts? facts = null) => new()
    {
        Path = path,
        IsError = true,
        ErrorKind = kind,
        Error = error,
        Hints = [new GitHint(key, facts)]
    };

    // Runs after git apply succeeded: re-reads the unstaged diff to count what is left, and copies the repository
    // status (from StatusAsync) into the inherited GitStatusResult members. Never turns a successful stage into an error.
    private async Task<GitStageHunksResult> BuildStageHunksResultAsync(
        string gitRoot, string? paths, string path, GitFileDiff before, GitLineSelectionResult selection, CancellationToken cancellationToken)
    {
        var expected = ExpectedRemainingHunks(before, selection);
        var after = await LoadFileDiffAsync(gitRoot, paths, cancellationToken);
        int? remaining = after.Diff is not null ? after.Diff.Hunks.Count
            : after.ErrorKind == GitErrorCodes.NoChanges ? 0
            : null;
        var notes = PartialHunkNotes(before, selection);
        var warnings = new List<string>();
        if (remaining != expected)
        {
            warnings.Add($"Staged {selection.WholeHunks.Count + selection.SubHunks.Count} hunk(s) but the remaining count is {remaining?.ToString() ?? "unknown"} " +
                         $"(expected {expected}); verify with {GitHintTokens.Inline(GitHintKey.GitDiffStagedCall)}.");
        }

        var status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        if (status.IsError)
            warnings.Add($"The hunks were staged but the status could not be read: {status.Error}");

        return new GitStageHunksResult
        {
            Path = path,
            StagedHunks = StagedEntries(before, selection),
            RemainingUnstagedHunks = remaining ?? expected,
            Note = notes.Count == 0 ? null : string.Join(' ', notes),
            Warning = warnings.Count == 0 ? null : string.Join(' ', warnings),
            Hints = remaining != expected ? new[] { new GitHint(GitHintKey.GitDiffStagedCall) } : null,
            Branch = status.Branch,
            IsClean = status.IsClean,
            Staged = status.Staged,
            Unstaged = status.Unstaged,
            Untracked = status.Untracked,
            IsTruncated = status.IsTruncated,
            TotalStagedCount = status.TotalStagedCount,
            TotalUnstagedCount = status.TotalUnstagedCount,
            TotalUntrackedCount = status.TotalUntrackedCount,
            StagedByStatus = status.StagedByStatus,
            UnstagedByStatus = status.UnstagedByStatus,
            InProgress = status.InProgress,
            AbortedOperation = status.AbortedOperation,
        };
    }

    /// <summary>
    /// Stages only the selected hunks of exactly one tracked text file into the index, by hunkIds (guarded by the
    /// fingerprint from operation=hunks) or by a new-file lineRange. Only the index changes; the working tree is
    /// never touched. git apply is all-or-nothing, so a rejected patch stages nothing.
    /// </summary>
    public async Task<GitStageHunksResult> StageHunksAsync(
        string gitRoot, string? paths, string? hunkIds, string? hunkFingerprint, string? lineRange,
        CancellationToken cancellationToken)
    {
        string? patchFile = null;
        try
        {
            if (string.IsNullOrWhiteSpace(hunkIds) == string.IsNullOrWhiteSpace(lineRange))
            {
                return StageHunksFailure("", GitErrorCodes.HunkSelection,
                    "Pass hunkIds (with hunkFingerprint) or lineRange, not both. Nothing was staged.", GitHintKey.HunkSelection);
            }

            var load = await LoadFileDiffAsync(gitRoot, paths, cancellationToken);
            if (load.Diff is null)
                return StageHunksFailure(load.Path, load.ErrorKind ?? GitErrorCodes.Fallback, load.Error ?? "", load.ErrorDetail ?? "", load.Hints);

            var rejection = SelectHunks(load.Diff, load.Path, hunkIds, hunkFingerprint, lineRange, out var selection);
            if (rejection is { } refused)
                return StageHunksFailure(load.Path, refused.Kind, refused.Error, refused.Detail ?? "", refused.Hints);

            // Outside the repo, so the workspace file watcher never sees it. Latin-1 keeps every byte of the
            // patch (including CR) exactly as git printed it.
            patchFile = Path.Combine(Path.GetTempPath(), "roslynsentinel-hunk-" + Guid.NewGuid().ToString("N") + ".patch");
            await File.WriteAllBytesAsync(patchFile, Encoding.Latin1.GetBytes(GitHunkParser.BuildPatch(load.Diff, selection)), cancellationToken);

            _afterHunkDiffLoaded?.Invoke(gitRoot);
            if (await IndexEntryChangedMessageAsync(gitRoot, load.Path, load.Diff, cancellationToken) is { } indexChanged)
            {
                // indexChanged carries the Inline tokens of the hunks and stage calls for this path.
                var staleFailure = StageHunksFailure(load.Path, GitErrorCodes.HunkStale, indexChanged, GitHintKey.HunkStale);
                var pathFacts = new GitHintFacts { Path = load.Path };
                return WithHint(WithHint(staleFailure, new GitHint(GitHintKey.GitHunksPathCall, pathFacts)), new GitHint(GitHintKey.GitStagePathCall, pathFacts));
            }

            var apply = await RunGitAsync(gitRoot,
                ["apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", patchFile], cancellationToken);
            if (apply.ExitCode != 0)
            {
                return StageHunksFailure(load.Path, GitErrorCodes.Fallback,
                    $"git apply rejected the selected hunks: {CleanGitStderr(apply.Stderr)}. Nothing was staged.",
                    GitHintKey.HunkApplyRejectedNextStep);
            }

            return await BuildStageHunksResultAsync(gitRoot, paths, load.Path, load.Diff, selection, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStageHunksResult>(ex, "stage hunks");
        }
        finally
        {
            if (patchFile is not null)
            {
                try
                {
                    File.Delete(patchFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not delete temporary hunk patch file {PatchFile}", patchFile);
                }
            }
        }
    }

    /// <summary>
    /// Best-effort check that <paramref name="text"/> decoded cleanly as UTF-8, for surfacing a
    /// warning rather than silently returning corrupted diff text - see
    /// docs/current/blockers/blocking_error_git_diff_mojibake_display.md. Two independent signals:
    /// U+FFFD (the .NET/UTF-8 decoder's own "this byte sequence was invalid" replacement character),
    /// and the specific C1-control-block run (U+0080-U+009F) that appears when UTF-8 multi-byte
    /// sequences get mis-decoded one byte at a time through a Windows-125x-family legacy codepage
    /// instead (the "ΓÇö"-style mojibake the linked blocker documented). Neither check requires
    /// knowing what the correct text should have been - both are shapes that well-formed text
    /// essentially never contains on their own.
    /// </summary>
    private static string? DetectDecodeCorruption(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        if (text.Contains('�'))
        {
            return "Output contains U+FFFD (Unicode replacement character), meaning some bytes " +
                   "from git could not be decoded as UTF-8 and were lost. Non-ASCII content in this " +
                   "result may be corrupted or missing - re-check the source file directly (ReadFile/" +
                   "GetFileOutline) rather than trusting this text for anything non-ASCII.";
        }

        var suspiciousRunLength = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u0080' and <= '\u009F')
            {
                suspiciousRunLength++;
                if (suspiciousRunLength >= 2)
                {
                    return "Output contains a run of C1 control characters (U+0080-U+009F), the " +
                           "signature of a UTF-8 multi-byte sequence mis-decoded one byte at a time " +
                           "through a legacy codepage (e.g. an em dash showing up as ΓÇö-style " +
                           "mojibake). Non-ASCII content in this result is likely corrupted - re-check " +
                           "the source file directly (ReadFile/GetFileOutline) rather than trusting this text.";
                }
            }
            else
            {
                suspiciousRunLength = 0;
            }
        }

        return null;
    }

    private static string StatusLabel(char code) => code switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'U' => "conflict",
        _ => code.ToString()
    };

    /// <summary>Default for the status <c>maxEntries</c> parameter (the long-standing 50-entry threshold).</summary>
    public const int DefaultStatusMaxEntries = 50;

    /// <summary>Upper bound accepted for the status <c>maxEntries</c> parameter.</summary>
    public const int MaxStatusMaxEntries = 5000;

    /// <summary>
    /// True for the seven unmerged XY pairs porcelain v1 reports (DD, AU, UD, UA, DU, AA, UU).
    /// "A" or "D" alone mean staged add/delete, so only these exact pairs (every one of which
    /// contains a U, or is AA or DD) are conflicts - AA and DD must not read as staged add/delete.
    /// </summary>
    private static bool IsConflictPair(char x, char y) =>
        x == 'U' || y == 'U' || (x == 'A' && y == 'A') || (x == 'D' && y == 'D');

    /// <summary>
    /// Detects a multi-step operation left half-finished by a conflict, from the repository's own
    /// state files: "rebase" (rebase-merge/ or rebase-apply/), "am" (rebase-apply/applying),
    /// "cherry-pick" (CHERRY_PICK_HEAD), "revert" (REVERT_HEAD), "merge" (MERGE_HEAD). Returns null
    /// when nothing is in progress. Rebase is checked first because a conflicted rebase also writes
    /// CHERRY_PICK_HEAD. The git dir comes from rev-parse (not gitRoot + ".git") so linked worktrees,
    /// where .git is a file and the state files live in the per-worktree git dir, resolve correctly.
    /// </summary>
    private async Task<string?> DetectInProgressAsync(string gitRoot, CancellationToken cancellationToken)
    {
        // Through the seam (libgit2 reads the per-worktree git dir in process; the CLI's `rev-parse --absolute-git-dir` answers when it declines or fails). Either way it is the
        // same GitOperationState check the status reader runs, so every caller agrees on what "in progress" means.
        return await Task.Run(() => _gitReader.GetInProgressOperation(gitRoot, GitProcessTimeout), cancellationToken);
    }

    /// <summary>
    /// Text to append to a failed pull/revert/commit when the failure left the repository
    /// mid-operation (a conflict): names the state and the way out. Empty when nothing is in
    /// progress, and on any detection failure - it is advice, never a reason to mask the real error.
    /// </summary>
    private async Task<string> InProgressAdviceAsync(string gitRoot, CancellationToken cancellationToken)
    {
        string? op;
        try
        {
            op = await DetectInProgressAsync(gitRoot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }

        return op switch
        {
            null => "",
            "rebase" or "am" => $" Repository is mid-{op}. This tool cannot continue it; call {GitHintTokens.Inline(GitHintKey.GitAbortCall)} to back out (the branch returns to its pre-{op} state), or finish it in the shell.",
            _ => $" Repository is mid-{op}. Resolve the conflicts and commit, or call {GitHintTokens.Inline(GitHintKey.GitAbortCall)} to back out.",
        };
    }

    /// <summary>
    /// Appends the mid-operation advice to a failed <paramref name="result"/> and, when the repository
    /// really is mid-operation, classifies it as <see cref="GitErrorCodes.OperationInProgress"/> unless a
    /// more specific kind was already set. No-op when nothing is in progress.
    /// </summary>
    private async Task<T> AppendInProgressAsync<T>(T result, string gitRoot, CancellationToken cancellationToken) where T : GitResult
    {
        var advice = await InProgressAdviceAsync(gitRoot, cancellationToken);
        if (advice.Length == 0)
            return result;

        result.Error += advice;
        result.ErrorKind ??= GitErrorCodes.OperationInProgress;
        // A hint whose slot is ErrorDetail renders into ErrorDetail at the tool boundary, so it counts as "already set" exactly as a text did.
        var hints = result.Hints is null ? new List<GitHint>() : new List<GitHint>(result.Hints);
        if (result.ErrorDetail is null && !hints.Any(h => GitHintKeySlots.Of(h.Key) == GitHintSlot.ErrorDetail))
            hints.Add(new GitHint(GitHintKey.OperationInProgress));
        // The advice carries the abort call as an Inline token; the hint (slot Inline, never ErrorDetail) renders it at the tool boundary.
        hints.Add(new GitHint(GitHintKey.GitAbortCall));
        result.Hints = hints;
        return result;
    }

    /// <summary>
    /// True when the index holds nothing to commit: for no paths, the whole index equals HEAD; for named
    /// paths, those paths do. Structural (git diff --cached --quiet) rather than matching git's
    /// localizable "nothing to commit" text.
    /// </summary>
    private async Task<bool> NothingStagedAsync(string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        string[]? list = null;
        if (!string.IsNullOrWhiteSpace(paths))
        {
            list = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var parseError);
            if (parseError != null || list == null)
                return false;
        }

        // Through the seam: false (no staged changes) only when git, or libgit2 on a plain literal path list, says the index equals HEAD; a failed read is not "nothing staged", as before.
        return await Task.Run(() => _gitReader.HasStagedChanges(gitRoot, list, GitProcessTimeout), cancellationToken) == false;
    }

    /// <summary>True when git's own English "nothing to commit" status text is in the output of a failed commit; only the tie-break between a declining pre-commit hook and an empty index (see CommitAsync).</summary>
    private static bool IsGitNothingToCommitText(string stdout, string stderr)
    {
        static bool Has(string text) =>
            text.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("nothing added to commit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no changes added to commit", StringComparison.OrdinalIgnoreCase);
        return Has(stdout) || Has(stderr);
    }

    /// <summary>
    /// Presents the reader's status rows exactly as the tool always has: staged and unstaged entries with a label per XY
    /// letter, conflicts labelled "conflict" on both sides, untracked paths listed individually, and past
    /// <paramref name="maxEntries"/> a 10-per-list sample with the full counts.
    /// </summary>
    private static GitStatusResult BuildStatusResult(WorkingTreeStatus snapshot, int maxEntries)
    {
        var branch = snapshot.Branch ?? "unknown";
        var staged = new List<GitStatusEntry>();
        var unstaged = new List<GitStatusEntry>();
        var untracked = new List<string>();

        foreach (var change in snapshot.Changes)
        {
            var x = change.IndexState;
            var y = change.WorkTreeState;
            var path = change.Path;
            var originalPath = change.OriginalPath;

            if (x == '?' && y == '?')
            {
                untracked.Add(path);
                continue;
            }

            if (IsConflictPair(x, y))
            {
                // Both sides are labelled "conflict": AA/DD would otherwise read as a staged
                // add/delete, which is the opposite of what an unmerged path is.
                staged.Add(new GitStatusEntry { Status = "conflict", Path = path });
                unstaged.Add(new GitStatusEntry { Status = "conflict", Path = path });
                continue;
            }

            if (x != ' ' && x != '?')
            {
                staged.Add(new GitStatusEntry
                {
                    Status = StatusLabel(x),
                    Path = path,
                    OriginalPath = x is 'R' or 'C' ? originalPath : null,
                });
            }
            if (y != ' ' && y != '?')
            {
                unstaged.Add(new GitStatusEntry
                {
                    Status = StatusLabel(y),
                    Path = path,
                    OriginalPath = y is 'R' or 'C' ? originalPath : null,
                });
            }
        }

        bool isClean = staged.Count == 0 && unstaged.Count == 0 && untracked.Count == 0;
        int total = staged.Count + unstaged.Count + untracked.Count;
        var sampleSize = Math.Min(10, maxEntries);

        if (total > maxEntries)
        {
            return new GitStatusResult
            {
                IsError = false,
                Branch = branch,
                IsClean = isClean,
                IsTruncated = true,
                InProgress = snapshot.InProgress,
                TotalStagedCount = staged.Count,
                TotalUnstagedCount = unstaged.Count,
                TotalUntrackedCount = untracked.Count,
                StagedByStatus = staged.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
                UnstagedByStatus = unstaged.GroupBy(e => e.Status).ToDictionary(g => g.Key, g => g.Count()),
                Staged = staged.Take(sampleSize).ToList(),
                Unstaged = unstaged.Take(sampleSize).ToList(),
                Untracked = untracked.Take(sampleSize).ToList(),
            };
        }

        return new GitStatusResult
        {
            IsError = false,
            Branch = branch,
            IsClean = isClean,
            InProgress = snapshot.InProgress,
            Staged = staged,
            Unstaged = unstaged,
            Untracked = untracked,
        };
    }

    /// <summary>
    /// The status reader could not answer (git failed, was missing or timed out). Re-runs the status command once so the
    /// caller gets git's own message, as the tool always reported it.
    /// </summary>
    private async Task<GitStatusResult> StatusFailureAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var branchRaw = await RunGitAsync(gitRoot,
            ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        var branch = branchRaw.ExitCode == 0 ? branchRaw.Stdout.Trim() : "unknown";

        var statusRaw = await RunGitAsync(gitRoot,
            ["-c", "core.quotePath=false", "status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken);
        var error = statusRaw.ExitCode != 0
            ? CleanGitStderr(statusRaw.Stderr)
            : "git status could not be read (it failed or timed out); retry the call. Nothing was changed.";
        return new GitStatusResult { IsError = true, Branch = branch, Error = error };
    }

    public async Task<GitStatusResult> StatusAsync(string gitRoot, int maxEntries, CancellationToken cancellationToken)
    {
        if (maxEntries < 1 || maxEntries > MaxStatusMaxEntries)
        {
            return new GitStatusResult
            {
                IsError = true,
                Error = $"maxEntries must be between 1 and {MaxStatusMaxEntries} (got {maxEntries}). " +
                        $"Retry with a value in that range, e.g. maxEntries: {DefaultStatusMaxEntries}. Nothing was changed."
            };
        }

        try
        {
            // The typed seam answers in-process (libgit2) and falls back to the git CLI on its own. It blocks, so it runs off the
            // caller's thread; the token only stops a call that has not started (libgit2 cannot be cancelled mid-call and the CLI
            // reader is bounded by GitProcessTimeout instead).
            var snapshot = await Task.Run(() => _gitReader.GetRepositoryStatus(gitRoot, GitProcessTimeout), cancellationToken);
            if (snapshot is null)
                return await StatusFailureAsync(gitRoot, cancellationToken);

            return BuildStatusResult(snapshot, maxEntries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStatusResult>(ex, "status");
        }
    }

    /// <summary>
    /// Presents a reader's commit as the tool always has. The message gets what the old text pipeline gave it: every line break
    /// (CR, LF or CRLF) becomes the platform newline (<see cref="RejoinLines"/>), then leading and trailing line breaks are trimmed.
    /// </summary>
    private static GitCommitEntry ToCommitEntry(LogCommit commit) => new()
    {
        Hash = commit.Sha,
        ShortHash = commit.ShortSha,
        Author = commit.AuthorName,
        Date = commit.AuthorDate,
        Message = RejoinLines(commit.Message).Trim('\n', '\r'),
    };

    /// <summary>
    /// The log reader could not answer (unknown revision, unborn HEAD, git failed or timed out). Runs the same command through the tool's own
    /// runner so the caller gets git's message exactly as before; if it succeeds this time, its output is presented like a reader's.
    /// </summary>
    private async Task<GitLogResult> LogViaCliAsync(
        string gitRoot, int count, string? refName, List<string>? pathList, string? pickaxe, bool pickaxeRegex, bool follow, CancellationToken cancellationToken)
    {
        var args = CliGitReader.BuildLogArguments(count, refName, pathList, pickaxe, pickaxeRegex, follow);
        var logRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
        if (logRaw.ExitCode != 0)
            return new GitLogResult { IsError = true, Error = CleanGitStderr(logRaw.Stderr) };

        return new GitLogResult { IsError = false, Commits = [.. CliGitReader.ParseLogEntries(logRaw.Stdout).Select(ToCommitEntry)] };
    }

    public async Task<GitLogResult> LogAsync(
        string gitRoot, int count, string? refName, string? paths, string? pickaxe, bool pickaxeRegex, bool follow, CancellationToken cancellationToken, bool nameOnly = false)
    {
        count = Math.Clamp(count, 1, 100);

        // nameOnly keeps only the one-line-per-commit text (NameOnlyLines) and drops the Commits list, so the output stays small.
        GitLogResult Shape(GitLogResult result)
        {
            if (!nameOnly || result.IsError)
                return result;
            result.NameOnlyLines = string.Join("\n", result.Commits.Select(c => $"{c.ShortHash} {c.Message.Split('\n')[0].TrimEnd('\r')}"));
            result.Commits = [];
            return result;
        }

        try
        {
            List<string>? pathList = null;
            if (!string.IsNullOrWhiteSpace(paths))
            {
                var parsedPaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitLogResult { IsError = true, Error = pathsError };

                pathList = [.. parsedPaths!];
                if (follow && pathList.Count != 1)
                    return new GitLogResult { IsError = true, Error = $"follow needs exactly one path in paths, but {pathList.Count} were supplied. Pass a single file path, or omit follow." };
            }
            else if (follow)
            {
                return new GitLogResult { IsError = true, Error = "follow needs exactly one path in paths, but none was supplied. Pass a single file path, or omit follow." };
            }

            // Pickaxe (-S/-G) and --follow have no libgit2 equivalent that matches git, so they go straight to git. Offering them to the
            // reader would only make it decline, and a decline is recorded as a fallback (FirstFallbackReason), which is meant to flag a
            // broken libgit2, not an option that is known to be CLI-only.
            if (!string.IsNullOrWhiteSpace(pickaxe) || follow)
                return Shape(await LogViaCliAsync(gitRoot, count, refName, pathList, pickaxe, pickaxeRegex, follow, cancellationToken));

            // The typed seam answers in-process (libgit2) where it can reproduce git's output exactly and runs the git CLI otherwise
            // (ranges, merges under a path filter, ...). It blocks, so it runs off the caller's thread; the token only
            // stops a call that has not started.
            var commits = await Task.Run(
                () => _gitReader.GetLog(gitRoot, count, refName, pathList, pickaxe, pickaxeRegex, follow, GitProcessTimeout), cancellationToken);
            if (commits is null)
                return Shape(await LogViaCliAsync(gitRoot, count, refName, pathList, pickaxe, pickaxeRegex, follow, cancellationToken));

            return Shape(new GitLogResult { IsError = false, Commits = [.. commits.Select(ToCommitEntry)] });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitLogResult>(ex, "log");
        }
    }

    // The well-known, always-valid hash of git's empty tree object - same in every repository,
    // since it depends only on the (fixed) serialization of "a tree with zero entries." Used as
    // the diff base for a commit that has no parent (git's own `git diff <hash>^ <hash>` fails
    // with "unknown revision" in exactly that case, since `^` has nothing to resolve to).
    private const string EmptyTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>
    /// Caps <paramref name="text"/> at <paramref name="maxBytes"/> UTF-8 bytes (not chars), cutting
    /// only on a whole-character boundary so a surrogate pair is never split, and appends a
    /// truncation marker when anything was dropped.
    /// </summary>
    private static string CapToUtf8Bytes(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
            return text;

        var bytes = 0;
        var chars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes)
                break;
            bytes += runeBytes;
            chars += rune.Utf16SequenceLength;
        }

        return text[..chars] + $"\n... (truncated at {maxBytes} bytes)";
    }

    /// <summary>
    /// Returns a refusal message when both output-format flags are set, else null. They pick
    /// different git output formats (<c>--name-status</c> vs <c>--stat</c>) and cannot be combined.
    /// </summary>
    private static string? ValidateDiffFormatFlags(string operationName, bool nameOnly, bool stat) =>
        nameOnly && stat
            ? $"Git {operationName}: both 'nameOnly' and 'stat' were set, but they select different output formats " +
              "(nameOnly = a --name-status file list, stat = --stat text). Pass only one of them, or neither for the full patch. Nothing was run."
            : null;

    /// <summary>
    /// Counts the files a diff-family output covers, per the format that was requested: one line
    /// per file for <c>--name-status</c>, the trailing "N files changed" summary for <c>--stat</c>,
    /// and the number of "diff --git" headers for a full patch.
    /// </summary>
    private static int CountChangedFiles(string output, bool nameOnly, bool stat)
    {
        if (nameOnly)
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

        if (stat)
        {
            var summary = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            var match = summary is null
                ? null
                : System.Text.RegularExpressions.Regex.Match(summary, @"(\d+) files? changed");
            return match is { Success: true } ? int.Parse(match.Groups[1].Value) : 0;
        }

        return output.Split('\n').Count(l => l.StartsWith("diff --git", StringComparison.Ordinal));
    }

    /// <summary>The text <c>git diff --name-status</c> prints for the rows the seam returned: one tab-separated line per file, each ending in a newline.</summary>
    private static string FormatNameStatus(IReadOnlyList<NameStatusEntry> entries) =>
        // Environment.NewLine, not "\n": RunGitAsync re-terminates every line that way (RejoinLines), and the text here must be what that path produced.
        string.Concat(entries.Select(entry => entry.OldPath is null
            ? $"{entry.Status}\t{entry.Path}{Environment.NewLine}"
            : $"{entry.Status}\t{entry.OldPath}\t{entry.Path}{Environment.NewLine}"));

    /// <summary>True for the diff targets the seam answers in-process: the index against HEAD ("staged") and refA..refB (not the three-dot form). Anything that compares with the working tree stays on git.</summary>
    private static bool IsSeamDiffTarget(string target) =>
        target == "staged" || (target.Contains("..", StringComparison.Ordinal) && !target.Contains("...", StringComparison.Ordinal));

    public async Task<GitDiffResult> DiffAsync(
        string gitRoot, string target, string? paths, int maxBytes, bool nameOnly, bool stat, CancellationToken cancellationToken)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 524288);
        var formatError = ValidateDiffFormatFlags("diff", nameOnly, stat);
        if (formatError is not null)
            return new GitDiffResult { IsError = true, Error = formatError };

        try
        {
            // core.quotePath=false so non-ASCII paths in headers and file lists are not octal-escaped.
            var args = new List<string> { "-c", "core.quotePath=false", "diff" };
            if (nameOnly)
                args.Add("--name-status");
            else if (stat)
                args.Add("--stat=200");

            // unstaged is an alias of working (uncommitted changes vs the index)
            if (string.Equals(target, "unstaged", StringComparison.OrdinalIgnoreCase))
                target = "working";

            if (target == "staged")
            {
                args.Add("--cached");
            }
            else if (target.Contains("...", StringComparison.Ordinal) || target.Contains("..", StringComparison.Ordinal))
            {
                // A caller-supplied range (refA..refB or refA...refB) - pass straight through as a
                // single arg, exactly like the git CLI accepts it, rather than trying to split and
                // reinterpret it ourselves.
                args.Add(target);
            }
            else if (target != "working")
            {
                // Diff the working tree against an arbitrary single ref (HEAD, a branch, a SHA) -
                // equivalent to `git diff <target>`. This is NOT "what did this commit change"
                // (that is ShowAsync's job, diffing the commit against its parent) - conflating the
                // two here previously made `target: "HEAD"` return the last commit's diff instead of
                // the working tree's uncommitted changes against HEAD.
                args.Add(target);
            }

            IReadOnlyList<string>? pathLimit = null; // null: no "--" in the command
            if (!string.IsNullOrWhiteSpace(paths))
            {
                var parsedPaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitDiffResult { IsError = true, Error = pathsError };

                pathLimit = parsedPaths;
                args.Add("--");
                args.AddRange(parsedPaths!);
            }

            // staged or refA..refB: the file list (nameOnly) and the patch text (neither nameOnly nor stat) are served by the seam (libgit2, git CLI as fallback;
            // the composite reader declines to the CLI for every patch whose text libgit2 would not reproduce byte for byte). Everything else stays on the git CLI:
            //  - working and a single ref compare with working-tree files: libgit2's index-to-workdir diff lists a stat-dirty file whose only difference is EOL
            //    conversion (core.autocrlf, .gitattributes) as modified where git lists nothing, and its tree-to-workdir diff has no index awareness;
            //  - stat: libgit2's DiffStats does not reproduce git's stat graph widths.
            // A null answer re-runs the command below so git's own error text is what the caller sees.
            var nameStatus = nameOnly && IsSeamDiffTarget(target)
                ? await Task.Run(() => _gitReader.GetNameStatus(gitRoot, target, pathLimit, GitProcessTimeout), cancellationToken)
                : null;
            var patchText = !nameOnly && !stat && IsSeamDiffTarget(target)
                ? await Task.Run(() => _gitReader.GetPatchText(gitRoot, target, pathLimit, GitProcessTimeout), cancellationToken)
                : null;
            var diffRaw = nameStatus is not null
                ? new GitRawResult { ExitCode = 0, Stdout = FormatNameStatus(nameStatus) }
                : patchText is not null
                    // RejoinLines: the text must be what RunGitAsync would have returned for the same git output.
                    ? new GitRawResult { ExitCode = 0, Stdout = RejoinLines(patchText) }
                    : await RunGitAsync(gitRoot, [.. args], cancellationToken);

            if (diffRaw.ExitCode != 0)
            {
                var cleaned = CleanGitStderr(diffRaw.Stderr);
                if (diffRaw.Stderr.Contains("bad revision", StringComparison.OrdinalIgnoreCase) ||
                    diffRaw.Stderr.Contains("unknown revision", StringComparison.OrdinalIgnoreCase) ||
                    diffRaw.Stderr.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase))
                {
                    cleaned += " Valid diff targets: working (alias: unstaged) = uncommitted changes vs the index; staged = index vs HEAD; a commit, branch or tag = working tree vs that ref; refA..refB or refA...refB = a range.";
                }
                return new GitDiffResult { IsError = true, Error = cleaned };
            }

            var filesChanged = CountChangedFiles(diffRaw.Stdout, nameOnly, stat);
            var diff = CapToUtf8Bytes(diffRaw.Stdout, maxBytes);

            return new GitDiffResult { IsError = false, Diff = diff, FilesChanged = filesChanged, Warning = DetectDecodeCorruption(diff) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitDiffResult>(ex, "diff");
        }
    }

    /// <summary>
    /// Shows one commit's metadata (hash/author/date/full body) plus the diff it introduced -
    /// equivalent to <c>git show <target></c>. Falls back to the empty tree for a diff base
    /// when <paramref name="target"/> has no parent (a repo's first commit), same as DiffAsync.
    /// </summary>
    public async Task<GitShowResult> ShowAsync(
        string gitRoot, string target, string? paths, int maxBytes, bool nameOnly, bool stat, CancellationToken cancellationToken)
    {
        maxBytes = Math.Clamp(maxBytes, 1024, 524288);
        var formatError = ValidateDiffFormatFlags("show", nameOnly, stat);
        if (formatError is not null)
            return new GitShowResult { IsError = true, Error = formatError };

        try
        {
            // The header (hash, author, date, message) is served by the seam; a null answer (or a git failure) re-runs `git show` so git's own error text is reported.
            var header = await Task.Run(() => _gitReader.GetCommitHeader(gitRoot, target, GitProcessTimeout), cancellationToken);
            if (header is null)
            {
                const string fieldSep = "\x1f";
                var format = $"%H{fieldSep}%h{fieldSep}%an{fieldSep}%aI{fieldSep}%B";

                var metaRaw = await RunGitAsync(
                    gitRoot, ["show", $"--format={format}", "--no-patch", target], cancellationToken);
                if (metaRaw.ExitCode != 0)
                    return new GitShowResult { IsError = true, Error = CleanGitStderr(metaRaw.Stderr) };

                var metaParts = metaRaw.Stdout.TrimEnd('\n', '\r').Split(fieldSep);
                if (metaParts.Length < 5)
                    return new GitShowResult { IsError = true, Error = $"Could not parse commit metadata for '{target}'." };

                header = new CommitHeader(metaParts[0], metaParts[2], metaParts[3], metaParts[4]);
            }

            IReadOnlyList<string>? pathLimit = null; // null: no "--" in the command
            if (!string.IsNullOrWhiteSpace(paths))
            {
                var parsedPaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitShowResult { IsError = true, Error = pathsError };

                pathLimit = parsedPaths;
            }

            // nameOnly: the file list is served by the seam, as for diff; so is the patch (neither nameOnly nor stat), with the git CLI below as fallback when the
            // reader declines. Only stat stays on the git CLI, for the reason given in DiffAsync.
            var nameStatus = nameOnly
                ? await Task.Run(() => _gitReader.GetCommitNameStatus(gitRoot, target, pathLimit, GitProcessTimeout), cancellationToken)
                : null;
            var patchText = !nameOnly && !stat
                ? await Task.Run(() => _gitReader.GetCommitPatchText(gitRoot, target, pathLimit, GitProcessTimeout), cancellationToken)
                : null;
            GitRawResult diffRaw;
            if (nameStatus is not null)
            {
                diffRaw = new GitRawResult { ExitCode = 0, Stdout = FormatNameStatus(nameStatus) };
            }
            else if (patchText is not null)
            {
                // RejoinLines: the text must be what RunGitAsync would have returned for the same git output.
                diffRaw = new GitRawResult { ExitCode = 0, Stdout = RejoinLines(patchText) };
            }
            else
            {
                var revParseRaw = await RunGitAsync(gitRoot, ["rev-parse", "--verify", "--quiet", $"{target}^"], cancellationToken);
                // core.quotePath=false so non-ASCII paths in headers and file lists are not octal-escaped.
                var diffArgs = new List<string> { "-c", "core.quotePath=false", "diff" };
                if (nameOnly)
                    diffArgs.Add("--name-status");
                else if (stat)
                    diffArgs.Add("--stat=200");
                diffArgs.Add(revParseRaw.ExitCode == 0 ? $"{target}^" : EmptyTreeHash);
                diffArgs.Add(target);
                if (pathLimit is not null)
                {
                    diffArgs.Add("--");
                    diffArgs.AddRange(pathLimit);
                }

                diffRaw = await RunGitAsync(gitRoot, [.. diffArgs], cancellationToken);
            }

            if (diffRaw.ExitCode != 0)
                return new GitShowResult { IsError = true, Error = CleanGitStderr(diffRaw.Stderr) };

            var filesChanged = CountChangedFiles(diffRaw.Stdout, nameOnly, stat);
            var diff = CapToUtf8Bytes(diffRaw.Stdout, maxBytes);

            return new GitShowResult
            {
                IsError = false,
                Hash = header.Sha,
                Author = header.AuthorName,
                Date = header.AuthorDate,
                Message = RejoinLines(header.Message).Trim('\n', '\r'),
                Diff = diff,
                FilesChanged = filesChanged,
                Warning = DetectDecodeCorruption(diff) ?? DetectDecodeCorruption(header.Message),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitShowResult>(ex, "show");
        }
    }

    private static GitHunksResult HunksFailure(string path, string kind, string error, string detail,
        IReadOnlyList<GitHint>? hints = null) => new()
        {
            Path = path,
            IsError = true,
            ErrorKind = kind,
            Error = error,
            ErrorDetail = detail,
            Hints = hints
        };

    /// <summary>Keeps only the hints whose slot lives in Error. The CommitAsync copy of a stage failure drops ErrorDetail
    /// today, so an ErrorDetail-slot hint must not travel there (it would add a Detail that is null now).</summary>
    private static IReadOnlyList<GitHint>? HintsForErrorOnlyCopy(IReadOnlyList<GitHint>? hints)
    {
        var kept = hints?.Where(h => GitHintKeySlots.Of(h.Key) is GitHintSlot.Inline or GitHintSlot.ErrorReplace).ToList();
        return kept is { Count: > 0 } ? kept : null;
    }

    /// <summary>
    /// Lists the unstaged hunks of exactly one tracked text file (zero-context diff), each with a 1-based id, line
    /// numbers and a short preview, plus a fingerprint of the whole file diff. Read-only: nothing is staged.
    /// </summary>
    public async Task<GitHunksResult> HunksAsync(
        string gitRoot, string? paths, int count, CancellationToken cancellationToken)
    {
        try
        {
            var load = await LoadFileDiffAsync(gitRoot, paths, cancellationToken);
            if (load.Diff is null)
                return HunksFailure(load.Path, load.ErrorKind ?? GitErrorCodes.Fallback, load.Error ?? "", load.ErrorDetail ?? "", load.Hints);

            return BuildHunksResult(load.Path, load.Diff, count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitHunksResult>(ex, "hunks");
        }
    }

    private enum PathClassification
    {
        /// <summary>Not tracked, not in the index, not on disk.</summary>
        Missing,
        /// <summary>Tracked and currently in the index.</summary>
        InIndex,
        /// <summary>Tracked in HEAD but not in the index (deletion staged or about to be staged).</summary>
        HeadOnly,
        /// <summary>Untracked but exists on disk.</summary>
        Untracked
    }

    private record ClassifiedPath(string Path, PathClassification Classification);

    /// <summary>
    /// Validates that a path resolves within the repo root. Returns null if valid, or an error message
    /// naming the path if it resolves outside.
    /// </summary>
    private static string? ValidateRepoRoot(string gitRoot, string path)
    {
        try
        {
            var repoPath = new DirectoryInfo(gitRoot).FullName.TrimEnd(Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(gitRoot, path));
            var normRepoPath = repoPath + Path.DirectorySeparatorChar;

            // Compare: path must start with repo root (with trailing separator) or equal repo root exactly
            if (!fullPath.StartsWith(normRepoPath, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.Equals(repoPath, StringComparison.OrdinalIgnoreCase))
            {
                return $"Path resolves outside the repository root: {path}";
            }

            return null;
        }
        catch
        {
            return $"Invalid path: {path}";
        }
    }

    /// <summary>
    /// Parses the ignored-paths advisory from git stderr when `git add` exits with code 1.
    /// Extracts paths between "The following paths are ignored" and the next hint/error line.
    /// Returns a list of ignored paths, or empty if the pattern is not found.
    /// </summary>
    private static List<string> ParseIgnoredPaths(string stderr)
    {
        var ignoredPaths = new List<string>();
        var lines = stderr.Split('\n');
        var inIgnoredBlock = false;

        foreach (var line in lines)
        {
            if (line.Contains("The following paths are ignored"))
            {
                inIgnoredBlock = true;
                continue;
            }

            if (inIgnoredBlock)
            {
                // The block ends at the first blank or hint line. GitTool prints each ignored path
                // unindented (verified on git 2.55), so any other line here is a path.
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("hint:"))
                    break;

                ignoredPaths.Add(line.Trim());
            }
        }

        return ignoredPaths;
    }

    /// <summary>
    /// Cleans git stderr output by removing noisy CRLF warning lines and capping total length.
    /// Removes lines matching:
    /// - "warning: ... LF will be replaced by CRLF ..."
    /// - "The file will have its original line endings ..."
    /// Caps output at approximately 2000 characters with a "(N more lines omitted)" suffix.
    /// </summary>
    private static string CleanGitStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return stderr;

        // RunGitAsync rebuilds stderr with AppendLine (CRLF on Windows); drop the CR of each line so
        // none survives into the joined message.
        var lines = stderr.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var cleaned = new List<string>();

        foreach (var line in lines)
        {
            // Skip CRLF warning lines
            if (line.Contains("warning:") && line.Contains("LF will be replaced by CRLF"))
                continue;
            if (line.Contains("The file will have its original line endings"))
                continue;

            cleaned.Add(line);
        }

        var result = string.Join("\n", cleaned).Trim();

        // Cap at ~2000 characters
        const int maxChars = 2000;
        if (result.Length > maxChars)
        {
            // Count how many lines we're omitting
            var truncated = result.Substring(0, maxChars);
            var remainingText = result.Substring(maxChars);
            var omittedLines = remainingText.Count(c => c == '\n');
            result = truncated + $"\n({omittedLines} more lines omitted)";
        }

        return result;
    }

    /// <summary>
    /// Classifies a list of paths by their git and filesystem state. Uses git ls-files (index),
    /// git ls-tree (HEAD), and disk existence to determine whether each path is:
    /// - Missing: not tracked, not indexed, not on disk
    /// - InIndex: currently in the staging index
    /// - HeadOnly: tracked in HEAD but not in the current index (deletion candidate)
    /// - Untracked: untracked but exists on disk
    ///
    /// Paths are normalized to forward slashes for git compatibility. Normalizes input from the
    /// caller's repo-relative paths.
    /// </summary>
    private async Task<List<ClassifiedPath>> ClassifyPathsAsync(
        string gitRoot, List<string> filePaths, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0)
            return [];

        // Normalize paths: \ to / for git, and remove any trailing slashes.
        var normalized = filePaths
            .Select(p => p.Replace('\\', '/').TrimEnd('/'))
            .ToList();

        // Index state: the entries matching the paths (git --literal-pathspecs ls-files -z --cached -- <paths>). The typed seam reads the
        // index in-process (libgit2) and runs that git command for what it declines. The reads block, so they run off the caller's thread;
        // the token only stops a call that has not started. A null answer (git failed) is empty, as the non-zero exit was.
        var indexPaths = await Task.Run(() => _gitReader.ListIndexPathsMatching(gitRoot, normalized, GitProcessTimeout), cancellationToken);
        var inIndex = new HashSet<string>(indexPaths ?? [], StringComparer.OrdinalIgnoreCase);

        // HEAD state: the files in HEAD's tree matching the paths (git --literal-pathspecs ls-tree -r -z --name-only HEAD -- <paths>).
        // For an unborn HEAD (no commits) the answer is null, which we treat as empty.
        var headPaths = await Task.Run(() => _gitReader.ListHeadPathsMatching(gitRoot, normalized, GitProcessTimeout), cancellationToken);
        var inHead = new HashSet<string>(headPaths ?? [], StringComparer.OrdinalIgnoreCase);

        // Classify each path
        var result = new List<ClassifiedPath>();
        foreach (var path in normalized)
        {
            var fullPath = Path.Combine(gitRoot, path);
            var fileExists = File.Exists(fullPath);
            var dirExists = Directory.Exists(fullPath);
            var diskExists = fileExists || dirExists;

            // A tracked directory deleted from disk never appears as an entry itself; ls-files and
            // ls-tree list the files beneath it. Match on the "<path>/" prefix, but only when the
            // path is gone from disk: a directory that still exists keeps its previous handling
            // (plain git add, which also picks up new untracked files inside it).
            var prefix = path + "/";
            var inIndexState = inIndex.Contains(path)
                || (!diskExists && inIndex.Any(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            var inHeadState = inHead.Contains(path)
                || (!diskExists && inHead.Any(e => e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

            var classification = (inIndexState, inHeadState, diskExists) switch
            {
                (true, _, _) => PathClassification.InIndex,
                (false, true, false) => PathClassification.HeadOnly,
                (false, false, true) => PathClassification.Untracked,
                _ => PathClassification.Missing
            };

            result.Add(new ClassifiedPath(path, classification));
        }

        return result;
    }

    /// <summary>
    /// Returns those of <paramref name="paths"/> that .gitignore rules would make <c>git add</c>
    /// refuse (git check-ignore, exit 0 = at least one ignored, 1 = none). Checked BEFORE adding
    /// because <c>git add a.txt ignored/b.txt</c> stages a.txt and only then exits 1 with the
    /// advisory, so detecting the advisory afterwards cannot honour "Nothing was staged." Also names
    /// the paths the caller listed, whereas the advisory names the ignored ancestor directory.
    /// </summary>
    private async Task<List<string>> FindIgnoredPathsAsync(
        string gitRoot, List<string> paths, CancellationToken cancellationToken)
    {
        // The typed seam answers in-process (libgit2's ignore rules) where that matches git check-ignore, and runs git check-ignore -q for
        // each path otherwise. It blocks, so it runs off the caller's thread; the token only stops a call that has not started.
        var ignored = await Task.Run(() => _gitReader.FindIgnoredPaths(gitRoot, paths, GitProcessTimeout), cancellationToken);
        return ignored is null ? [] : [.. ignored];
    }

    /// <summary>
    /// Stages files according to <paramref name="scope"/>. Scope and path list are one decision
    /// rather than two independent inputs: the former <c>stageAll</c> boolean could override an
    /// explicit file list and run <c>git add -A</c>, quietly staging unrelated untracked files.
    /// Every mismatch between scope and paths is refused up front, naming the conflict and the
    /// call that would have worked.
    /// </summary>
    public async Task<GitStatusResult> StageAsync(
        string gitRoot, GitStageScope scope, string? paths, CancellationToken cancellationToken)
    {
        var hasPaths = !string.IsNullOrWhiteSpace(paths);

        if (scope != GitStageScope.listed && hasPaths)
        {
            return new GitStatusResult
            {
                IsError = true,
                Error = $"You named files to stage but passed scope=\"{scope}\", which ignores them. " +
                        "Pass scope=\"listed\" to stage exactly the files you named (untracked ones " +
                        $"included), or drop the file list to stage by scope=\"{scope}\". Nothing was staged."
            };
        }

        if (scope == GitStageScope.listed && !hasPaths)
        {
            return new GitStatusResult
            {
                IsError = true,
                Error = "scope=\"listed\" stages exactly the files you name, but no files were given. " +
                        "Pass paths as a comma-separated list of repo-relative paths, e.g. " +
                        "paths: \"src/Foo.cs,docs/notes.md\". To stage without naming files use " +
                        "scope=\"tracked\" (already-tracked changes only) or scope=\"all\" (everything, " +
                        "untracked included). Nothing was staged."
            };
        }

        try
        {
            switch (scope)
            {
                case GitStageScope.all:
                    break;
                case GitStageScope.listed:
                    // For scope=listed, classify the paths first to handle deletions (HeadOnly)
                    // and ignored files (Untracked under .gitignore) correctly.
                    var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                    if (pathsError != null)
                        return new GitStatusResult { IsError = true, Error = pathsError };

                    // Validate that paths stay within repo
                    foreach (var p in filePaths!)
                    {
                        var error = ValidateRepoRoot(gitRoot, p);
                        if (error != null)
                        {
                            return new GitStatusResult
                            {
                                IsError = true,
                                ErrorKind = GitErrorCodes.PathNotFound,
                                Hints = [new GitHint(GitHintKey.PathNotFound)],
                                Error = $"{error}. Nothing was staged."
                            };
                        }
                    }

                    // Classify paths
                    var classified = await ClassifyPathsAsync(gitRoot, filePaths!.ToList(), cancellationToken);

                    // Check for missing paths
                    var missing = classified.Where(cp => cp.Classification == PathClassification.Missing).ToList();
                    if (missing.Count > 0)
                    {
                        var missingList = string.Join(", ", missing.Select(m => $"\"{m.Path}\""));
                        return new GitStatusResult
                        {
                            IsError = true,
                            ErrorKind = GitErrorCodes.PathNotFound,
                            Hints = [new GitHint(GitHintKey.PathNotFound)],
                            Error = $"The following paths are not tracked and do not exist on disk: {missingList}. Nothing was staged."
                        };
                    }

                    // Separate paths by classification for staged staging
                    var inIndex = classified.Where(cp => cp.Classification == PathClassification.InIndex).Select(cp => cp.Path).ToList();
                    var headOnly = classified.Where(cp => cp.Classification == PathClassification.HeadOnly).Select(cp => cp.Path).ToList();
                    var untracked = classified.Where(cp => cp.Classification == PathClassification.Untracked).Select(cp => cp.Path).ToList();

                    // Stage InIndex paths using git --literal-pathspecs add -u -- <paths>
                    if (inIndex.Count > 0)
                    {
                        var addURaw = await RunIndexStageAsync(gitRoot, IndexStageMode.UpdateListed, inIndex, cancellationToken);
                        if (addURaw.ExitCode != 0)
                        {
                            return new GitStatusResult
                            {
                                IsError = true,
                                Error = $"git add -u failed: {CleanGitStderr(addURaw.Stderr)}"
                            };
                        }
                    }

                    // Stage HeadOnly paths - these are deletions already staged or staged here via the pathspec
                    // We skip them and report them as already staged (no action needed)
                    // They will be committed if included in the pathspec

                    // Stage Untracked paths using plain git --literal-pathspecs add -- <paths>
                    if (untracked.Count > 0)
                    {
                        // Refuse before mutating anything: git add stages the non-ignored paths of a
                        // mixed list and only then exits 1, so a post-hoc check cannot promise atomicity.
                        var ignoredListed = await FindIgnoredPathsAsync(gitRoot, untracked, cancellationToken);
                        if (ignoredListed.Count > 0)
                        {
                            var ignoredNames = string.Join(", ", ignoredListed.Select(p => $"\"{p}\""));
                            return new GitStatusResult
                            {
                                IsError = true,
                                ErrorKind = GitErrorCodes.IgnoredPath,
                                Hints = [new GitHint(GitHintKey.IgnoredPath)],
                                Error = $"The following paths are ignored by .gitignore: {ignoredNames}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to stage them. Nothing was staged."
                            };
                        }

                        var addRaw = await RunIndexStageAsync(gitRoot, IndexStageMode.AddListed, untracked, cancellationToken);

                        // Backstop: check for ignored-path advisory in stderr
                        if (addRaw.ExitCode != 0 && addRaw.Stderr.Contains("The following paths are ignored"))
                        {
                            var ignoredPaths = ParseIgnoredPaths(addRaw.Stderr);
                            if (ignoredPaths.Count > 0)
                            {
                                var ignoredList = string.Join(", ", ignoredPaths.Select(p => $"\"{p}\""));
                                return new GitStatusResult
                                {
                                    IsError = true,
                                    ErrorKind = GitErrorCodes.IgnoredPath,
                                    Hints = [new GitHint(GitHintKey.IgnoredPath)],
                                    Error = $"The following paths are ignored by .gitignore: {ignoredList}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to stage them. Nothing was staged."
                                };
                            }
                        }

                        if (addRaw.ExitCode != 0)
                        {
                            return new GitStatusResult
                            {
                                IsError = true,
                                Error = $"git add failed: {CleanGitStderr(addRaw.Stderr)}"
                            };
                        }
                    }

                    return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
                case GitStageScope.tracked:
                default:
                    var trackedRaw = await RunIndexStageAsync(gitRoot, IndexStageMode.UpdateTracked, [], cancellationToken);
                    if (trackedRaw.ExitCode != 0)
                        return new GitStatusResult { IsError = true, Error = $"git add failed: {CleanGitStderr(trackedRaw.Stderr)}" };
                    return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
            }

            var stageRaw = await RunIndexStageAsync(gitRoot, IndexStageMode.AddAll, [], cancellationToken);
            if (stageRaw.ExitCode != 0)
                return new GitStatusResult { IsError = true, Error = $"git add failed: {CleanGitStderr(stageRaw.Stderr)}" };

            return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStatusResult>(ex, "stage");
        }
    }
    /// <summary>
    /// Removes files from the index (<c>git reset</c>), leaving the working tree untouched. The
    /// tool could previously stage but never un-stage, so any mis-stage forced a shell fallback ->
    /// a state the tool could create but not exit. With no paths this resets the whole index; with
    /// paths it un-stages only those.
    /// </summary>
    public async Task<GitStatusResult> UnstageAsync(
        string gitRoot, string? paths, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> resetPaths;
            if (string.IsNullOrWhiteSpace(paths))
            {
                // Whole index. Deliberately a plain `git reset` (mixed, no ref): it un-stages
                // everything while leaving every working-tree edit on disk, so this can never
                // destroy uncommitted work the way `reset --hard` would.
                resetPaths = [];
            }
            else
            {
                var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitStatusResult { IsError = true, Error = pathsError };
                resetPaths = filePaths!;
            }

            var resetRaw = await RunIndexUnstageAsync(gitRoot, resetPaths, cancellationToken);

            // `git reset` exits 1 when the index still differs from HEAD after the reset, which is
            // the normal outcome here, not a failure. Treat stderr content as the real signal
            // rather than trusting the exit code alone.
            var resetErrText = CleanGitStderr(resetRaw.Stderr);
            if (resetRaw.ExitCode != 0 && resetErrText.Length > 0)
                return new GitStatusResult { IsError = true, Error = $"git reset failed: {resetErrText}" };

            return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStatusResult>(ex, "unstage");
        }
    }
    public async Task<GitCommitResult> CommitAsync(
        string gitRoot, string? message, GitStageScope? scope, string? paths, bool amend, CancellationToken cancellationToken)
    {
        // Amend keeps HEAD's message when none is supplied (git commit --amend --no-edit);
        // a plain commit has no prior message to fall back on, so it stays required.
        if (string.IsNullOrWhiteSpace(message) && !amend)
        {
            return new GitCommitResult
            {
                IsError = true,
                Error = "message is required for operation=commit. Pass message: \"<what this commit does>\". Nothing was committed."
            };
        }

        try
        {
            // Refuse before anything is staged or rewritten. Force-push is not exposed, so amending
            // a commit the upstream already has would leave local and remote diverged with no way
            // to reconcile them through this tool. No upstream configured means nothing is known
            // to be published, so the amend is allowed.
            if (amend)
            {
                // One seam read for both git calls (rev-parse @{upstream}, then merge-base --is-ancestor HEAD @{upstream}): the upstream's name when HEAD is the upstream tip or an
                // ancestor of it = already pushed; null for no upstream, an upstream that does not contain HEAD, or a failed read (all of which allow the amend, as before).
                var containingUpstream = await Task.Run(() => _gitReader.GetUpstreamContainingHead(gitRoot, GitProcessTimeout), cancellationToken);
                if (containingUpstream is not null)
                {
                    return new GitCommitResult
                    {
                        IsError = true,
                        Error = $"HEAD is already contained in its upstream '{containingUpstream}', so amending it would rewrite published history, " +
                                "and this tool does not expose force-push (the branch would diverge from the remote). " +
                                "Make a new commit instead, or use operation=revert to undo the pushed commit. Nothing was amended."
                    };
                }
            }

            // For scope=listed, classify paths first to determine which ones need pre-staging
            // (untracked only) vs those already in the index.
            bool stagedUntrackedFiles = false;
            // Kept for the in-process commit route below (null unless scope=listed with paths).
            string[]? listedFilePaths = null;
            List<ClassifiedPath>? classifiedListed = null;
            if (scope == GitStageScope.listed && !string.IsNullOrWhiteSpace(paths))
            {
                var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out var pathsError);
                if (pathsError != null)
                    return new GitCommitResult { IsError = true, Error = pathsError };

                // Validate that paths stay within repo
                foreach (var p in filePaths!)
                {
                    var error = ValidateRepoRoot(gitRoot, p);
                    if (error != null)
                    {
                        return new GitCommitResult
                        {
                            IsError = true,
                            ErrorKind = GitErrorCodes.PathNotFound,
                            Hints = [new GitHint(GitHintKey.PathNotFound)],
                            Error = $"{error}. Nothing was committed."
                        };
                    }
                }

                // Classify paths
                var classified = await ClassifyPathsAsync(gitRoot, filePaths!.ToList(), cancellationToken);
                listedFilePaths = filePaths;
                classifiedListed = classified;

                // Check for missing paths - reject early
                var missing = classified.Where(cp => cp.Classification == PathClassification.Missing).ToList();
                if (missing.Count > 0)
                {
                    var missingList = string.Join(", ", missing.Select(m => $"\"{m.Path}\""));
                    return new GitCommitResult
                    {
                        IsError = true,
                        ErrorKind = GitErrorCodes.PathNotFound,
                        Hints = [new GitHint(GitHintKey.PathNotFound)],
                        Error = $"The following paths are not tracked and do not exist on disk: {missingList}. Nothing was committed."
                    };
                }

                // Pre-stage only Untracked paths (they must be in the index for the pathspec to match)
                var untracked = classified.Where(cp => cp.Classification == PathClassification.Untracked).Select(cp => cp.Path).ToList();
                if (untracked.Count > 0)
                {
                    // Refuse before mutating anything (see StageAsync): a mixed list would otherwise
                    // have its non-ignored paths staged before git add exits 1.
                    var ignoredListed = await FindIgnoredPathsAsync(gitRoot, untracked, cancellationToken);
                    if (ignoredListed.Count > 0)
                    {
                        var ignoredNames = string.Join(", ", ignoredListed.Select(p => $"\"{p}\""));
                        return new GitCommitResult
                        {
                            IsError = true,
                            ErrorKind = GitErrorCodes.IgnoredPath,
                            Hints = [new GitHint(GitHintKey.IgnoredPath)],
                            Error = $"The following paths are ignored by .gitignore: {ignoredNames}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to commit them. Nothing was committed."
                        };
                    }

                    // The same whole-file stage StageAsync uses (git --literal-pathspecs add -- paths): libgit2 in process for ordinary text files, exactly that
                    // git command for everything libgit2 declines, so the result and every error text below are what the CLI gave.
                    var addRaw = await RunIndexStageAsync(gitRoot, IndexStageMode.AddListed, untracked, cancellationToken);

                    // Backstop: check for ignored-path advisory
                    if (addRaw.ExitCode != 0 && addRaw.Stderr.Contains("The following paths are ignored"))
                    {
                        var ignoredPaths = ParseIgnoredPaths(addRaw.Stderr);
                        if (ignoredPaths.Count > 0)
                        {
                            var ignoredList = string.Join(", ", ignoredPaths.Select(p => $"\"{p}\""));
                            return new GitCommitResult
                            {
                                IsError = true,
                                ErrorKind = GitErrorCodes.IgnoredPath,
                                Hints = [new GitHint(GitHintKey.IgnoredPath)],
                                Error = $"The following paths are ignored by .gitignore: {ignoredList}. The tool has no -f flag to force-add them. Remove them from the list or un-ignore them in .gitignore to commit them. Nothing was committed."
                            };
                        }
                    }

                    if (addRaw.ExitCode != 0)
                    {
                        var addFailed = new GitCommitResult
                        {
                            IsError = true,
                            Error = $"git add failed: {CleanGitStderr(addRaw.Stderr)}"
                        };
                        var addLockPath = await FindForeignIndexLockAsync(gitRoot, addRaw.ExitCode, cancellationToken);
                        if (addLockPath is not null)
                        {
                            addFailed.ErrorKind = GitErrorCodes.IndexLocked;
                            addFailed.Hints = [new GitHint(GitHintKey.IndexLocked, new GitHintFacts { LockPath = addLockPath })];
                        }
                        return addFailed;
                    }

                    stagedUntrackedFiles = true;
                }
            }
            else if (scope is { } explicitScope && scope != GitStageScope.listed)
            {
                // For scope=all or scope=tracked, pre-stage as requested
                var stageResult = await StageAsync(gitRoot, explicitScope, paths, cancellationToken);
                if (stageResult.IsError)
                    return new GitCommitResult { IsError = true, Error = stageResult.Error, Hints = HintsForErrorOnlyCopy(stageResult.Hints) };
            }

            // Default on (git config roslynsentinel.inProcessCommit=false turns it off): a plain new commit made in process through the compare-and-swap primitive, with no git process.
            // Null (the route is off, or it declined and counted why) continues below with exactly the CLI path of before.
            if (await TryCommitInProcessAsync(gitRoot, message, amend, scope, paths, listedFilePaths, classifiedListed, cancellationToken) is { } inProcessResult)
                return inProcessResult;

            // BuildTool commit arguments
            var messageArgs = (amend, HasMessage: !string.IsNullOrWhiteSpace(message)) switch
            {
                (amend: true, HasMessage: true) => new[] { "--amend", "-m", message! },
                (amend: true, HasMessage: false) => new[] { "--amend", "--no-edit" },
                _ => new[] { "-m", message! }
            };

            // configPrefix (git -c options) is empty unless commit trailers are on.
            string[] BuildCommitArgs(string[] msgArgs, string[] configPrefix)
            {
                if (!string.IsNullOrWhiteSpace(paths))
                {
                    // When specific files were named, restrict the commit to those paths via --only
                    var filePaths = DelimitedListParser.ParseStringOrJsonArrayToList(paths, out _);
                    if (filePaths != null && filePaths.Length > 0)
                    {
                        var commitBase = configPrefix.Concat(new[] { "--literal-pathspecs", "commit" }).Concat(msgArgs).Concat(new[] { "--only", "--" }).ToArray();
                        return commitBase.Concat(filePaths).ToArray();
                    }
                }

                return configPrefix.Concat(new[] { "commit" }).Concat(msgArgs).ToArray();
            }

            // Read BEFORE the commit: DeriveNewCommitHashAsync's fallback proves a HEAD is ours by its parent chain.
            var headBefore = await ReadHeadWithParentsAsync(gitRoot, cancellationToken);
            // A commit that concludes a merge has MERGE_HEAD as its second parent (IsOwnHead's fallback needs to expect it).
            var mergeHeadBefore = amend ? null : await ReadMergeHeadAsync(gitRoot, cancellationToken);

            // Opt-in (git config roslynsentinel.commitTrailers=true): Tool-Call-Id / Tool-Agent trailers, so the commit can be found by id.
            // Off (the default) passes exactly the arguments of before.
            var trailerPlan = await PlanCommitTrailersAsync(gitRoot, messageArgs, amend, !string.IsNullOrWhiteSpace(message), cancellationToken);
            var commitRaw = await RunGitAsync(gitRoot,
                trailerPlan is { } firstPlan ? BuildCommitArgs(firstPlan.MessageArgs, GitCommitTrailers.ReplaceConfigArgs) : BuildCommitArgs(messageArgs, []),
                cancellationToken);
            if (trailerPlan is not null && GitCommitTrailers.IsTrailerOptionUnsupported(commitRaw.ExitCode, commitRaw.Stderr))
            {
                // `git commit --trailer` needs git 2.32. Older git fails while parsing options, before anything is committed, so the retry
                // is safe. Decision: commit without the trailers rather than fail (they are an aid, and the summary-line derivation
                // below still identifies the commit); the log records it.
                _logger.LogInformation("git commit does not support --trailer (git older than 2.32); committing without Tool-Call-Id/Tool-Agent trailers.");
                trailerPlan = null;
                commitRaw = await RunGitAsync(gitRoot, BuildCommitArgs(messageArgs, []), cancellationToken);
            }
            if (commitRaw.ExitCode != 0)
            {
                var detail = string.Join("\n", new[] { commitRaw.Stdout.Trim(), CleanGitStderr(commitRaw.Stderr) }
                    .Where(s => !string.IsNullOrEmpty(s)));
                var errorText = string.IsNullOrEmpty(detail)
                    ? $"git commit exited with code {commitRaw.ExitCode}"
                    : detail;

                // If we staged untracked files and the commit failed, provide recovery info
                string failureNote = "";
                if (stagedUntrackedFiles)
                {
                    // The staged file list through the seam (one entry per path, as the old `diff --cached --name-only` printed); a failed read counts 0, as before.
                    var stagedCount = (await Task.Run(() => _gitReader.GetNameStatus(gitRoot, "staged", null, GitProcessTimeout), cancellationToken))?.Count ?? 0;
                    failureNote = $" Staging already ran; the index now holds {stagedCount} staged paths. Call {GitHintTokens.Inline(GitHintKey.GitUnstageCall)} to undo.";
                }

                var failed = new GitCommitResult { IsError = true, Error = $"git commit failed: {errorText}{failureNote}" };

                // Classify structurally, not from git's localizable text. Amend is skipped (an amend
                // with nothing staged is legal), as is a path list that was not classified up front
                // (an unknown pathspec would otherwise also look like "nothing staged").
                var pathsWereClassified = string.IsNullOrWhiteSpace(paths) || scope == GitStageScope.listed;
                var nothingStaged = !amend && pathsWereClassified && await NothingStagedAsync(gitRoot, paths, cancellationToken);

                // Tested first: a held lock makes both the pre-commit staging and git commit itself fail with exit 128, while the nothing-staged probe reads the index without the lock and can see it empty, so NothingToCommit would mask the real cause.
                var indexLockPath = await FindForeignIndexLockAsync(gitRoot, commitRaw.ExitCode, cancellationToken);
                if (indexLockPath is not null)
                {
                    failed.ErrorKind = GitErrorCodes.IndexLocked;
                    failed.Hints = [new GitHint(GitHintKey.IndexLocked, new GitHintFacts { LockPath = indexLockPath })];
                }
                // Hook rejection, tested before the NothingToCommit fallback (an empty index plus a rejecting pre-commit hook would
                // otherwise send the caller to stage). git prints nothing of its own when a hook declines, so the signal is structural
                // first: exit 1 (git's die() paths such as no identity or a locked index exit 128, a declining hook makes commit exit 1)
                // and a blocking hook exists (GitHooks.HasBlockingHook, the helper the in-process route uses to decline). The one
                // ambiguity is an empty index with a pre-commit hook: git runs pre-commit before its emptiness check, so exit 1 is either
                // "hook declined" or "hook passed, nothing to commit"; there git's English status text ("nothing to commit", "no changes
                // added to commit", "nothing added to commit") is the tie-break, and a localised git without that text reads as a rejection
                // (the error text still carries git's output). A commit-msg hook never runs on an empty index, so there the text is always present.
                else if (commitRaw.ExitCode == 1
                    && GitHooks.HasBlockingHook(gitRoot).Outcome == BlockingHookOutcome.Found
                    && !(nothingStaged && IsGitNothingToCommitText(commitRaw.Stdout, commitRaw.Stderr)))
                {
                    failed.ErrorKind = GitErrorCodes.HookRejected;
                    failed.Hints = [new GitHint(GitHintKey.HookRejected)];
                }
                else if (nothingStaged)
                {
                    failed.ErrorKind = GitErrorCodes.NothingToCommit;
                    failed.Hints = [new GitHint(GitHintKey.NothingToCommit)];
                }

                // failureNote (set only when stagedUntrackedFiles) carries the unstage call as an Inline token.
                if (stagedUntrackedFiles)
                    failed = WithHint(failed, new GitHint(GitHintKey.GitUnstageCall));

                return await AppendInProgressAsync(failed, gitRoot, cancellationToken);
            }

            // Never `rev-parse HEAD`: another server instance sharing this working tree can commit between the
            // git commit above and any readback, so HEAD may already be that other commit. The hash comes from the
            // commit's own summary line (see DeriveNewCommitHashAsync); empty means it could not be established.
            // With trailers on, the new commit is the one in the last few HEAD ancestors that carries this call's id: exact even when
            // other commits landed meanwhile. Not found (a hook rewrote the message, more than LookupDepth commits landed, the lookup
            // failed) falls through to the summary-line derivation, whose own fallback is the parent-chain proof.
            var hash = trailerPlan is { } usedPlan
                ? await Task.Run(() => _gitReader.FindCommitByTrailer(gitRoot, GitCommitTrailers.ToolCallIdKey, usedPlan.CallId, GitCommitTrailers.LookupDepth, GitProcessTimeout), cancellationToken) ?? ""
                : "";
            if (hash.Length == 0)
                hash = await DeriveNewCommitHashAsync(gitRoot, commitRaw.Stdout, headBefore, amend, mergeHeadBefore, cancellationToken);

            // Sanity check, not a correctness fix: a SHA-1 hash is always exactly 40 hex
            // characters. This guards against a suspected (unconfirmed, see
            // docs/current/findings/closed/finding_git_commit_commithash_length_unconfirmed_no_source_mechanism.md)
            // response-length anomaly reported across multiple sessions - if this ever fires, it
            // proves the corruption happens at or before this point rather than in a later
            // serialization/transport layer, which the original investigation could not determine.
            if (hash.Length > 0 && (hash.Length != 40 || !hash.All(Uri.IsHexDigit)))
            {
                _logger.LogWarning(
                    "The derived commit hash is not a well-formed 40-character SHA-1 hash: " +
                    "length={Length}, value={Hash}. Returning it to the caller as-is for visibility.",
                    hash.Length, hash);
            }

            // message can be null here (amend --no-edit kept HEAD's existing message), so read
            // back the commit's actual message rather than echoing the (possibly absent) input.
            // Read by the explicit hash, not implicit HEAD (same race). With no hash the message is unknown for an
            // amend that kept it, and the submitted text otherwise.
            var finalMessage = message ?? "";
            if (hash.Length > 0)
            {
                // The raw %B text through the seam; RejoinLines keeps the line endings RunGitAsync always gave this text (CRLF on Windows), then the same Trim.
                var committedMessage = await Task.Run(() => _gitReader.GetCommitMessage(gitRoot, hash, GitProcessTimeout), cancellationToken);
                if (committedMessage is not null)
                    finalMessage = RejoinLines(committedMessage).Trim();
            }

            // Populate RemainingStaged: list of paths still in the index after the commit.
            // Known limit: this reads the shared index, so a concurrent session staging or committing in the same
            // working tree can add or remove paths here. It cannot be made exact without a repo-wide lock, so it is
            // advisory (what the index held when this readback ran), unlike CommitHash/Message which are race-free.
            // core.quotePath=false so non-ASCII paths come back verbatim and can be passed to `paths`.
            // Through the seam: the staged name-status list is what `diff --cached --name-only` printed plus a status letter, and a rename lists its new path as Path, like name-only.
            // A failed read leaves the list empty, as before.
            var remainingStaged = (await Task.Run(() => _gitReader.GetNameStatus(gitRoot, "staged", null, GitProcessTimeout), cancellationToken))
                ?.Select(entry => entry.Path).ToList() ?? [];

            return new GitCommitResult { IsError = false, CommitHash = hash, Message = finalMessage, RemainingStaged = remainingStaged };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitCommitResult>(ex, "commit");
        }
    }

    private async Task<string?> FindForeignIndexLockAsync(string gitRoot, int exitCode, CancellationToken cancellationToken)
    {
        // Exit 128 is git's die() path for a held lock; the lock file lives in the absolute git dir.
        var gitDir = exitCode == 128 ? await RunGitAsync(gitRoot, ["rev-parse", "--absolute-git-dir"], cancellationToken) : null;
        var indexLockPath = gitDir is { ExitCode: 0 } ? Path.Combine(gitDir.Stdout.Trim(), "index.lock") : null;
        return indexLockPath is not null && File.Exists(indexLockPath) ? indexLockPath : null;
    }

    /// <summary>
    /// The in-process commit route of <see cref="CommitAsync"/> (git config <c>roslynsentinel.inProcessCommit</c>, default on; see <see cref="InProcessCommitRoute"/>): returns the finished result when the commit was made
    /// in process, null for "continue with the CLI path exactly as before" (the route is off, or it declined: counted with its reason on the route). Simple case only: a plain new commit (an amend is a counted decline) of the
    /// whole index, or of listed paths (<c>scope=listed</c>). The listed paths' content is staged FIRST, tracked ones here (<c>git add -u</c>, in process where libgit2 can) and untracked ones by the pre-stage
    /// CommitAsync already ran, so HEAD plus those index entries is what <c>git commit --only -- paths</c> would store; entries of other paths stay untouched, as with --only. Every decline decidable without
    /// touching the repository runs before that staging; the primitive's own declines come after it, and then the CLI's <c>--only</c> commit stores the same content from the working tree. After a commit the hash is the
    /// primitive's (no derivation), the message is the text stored, and the remaining-staged list comes from the same seam read as the CLI path. Never throws except for cancellation.
    /// </summary>
    private async Task<GitCommitResult?> TryCommitInProcessAsync(
        string gitRoot, string? message, bool amend, GitStageScope? scope, string? paths, string[]? listedFilePaths, List<ClassifiedPath>? classified, CancellationToken cancellationToken)
    {
        CommitRouteContext? context = null;
        (CasCommitResult Outcome, string? Stored)? madeCommit = null;
        try
        {
            if (!await Task.Run(() => _commitRoute.IsEnabled(_gitReader, gitRoot, GitProcessTimeout), cancellationToken))
            {
                return null;
            }

            // From here the route is enabled: every way out that is not a commit is a counted decline.
            var kind = amend ? "amend" : !string.IsNullOrWhiteSpace(paths) ? "only-paths" : "plain";
            context = new CommitRouteContext(kind, listedFilePaths);
            // TODO the condition is amend || IsNullOrWhiteSpace(message), but a blank message without amend is refused at the top of CommitAsync, so only amend reaches this branch; keep the reason text (the Step 5 audit script catalogue reads it)
            if (amend || string.IsNullOrWhiteSpace(message))
            {
                _commitRoute.RecordDecline("an amend rewrites HEAD; the CLI does it.", gitRoot, context);
                return null;
            }

            var listed = !string.IsNullOrWhiteSpace(paths);
            if (listed && (scope != GitStageScope.listed || listedFilePaths is not { Length: > 0 } || classified is null))
            {
                _commitRoute.RecordDecline("paths were given without scope=listed; the CLI decides what they mean.", gitRoot, context);
                return null;
            }

            var listedForRoute = listed ? listedFilePaths : null;
            var decline = await Task.Run(() => _commitRoute.DeclineBeforeStaging(_gitReader, gitRoot, listedForRoute, GitProcessTimeout), cancellationToken);
            if (decline is not null)
            {
                _commitRoute.RecordDecline(decline, gitRoot, context);
                return null;
            }

            if (listed)
            {
                var tracked = classified!.Where(cp => cp.Classification == PathClassification.InIndex).Select(cp => cp.Path).ToList();
                if (tracked.Count > 0)
                {
                    var staged = await RunIndexStageAsync(gitRoot, IndexStageMode.UpdateListed, tracked, cancellationToken);
                    if (staged.ExitCode != 0)
                    {
                        _commitRoute.RecordDecline("staging the listed tracked paths failed; the CLI commits them with --only.", gitRoot, context);
                        return null;
                    }
                }
            }

            // The same trailer decision as the CLI path (roslynsentinel.commitTrailers, the call id the response echoes or a random one); null call id means cleanup only.
            var trailerPlan = await PlanCommitTrailersAsync(gitRoot, ["-m", message], amend: false, hasMessage: true, cancellationToken);
            var agentId = ToolCallContext.Current?.AgentId;
            var committed = await Task.Run(
                () =>
                {
                    var outcome = _commitRoute.Commit(gitRoot, message, trailerPlan?.CallId, agentId, listedForRoute, out var storedMessage);
                    return (Outcome: outcome, Stored: storedMessage);
                },
                cancellationToken);
            if (!committed.Outcome.IsCommitted)
            {
                return null;
            }
            madeCommit = committed;

            // The commit exists: nothing below may fall back to the CLI (it would make a second commit) or fail the call.
            var remainingStaged = new List<string>();
            try
            {
                remainingStaged = (await Task.Run(() => _gitReader.GetNameStatus(gitRoot, "staged", null, GitProcessTimeout), CancellationToken.None))
                    ?.Select(entry => entry.Path).ToList() ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not list the paths still staged after the in-process commit; reporting none.");
            }

            return new GitCommitResult
            {
                IsError = false,
                CommitHash = committed.Outcome.CommitId!,
                Message = RejoinLines(committed.Stored ?? message).Trim(),
                RemainingStaged = remainingStaged,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Once the commit exists the CLI path would make a second commit, so nothing after it may decline or return null.
            if (madeCommit is { } made)
            {
                return new GitCommitResult
                {
                    IsError = false,
                    CommitHash = made.Outcome.CommitId!,
                    Message = RejoinLines(made.Stored ?? message ?? string.Empty).Trim(),
                    RemainingStaged = [],
                };
            }

            _commitRoute.RecordDecline($"{ex.GetType().Name}: {ex.Message}", gitRoot, context);
            return null;
        }
    }

    /// <summary>
    /// Backs out of whichever multi-step operation a conflict left half-finished (merge, rebase,
    /// cherry-pick, revert or am) by running its own <c>--abort</c>, which restores the pre-operation
    /// state. Nothing in progress is an error, not a silent success, so a caller never believes a
    /// back-out happened when it did not. Returns the status after the abort.
    /// </summary>
    public async Task<GitStatusResult> AbortAsync(string gitRoot, CancellationToken cancellationToken)
    {
        try
        {
            var op = await DetectInProgressAsync(gitRoot, cancellationToken);
            if (op is null)
            {
                return new GitStatusResult
                {
                    IsError = true,
                    Error = "Nothing to abort: no merge, rebase, cherry-pick, revert or am is in progress. Call status to see the current state. Nothing was changed."
                };
            }

            var abortRaw = await RunGitAsync(gitRoot, [op, "--abort"], cancellationToken);
            if (abortRaw.ExitCode != 0)
            {
                var detail = string.Join("\n", new[] { abortRaw.Stdout.Trim(), CleanGitStderr(abortRaw.Stderr) }.Where(s => s.Length > 0));
                return new GitStatusResult { IsError = true, Error = $"git {op} --abort failed: {detail} The repository is still mid-{op}." };
            }

            var status = await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
            status.AbortedOperation = op;
            return status;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStatusResult>(ex, "abort");
        }
    }

    public async Task<GitRevertResult> RevertAsync(
        string gitRoot, string? commitHash, bool noCommit, int? mainline, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(commitHash))
            return new GitRevertResult { IsError = true, ErrorKind = GitErrorCodes.RefRequired, Hints = [new GitHint(GitHintKey.RefRequiredRevert)], Error = "ref is required for operation=revert." };

        if (mainline is < 1)
            return new GitRevertResult { IsError = true, Error = $"mainline must be 1 or greater (got {mainline}); 1 is the parent that was merged into. Nothing was reverted." };

        try
        {
            // Count the commit's parents up front so a merge commit gets an error that names the
            // `mainline` parameter, rather than git's "commit X is a merge but no -m option was given".
            var parentsRaw = await RunGitAsync(gitRoot, ["rev-list", "--parents", "-n", "1", commitHash, "--"], cancellationToken);
            if (parentsRaw.ExitCode != 0)
                return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"Cannot resolve ref '{commitHash}': {CleanGitStderr(parentsRaw.Stderr)} Nothing was reverted." };

            var parentCount = parentsRaw.Stdout.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length - 1;
            if (parentCount > 1 && mainline is null)
                return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"Commit '{commitHash}' is a merge commit with {parentCount} parents. Pass mainline: 1 (the branch merged into; almost always right) to {parentCount} to say which parent to keep. Nothing was reverted." };
            if (mainline is { } requestedMainline)
            {
                if (parentCount <= 1)
                    return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"mainline was supplied but commit '{commitHash}' is not a merge commit. Omit mainline. Nothing was reverted." };
                if (requestedMainline > parentCount)
                    return new GitRevertResult { IsError = true, CommitHash = commitHash, Error = $"mainline {requestedMainline} is out of range: commit '{commitHash}' has {parentCount} parents. Nothing was reverted." };
            }

            var args = new List<string> { "revert", "--no-edit" };
            if (noCommit)
                args.Add("--no-commit");
            if (mainline is { } parentNumber)
            {
                args.Add("-m");
                args.Add(parentNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            args.Add(commitHash);

            // Read BEFORE the revert: DeriveNewCommitHashAsync's fallback proves a HEAD is ours by its parent chain.
            var headBefore = noCommit ? null : await ReadHeadWithParentsAsync(gitRoot, cancellationToken);

            // Opt-in (git config roslynsentinel.commitTrailers=true): git revert has no --trailer, so the revert is staged with
            // --no-commit and committed with them (RevertWithTrailersAsync, which falls back to the single call below wherever the
            // two steps could differ). Off, or noCommit, is exactly the single `git revert` of before.
            // --no-verify here MIRRORS git revert's own hook behaviour (a plain git revert never runs pre-commit or commit-msg, so the
            // two-step path must not either); it is not a policy skip of hooks.
            var trailerPlan = noCommit ? null : await PlanCommitTrailersAsync(gitRoot, ["--no-edit", "--no-verify"], amend: false, hasMessage: true, cancellationToken);
            string? trailerCallId = null;
            GitRawResult revertRaw;
            if (trailerPlan is { } plan)
            {
                var noCommitArgs = new List<string>(args);
                noCommitArgs.Insert(2, "--no-commit");
                (revertRaw, trailerCallId) = await RevertWithTrailersAsync(gitRoot, [.. args], [.. noCommitArgs], plan, headBefore, cancellationToken);
            }
            else
            {
                revertRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            }

            if (revertRaw.ExitCode != 0)
                return await AppendInProgressAsync(new GitRevertResult { IsError = true, CommitHash = commitHash, Error = CleanGitStderr(revertRaw.Stderr) }, gitRoot, cancellationToken);

            string newHash = "";
            if (!noCommit)
            {
                // Never `rev-parse HEAD` (another session may have committed since); an empty hash means it could not be
                // established, see DeriveNewCommitHashAsync.
                // With trailers committed, the commit is the one carrying this call's id; not found falls back to the derivation.
                if (trailerCallId is not null)
                {
                    newHash = await Task.Run(() => _gitReader.FindCommitByTrailer(gitRoot, GitCommitTrailers.ToolCallIdKey, trailerCallId, GitCommitTrailers.LookupDepth, GitProcessTimeout), cancellationToken) ?? "";
                }

                if (newHash.Length == 0)
                    newHash = await DeriveNewCommitHashAsync(gitRoot, revertRaw.Stdout, headBefore, amend: false, cancellationToken);
            }

            return new GitRevertResult
            {
                IsError = false,
                CommitHash = newHash,
                Message = noCommit ? "Revert staged - call commit to finalise." : revertRaw.Stdout.Trim(),
                PendingCommit = noCommit,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitRevertResult>(ex, "revert");
        }
    }

    // The "[<branch> <abbrev>] <subject>" summary line `git commit` / `git revert` print on stdout for the commit they
    // just created: "[master 1a2b3c4]", "[master (root-commit) 1a2b3c4]", "[detached HEAD 1a2b3c4]". Everything before the
    // abbreviation is free text (translated "(root-commit)", a branch name that may itself contain ']'), so the match is anchored on
    // the END of the bracket: the earliest " <4-40 lowercase hex>]" that is followed by a space or the end of the line. A branch
    // name has no space, so the earliest such spot is git's own; a subject that itself contains "[abc1234]" comes after it.
    private static readonly System.Text.RegularExpressions.Regex CommitSummaryLine = new(
        @"^\[[^\r\n]+? ([0-9a-f]{4,40})\](?=[ \t\r\n]|$)",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// HEAD's full hash followed by its parents' full hashes (one <c>rev-list --parents</c> call, so the pair is
    /// consistent), or null when HEAD does not resolve (unborn branch or git failure).
    /// </summary>
    private async Task<string[]?> ReadHeadWithParentsAsync(string gitRoot, CancellationToken cancellationToken)
    {
        // Through the reader seam (libgit2 in process, the CLI's `rev-list --parents -n 1 HEAD --` when it declines or fails). A null is an unborn HEAD or a failed read, as before.
        var head = await Task.Run(() => _gitReader.GetHeadWithParents(gitRoot, GitProcessTimeout), cancellationToken);
        return head is null ? null : [head.Sha, .. head.ParentShas];
    }

    /// <summary>
    /// True only when <paramref name="after"/> (HEAD plus its parents, read after the commit) is provably the commit
    /// created by this call, judged against <paramref name="before"/> (read before it). A new commit's only parent is
    /// the old HEAD (none on an unborn branch); an amended commit has the old HEAD's parents and a new hash. A commit
    /// by another session landing in between (or after) changes the chain, so the check then fails rather than
    /// guessing.
    /// </summary>
    internal static bool IsOwnHead(string[]? before, string[]? after, bool replacesHead, string? mergeHead = null)
    {
        if (after is null || after.Length == 0)
            return false;

        if (replacesHead)
            return before is { Length: > 0 }
                && after[0] != before[0]
                && after.Skip(1).SequenceEqual(before.Skip(1));

        var expectedParents = before is { Length: > 0 } ? new[] { before[0] } : [];
        // A merge-resolution commit has two parents: the old HEAD and the commit that was being merged (MERGE_HEAD, read before the commit).
        if (!string.IsNullOrEmpty(mergeHead))
            expectedParents = [.. expectedParents, mergeHead];
        return (before is null || after[0] != before[0])
            && after.Skip(1).SequenceEqual(expectedParents);
    }

    /// <summary>
    /// Full hash of the commit named by the LAST <c>[branch abbrev]</c> summary line in <paramref name="commandStdout"/>
    /// (a hook may print look-alike lines earlier; git prints its own summary last), or null when no line matches or
    /// the abbreviation does not resolve to exactly one commit. <c>rev-parse --verify</c> fails on an ambiguous
    /// abbreviation instead of choosing one, which is the behaviour wanted here. Earlier matches are tried only when
    /// a later one does not resolve.
    /// </summary>
    private async Task<string?> ResolveSummaryHashAsync(string gitRoot, string commandStdout, CancellationToken cancellationToken)
    {
        var matches = CommitSummaryLine.Matches(commandStdout);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var abbrev = matches[i].Groups[1].Value;
            // Through the seam (libgit2 for a unique commit prefix, `rev-parse --verify --quiet <abbrev>^{commit}` for everything it declines: an ambiguous prefix, a ref named alike).
            var full = await Task.Run(() => _gitReader.ResolveCommitAbbreviation(gitRoot, abbrev, GitProcessTimeout), cancellationToken);
            if (full is null)
                continue;

            // SHA-1 (40) or SHA-256 (64) object names only.
            if (full.Length is 40 or 64 && full.All(Uri.IsHexDigit))
                return full;
        }

        return null;
    }

    /// <summary>
    /// The commit trailers for this call, or null for "behave exactly as without them" (the repo's <c>roslynsentinel.commitTrailers</c> is
    /// not true, or an amend's message cannot be read back). Otherwise the call id to look the new commit up by and the message arguments
    /// to commit with: <paramref name="messageArgs"/> plus <c>--trailer Tool-Call-Id: &lt;id&gt;</c> and, when the call has an agent id,
    /// <c>--trailer Tool-Agent: &lt;agent&gt;</c>. The id is the one the response echoes (<see cref="ToolCallContext.ToolCallId"/>); a call
    /// without one (no echo filter, tests) gets a fresh random id, which still serves the lookup. An amend without a message keeps HEAD's
    /// message but with its own Tool-Call-Id/Tool-Agent trailers removed first, so a stale agent trailer cannot survive next to the new id
    /// (the <c>ifexists=replace</c> config only replaces a trailer that is being written again).
    /// </summary>
    private async Task<(string CallId, string[] MessageArgs)?> PlanCommitTrailersAsync(
        string gitRoot, string[] messageArgs, bool amend, bool hasMessage, CancellationToken cancellationToken)
    {
        var enabled = await Task.Run(() => _gitReader.GetBoolConfig(gitRoot, GitCommitTrailers.ConfigKey, GitProcessTimeout), cancellationToken);
        if (enabled != true)
            return null;

        var context = ToolCallContext.Current;
        var callId = context?.ToolCallId ?? Guid.NewGuid().ToString("N")[..12];
        var args = messageArgs;
        if (amend && !hasMessage)
        {
            // Raw %B (no line re-join), so everything but the two trailer lines stays byte-identical.
            var head = await Task.Run(() => _gitReader.GetCommitHeader(gitRoot, "HEAD", GitProcessTimeout), cancellationToken);
            if (head is null)
                return null;

            args = ["--amend", "-m", GitCommitTrailers.StripOwnTrailers(head.Message)];
        }

        return (callId, [.. args, .. GitCommitTrailers.TrailerArgs(callId, context?.AgentId)]);
    }

    /// <summary>The full hash MERGE_HEAD names (a merge is waiting to be concluded by the next commit), or null when no merge is in progress.</summary>
    private async Task<string?> ReadMergeHeadAsync(string gitRoot, CancellationToken cancellationToken)
    {
        // Through the reader seam (libgit2 reads the MERGE_HEAD pseudo-ref file, the CLI's `rev-parse --verify --quiet MERGE_HEAD^{commit}` answers when it declines or fails).
        return await Task.Run(() => _gitReader.GetMergeHead(gitRoot, GitProcessTimeout), cancellationToken);
    }

    /// <summary>
    /// A revert that carries the commit trailers of <paramref name="plan"/>: <c>git revert --no-commit</c>, then <c>git commit --no-edit</c>
    /// (git picks the revert message up from MERGE_MSG) with the trailers. Returns the raw result to report and the call id when the trailers
    /// were committed (null: the plain <paramref name="plainArgs"/> revert answered instead, so there is no trailer to look up).
    /// <para>
    /// Every case where the two steps could behave differently from today's single <c>git revert</c> runs that single call, so its output and
    /// repository state are today's exactly: an operation already in progress or staged changes (plain revert refuses; <c>--no-commit</c>
    /// would not, and the commit would sweep the staged files into the revert), a revert that fails or conflicts (undone with
    /// <c>reset --merge</c>, safe because the index was clean and nothing was in progress, then re-run plainly so the error text, hints and
    /// in-progress state are today's), and a revert that changes nothing (same). Known differences when it does commit: <c>--no-verify</c>
    /// keeps the hook set of a plain revert (no pre-commit, no commit-msg; plain revert never runs them), but prepare-commit-msg sees source
    /// "merge" instead of "message"; and the stdout (reported as Message) has no " Date:" line, which only git revert's own summary prints.
    /// </para>
    /// </summary>
    private async Task<(GitRawResult Raw, string? CallId)> RevertWithTrailersAsync(
        string gitRoot, string[] plainArgs, string[] noCommitArgs, (string CallId, string[] MessageArgs) plan,
        string[]? headBefore, CancellationToken cancellationToken)
    {
        async Task<bool> HasStagedChangesAsync()
        {
            // 0 = identical to HEAD, 1 = differs; anything else (error) is treated as "has changes", the conservative answer.
            var raw = await RunGitAsync(gitRoot, ["diff", "--cached", "--quiet"], cancellationToken);
            return raw.ExitCode != 0;
        }

        async Task<(GitRawResult Raw, string? CallId)> PlainAfterUndoAsync()
        {
            await RunGitAsync(gitRoot, ["reset", "--merge"], cancellationToken);
            return (await RunGitAsync(gitRoot, plainArgs, cancellationToken), null);
        }

        if (headBefore is null
            || await DetectInProgressAsync(gitRoot, cancellationToken) is not null
            || await HasStagedChangesAsync())
        {
            return (await RunGitAsync(gitRoot, plainArgs, cancellationToken), null);
        }

        var staged = await RunGitAsync(gitRoot, noCommitArgs, cancellationToken);
        if (staged.ExitCode != 0 || !await HasStagedChangesAsync())
        {
            return await PlainAfterUndoAsync();
        }

        // The staged revert was computed against headBefore; committing it on top of a different HEAD would silently undo whatever
        // landed in between. Stop with the revert still staged (abort backs it out) instead.
        // Untested on purpose: the window is the gap between two git processes and git revert --no-commit runs no hook that could move HEAD inside it, and RunGitAsync has no seam to inject a move.
        var headNow = await ReadHeadWithParentsAsync(gitRoot, cancellationToken);
        if (headNow is null || headNow[0] != headBefore[0])
        {
            return (new GitRawResult
            {
                ExitCode = 1,
                Stdout = "",
                Stderr = "HEAD moved while the revert was being staged (another session committed), so it was not committed. The staged revert is still pending.",
            }, null);
        }

        // plan.MessageArgs carries --no-verify: it MIRRORS git revert's own hook behaviour (plain git revert never runs pre-commit or
        // commit-msg, so this commit must not either); it is not a policy skip of hooks.
        var commitArgs = new[] { "commit" }.Concat(plan.MessageArgs).ToArray();
        var committed = await RunGitAsync(gitRoot, commitArgs, cancellationToken);
        if (GitCommitTrailers.IsTrailerOptionUnsupported(committed.ExitCode, committed.Stderr))
        {
            // git older than 2.32: option parsing failed before anything was committed, so commit without the trailers (see CommitAsync).
            _logger.LogInformation("git commit does not support --trailer (git older than 2.32); reverting without Tool-Call-Id/Tool-Agent trailers.");
            // --no-verify MIRRORS git revert's own hook behaviour (plain git revert never runs pre-commit or commit-msg); not a policy skip of hooks.
            committed = await RunGitAsync(gitRoot, ["commit", "--no-edit", "--no-verify"], cancellationToken);
            return (committed, null);
        }

        return (committed, committed.ExitCode == 0 ? plan.CallId : null);
    }

    /// <summary>
    /// The full hash of the commit that <c>git commit</c> / <c>git revert</c> just created, never "whatever HEAD is now":
    /// another server instance sharing the working tree can commit between the git call and any readback. Order:
    /// (1) the summary line git printed for this very commit, expanded with <c>rev-parse --verify</c>; (2) only if that
    /// fails, HEAD, accepted when its parent chain proves it is ours (see <see cref="IsOwnHead"/>); (3) otherwise the
    /// empty string, meaning "the commit exists but its hash could not be established" - the caller must say so,
    /// and the log carries the detail. Never falls back to an unchecked HEAD.
    /// </summary>
    internal Task<string> DeriveNewCommitHashAsync(
        string gitRoot, string commandStdout, string[]? headBefore, bool amend, CancellationToken cancellationToken) =>
        DeriveNewCommitHashAsync(gitRoot, commandStdout, headBefore, amend, mergeHead: null, cancellationToken);

    /// <summary><see cref="DeriveNewCommitHashAsync(string, string, string[], bool, CancellationToken)"/> for a commit that may conclude a merge: <paramref name="mergeHead"/> is MERGE_HEAD as read before the commit (null when no merge was in progress).</summary>
    internal async Task<string> DeriveNewCommitHashAsync(
        string gitRoot, string commandStdout, string[]? headBefore, bool amend, string? mergeHead, CancellationToken cancellationToken)
    {
        var fromSummary = await ResolveSummaryHashAsync(gitRoot, commandStdout, cancellationToken);
        if (fromSummary is not null)
            return fromSummary;

        var headAfter = await ReadHeadWithParentsAsync(gitRoot, cancellationToken);
        if (IsOwnHead(headBefore, headAfter, replacesHead: amend, mergeHead))
        {
            _logger.LogInformation("The commit summary line was unparsable or ambiguous; HEAD {Head} is proven to be the new commit by its parent chain.", headAfter![0]);
            return headAfter[0];
        }

        _logger.LogWarning(
            "The new commit's hash could not be established: no usable summary line in the git output and HEAD is not provably the new commit " +
            "(another session probably committed meanwhile). Returning an empty CommitHash instead of a guess.");
        return "";
    }

    /// <summary>
    /// Moves HEAD/the current branch to <paramref name="refName"/> without ever touching the working
    /// tree, so this can never discard uncommitted edits the way <c>git reset --hard</c> would -- only
    /// <see cref="GitResetMode.soft"/> and <see cref="GitResetMode.mixed"/> are exposed, matching
    /// <see cref="UnstageAsync"/>'s existing precedent of never surfacing a destructive git mode.
    /// </summary>
    public async Task<GitStatusResult> ResetAsync(
        string gitRoot, string? refName, GitResetMode mode, CancellationToken cancellationToken)
    {
        // No default ref: a silent HEAD~1 meant a bare reset call quietly undid the last commit.
        if (string.IsNullOrWhiteSpace(refName))
        {
            return new GitStatusResult
            {
                IsError = true,
                ErrorKind = GitErrorCodes.RefRequired,
                Hints = [new GitHint(GitHintKey.RefRequiredReset)],
                Error = "reset needs an explicit ref, e.g. ref: \"HEAD~1\" to undo the last commit (working tree kept). " +
                        "To unstage without moving HEAD use operation=unstage. Nothing was reset."
            };
        }

        var target = refName;

        try
        {
            var modeFlag = mode == GitResetMode.soft ? "--soft" : "--mixed";
            var resetRaw = await RunGitAsync(gitRoot, ["reset", modeFlag, target], cancellationToken);

            if (resetRaw.ExitCode != 0)
                return new GitStatusResult { IsError = true, Error = $"git reset failed: {CleanGitStderr(resetRaw.Stderr)}" };

            return await StatusAsync(gitRoot, DefaultStatusMaxEntries, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitStatusResult>(ex, "reset");
        }
    }

    /// Lists branches (default), or creates/deletes one when <paramref name="branchName"/> is
    /// given. Creation is a plain, non-destructive <c>git branch <name> [startPoint]</c> -> it
    /// does not switch to the new branch; use <c>checkout</c> for that, optionally with
    /// <c>createBranch=true</c> to do both in one call. Deletion uses the safe <c>-d</c> form
    /// (refuses an unmerged branch) rather than <c>-D</c>, so a mistaken delete can't silently
    /// discard commits.
    /// </summary>
    public async Task<GitBranchResult> BranchAsync(
        string gitRoot, string? branchName, string? startPoint, bool deleteBranch, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                // The typed seam lists in-process (libgit2) where that reproduces git branch --list --all and runs the git CLI otherwise
                // (a detached HEAD, branch.sort). It blocks, so it runs off the caller's thread; the token only stops a call that has not started.
                var listed = await Task.Run(() => _gitReader.ListBranches(gitRoot, GitProcessTimeout), cancellationToken);
                return listed is null
                    ? await BranchListViaCliAsync(gitRoot, cancellationToken) // git could not answer: run it once more for its own error text
                    : new GitBranchResult { IsError = false, Branches = [.. listed.Select(ToBranchEntry)] };
            }

            if (deleteBranch)
            {
                var delRaw = await RunGitAsync(gitRoot,
                    ["branch", "-d", branchName], cancellationToken);
                if (delRaw.ExitCode != 0)
                    return new GitBranchResult
                    {
                        IsError = true,
                        Error = $"git branch -d failed: {CleanGitStderr(delRaw.Stderr)}. If the branch truly should be discarded unmerged, this tool deliberately does not expose -D; use the shell as a documented exception."
                    };

                return new GitBranchResult { IsError = false, Deleted = branchName };
            }

            string[] createArgs = string.IsNullOrWhiteSpace(startPoint)
                ? ["branch", branchName]
                : ["branch", branchName, startPoint];
            var createRaw = await RunGitAsync(gitRoot, createArgs, cancellationToken);
            if (createRaw.ExitCode != 0)
                return new GitBranchResult { IsError = true, Error = $"git branch failed: {CleanGitStderr(createRaw.Stderr)}" };

            return new GitBranchResult { IsError = false, Created = branchName };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UnexpectedFailure<GitBranchResult>(ex, "branch");
        }
    }

    private static GitBranchEntry ToBranchEntry(BranchRef branch) =>
        new()
        {
            Name = branch.Name,
            IsCurrent = branch.IsCurrent,
            IsRemote = branch.IsRemote
        };

    /// <summary>The branch listing through the git CLI, with git's own error text; used when the reader could not answer.</summary>
    private async Task<GitBranchResult> BranchListViaCliAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var listRaw = await RunGitAsync(gitRoot, ["branch", "--list", "--all"], cancellationToken);
        if (listRaw.ExitCode != 0)
            return new GitBranchResult { IsError = true, Error = CleanGitStderr(listRaw.Stderr) };

        return new GitBranchResult { IsError = false, Branches = [.. CliGitReader.ParseBranchList(listRaw.Stdout).Select(ToBranchEntry)] };
    }

    /// <summary>
    /// Switches to <paramref name="branchName"/>, optionally creating it first
    /// (<c>git checkout -b</c>) when <paramref name="createBranch"/> is set and it doesn't already
    /// exist. Uses plain <c>checkout</c> rather than <c>switch</c> for compatibility with older git.
    /// </summary>
    public async Task<GitCheckoutResult> CheckoutAsync(
        string gitRoot, string? branchName, bool createBranch, string? startPoint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            return new GitCheckoutResult
            {
                IsError = true,
                ErrorKind = GitErrorCodes.RefRequired,
                Hints = [new GitHint(GitHintKey.RefRequiredCheckout), new GitHint(GitHintKey.GitRemoteCheckoutCall)],
                Error = $"branchName is required for {GitHintTokens.Inline(GitHintKey.GitRemoteCheckoutCall)}. Pass the branch to switch to, and createBranch=true if it doesn't exist yet."
            };
        }

        // startPoint only means something when a branch is being created. Silently dropping it
        // would leave the caller believing the branch was based on it.
        if (!createBranch && !string.IsNullOrWhiteSpace(startPoint))
        {
            return new GitCheckoutResult
            {
                IsError = true,
                Branch = branchName,
                Error = $"startPoint '{startPoint}' was supplied without createBranch=true, so it would be ignored. " +
                        "Pass createBranch=true to create the branch from it, or omit startPoint to switch to an existing branch. Nothing was checked out."
            };
        }

        try
        {
            // createBranch on a branch that already exists is a plain checkout (idempotent for
            // retries); only a genuinely missing branch is created with -b. CreatedNewBranch
            // reports which of the two actually happened.
            var createNew = false;
            string? note = null;
            if (createBranch)
            {
                var existsRaw = await RunGitAsync(gitRoot,
                    ["rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}"], cancellationToken);
                if (existsRaw.ExitCode == 0)
                {
                    note = $"Branch '{branchName}' already existed, so it was checked out as-is rather than created" +
                           (string.IsNullOrWhiteSpace(startPoint) ? "." : $"; startPoint '{startPoint}' was ignored.");
                }
                else
                {
                    createNew = true;
                }
            }

            // Trailing "--" on the plain form: a branch that shares its name with a file or
            // directory is otherwise ambiguous, and git refuses ("ambiguous argument").
            List<string> args = createNew
                ? string.IsNullOrWhiteSpace(startPoint)
                    ? ["checkout", "-b", branchName]
                    : ["checkout", "-b", branchName, startPoint]
                : ["checkout", branchName, "--"];

            var checkoutRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            if (checkoutRaw.ExitCode != 0)
                return new GitCheckoutResult { IsError = true, Branch = branchName, Error = CleanGitStderr(checkoutRaw.Stderr) };

            return new GitCheckoutResult { IsError = false, Branch = branchName, CreatedNewBranch = createNew, Note = note };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = UnexpectedFailure<GitCheckoutResult>(ex, "checkout");
            failed.Branch = branchName;
            return failed;
        }
    }

    /// <summary>
    /// Pushes the current branch to <paramref name="remoteName"/>. Never forces -> there is no
    /// exposed <c>--force</c>/<c>--force-with-lease</c>, since a wrong force-push is destructive to
    /// shared history and this tool has no confirmation gate for it yet.
    /// </summary>
    public async Task<GitRemoteResult> PushAsync(
        string gitRoot, string remoteName, bool setUpstream, CancellationToken cancellationToken)
    {
        try
        {
            var branchRaw = await RunGitAsync(gitRoot,
                ["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
            var branch = branchRaw.ExitCode == 0 ? branchRaw.Stdout.Trim() : null;

            List<string> args = ["push"];
            if (setUpstream)
                args.Add("-u");
            args.Add(remoteName);
            if (!string.IsNullOrEmpty(branch))
                args.Add(branch);

            var pushRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            var detail = string.Join("\n", new[] { pushRaw.Stdout.Trim(), CleanGitStderr(pushRaw.Stderr) }.Where(s => s.Length > 0));
            if (pushRaw.ExitCode != 0)
                return new GitRemoteResult { IsError = true, Operation = "push", Error = detail.Length > 0 ? detail : $"git push exited with code {pushRaw.ExitCode}" };

            return new GitRemoteResult { IsError = false, Operation = "push", Detail = detail };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = UnexpectedFailure<GitRemoteResult>(ex, "push");
            failed.Operation = "push";
            return failed;
        }
    }

    public async Task<GitRemoteResult> FetchAsync(
        string gitRoot, string remoteName, CancellationToken cancellationToken)
    {
        try
        {
            var fetchRaw = await RunGitAsync(gitRoot, ["fetch", remoteName], cancellationToken);
            var detail = string.Join("\n", new[] { fetchRaw.Stdout.Trim(), CleanGitStderr(fetchRaw.Stderr) }.Where(s => s.Length > 0));
            if (fetchRaw.ExitCode != 0)
                return new GitRemoteResult { IsError = true, Operation = "fetch", Error = detail.Length > 0 ? detail : $"git fetch exited with code {fetchRaw.ExitCode}" };

            return new GitRemoteResult { IsError = false, Operation = "fetch", Detail = detail };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = UnexpectedFailure<GitRemoteResult>(ex, "fetch");
            failed.Operation = "fetch";
            return failed;
        }
    }

    /// <summary>
    /// Pulls the current branch from <paramref name="remoteName"/> - a plain merge (git's default)
    /// unless <paramref name="rebase"/> is set. No <c>--force</c> exposed -> pull never force-rewrites
    /// remote-tracking state.
    /// </summary>
    public async Task<GitRemoteResult> PullAsync(
        string gitRoot, string remoteName, bool rebase, CancellationToken cancellationToken)
    {
        try
        {
            // Always state the strategy: with neither --rebase nor --no-rebase, git 2.27+ refuses a
            // pull of divergent branches ("Need to specify how to reconcile divergent branches")
            // unless pull.rebase/pull.ff is configured, which the caller has no way to see or fix.
            var args = new List<string> { "pull", rebase ? "--rebase" : "--no-rebase", remoteName };
            var pullRaw = await RunGitAsync(gitRoot, [.. args], cancellationToken);
            var detail = string.Join("\n", new[] { pullRaw.Stdout.Trim(), CleanGitStderr(pullRaw.Stderr) }.Where(s => s.Length > 0));
            if (pullRaw.ExitCode != 0)
                return await AppendInProgressAsync(new GitRemoteResult { IsError = true, Operation = "pull", Error = detail.Length > 0 ? detail : $"git pull exited with code {pullRaw.ExitCode}" }, gitRoot, cancellationToken);

            return new GitRemoteResult { IsError = false, Operation = "pull", Detail = detail };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = UnexpectedFailure<GitRemoteResult>(ex, "pull");
            failed.Operation = "pull";
            return failed;
        }
    }
}
