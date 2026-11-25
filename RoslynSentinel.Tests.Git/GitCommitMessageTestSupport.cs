using System.Diagnostics;
using System.Text;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Ground truth for the commit message parity tests: the installed git commits with exactly the arguments <c>GitImpl.CommitAsync</c> builds (the config prefix of
/// <see cref="GitCommitTrailers.ReplaceConfigArgs"/> only when trailers are on, <c>commit -m message</c>, then <see cref="GitCommitTrailers.TrailerArgs"/>), and the stored message is read back as raw bytes
/// (<c>git cat-file commit HEAD</c>, everything after the first blank line). The child inherits this process's environment and config view, the same one the in-process config check reads.
/// </summary>
internal static class GitCommitMessageTestSupport
{
    /// <summary>Runs git and returns exit code, raw stdout bytes and stderr; throws TimeoutException (process killed) after 60 s.</summary>
    internal static (int ExitCode, byte[] Output, string Error) RunGitBytes(string workingDirectory, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_COUNT"] = "3";
        startInfo.Environment["GIT_CONFIG_KEY_0"] = "gc.auto";
        startInfo.Environment["GIT_CONFIG_VALUE_0"] = "0";
        startInfo.Environment["GIT_CONFIG_KEY_1"] = "maintenance.auto";
        startInfo.Environment["GIT_CONFIG_VALUE_1"] = "false";
        startInfo.Environment["GIT_CONFIG_KEY_2"] = "commit.gpgsign";
        startInfo.Environment["GIT_CONFIG_VALUE_2"] = "false";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null for git.");
        process.StandardInput.Close();
        var stdErr = Task.Factory.StartNew(() => process.StandardError.ReadToEnd(), TaskCreationOptions.LongRunning);
        var stdOut = Task.Factory.StartNew(
            () =>
            {
                using var buffer = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(buffer);
                return buffer.ToArray();
            },
            TaskCreationOptions.LongRunning);

        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"git {string.Join(' ', args.Select(a => a.Length > 40 ? a[..40] + "..." : a))} did not exit within 60 s.");
        }

        Task.WaitAll([stdOut, stdErr], TimeSpan.FromSeconds(10));
        return (process.ExitCode, stdOut.Result, stdErr.Result);
    }

    /// <summary>
    /// Commits an empty commit with the real git using the commit tool's argument shape and returns the stored message bytes (null when git refused, with its stderr).
    /// <paramref name="extraConfigArgs"/> (<c>-c</c> pairs) go first, as the trailer config prefix does.
    /// </summary>
    internal static (int ExitCode, byte[]? Message, string Error) CommitWithRealGit(string repository, string message, string? toolCallId, string? toolAgent, IReadOnlyList<string>? extraConfigArgs = null)
    {
        var args = new List<string> { "-c", "user.name=Test", "-c", "user.email=test@example.com" };
        if (extraConfigArgs is not null)
        {
            args.AddRange(extraConfigArgs);
        }

        if (toolCallId is not null)
        {
            args.AddRange(GitCommitTrailers.ReplaceConfigArgs);
        }

        args.AddRange(["commit", "--allow-empty", "--quiet", "-m", message]);
        if (toolCallId is not null)
        {
            args.AddRange(GitCommitTrailers.TrailerArgs(toolCallId, toolAgent));
        }

        var committed = RunGitBytes(repository, args);
        if (committed.ExitCode != 0)
        {
            return (committed.ExitCode, null, committed.Error.Trim());
        }

        var commit = RunGitBytes(repository, ["cat-file", "commit", "HEAD"]);
        if (commit.ExitCode != 0)
        {
            throw new InvalidOperationException($"git cat-file commit HEAD failed: {commit.Error.Trim()}");
        }

        var raw = commit.Output;
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n' && raw[i + 1] == (byte)'\n')
            {
                return (0, raw[(i + 2)..], string.Empty);
            }
        }

        return (0, [], string.Empty);
    }

    /// <summary>
    /// Runs the real git and <see cref="GitCommitMessage.Format"/> on the same input. Status is "match" (same bytes, or git refused and the port declined), "declined" (git committed, the port
    /// declined: allowed, never wrong) or "mismatch" (different bytes, or one side refused alone); Detail describes it.
    /// </summary>
    internal static (string Status, string Detail) CompareWithGit(string repository, string message, string? toolCallId, string? toolAgent)
    {
        var real = CommitWithRealGit(repository, message, toolCallId, toolAgent);
        var ours = GitCommitMessage.Format(message, toolCallId, toolAgent);
        var input = Describe(message, toolCallId, toolAgent);
        if (real.ExitCode != 0)
        {
            return ours.IsFormatted
                ? ("mismatch", $"git refused ({real.Error}) but the port produced {Show(ours.Message)}. {input}")
                : ("match", $"both refuse. {input}");
        }

        if (!ours.IsFormatted)
        {
            return ("declined", $"{ours.Detail} {input}");
        }

        var expected = Encoding.UTF8.GetBytes(ours.Message!);
        return real.Message!.AsSpan().SequenceEqual(expected)
            ? ("match", input)
            : ("mismatch", $"git stored {Show(Encoding.UTF8.GetString(real.Message!))} but the port produced {Show(ours.Message)}. {input}");
    }

    private static string Describe(string message, string? toolCallId, string? toolAgent) =>
        $"input {Show(message)}, callId {(toolCallId is null ? "none" : Show(toolCallId))}, agent {(toolAgent is null ? "none" : Show(toolAgent))}";

    /// <summary>A one-line, visible form of a text: CR, LF, TAB, VT, FF and NUL spelled out, long texts cut.</summary>
    internal static string Show(string? text)
    {
        if (text is null)
        {
            return "<null>";
        }

        var cut = text.Length > 300 ? text[..300] + $"...(+{text.Length - 300} chars)" : text;
        return "[" + cut.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\t", "<TAB>").Replace("\v", "<VT>").Replace("\f", "<FF>").Replace("\0", "<NUL>") + "]";
    }

    /// <summary>A new repository under a fresh long-form temp directory; the caller deletes it with <see cref="CasCommitTestSupport.DeleteTree"/>.</summary>
    internal static string NewRepository(string prefix)
    {
        var root = GitHookTestSupport.NewRoot(prefix);
        var result = RunGitBytes(root, ["init", "--quiet"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git init failed: {result.Error.Trim()}");
        }

        return root;
    }
}
