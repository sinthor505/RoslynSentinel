using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Corpus for the libgit2-patch parity probe (see <c>GitPatchParityHarness</c>). Each scenario records the outcome for a
/// staged diff and for the committed range, through the composite reader. A routed scenario must equal the CLI text with one libgit2 call;
/// a declined one must show one counted planned decline with CLI-equal text. Routed scenarios that differ are [Explicit] production defects.
/// The Measure test still writes the table to the temp directory <c>rs-patch-parity/table.tsv</c>.
/// </summary>
[TestFixture]
[NonParallelizable] // starts git processes and appends to one shared table; kept serial.
[Category("OutOfProcess")] // starts git processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class GitPatchParityCorpusTests
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
        _root = Path.Combine(Path.GetTempPath(), "rs-ppc-" + Guid.NewGuid().ToString("N")[..12]);
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

    /// <summary>A repository with its config pinned repo-locally (identity, no signing, no gc, hooks only from its own .git/hooks), core.autocrlf set as asked, and filemode off (Windows host: an index-only mode change survives add -A).</summary>
    private string NewRepo(string name, bool autocrlf = false)
    {
        var directory = Path.Combine(_root, name);
        InitRepo(directory);
        RunGit(directory, "config", "core.autocrlf", autocrlf ? "true" : "false");
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

    private static void Report(string scenario, string target, string outcome)
    {
        GitPatchParityHarness.Record(scenario, target, outcome);
        TestContext.Out.WriteLine(scenario + "\t" + target + "\t" + outcome);
    }

    private static Task PinAsync(string scenario, string directory, string target, bool routed) =>
    routed
        ? GitPatchParityHarness.AssertRoutedIdenticalAsync(scenario, directory, target)
        : GitPatchParityHarness.AssertDeclinedAsync(scenario, directory, target);

    /// <summary>
    /// The base is already committed. Applies the change, stages it and pins the staged diff, then commits it and pins the committed range (base..new).
    /// <paramref name="routed"/> is the expected class: libgit2 serves the text, or it declines and the CLI answers.
    /// </summary>
    internal static async Task PinStagedAndRangeAsync(string scenario, string directory, bool routed, Action apply)
    {
        var baseSha = Head(directory);
        apply();
        RunGit(directory, "add", "-A");
        await PinAsync(scenario, directory, "staged", routed);

        CommitAll(directory, scenario + " change");
        var range = baseSha + ".." + Head(directory);
        await PinAsync(scenario, directory, range, routed);
    }

    private static readonly byte[] BinaryPayloadA = new byte[] { 0x50, 0x00, 0x4B, 0x03, 0x00, 0xFF, 0xFE, 0x00, 0x0A };
    private static readonly byte[] BinaryPayloadB = new byte[] { 0x50, 0x00, 0x4B, 0x03, 0x00, 0xAB, 0xFE, 0x00, 0x0A, 0x00 };

    // ---- scenarios ----

    [Test]
    public async Task Scenario01_PlainModify_ThreeContextLines_TwoHunksAsync()
    {
        var dir = NewRepo("s01");
        var lines = NumberedLines(30, "a");
        WriteLines(dir, "plain.txt", lines);
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("01_PlainModify", dir, true, () =>
        {
            lines[4] = "a line 005 changed";
            lines[24] = "a line 025 changed";
            WriteLines(dir, "plain.txt", lines);
        });
    }

    [Test]
    public async Task Scenario02_LargeFile_HunkGapOfSixAndSevenAsync()
    {
        var dir = NewRepo("s02");
        var lines = NumberedLines(300, "b");
        WriteLines(dir, "large.txt", lines);
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("02_LargeFileHunkGaps", dir, true, () =>
        {
            // 10 and 17 leave six unchanged lines between them (git merges the hunks);
            // 17 and 25 leave seven (separate hunks); 100, 200 and 290 are far apart.
            foreach (var n in new[] { 10, 17, 25, 100, 200, 290 })
            {
                lines[n - 1] = $"b line {n:D3} changed";
            }

            WriteLines(dir, "large.txt", lines);
        });
    }

    [Test]
    public async Task Scenario03a_Rename_NoEditsAsync()
    {
        var dir = NewRepo("s03a");
        WriteLines(dir, "rename.txt", NumberedLines(20, "c"));
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("03a_RenameNoEdits", dir, false, () =>
        {
            RunGit(dir, "mv", "rename.txt", "renamed.txt");
        });
    }

    [Test]
    public async Task Scenario03b_Rename_WithEditsAsync()
    {
        var dir = NewRepo("s03b");
        var lines = NumberedLines(20, "c");
        WriteLines(dir, "rename.txt", lines);
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("03b_RenameWithEdits", dir, false, () =>
        {
            lines[2] = "c line 003 changed";
            lines[14] = "c line 015 changed";
            WriteLines(dir, "rename.txt", lines);
            RunGit(dir, "mv", "rename.txt", "renamed.txt");
        });
    }

    [Test]
    public async Task Scenario04a_Binary_AddedAsync()
    {
        var dir = NewRepo("s04a");
        WriteText(dir, "keep.txt", "keep\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("04a_BinaryAdded", dir, true, () => WriteBytes(dir, "data.bin", BinaryPayloadA));
    }

    [Test]
    public async Task Scenario04b_Binary_ModifiedAsync()
    {
        var dir = NewRepo("s04b");
        WriteBytes(dir, "data.bin", BinaryPayloadA);
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("04b_BinaryModified", dir, true, () => WriteBytes(dir, "data.bin", BinaryPayloadB));
    }

    [Test]
    public async Task Scenario05a_ModeChangeOnly_644To755Async()
    {
        var dir = NewRepo("s05a");
        WriteText(dir, "mode.sh", "echo one\necho two\necho three\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("05a_ModeOnly", dir, true, () => RunGit(dir, "update-index", "--chmod=+x", "mode.sh"));
    }

    [Test]
    public async Task Scenario05b_ModeChangePlusContentEdit_Async()
    {
        var dir = NewRepo("s05b");
        WriteText(dir, "mode.sh", "echo one\necho two\necho three\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("05b_ModeAndEdit", dir, true, () =>
        {
            WriteText(dir, "mode.sh", "echo one\necho TWO\necho three\n");
            RunGit(dir, "update-index", "--chmod=+x", "mode.sh");
        });
    }

    [Test]
    public async Task Scenario06a_CrlfContent_AutocrlfTrueAsync()
    {
        var dir = NewRepo("s06a", autocrlf: true);
        WriteText(dir, "crlf.txt", "one\r\ntwo\r\nthree\r\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("06a_CrlfAutocrlfTrue", dir, true, () =>
            WriteText(dir, "crlf.txt", "one\r\nTWO\r\nthree\r\n"));
    }

    [Test]
    public async Task Scenario06b_CrlfContent_GitattributesTextAutoAsync()
    {
        var dir = NewRepo("s06b");
        WriteText(dir, ".gitattributes", "* text=auto\n");
        WriteText(dir, "crlf.txt", "one\r\ntwo\r\nthree\r\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("06b_CrlfTextAuto", dir, true, () =>
            WriteText(dir, "crlf.txt", "one\r\nTWO\r\nthree\r\n"));
    }

    [Test]
    public async Task Scenario07a_NoFinalNewline_AddedAsync()
    {
        var dir = NewRepo("s07a");
        WriteText(dir, "keep.txt", "keep\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("07a_NoFinalNewlineAdded", dir, true, () =>
            WriteText(dir, "nonl.txt", "alpha\nbeta"));
    }

    [Test]
    public async Task Scenario07b_NoFinalNewline_RemovedAsync()
    {
        var dir = NewRepo("s07b");
        // The base lacks the final newline; the change adds one.
        WriteText(dir, "nonl.txt", "alpha\nbeta");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("07b_NoFinalNewlineGained", dir, true, () =>
            WriteText(dir, "nonl.txt", "alpha\nbeta\n"));
    }

    [Test]
    public async Task Scenario08a_EmptyFile_AddedAsync()
    {
        var dir = NewRepo("s08a");
        WriteText(dir, "keep.txt", "keep\n");
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("08a_EmptyFileAdded", dir, false, () => WriteText(dir, "empty.txt", string.Empty));
    }

    [Test]
    public async Task Scenario08b_EmptyFile_DeletedAsync()
    {
        var dir = NewRepo("s08b");
        WriteText(dir, "empty.txt", string.Empty);
        CommitAll(dir, "base");

        await PinStagedAndRangeAsync("08b_EmptyFileDeleted", dir, false, () => RunGit(dir, "rm", "-q", "empty.txt"));
    }

    // ---- measurement ----

    [Test]
    public void Measure_CliPatchVersusLibgit2Patch_MedianOf10()
    {
        var oneFile = SeedStagedRepo("m01", fileCount: 1);
        var twentyFiles = SeedStagedRepo("m20", fileCount: 20);

        MeasureAndReport("1-file", oneFile);
        MeasureAndReport("20-file", twentyFiles);
    }

    /// <summary>A repository with <paramref name="fileCount"/> files of 100 lines each, a few edits per file, all staged.</summary>
    private string SeedStagedRepo(string name, int fileCount)
    {
        var dir = NewRepo(name);
        for (var f = 0; f < fileCount; f++)
        {
            WriteLines(dir, $"f{f:D2}.txt", NumberedLines(100, $"m{f:D2}"));
        }

        CommitAll(dir, "base");

        for (var f = 0; f < fileCount; f++)
        {
            var lines = NumberedLines(100, $"m{f:D2}");
            lines[4] = $"m{f:D2} line 005 changed";
            lines[49] = $"m{f:D2} line 050 changed";
            lines[94] = $"m{f:D2} line 095 changed";
            WriteLines(dir, $"f{f:D2}.txt", lines);
        }

        RunGit(dir, "add", "-A");
        return dir;
    }

    private static void MeasureAndReport(string target, string dir)
    {
        var cli = GitPatchParityHarness.MedianMs(() =>
            GitPatchParityHarness.CliToolTextAsync(dir, "staged").GetAwaiter().GetResult());
        var libgit2 = GitPatchParityHarness.MedianMs(() =>
            GitPatchParityHarness.Libgit2ToolText(dir, "staged"));
        Report("MEASURE", target, $"cli={cli:F1};libgit2={libgit2:F1}");
    }
}
