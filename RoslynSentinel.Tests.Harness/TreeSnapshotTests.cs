using NUnit.Framework;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class TreeSnapshotTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tree-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Test]
    public void Capture_ExcludesBinObjAndGit()
    {
        Write("src/Program.cs", "class Program { }");
        Write("bin/Debug/app.dll", "binary");
        Write("src/obj/project.assets.json", "{}");
        Write(".git/HEAD", "ref: refs/heads/master");
        Write(".vs/state.json", "{}");
        Write("TestResults/run.trx", "<trx />");
        Write(".roslynsentinel/ledger/entry.json", "{}");
        Write(".roslynsentinel/write.lock", "");

        var snapshot = TreeSnapshot.Capture(_dir);

        Assert.That(snapshot.Keys, Is.EquivalentTo(new[] { "src/Program.cs" }));
        Assert.That(snapshot.Keys, Has.None.StartsWith(".roslynsentinel/"));
        Assert.That(snapshot["src/Program.cs"], Has.Length.EqualTo(64));
        Assert.That(snapshot["src/Program.cs"], Is.EqualTo(snapshot["src/Program.cs"].ToLowerInvariant()));
    }

    [Test]
    public void Diff_ListsAddedChangedAndRemoved()
    {
        Write("a.cs", "one");
        Write("b.cs", "two");
        Write("c.cs", "three");
        var before = TreeSnapshot.Capture(_dir);

        Write("a.cs", "one changed");
        File.Delete(Path.Combine(_dir, "b.cs"));
        Write("sub/d.cs", "four");
        var after = TreeSnapshot.Capture(_dir);

        var diff = TreeSnapshot.Diff(before, after);

        Assert.That(diff, Is.EqualTo(new[] { "added: sub/d.cs", "changed: a.cs", "removed: b.cs" }));
    }

    [Test]
    public void Diff_IdenticalTrees_IsEmpty()
    {
        Write("a.cs", "one");
        Write("sub/b.cs", "two");

        var first = TreeSnapshot.Capture(_dir);
        var second = TreeSnapshot.Capture(_dir);

        Assert.That(TreeSnapshot.Diff(first, second), Is.Empty);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
