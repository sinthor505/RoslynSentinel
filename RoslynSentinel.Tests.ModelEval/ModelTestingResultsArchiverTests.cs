using RoslynSentinel.Common.AgentLoop;
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

    [Test]
    [NonParallelizable]
    public void ArchiveRunDirectory_FromTheTestBody_WritesAnUnfinishedOutcome()
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
            var archivedResult = Path.Combine(tmp, "results", suffix, "SomeTest", "20260101-000000-000", "test-result.txt");
            Assert.That(File.Exists(archivedResult), Is.True, $"expected archived test-result.txt at '{archivedResult}'");
            Assert.That(File.ReadAllLines(archivedResult)[0], Is.EqualTo("Outcome: Unfinished (overwritten when the test ends)"));
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
    public void PendingArchivedOutcomes_Rewrite_ReplacesTheUnfinishedOutcomeInBothDirectories()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var runDirectory = Path.Combine(tmp, "run");
        var archivedDirectory = Path.Combine(tmp, "archived");
        try
        {
            Directory.CreateDirectory(runDirectory);
            Directory.CreateDirectory(archivedDirectory);
            RoslynSentinel.Common.AgentLoop.RunDirectoryArchive.WriteTestResult(runDirectory, null, "X", null, null);
            RoslynSentinel.Common.AgentLoop.RunDirectoryArchive.WriteTestResult(archivedDirectory, null, "X", null, null);

            PendingArchivedOutcomes.Register("id-1", runDirectory, archivedDirectory);
            PendingArchivedOutcomes.Rewrite("id-1", "Passed", "X", null, null);

            Assert.That(File.ReadAllLines(Path.Combine(runDirectory, "test-result.txt"))[0], Is.EqualTo("Outcome: Passed"));
            Assert.That(File.ReadAllLines(Path.Combine(archivedDirectory, "test-result.txt"))[0], Is.EqualTo("Outcome: Passed"));

            // The entry was consumed by the first Rewrite, so a second one must not change the files.
            PendingArchivedOutcomes.Rewrite("id-1", "Failed", "X", null, null);

            Assert.That(File.ReadAllLines(Path.Combine(runDirectory, "test-result.txt"))[0], Is.EqualTo("Outcome: Passed"));
            Assert.That(File.ReadAllLines(Path.Combine(archivedDirectory, "test-result.txt"))[0], Is.EqualTo("Outcome: Passed"));

            // A null archived directory and a directory that no longer exists must neither throw nor change anything.
            var missingDirectory = Path.Combine(tmp, "missing");
            Assert.That(() => PendingArchivedOutcomes.Register("id-2", missingDirectory, null), Throws.Nothing);
            Assert.That(() => PendingArchivedOutcomes.Rewrite("id-2", "Passed", "X", null, null), Throws.Nothing);
        }
        finally
        {
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }
}
