using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Engines.Basic;
using static RoslynSentinel.Tests.Battery.Basic.GitTestRepo;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
[Category("OutOfProcess")] // starts git/dotnet/powershell processes; excluded from RunTest suite "fast" (the default), run with suite "full"
public class ResidualMentionScannerTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "ResidualMentionScanner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            // git marks object files read-only; clear that so the tree can be deleted.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteFileUnderRoot(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static Solution BuildSolution(params (string ProjectName, string FilePath, string Text)[] documents)
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        foreach (var group in documents.GroupBy(d => d.ProjectName))
        {
            var projectId = ProjectId.CreateNewId();
            solution = solution.AddProject(projectId, group.Key, group.Key, LanguageNames.CSharp);
            foreach (var document in group)
            {
                solution = solution.AddDocument(
                    DocumentId.CreateNewId(projectId),
                    Path.GetFileName(document.FilePath),
                    SourceText.From(document.Text),
                    filePath: document.FilePath);
            }
        }

        return solution;
    }

    private static ResidualBucket BucketFor(List<ResidualMention> mentions, string lineMarker)
    {
        var hits = mentions.Where(m => m.LineText.Contains(lineMarker, StringComparison.Ordinal)).ToList();
        Assert.That(hits, Has.Count.EqualTo(1), $"Expected exactly one mention on a line containing '{lineMarker}'.");
        return hits[0].Bucket;
    }

    [Test]
    public void BuildPattern_Namespace_MatchesDottedTokenBoundaryOnly()
    {
        var pattern = ResidualMentionScanner.BuildPattern("RoslynSentinel.Engines.Basic", isNamespace: true);

        Assert.Multiple(() =>
        {
            Assert.That(pattern.IsMatch("RoslynSentinel.Engines.Basic"), Is.True);
            Assert.That(pattern.IsMatch("RoslynSentinel.Engines.Basic.Sub"), Is.True);
            Assert.That(pattern.IsMatch("using RoslynSentinel.Engines.Basic;"), Is.True);
            Assert.That(pattern.IsMatch("RoslynSentinel.Engines.BasicX"), Is.False);
            Assert.That(pattern.IsMatch("Other.RoslynSentinel.Engines.Basic"), Is.False);
            Assert.That(pattern.IsMatch("RoslynSentinel.Engines.Advanced"), Is.False);
        });
    }

    [Test]
    public void BuildPattern_SimpleName_MatchesWholeWordOnly()
    {
        var pattern = ResidualMentionScanner.BuildPattern("Foo", isNamespace: false);

        Assert.Multiple(() =>
        {
            Assert.That(pattern.IsMatch("Foo"), Is.True);
            Assert.That(pattern.IsMatch("FooBar"), Is.False);
            Assert.That(pattern.IsMatch("xFoo"), Is.False);
        });
    }

    [Test]
    public async Task ScanAsync_CSharpDocument_BucketsByTriviaTokenAndPathPosition()
    {
        var source = string.Join("\n", new[]
        {
        "using OldName = System.Text.StringBuilder;",
        "",
        "namespace Demo;",
        "",
        "// OldName in a line comment",
        "/// <summary>OldName in a doc comment</summary>",
        "public class Holder",
        "{",
        "    public string Text = \"OldName in a literal\";",
        "    public string Path = \"OldName/File.cs::M\";",
        "    public void M()",
        "    {",
        "        var OldName = 1;",
        "    }",
        "}",
    });
        var solution = BuildSolution(("Demo", Path.Combine(_root, "Demo", "Holder.cs"), source));

        var mentions = await ResidualMentionScanner.ScanAsync(solution, _root, "OldName", isNamespace: false);

        Assert.Multiple(() =>
        {
            Assert.That(mentions, Has.Count.EqualTo(6));
            Assert.That(BucketFor(mentions, "using OldName"), Is.EqualTo(ResidualBucket.Code));
            Assert.That(BucketFor(mentions, "line comment"), Is.EqualTo(ResidualBucket.Comment));
            Assert.That(BucketFor(mentions, "doc comment"), Is.EqualTo(ResidualBucket.Comment));
            Assert.That(BucketFor(mentions, "in a literal"), Is.EqualTo(ResidualBucket.String));
            Assert.That(BucketFor(mentions, "File.cs::M"), Is.EqualTo(ResidualBucket.PathLike));
            Assert.That(BucketFor(mentions, "var OldName"), Is.EqualTo(ResidualBucket.Code));
        });
    }

    [Test]
    public async Task ScanAsync_NonSourceFiles_ReturnedAsNonSourceExceptPathLike()
    {
        WriteFileUnderRoot("notes.md", "Mentions OldName here.\n");
        WriteFileUnderRoot("a.csproj", "<!-- OldName -->\n");
        WriteFileUnderRoot("b.json", "{ \"k\": \"OldName\" }\n");
        WriteFileUnderRoot("c.ps1", "# OldName\n");
        WriteFileUnderRoot("d.props", "<!-- OldName -->\n");
        WriteFileUnderRoot("e.targets", "<!-- OldName -->\n");
        WriteFileUnderRoot("paths.md", "Layout: OldName/Impl.cs\n");

        var mentions = await ResidualMentionScanner.ScanAsync(new AdhocWorkspace().CurrentSolution, _root, "OldName", isNamespace: false);

        var byFile = mentions.ToDictionary(m => m.FilePath.Relative, m => m, StringComparer.OrdinalIgnoreCase);
        Assert.Multiple(() =>
        {
            Assert.That(mentions, Has.Count.EqualTo(7));
            foreach (var file in new[] { "notes.md", "a.csproj", "b.json", "c.ps1", "d.props", "e.targets" })
            {
                Assert.That(byFile.ContainsKey(file), Is.True, $"Expected a mention in {file}.");
                Assert.That(byFile[file].Bucket, Is.EqualTo(ResidualBucket.NonSource), file);
                Assert.That(byFile[file].FilePath.Absolute, Is.EqualTo(Path.Combine(_root, file)), file);
            }

            Assert.That(byFile.ContainsKey("paths.md"), Is.True, "Expected a mention in paths.md.");
            Assert.That(byFile["paths.md"].Bucket, Is.EqualTo(ResidualBucket.PathLike));
        });
    }

    [Test]
    public async Task ScanAsync_SkippedDirectoriesGeneratedFilesAndExtensions_AreNotReturned()
    {
        WriteFileUnderRoot(Path.Combine("bin", "Debug", "x.md"), "OldName\n");
        WriteFileUnderRoot(Path.Combine("obj", "y.json"), "{ \"k\": \"OldName\" }\n");
        WriteFileUnderRoot(Path.Combine(".git", "z.md"), "OldName\n");
        WriteFileUnderRoot(Path.Combine(".roslynsentinel", "w.md"), "OldName\n");
        WriteFileUnderRoot(Path.Combine("Worktree", "nested", "v.md"), "OldName\n");
        WriteFileUnderRoot("notes.txt", "OldName\n");
        WriteFileUnderRoot("Keep.md", "OldName kept\n");

        var solution = BuildSolution(
            ("P", Path.Combine(_root, "Foo.g.cs"), "// OldName generated\n"),
            ("P", Path.Combine(_root, "obj", "Gen.cs"), "// OldName in obj\n"),
            ("P", Path.Combine(_root, "Real.cs"), "// OldName real\n"));

        var mentions = await ResidualMentionScanner.ScanAsync(solution, _root, "OldName", isNamespace: false);

        Assert.Multiple(() =>
        {
            Assert.That(mentions.Select(m => m.FilePath.Relative), Is.EquivalentTo(new[] { "Keep.md", "Real.cs" }));
            Assert.That(mentions.Single(m => m.FilePath.Relative == "Real.cs").Bucket, Is.EqualTo(ResidualBucket.Comment));
        });
    }

    [Test]
    public async Task ScanAsync_GitRepo_ReportsTrackedMarkdownMention()
    {
        InitGitRepo();
        WriteFileUnderRoot("notes.md", "Mentions OldName here.\n");
        RunGit(_root, "add", "notes.md");

        var mentions = await ResidualMentionScanner.ScanAsync(new AdhocWorkspace().CurrentSolution, _root, "OldName", isNamespace: false);

        Assert.That(mentions.Select(m => m.FilePath.Relative), Is.EqualTo(new[] { "notes.md" }));
    }

    [Test]
    public async Task ScanAsync_GitRepo_IgnoredAndUntrackedNonSourceFiles_AreNotReported()
    {
        InitGitRepo();
        WriteFileUnderRoot(".gitignore", "bin-vscode/\n");
        WriteFileUnderRoot("notes.md", "Mentions OldName here.\n");
        WriteFileUnderRoot(Path.Combine("bin-vscode", "app.deps.json"), "{ \"k\": \"OldName\" }\n");
        WriteFileUnderRoot("untracked.md", "OldName untracked\n");
        RunGit(_root, "add", ".gitignore", "notes.md");

        var mentions = await ResidualMentionScanner.ScanAsync(new AdhocWorkspace().CurrentSolution, _root, "OldName", isNamespace: false);

        Assert.That(mentions.Select(m => m.FilePath.Relative), Is.EqualTo(new[] { "notes.md" }));
    }

    // Turns _root into a git repository. No commit is made: --cached reads the index.
    private void InitGitRepo()
    {
        RunGit(_root, "init", "--quiet");
    }

    [Test]
    public async Task ScanAsync_SameCSharpPathInTwoProjects_YieldsOneMentionPerLine()
    {
        var sharedPath = Path.Combine(_root, "Shared.cs");
        var text = "// OldName once\nvar OldName = 2;\n";
        var solution = BuildSolution(
            ("P1", sharedPath, text),
            ("P2", sharedPath, text));

        var mentions = await ResidualMentionScanner.ScanAsync(solution, _root, "OldName", isNamespace: false);

        Assert.Multiple(() =>
        {
            Assert.That(mentions, Has.Count.EqualTo(2));
            Assert.That(mentions.Select(m => m.LineNumber).OrderBy(n => n), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(mentions.All(m => m.FilePath.Relative == "Shared.cs"), Is.True);
        });
    }
}
