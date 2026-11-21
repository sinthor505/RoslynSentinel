namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// <see cref="RoslynSentinel.Common.AgentLoop.EvalHostTable.DeriveSuffix(string)"/>: only the exact alias base URLs map to the short alias folder;
/// any other URL gets the sanitised-URL suffix that roslynsentinel-modeleval.ps1 computes. No live model.
/// </summary>
[TestFixture]
public class ModelTestingResultsArchiverTests
{
    [TestCase("http://192.168.1.112:1234/v1", "112")]
    [TestCase("http://192.168.1.113:1234/v1", "113")]
    [TestCase("http://192.168.10.85:1234/v1", "85")]
    [TestCase("http://192.168.1.112:1234/v1/", "112")]
    [TestCase("HTTP://192.168.1.113:1234/V1", "113")]
    public void DeriveHostSuffix_ExactAliasUrl_MapsToTheAlias(string baseUrl, string expected)
    {
        Assert.That(RoslynSentinel.Common.AgentLoop.EvalHostTable.DeriveSuffix(baseUrl), Is.EqualTo(expected));
    }

    [TestCase("http://192.168.1.112:8888/v1", "http___192_168_1_112_8888_v1")]
    [TestCase("http://192.168.1.113:8888/v1", "http___192_168_1_113_8888_v1")]
    [TestCase("http://localhost:1234/v1", "http___localhost_1234_v1")]
    [TestCase("http://192.168.10.85:8888/v1", "http___192_168_10_85_8888_v1")]
    public void DeriveHostSuffix_AnyOtherUrl_GetsTheSanitisedUrl(string baseUrl, string expected)
    {
        Assert.That(RoslynSentinel.Common.AgentLoop.EvalHostTable.DeriveSuffix(baseUrl), Is.EqualTo(expected));
    }

    // NonParallelizable: both tests set process-global environment variables that ScratchRoot reads,
    // so they must not interleave with each other or with other tests that resolve the results root.
    [Test]
    [NonParallelizable]
    public void ArchiveRunDirectory_UsesTheScratchRoot_AndWritesNothingUnderTheRepo()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var originalScratch = Environment.GetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.EnvironmentVariable);
        var originalResults = Environment.GetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.ResultsRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.EnvironmentVariable, Path.Combine(tmp, "scratch"));
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.ResultsRootEnvironmentVariable, null);

            var runDirectory = Path.Combine(tmp, "work", "model-eval", "SomeTest", "20260101-000000-000");
            Directory.CreateDirectory(runDirectory);
            File.WriteAllText(Path.Combine(runDirectory, "agent.log"), "log");

            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);

            var suffix = RoslynSentinel.Common.AgentLoop.EvalHostTable.DeriveSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl);
            var archivedLog = Path.Combine(tmp, "scratch", "ModelTestingResults", suffix, "SomeTest", "20260101-000000-000", "agent.log");
            Assert.That(File.Exists(archivedLog), Is.True, $"expected archived log at '{archivedLog}'");

            var repoArchive = Path.Combine(RoslynSentinel.Common.RepoRootLocator.Find(), "ModelTestingResults", suffix, "SomeTest", "20260101-000000-000");
            Assert.That(Directory.Exists(repoArchive), Is.False, $"archive must not be written under the repo at '{repoArchive}'");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.EnvironmentVariable, originalScratch);
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.ResultsRootEnvironmentVariable, originalResults);
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }

    [Test]
    [NonParallelizable]
    public void ArchiveRunDirectory_ResultsRootEnvBeatsTheScratchRoot()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var originalScratch = Environment.GetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.EnvironmentVariable);
        var originalResults = Environment.GetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.ResultsRootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.EnvironmentVariable, Path.Combine(tmp, "scratch"));
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.ResultsRootEnvironmentVariable, Path.Combine(tmp, "results"));

            var runDirectory = Path.Combine(tmp, "work", "model-eval", "SomeTest", "20260101-000000-000");
            Directory.CreateDirectory(runDirectory);
            File.WriteAllText(Path.Combine(runDirectory, "agent.log"), "log");

            ModelTestingResultsArchiver.ArchiveRunDirectory(runDirectory);

            var suffix = RoslynSentinel.Common.AgentLoop.EvalHostTable.DeriveSuffix(RoslynSentinel.Common.LlmOptions.BaseUrl);
            var archivedLog = Path.Combine(tmp, "results", suffix, "SomeTest", "20260101-000000-000", "agent.log");
            Assert.That(File.Exists(archivedLog), Is.True, $"expected archived log at '{archivedLog}'");

            var scratchResults = Path.Combine(tmp, "scratch", "ModelTestingResults");
            Assert.That(Directory.Exists(scratchResults), Is.False, $"scratch results root must not be used at '{scratchResults}'");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.EnvironmentVariable, originalScratch);
            Environment.SetEnvironmentVariable(RoslynSentinel.Common.ScratchRoot.ResultsRootEnvironmentVariable, originalResults);
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }
}
