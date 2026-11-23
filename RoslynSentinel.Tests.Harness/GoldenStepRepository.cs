using RoslynSentinel.Tests.Battery.Basic;

namespace RoslynSentinel.Tests.ModelEval;

public sealed class GoldenStepRepository
{
    private GoldenStepRepository(string repoDirectory)
    {
        RepoDirectory = repoDirectory;
    }

    public string RepoDirectory { get; }

    public static GoldenStepRepository Create(string repoDirectory, int lastStep, Func<int, IReadOnlyDictionary<string, string>> filesForStep)
    {
        GitTestRepo.InitRepo(repoDirectory);
        GitTestRepo.RunGit(repoDirectory, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(repoDirectory, ".gitignore"), "bin/\nobj/\n.vs/\n.roslynsentinel/\n");
        GitTestRepo.CommitAll(repoDirectory, "Sample as copied by the fixture");

        for (var step = 0; step <= lastStep; step++)
        {
            var files = filesForStep(step);
            if (files.Count == 0)
            {
                // A step with no files tags the current commit; CommitAll would add an empty commit.
                GitTestRepo.RunGit(repoDirectory, "tag", TagForStep(step));
                continue;
            }

            foreach (var (relativePath, content) in files)
            {
                var fullPath = Path.Combine(repoDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, content);
            }

            GitTestRepo.CommitAll(repoDirectory, $"Golden step {step}");
            GitTestRepo.RunGit(repoDirectory, "tag", TagForStep(step));
        }

        return new GoldenStepRepository(repoDirectory);
    }

    public static string TagForStep(int step)
    {
        return $"step-{step}";
    }

    public void CheckoutStep(int step, string branchName)
    {
        GitTestRepo.RunGit(RepoDirectory, "checkout", "-B", branchName, TagForStep(step));
    }

    public IReadOnlyList<string> ChangedPathsSinceStep(int step)
    {
        GitTestRepo.RunGit(RepoDirectory, "add", "-A");
        var output = GitTestRepo.RunGit(RepoDirectory, "diff", "--cached", "--name-only", TagForStep(step));
        return output
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToList();
    }

    public string CommitWorkingTree(string subject)
    {
        return GitTestRepo.CommitAll(RepoDirectory, subject);
    }

    public string DiffAgainstStep(int step)
    {
        return GitTestRepo.RunGit(RepoDirectory, "diff", TagForStep(step), "HEAD");
    }
}
