namespace RoslynSentinel.Tests.ModelEval;

internal sealed class GoldenStepRepository
{
    private GoldenStepRepository(string repoDirectory)
    {
        RepoDirectory = repoDirectory;
    }

    public string RepoDirectory { get; }

    public static GoldenStepRepository Create(string repoDirectory, int lastStep, Func<int, IReadOnlyDictionary<string, string>> filesForStep)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public static string TagForStep(int step)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public void CheckoutStep(int step, string branchName)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public IReadOnlyList<string> ChangedPathsSinceStep(int step)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public string CommitWorkingTree(string subject)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }

    public string DiffAgainstStep(int step)
    {
        throw new NotImplementedException(); // TODO implement (Step 3)
    }
}
