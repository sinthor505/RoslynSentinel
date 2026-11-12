using System.Diagnostics;
using System.Text;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Second corpus for the libgit2-patch parity probe (see <c>GitPatchParityHarness</c>), scenarios 9 to 16. Same contract as
/// <c>GitPatchParityCorpusTests</c>: each comparison is an asserted pin through the composite reader. A routed scenario that differs
/// from the CLI is a production defect and is marked [Explicit] with the first differing line.
/// </summary>
[TestFixture]
[NonParallelizable] // starts git processes and appends to one shared table; kept serial.
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitPatchParityCorpusMoreTests
{

    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        if (!IsGitAvailable())
        {
            Assert.Ignore("git is not available on PATH.");
        }

        // Short directory names: a long scenario name pushed a linked-worktree git dir past MAX_PATH once (see GitCommitSeamParityTests).
        _root = Path.Combine(Path.GetTempPath(), "rs-ppm-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (_root is null || !Directory.Exists(_root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    // ---- helpers ----

    /// <summary>A repository with its config pinned repo-locally (identity, no signing, no gc, hooks only from its own .git/hooks), core.autocrlf false, and filemode off (Windows host: an index-only mode change survives add -A).</summary>
    private string NewRepo(string name)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", "false");
        RunGit(directory, "config", "core.filemode", "false");
        RunGit(directory, "config", "user.name", "Test");
        RunGit(directory, "config", "user.email", "test@example.com");
        RunGit(directory, "config", "commit.gpgsign", "false");
        RunGit(directory, "config", "gc.auto", "0");
        RunGit(directory, "config", "maintenance.auto", "false");
        var hooks = Path.Combine(directory, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        RunGit(directory, "config", "core.hooksPath", hooks.Replace('\\', '/'));
        return directory;
    }

    private static void WriteText(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteBytes(string directory, string relativePath, byte[] bytes)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Writes the lines with LF endings and a final newline.</summary>
    private static void WriteLines(string directory, string relativePath, IEnumerable<string> lines) =>
        WriteText(directory, relativePath, string.Join("\n", lines) + "\n");

    private static string[] NumberedLines(int count, string prefix) =>
        Enumerable.Range(1, count).Select(n => $"{prefix} line {n:D3} original").ToArray();

    private static string Head(string directory) => RunGit(directory, "rev-parse", "HEAD");

    /// <summary>The object id git writes for <paramref name="content"/> fed on stdin (hash-object -w --stdin).</summary>
    private static string HashObjectFromStdin(string directory, string content)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("hash-object");
        startInfo.ArgumentList.Add("-w");
        startInfo.ArgumentList.Add("--stdin");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(content);
        process.StandardInput.Close();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("git hash-object did not exit within 60 seconds.");
        }

        var output = stdout.GetAwaiter().GetResult().Trim();
        stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("git hash-object exited with " + process.ExitCode + ".");
        }

        return output;
    }

    /// <summary>A C-like file: a column-0 function header, an indented 20-line body and a closing brace.</summary>
    private static List<string> CSourceLines()
    {
        var lines = new List<string> { "/* sample */", "int counter;", "void Foo()", "{" };
        for (var n = 1; n <= 20; n++)
        {
            lines.Add($"    step {n:D3};");
        }

        lines.Add("}");
        return lines;
    }

    /// <summary>Copies of an identical function block, separated by blank lines.</summary>
    private static List<string> HelperBlocks(int count)
    {
        var lines = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                lines.Add(string.Empty);
            }

            lines.AddRange(new[] { "void Helper()", "{", "    var total = 0;", "    return total;", "}" });
        }

        return lines;
    }

    /// <summary>Base commit holds foo.c; the change edits body line 9 and inserts a line after it. Pins the staged diff and the range.</summary>
    private static async Task PinCFunctionScenarioAsync(string scenario, string directory, string? gitattributes, bool routed)
    {
        var lines = CSourceLines();
        if (gitattributes is not null)
        {
            WriteText(directory, ".gitattributes", gitattributes);
        }

        WriteLines(directory, "foo.c", lines);
        CommitAll(directory, "base");

        await GitPatchParityCorpusTests.PinStagedAndRangeAsync(scenario, directory, routed, () =>
        {
            lines[12] = "    step 009 changed;";
            lines.Insert(13, "    extra;");
            WriteLines(directory, "foo.c", lines);
        });
    }

    // ---- scenarios ----

    [Test]
    public async Task Scenario09_SubmoduleAsync()
    {
        var subSource = NewRepo("s09sub");
        WriteText(subSource, "sub.txt", "sub one\n");
        CommitAll(subSource, "sub base");

        var dir = NewRepo("s09");
        WriteText(dir, "main.txt", "main\n");
        var subPath = Path.GetFullPath(subSource).Replace('\\', '/');
        RunGit(dir, "-c", "protocol.file.allow=always", "submodule", "add", subPath, "sub");
        CommitAll(dir, "base");

        await GitPatchParityCorpusTests.PinStagedAndRangeAsync("09_Submodule", dir, false, () =>
        {
            var submodule = Path.Combine(dir, "sub");
            WriteText(submodule, "sub.txt", "sub one\nsub two\n");
            CommitAll(submodule, "sub change");
            RunGit(dir, "add", "sub");
        });
    }

    [Test]
    public async Task Scenario10_UnicodeAndSpacePathsAsync()
    {
        var dir = NewRepo("s10");
        const string spacedPath = "dir with space/é.txt";
        const string japanesePath = "日本語 file.txt";
        WriteLines(dir, spacedPath, NumberedLines(20, "e"));
        CommitAll(dir, "base");

        await GitPatchParityCorpusTests.PinStagedAndRangeAsync("10_UnicodeAndSpacePaths", dir, false, () =>
        {
            var lines = NumberedLines(20, "e");
            lines[9] = "e line 010 changed";
            WriteLines(dir, spacedPath, lines);
            WriteLines(dir, japanesePath, NumberedLines(5, "j"));
        });
    }

    [Test]
    public async Task Scenario11_NonUtf8ContentAsync()
    {
        var dir = NewRepo("s11");
        WriteBytes(dir, "latin1.txt", Encoding.Latin1.GetBytes("first line\ncafé è context\nthird line\nfourth line\n"));
        CommitAll(dir, "base");

        await GitPatchParityCorpusTests.PinStagedAndRangeAsync("11_NonUtf8Content", dir, true, () =>
            WriteBytes(dir, "latin1.txt", Encoding.Latin1.GetBytes("first line\ncafé è context\nthird line changed éè\nfourth line\n")));
    }

    [Test]
    [TestCase(true)]
    [TestCase(false)]
    public async Task Scenario12_AmbiguousHunkPlacementAsync(bool heuristic)
    {
        var dir = NewRepo("s12");
        WriteLines(dir, "blocks.c", HelperBlocks(2));
        CommitAll(dir, "base");

        // A third identical copy goes in directly after the first, so the inserted lines can sit in two places.
        var value = heuristic ? "true" : "false";
        RunGit(dir, "config", "diff.indentHeuristic", value);
        try
        {
            await GitPatchParityCorpusTests.PinStagedAndRangeAsync("12_Ambiguous_indentHeuristic=" + value, dir, true, () =>
                WriteLines(dir, "blocks.c", HelperBlocks(3)));
        }
        finally
        {
            RunGit(dir, "config", "--unset", "diff.indentHeuristic");
        }
    }

    [Test]
    public async Task Scenario13a_CFunctionContextAsync()
    {
        var dir = NewRepo("s13a");
        await PinCFunctionScenarioAsync("13a_CFunctionContext", dir, gitattributes: null, routed: true);
    }

    [Test]
    public async Task Scenario13b_MarkdownProseAsync()
    {
        var dir = NewRepo("s13b");
        var lines = NumberedLines(30, "prose");
        WriteLines(dir, "notes.md", lines);
        CommitAll(dir, "base");

        await GitPatchParityCorpusTests.PinStagedAndRangeAsync("13b_MarkdownProse", dir, true, () =>
        {
            lines[12] = "prose line 013 changed";
            WriteLines(dir, "notes.md", lines);
        });
    }

    [Test]
    public async Task Scenario13c_CppDriverAttributeAsync()
    {
        var dir = NewRepo("s13c");
        await PinCFunctionScenarioAsync("13c_CppDriverAttribute", dir, gitattributes: "*.c diff=cpp\n", routed: false);
    }

    [Test]
    [TestCase("diff.context", "1", false)]
    [TestCase("diff.interHunkContext", "8", false)]
    [TestCase("diff.algorithm", "patience", true)]
    [TestCase("diff.indentHeuristic", "false", true)]
    [TestCase("diff.noprefix", "true", true)]
    [TestCase("diff.mnemonicPrefix", "true", true)]
    [TestCase("color.diff", "always", false)]
    [TestCase("color.ui", "always", false)]
    [TestCase("diff.renames", "false", true)]
    [TestCase("diff.external", "true", false)]
    [TestCase("diff.wsErrorHighlight", "all", true)]
    [TestCase("core.whitespace", "trailing-space", true)]
    public async Task Scenario14_DiffAndColorConfigAsync(string key, string value, bool routed)
    {
        var dir = NewRepo("s14");
        var lines = NumberedLines(40, "d");
        WriteLines(dir, "config.txt", lines);
        CommitAll(dir, "base");

        RunGit(dir, "config", key, value);
        try
        {
            await GitPatchParityCorpusTests.PinStagedAndRangeAsync("14_" + key + "=" + value, dir, routed, () =>
            {
                lines[9] = "d line 010 changed";
                lines[19] = "d line 020 changed";
                WriteLines(dir, "config.txt", lines);
            });
        }
        finally
        {
            RunGit(dir, "config", "--unset", key);
        }
    }

    [Test]
    public async Task Scenario15a_RegularFileToSymlinkInIndexAsync()
    {
        var dir = NewRepo("s15a");
        WriteText(dir, "link.txt", "regular content\n");
        CommitAll(dir, "base");
        var baseSha = Head(dir);

        // Symlinks may not be creatable on this host, so the typechange is staged through the index only.
        // Not the staged helper: its add -A would see the missing file and stage the removal instead.
        File.Delete(Path.Combine(dir, "link.txt"));
        var blob = HashObjectFromStdin(dir, "target.txt");
        RunGit(dir, "update-index", "--cacheinfo", "120000," + blob + ",link.txt");

        await GitPatchParityHarness.AssertDeclinedAsync("15a_RegularToSymlinkIndex", dir, "staged");

        RunGit(dir, "commit", "-q", "-m", "15a typechange");
        var range = baseSha + ".." + Head(dir);
        await GitPatchParityHarness.AssertDeclinedAsync("15a_RegularToSymlinkIndex", dir, range);
    }

    [Test]
    public async Task Scenario15b_DeletedThenReAddedAsync()
    {
        var dir = NewRepo("s15b");
        WriteLines(dir, "re.txt", NumberedLines(12, "r"));
        CommitAll(dir, "base");
        var baseSha = Head(dir);

        RunGit(dir, "rm", "-q", "re.txt");
        CommitAll(dir, "delete");

        // The range below starts at the base, before the delete, so it spans both commits.
        WriteLines(dir, "re.txt", NumberedLines(12, "s"));
        RunGit(dir, "add", "-A");
        await GitPatchParityHarness.AssertRoutedIdenticalAsync("15b_DeletedThenReAdded", dir, "staged");

        CommitAll(dir, "re-add");
        var range = baseSha + ".." + Head(dir);
        await GitPatchParityHarness.AssertRoutedIdenticalAsync("15b_DeletedThenReAdded", dir, range);
    }

    [Test]
    public async Task Scenario16a_MergeNoFastForwardAsync()
    {
        var dir = NewRepo("s16a");
        WriteLines(dir, "m.txt", NumberedLines(40, "m"));
        CommitAll(dir, "base");

        RunGit(dir, "checkout", "-q", "-b", "side");
        WriteText(dir, "side.txt", "side only\n");
        CommitAll(dir, "side change");
        RunGit(dir, "checkout", "-q", "-");

        var lines = NumberedLines(40, "m");
        lines[29] = "m line 030 changed";
        WriteLines(dir, "m.txt", lines);
        CommitAll(dir, "master change");
        var firstParent = Head(dir);

        RunGit(dir, "merge", "--no-ff", "-q", "-m", "merge side", "side");
        // show prints the first-parent diff of a merge, which is what this range compares.
        var range = firstParent + ".." + Head(dir);
        await GitPatchParityHarness.AssertRoutedIdenticalAsync("16a_MergeNoFastForward", dir, range);
        await GitPatchParityHarness.AssertRoutedIdenticalAsync("16a_MergeNoFastForward", dir, Head(dir), GitPatchParityHarness.PatchSurface.Show);
    }

    [Test]
    public async Task Scenario16b_RootCommitAsync()
    {
        var dir = NewRepo("s16b");
        WriteLines(dir, "first.txt", NumberedLines(10, "f"));
        WriteLines(dir, "second.txt", NumberedLines(6, "g"));
        CommitAll(dir, "root");
        var rootSha = Head(dir);

        await GitPatchParityHarness.AssertRoutedIdenticalAsync("16b_RootCommit", dir, rootSha, GitPatchParityHarness.PatchSurface.Show);
    }
}
