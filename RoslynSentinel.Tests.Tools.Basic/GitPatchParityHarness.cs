using System.Diagnostics;
using System.Reflection;
using System.Text;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Measurement harness: does a libgit2 <c>Patch</c> reproduce the text that <c>GitImpl.DiffAsync</c> shows for the same
/// diff? The CLI side is the real tool call; the libgit2 side is <c>Patch.Content</c> put through the same private
/// <c>RejoinLines</c> and <c>DetectDecodeCorruption</c> steps (reached by reflection, so a rename there breaks this
/// harness at run time). Test-only: nothing here is production code. Outcomes are appended to a tab-separated table
/// under the temp directory, which the dispatcher reads.
/// </summary>
internal static class GitPatchParityHarness
{
    private const int DiffMaxBytes = 524288;
    private const int OutcomeDetailMax = 120;
    private static readonly object RecordLock = new();

    private static readonly MethodInfo RejoinLinesMethod =
        typeof(GitImpl).GetMethod("RejoinLines", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GitImpl.RejoinLines was not found by reflection.");

    private static readonly MethodInfo DetectDecodeCorruptionMethod =
        typeof(GitImpl).GetMethod("DetectDecodeCorruption", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GitImpl.DetectDecodeCorruption was not found by reflection.");

    /// <summary>Runs git with the same -c core.quotePath=false prefix as the tool and returns the exit code and the untrimmed stdout bytes.</summary>
    internal static (int ExitCode, byte[] Stdout) RunGitBytes(string dir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotePath=false");
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        // Drain stderr concurrently so a chatty git cannot block on a full pipe while stdout is being read.
        var stderr = process.StandardError.ReadToEndAsync();
        using var stdout = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(stdout);
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("git " + string.Join(' ', args) + " did not exit within 60 seconds.");
        }

        stderr.GetAwaiter().GetResult();
        return (process.ExitCode, stdout.ToArray());
    }

    /// <summary>The libgit2 patch text for <c>staged</c> (index against HEAD) or <c>A..B</c> (tree of A against tree of B), with the options the seam uses plus the context and indent settings.</summary>
    internal static string Libgit2Patch(string dir, string target, bool indentHeuristic = true)
    {
        using var repo = new Repository(dir);
        // Similarity off: git's rename detection is not reproduced, so renames show up as delete plus add.
        var options = new CompareOptions
        {
            Similarity = SimilarityOptions.None,
            ContextLines = 3,
            IndentHeuristic = indentHeuristic,
        };
        var patch = target == "staged"
            ? repo.Diff.Compare<Patch>(repo.Head.Tip.Tree, DiffTargets.Index, paths: null, explicitPathsOptions: null, compareOptions: options)
            : RangeCompare(repo, target, options);
        return patch.Content;
    }

    private static Patch RangeCompare(Repository repo, string target, CompareOptions options)
    {
        var parts = target.Split("..", StringSplitOptions.None);
        if (parts.Length != 2)
        {
            throw new ArgumentException("Expected 'staged' or 'A..B', got '" + target + "'.", nameof(target));
        }

        return repo.Diff.Compare<Patch>(CommitTree(repo, parts[0]), CommitTree(repo, parts[1]), options);
    }

    private static Tree CommitTree(Repository repo, string revision) =>
        (repo.Lookup<Commit>(revision) ?? throw new ArgumentException("No commit matches '" + revision + "'.")).Tree;

    /// <summary>The tool-visible text and Warning for a diff target, from the real GitImpl.DiffAsync (git CLI route).</summary>
    internal static async Task<(string Text, string? Warning)> CliToolTextAsync(string dir, string target)
    {
        var impl = new GitImpl(
            new FakeWorkspaceManager(WorkspaceAccess.ReadOnly),
            NullLogger<GitImpl>.Instance,
            new FallbackGitReader(new Libgit2GitReader(), new CliGitReader(), _ => { }),
            new InProcessCommitRoute());
        var result = await impl.DiffAsync(dir, target, null, DiffMaxBytes, nameOnly: false, stat: false, CancellationToken.None);
        if (result.IsError)
        {
            throw new InvalidOperationException("GitImpl.DiffAsync failed for '" + target + "': " + result.Error);
        }

        return (result.Diff ?? string.Empty, result.Warning);
    }

    /// <summary>The text and Warning the tool would show if the libgit2 patch were served instead of the CLI output.</summary>
    internal static (string Text, string? Warning) Libgit2ToolText(string dir, string target) =>
        ToolTextFromRaw(Libgit2Patch(dir, target));

    internal static (string Text, string? Warning) ToolTextFromRaw(string raw)
    {
        var text = (string)RejoinLinesMethod.Invoke(null, new object?[] { raw })!;
        var warning = (string?)DetectDecodeCorruptionMethod.Invoke(null, new object?[] { text });
        return (text, warning);
    }

    /// <summary>
    /// Outcome for one diff target: "parity" when the tool-visible text and Warning are equal, "differs:&lt;line&gt;:..."
    /// for the first differing line (or the Warning), "libgit2-throws:&lt;type&gt;" when the libgit2 probe throws. Every
    /// outcome except the throw one ends with ";raw=equal" or ";raw=differs" (git stdout bytes against UTF-8 of the
    /// libgit2 content). <paramref name="indentHeuristic"/> is passed to the libgit2 side only.
    /// </summary>
    internal static async Task<string> CompareAsync(string dir, string target, bool indentHeuristic = true)
    {
        var (cliText, cliWarning) = await CliToolTextAsync(dir, target);
        var (gitExit, gitBytes) = RunGitBytes(dir, DiffArgs(target));
        if (gitExit != 0)
        {
            throw new InvalidOperationException("git " + string.Join(' ', DiffArgs(target)) + " exited with " + gitExit + ".");
        }

        string libRaw;
        try
        {
            libRaw = Libgit2Patch(dir, target, indentHeuristic);
        }
        catch (Exception ex)
        {
            return "libgit2-throws:" + ex.GetType().Name;
        }

        return OutcomeFor(cliText, cliWarning, gitBytes, libRaw);
    }

    /// <summary>
    /// Outcome for a root commit, in the shape <c>show</c> prints it: the CLI side is <c>git diff</c> of the empty tree
    /// against <paramref name="revision"/> (raw bytes, then the tool-visible text), the libgit2 side compares a null tree
    /// with the commit's tree. Outcome strings are the same as <see cref="CompareAsync"/>.
    /// </summary>
    internal static async Task<string> CompareRootCommitAsync(string dir, string revision)
    {
        const string emptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var (gitExit, gitBytes) = RunGitBytes(dir, "diff", emptyTree, revision);
        if (gitExit != 0)
        {
            throw new InvalidOperationException("git diff " + emptyTree + " " + revision + " exited with " + gitExit + ".");
        }

        var (cliText, cliWarning) = ToolTextFromRaw(Encoding.UTF8.GetString(gitBytes));
        string libRaw;
        try
        {
            using var repo = new Repository(dir);
            // Same CompareOptions as Libgit2Patch with the default context and indent settings.
            var options = new CompareOptions
            {
                Similarity = SimilarityOptions.None,
                ContextLines = 3,
                IndentHeuristic = true,
            };
            libRaw = repo.Diff.Compare<Patch>(null as Tree, CommitTree(repo, revision), options).Content;
        }
        catch (Exception ex)
        {
            return "libgit2-throws:" + ex.GetType().Name;
        }

        return await Task.FromResult(OutcomeFor(cliText, cliWarning, gitBytes, libRaw));
    }

    /// <summary>Compares the CLI tool-visible text and Warning with the libgit2 raw patch and returns the outcome string.</summary>
    private static string OutcomeFor(string cliText, string? cliWarning, byte[] gitBytes, string libRaw)
    {
        var rawSuffix = gitBytes.SequenceEqual(Encoding.UTF8.GetBytes(libRaw)) ? ";raw=equal" : ";raw=differs";
        var (libText, libWarning) = ToolTextFromRaw(libRaw);
        if (string.Equals(cliText, libText, StringComparison.Ordinal) && string.Equals(cliWarning, libWarning, StringComparison.Ordinal))
        {
            return "parity" + rawSuffix;
        }

        var cliLines = cliText.Split('\n');
        var libLines = libText.Split('\n');
        var count = Math.Max(cliLines.Length, libLines.Length);
        for (var i = 0; i < count; i++)
        {
            var cliLine = i < cliLines.Length ? cliLines[i] : null;
            var libLine = i < libLines.Length ? libLines[i] : null;
            if (!string.Equals(cliLine, libLine, StringComparison.Ordinal))
            {
                return "differs:" + Truncate(i + 1 + ": cli=" + Show(cliLine) + " libgit2=" + Show(libLine)) + rawSuffix;
            }
        }

        // The text matches line for line, so only the Warning can differ here.
        return "differs:" + Truncate("warning cli=" + (cliWarning ?? "<none>") + " libgit2=" + (libWarning ?? "<none>")) + rawSuffix;
    }

    private static string[] DiffArgs(string target) => target == "staged" ? ["diff", "--cached"] : ["diff", target];

    internal static string Show(string? line) => line is null ? "<missing>" : line.Replace("\r", "\\r").Replace("\t", "\\t");

    internal static string Truncate(string value) => value.Length <= OutcomeDetailMax ? value : value[..OutcomeDetailMax];

    /// <summary>Appends one scenario<TAB>target<TAB>outcome line to the shared table. Never truncates the file.</summary>
    internal static void Record(string scenario, string target, string outcome)
    {
        var line = Sanitize(scenario) + "\t" + Sanitize(target) + "\t" + Sanitize(outcome) + "\n";
        lock (RecordLock)
        {
            var directory = Path.Combine(Path.GetTempPath(), "rs-patch-parity");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "table.tsv"), line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static string Sanitize(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Runs the action once unmeasured, then <paramref name="runs"/> measured times, and returns the median in milliseconds.</summary>
    internal static double MedianMs(Action action, int runs = 10)
    {
        action();
        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var middle = runs / 2;
        return runs % 2 == 1 ? samples[middle] : (samples[middle - 1] + samples[middle]) / 2.0;
    }
}
