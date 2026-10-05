using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Basic;

// Slice 4 of the MoveMember reformat fix (docs/current/blockers/
// blocking_error_movemember_reformats_entire_source_and_caller_files.md): the shared write path
// must make a whole-file reformat visible (per-file changed-line counts) or refuse it (a change
// that alters an existing file's line-ending style). Driven through ValidateAndApplyHelper, the
// validate-then-write wrapper every tool uses, on an in-memory workspace.
[TestFixture]
public class WriteChokepointGuardrailTests
{
    private const string CrlfSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\r\n}\r\n";

    private static async Task<ApplyOutcome> ApplyAsync(
        InMemoryWorkspace workspace, Dictionary<FilePathWrapper, string> changes, bool dryRun = false)
    {
        var validation = new ValidationEngine(workspace.Manager, new DiffEngine(), NullLogger<ValidationEngine>.Instance);
        return await ValidateAndApplyHelper.ValidateAndApplyAsync(
            validation, workspace.Manager, NullLogger.Instance, changes, "GuardrailTest", dryRun: dryRun);
    }

    [Test]
    public async Task CrlfFile_RewrittenAsLf_IsRefusedWithDistinctCodeAndNothingWrittenAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("A.cs", CrlfSource));
        var path = workspace.PathOf("A.cs");
        var allLf = CrlfSource.Replace("\r\n", "\n").Replace("=> 1;", "=> 2;");

        var outcome = await ApplyAsync(workspace, new() { [path] = allLf });

        Assert.That(outcome.Error, Is.Not.Null);
        Assert.That(outcome.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.EolChangeRefused));
        Assert.That(outcome.Error.Message, Does.Contain(path).And.Contain("before: CRLF").And.Contain("after: LF"));
        Assert.That(outcome.Error.Message, Does.Contain("NormalizeWhitespace"), "message must name the likely cause");
        Assert.That(workspace.ReadText("A.cs"), Is.EqualTo(CrlfSource), "a refused change must not be written");
    }

    [Test]
    public async Task CrlfFile_GainingLfLines_IsRefusedAsMixedAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("A.cs", CrlfSource));
        var path = workspace.PathOf("A.cs");
        // One new method spliced in with LF line breaks, the rest still CRLF.
        var mixed = CrlfSource.Replace(
            "    public int X() => 1;\r\n",
            "    public int X() => 1;\n    public int Y() => 2;\n");

        var outcome = await ApplyAsync(workspace, new() { [path] = mixed });

        Assert.That(outcome.Error, Is.Not.Null);
        Assert.That(outcome.Error!.ErrorCode, Is.EqualTo(ToolErrorCode.EolChangeRefused));
        Assert.That(outcome.Error.Message, Does.Contain("before: CRLF").And.Contain("after: mixed"));
        Assert.That(workspace.ReadText("A.cs"), Is.EqualTo(CrlfSource));
    }

    [Test]
    public async Task DryRun_AlsoReportsTheEolRefusalAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("A.cs", CrlfSource));
        var allLf = CrlfSource.Replace("\r\n", "\n");

        var outcome = await ApplyAsync(workspace, new() { [workspace.PathOf("A.cs")] = allLf }, dryRun: true);

        Assert.That(outcome.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.EolChangeRefused));
    }

    [Test]
    public async Task LfFile_EditedAsLf_PassesAndReportsLineCountsAsync()
    {
        var lfSource = CrlfSource.Replace("\r\n", "\n");
        using var workspace = InMemoryWorkspace.Create(("A.cs", lfSource));
        var path = workspace.PathOf("A.cs");
        var edited = lfSource.Replace("=> 1;", "=> 2;");

        var outcome = await ApplyAsync(workspace, new() { [path] = edited });

        Assert.That(outcome.Error, Is.Null);
        Assert.That(workspace.ReadText("A.cs"), Is.EqualTo(edited));
        var change = outcome.LineChanges!.Single();
        Assert.That(change.FilePath, Is.EqualTo(path));
        Assert.That((change.LinesAdded, change.LinesRemoved), Is.EqualTo((1, 1)), "one line edited in place");
    }

    [Test]
    public async Task MixedFile_KeepingItsDominantStyle_IsNotRefusedAsync()
    {
        // 4 CRLF + 1 LF: already mixed, dominant CRLF. A small edit that keeps it that way is fine.
        var mixedSource = "namespace T;\r\npublic class A\r\n{\r\n    public int X() => 1;\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("A.cs", mixedSource));

        var outcome = await ApplyAsync(workspace, new() { [workspace.PathOf("A.cs")] = mixedSource.Replace("=> 1;", "=> 2;") });

        Assert.That(outcome.Error, Is.Null);
    }

    [Test]
    public async Task NewFile_IsExemptFromTheEolGuardAsync()
    {
        using var workspace = InMemoryWorkspace.Create(("A.cs", CrlfSource));
        var newPath = workspace.PathOf("B.cs");
        var newContent = "namespace T;\npublic class B\n{\n}\n";

        var outcome = await ApplyAsync(workspace, new() { [newPath] = newContent });

        Assert.That(outcome.Error, Is.Null);
        Assert.That(workspace.ReadText("B.cs"), Is.EqualTo(newContent));
        var change = outcome.LineChanges!.Single();
        Assert.That((change.LinesAdded, change.LinesRemoved), Is.EqualTo((4, 0)), "a new file is all added lines");
    }

    [Test]
    public void LineCounts_AWholeFileEolRewrite_ShowsEveryLineChanged()
    {
        // The blocker's signature: nothing but line endings differ, yet every line is rewritten.
        var change = FileLineChange.Compute("A.cs", CrlfSource, CrlfSource.Replace("\r\n", "\n"));

        Assert.That((change.LinesAdded, change.LinesRemoved), Is.EqualTo((5, 5)));
    }

    [Test]
    public void LineCounts_LostFinalNewline_IsVisible()
    {
        var change = FileLineChange.Compute("A.cs", "a\nb\n", "a\nb");

        Assert.That((change.LinesAdded, change.LinesRemoved), Is.EqualTo((1, 1)));
    }

    [Test]
    public void LineCounts_InsertionAndRemoval_AreCountedSeparately()
    {
        var change = FileLineChange.Compute("A.cs", "a\nb\nc\nd\n", "a\nc\nd\ne\nf\n");

        Assert.That((change.LinesAdded, change.LinesRemoved), Is.EqualTo((2, 1)));
    }

    [TestCase("a\r\nb\r\n", "a\r\nb\r\nc\r\n", false)]
    [TestCase("a\r\nb\r\n", "a\nb\n", true)]
    [TestCase("a\nb\n", "a\r\nb\r\n", true)]
    [TestCase("a\nb\n", "a\nb\r\n", true)]
    [TestCase("a\r\nb\r\nc\n", "a\r\nb\r\nc\r\nd\n", false)]
    [TestCase("a\r\nb\r\nc\n", "a\nb\nc\n", true)]
    [TestCase("single line", "a\r\nb\r\n", false)]
    [TestCase("a\r\nb\r\n", "no breaks", false)]
    public void Check_ExistingCsFile_FlagsOnlyEolStyleChanges(string before, string after, bool expectViolation)
    {
        var violation = EolChangeGuard.Check("C:\\x\\A.cs", before, after);

        Assert.That(violation is not null, Is.EqualTo(expectViolation));
    }

    [Test]
    public void Check_NewFile_NullBefore_IsAlwaysExempt()
    {
        Assert.That(EolChangeGuard.Check("C:\\x\\A.cs", null, "a\nb\r\n"), Is.Null);
    }

    // Blocker undolastapply_refused_eol_after_apply_normalized_mixed_file: the write path normalizes only the lines a
    // change added or rewrote; untouched lines keep their own terminator even in a mixed-EOL file.
    [TestCase("a\nb\r\nc\n", "a\nX\nb\r\nc\n", "\n", "a\nX\nb\r\nc\n", Description = "inserted LF line, stray CRLF kept")]
    [TestCase("a\nb\r\nc\n", "a\nX\r\nb\r\nc\n", "\n", "a\nX\nb\r\nc\n", Description = "inserted CRLF line in an LF-dominant file becomes LF")]
    [TestCase("a\nb\r\nc\n", "a\nb\nc\nd\n", "\n", "a\nb\r\nc\nd\n", Description = "proposal that was whole-content normalized by a tool: untouched CRLF line restored, new line LF")]
    [TestCase("a\r\nb\nc\n", "a\r\nc\n", "\n", "a\r\nc\n", Description = "removed line takes its own terminator with it")]
    [TestCase("a\r\nb\r\nc\n", "a\r\nX\nb\r\nc\n", "\r\n", "a\r\nX\r\nb\r\nc\n", Description = "CRLF-dominant file: inserted LF line becomes CRLF")]
    [TestCase("a\nb\r\nc\nd\ne\r\nf\n", "a\nX\nb\nc\nd\nY\nf\n", "\n", "a\nX\nb\r\nc\nd\nY\nf\n", Description = "LCS path: stray CRLF on a matched middle line survives, rewritten lines are LF")]
    [TestCase("a\nb\nc\n", "a\nb\nX\nc\n", "\n", "a\nb\nX\nc\n", Description = "pure LF behaves as before")]
    [TestCase("a\r\nb\r\n", "a\r\nb\r\nc\r\n", "\r\n", "a\r\nb\r\nc\r\n", Description = "pure CRLF behaves as before")]
    [TestCase("a\nb", "a\nb\nc", "\n", "a\nb\nc", Description = "final line gaining a successor gets the dominant EOL; new final line stays unterminated")]
    [TestCase("a\nb\n", "a\nb", "\n", "a\nb", Description = "lost final newline is not re-added")]
    public void NormalizeEolOfChangedLines_ChangesOnlyTheTouchedLines(string before, string after, string dominant, string expected)
    {
        Assert.That(EolUtilities.NormalizeEolOfChangedLines(before, after, dominant), Is.EqualTo(expected));
    }

    [Test]
    public void NormalizeEolOfChangedLines_MiddleTooLargeForTheLcsTable_TreatsItAsChangedWithoutFailing()
    {
        // 3000 x 3000 differing lines exceeds the LCS table cap: the whole middle is treated as rewritten, so it takes
        // the dominant EOL (over-normalizes) instead of throwing or allocating a huge table.
        var before = string.Concat(Enumerable.Range(0, 3000).Select(i => $"old{i}\r\n"));
        var after = string.Concat(Enumerable.Range(0, 3000).Select(i => $"new{i}\r\n"));

        var result = EolUtilities.NormalizeEolOfChangedLines(before, after, "\n");

        Assert.That(result, Is.EqualTo(after.Replace("\r\n", "\n")));
    }

    [Test]
    public async Task Chokepoint_RefusesEolChangeBeforeTouchingDiskAsync()
    {
        // Backstop for callers that reach ApplyProposedChangesAsync directly instead of going
        // through ValidateAndApplyHelper. Real manager + real file: the guard compares against the
        // on-disk pre-image and must refuse before the first write.
        var dir = Path.Combine(Path.GetTempPath(), "GuardrailChokepoint", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "A.cs");
        await File.WriteAllTextAsync(file, CrlfSource);
        try
        {
            using var manager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
            manager.SetTestSolution(new AdhocWorkspace().CurrentSolution);

            var result = await manager.ApplyProposedChangesAsync(
                new() { [file] = CrlfSource.Replace("\r\n", "\n") }, retryCount: 0, validateChanges: false);

            Assert.That(!result.IsError, Is.False);
            Assert.That(result.RefusalCode, Is.EqualTo(ToolErrorCode.EolChangeRefused));
            Assert.That(result.SucceededFiles, Is.Empty);
            Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(CrlfSource), "disk must be untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The guard only helps if the tools stop producing the bad output in the first place. These two
    // engines used to hard-code an EOL (CRLF for the using line, LF for the doc-comment lines) and
    // mixed it into files that used the other style.
    [TestCase("\n", "LF")]
    [TestCase("\r\n", "CRLF")]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task AddUsingDirective_KeepsTheFilesLineEndingAsync(string eol, string expectedStyle)
    {
        var source = string.Join(eol, "namespace T;", "", "public class A", "{", "    public int X() => 1;", "}", "");
        using var workspace = InMemoryWorkspace.Create(("A.cs", source));
        var engine = new BasicRefactoringEngine(workspace.Manager, NullLogger<BasicRefactoringEngine>.Instance, new SentinelConfiguration());

        var result = await engine.AddUsingDirectiveAsync(workspace.PathOf("A.cs"), "System.Linq");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("using System.Linq;"));
        var after = EolChangeGuard.Count(result.UpdatedText!);
        Assert.That(after.IsSingleStyle && after.Dominant == expectedStyle, Is.True, after.Describe());
    }

    [TestCase("\n", "LF")]
    [TestCase("\r\n", "CRLF")]
    [Category("BasicRefactoringEngine")] // sentinel:auto-category
    public async Task AddSummaryComment_KeepsTheFilesLineEndingAsync(string eol, string expectedStyle)
    {
        var source = string.Join(eol, "namespace T;", "", "public class A", "{", "    public int X(int a) => a;", "}", "");
        using var workspace = InMemoryWorkspace.Create(("A.cs", source));
        var engine = new BasicRefactoringEngine(workspace.Manager, NullLogger<BasicRefactoringEngine>.Instance, new SentinelConfiguration());
        var solution = workspace.Manager.CurrentSolution!;
        var document = solution.GetDocument(solution.GetDocumentIdsWithFilePath(workspace.PathOf("A.cs")).Single())!;

        var result = await engine.AddSummaryCommentCoreAsync(
            document, workspace.PathOf("A.cs"), "X", "Returns the value.", null, null, null, null, CancellationToken.None);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"));
        var after = EolChangeGuard.Count(result.UpdatedText!);
        Assert.That(after.IsSingleStyle && after.Dominant == expectedStyle, Is.True, after.Describe());
    }
}
