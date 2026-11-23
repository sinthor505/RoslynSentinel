using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval;

// Process-wide environment variables are mutated, so these tests must not run in parallel with each other.
[TestFixture]
[NonParallelizable]
public sealed class EvalQueueSetupMarkerTests
{
    private string? savedJob;
    private string? savedScratchRoot;
    private readonly List<string> tempPaths = new List<string>();

    [SetUp]
    public void SaveEnvironment()
    {
        savedJob = Environment.GetEnvironmentVariable(EvalQueueSetupMarker.JobEnvironmentVariable);
        savedScratchRoot = Environment.GetEnvironmentVariable(ScratchRootEnvironmentVariable);
    }

    [TearDown]
    public void RestoreEnvironment()
    {
        Environment.SetEnvironmentVariable(EvalQueueSetupMarker.JobEnvironmentVariable, savedJob);
        Environment.SetEnvironmentVariable(ScratchRootEnvironmentVariable, savedScratchRoot);
        foreach (string path in tempPaths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Temp cleanup is best effort.
            }
        }

        tempPaths.Clear();
    }

    private const string ScratchRootEnvironmentVariable = "ROSLYNSENTINEL_SCRATCH_ROOT";

    [Test]
    public void WriteIfQueued_WithJobSet_WritesMarkerContainingTimestamp()
    {
        string root = NewTempPath();
        string job = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(ScratchRootEnvironmentVariable, root);
        Environment.SetEnvironmentVariable(EvalQueueSetupMarker.JobEnvironmentVariable, job);

        EvalQueueSetupMarker.WriteIfQueued();

        string markerPath = EvalQueueSetupMarker.PathFor(root, job);
        Assert.That(File.Exists(markerPath), Is.True, "marker file should be created at " + markerPath);
        string text = File.ReadAllText(markerPath).Trim();
        Assert.That(
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            Is.True,
            "marker should contain a timestamp, was: " + text);
    }

    [Test]
    public void WriteIfQueued_WithJobUnset_CreatesNothing()
    {
        string root = NewTempPath();
        Environment.SetEnvironmentVariable(ScratchRootEnvironmentVariable, root);
        Environment.SetEnvironmentVariable(EvalQueueSetupMarker.JobEnvironmentVariable, null);

        EvalQueueSetupMarker.WriteIfQueued();

        Assert.That(Directory.Exists(root), Is.False, "no scratch directory should be created when not queued");
    }

    [Test]
    public void WriteIfQueued_WithScratchRootUnderAFile_DoesNotThrow()
    {
        string blockingFile = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(blockingFile)!);
        File.WriteAllText(blockingFile, "not a directory");
        string root = Path.Combine(blockingFile, "child");
        Environment.SetEnvironmentVariable(ScratchRootEnvironmentVariable, root);
        Environment.SetEnvironmentVariable(
            EvalQueueSetupMarker.JobEnvironmentVariable,
            Guid.NewGuid().ToString("N"));

        Assert.That(() => EvalQueueSetupMarker.WriteIfQueued(), Throws.Nothing);
    }

    private string NewTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "rs-evalqueue-marker-tests", Guid.NewGuid().ToString("N"));
        tempPaths.Add(path);
        return path;
    }
}
