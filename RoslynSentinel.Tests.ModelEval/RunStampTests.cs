using RoslynSentinel.Common;
using RoslynSentinel.Tests.ModelEval.LiveModel;

namespace RoslynSentinel.Tests.ModelEval;

[TestFixture]
public class RunStampTests
{
    [Test]
    public void ReadWorkingTreeStamp_ReturnsTheRepoHead()
    {
        var repoRoot = FindRepoRoot();

        // Other sessions commit into this shared working tree, so HEAD can move while the stamp is read.
        // Accept the HEAD from either side of the read; a stamp matching neither is still a real failure.
        var headBefore = RunGitRevParseHead(repoRoot);
        var stamp = ModelEvalAssemblySetUp.ReadWorkingTreeStamp(AppContext.BaseDirectory);
        var headAfter = RunGitRevParseHead(repoRoot);

        Assert.That(stamp, Is.Not.Null);
        Assert.That(stamp!.Value.CommitHash, Does.Match("^[0-9a-f]{40}$"));
        Assert.That(stamp.Value.CommitHash, Is.AnyOf(headBefore, headAfter));
    }

    [Test]
    public void ReadWorkingTreeStamp_OutsideARepo_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.That(ModelEvalAssemblySetUp.ReadWorkingTreeStamp(dir), Is.Null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void IsDirty_CountsTrackedChangesAndUntrackedSourceOnly()
    {
        Assert.That(ModelEvalAssemblySetUp.IsDirty(Array.Empty<WorkingTreeChange>()), Is.False);
        Assert.That(ModelEvalAssemblySetUp.IsDirty([new WorkingTreeChange("a.cs", null, ' ', 'M')]), Is.True);
        Assert.That(ModelEvalAssemblySetUp.IsDirty([new WorkingTreeChange("n.md", null, '?', '?')]), Is.False);
        Assert.That(ModelEvalAssemblySetUp.IsDirty([new WorkingTreeChange("n.cs", null, '?', '?')]), Is.True);
        Assert.That(ModelEvalAssemblySetUp.IsDirty([new WorkingTreeChange("x.csproj", null, '?', '?')]), Is.True);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new DirectoryNotFoundException("RoslynSentinel.slnx not found walking up from AppContext.BaseDirectory.");
    }

    private static string RunGitRevParseHead(string workingDirectory)
    {
        var head = new CliGitReader().GetHeadSha(workingDirectory, shortForm: false, TimeSpan.FromSeconds(20))?.Trim();
        if (head is null)
        {
            Assert.Inconclusive("git CLI did not answer within 20 s");
        }

        return head!;
    }
}
