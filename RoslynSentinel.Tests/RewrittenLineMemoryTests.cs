using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests;

/// <summary>The process-wide memory of server-side line rewrites (H3): what a stale oldContent is told, and when a row is dropped.</summary>
[TestFixture]
[Category("WorkspaceTools")]
public class RewrittenLineMemoryTests
{
    private static readonly string Path1 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rlm-tests", "A.cs");

    private static IReadOnlyDictionary<string, IReadOnlyList<(int Line, string OldText, string NewText)>> Rows(string path, params (int, string, string)[] rows)
        => new Dictionary<string, IReadOnlyList<(int Line, string OldText, string NewText)>> { [path] = rows.Select(r => (r.Item1, r.Item2, r.Item3)).ToList() };

    [Test]
    public void Explain_OldTextOfARewrittenLine_NamesFileLineSourceAndNewText()
    {
        var memory = new RewrittenLineMemory();
        memory.Remember("RenameSymbol", Rows(Path1, (4, "c.OldName(1);", "c.NewName(1);")));

        var note = memory.Explain(Path1, "x = 1;\n    c.OldName(1);\n", "x = 1;\n    c.NewName(1);\n");

        Assert.That(note, Is.EqualTo("A.cs:4 was rewritten by RenameSymbol earlier: it now reads 'c.NewName(1);'. Re-read the file or use the new text."));
    }

    [Test]
    public void Explain_TypoOrUnrelatedText_GivesNoNote()
    {
        var memory = new RewrittenLineMemory();
        memory.Remember("RenameSymbol", Rows(Path1, (4, "c.OldName(1);", "c.NewName(1);")));

        Assert.That(memory.Explain(Path1, "c.OtherName(1);", "c.NewName(1);"), Is.Null);
    }

    [Test]
    public void Explain_RowThatAnUnhookedWriteMadeStale_GivesNoNote()
    {
        var memory = new RewrittenLineMemory();
        memory.Remember("RenameSymbol", Rows(Path1, (4, "c.OldName(1);", "c.NewName(1);")));

        // The file no longer holds the new line (something else rewrote it), so "it now reads ..." would mislead.
        Assert.That(memory.Explain(Path1, "c.OldName(1);", "c.ThirdName(1);"), Is.Null);
        // The old line is still in the file (an identical line elsewhere): the text is not missing because of the rewrite.
        Assert.That(memory.Explain(Path1, "c.OldName(1);", "c.OldName(1);\nc.NewName(1);"), Is.Null);
    }

    [Test]
    public void Forget_DropsTheFilesRows()
    {
        var memory = new RewrittenLineMemory();
        memory.Remember("the rename lift", Rows(Path1, (4, "c.OldName(1);", "c.NewName(1);")));
        memory.Forget(Path1);

        Assert.That(memory.Explain(Path1, "c.OldName(1);", "c.NewName(1);"), Is.Null);
    }

    [Test]
    public void Explain_AfterTheMaxAge_GivesNoNote()
    {
        var now = new DateTime(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
        var memory = new RewrittenLineMemory(() => now);
        memory.Remember("RenameSymbol", Rows(Path1, (4, "c.OldName(1);", "c.NewName(1);")));

        now += RewrittenLineMemory.MaxAge - TimeSpan.FromSeconds(1);
        Assert.That(memory.Explain(Path1, "c.OldName(1);", "c.NewName(1);"), Is.Not.Null, "just inside the age cap");
        now += TimeSpan.FromSeconds(2);
        Assert.That(memory.Explain(Path1, "c.OldName(1);", "c.NewName(1);"), Is.Null, "past the age cap");
    }

    [Test]
    public void Remember_KeepsAtMostThirtyRowsPerFileAndFiftyFiles()
    {
        var memory = new RewrittenLineMemory();
        var rows = Enumerable.Range(1, 40).Select(i => (i, $"call{i}();", $"renamed{i}();")).ToArray();
        memory.Remember("RenameSymbol", Rows(Path1, rows));

        var fileText = string.Join("\n", rows.Select(r => r.Item3));
        Assert.That(memory.Explain(Path1, "call1();", fileText), Is.Null, "the oldest rows beyond 30 were dropped");
        Assert.That(memory.Explain(Path1, "call40();", fileText), Is.Not.Null);

        for (var i = 0; i < 60; i++)
        {
            memory.Remember("RenameSymbol", Rows(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rlm-tests", $"F{i}.cs"), (1, "old();", "new();")));
        }

        Assert.That(memory.Explain(Path1, "call40();", fileText), Is.Null, "the first file was evicted past 50 files");
        Assert.That(memory.Explain(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rlm-tests", "F59.cs"), "old();", "new();"), Is.Not.Null);
    }
}
