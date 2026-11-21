using System.Collections.Concurrent;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using RoslynSentinel.Common.AgentLoop;

[assembly: RoslynSentinel.Tests.ModelEval.RewriteBodyArchivedOutcome]

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// Tracks run directories (and their archived copies) that a test body archived while its
/// outcome was still Inconclusive, so test-result.txt can be rewritten with the real outcome
/// once the test ends. Keyed by the NUnit test id.
/// </summary>
internal static class PendingArchivedOutcomes
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<string>> Pending = new();

    /// <summary>Remembers each non-null directory archived for <paramref name="testId"/>.</summary>
    public static void Register(string testId, string? runDirectory, string? archivedDirectory)
    {
        var directories = Pending.GetOrAdd(testId, _ => new ConcurrentBag<string>());
        if (runDirectory is not null)
        {
            directories.Add(runDirectory);
        }

        if (archivedDirectory is not null)
        {
            directories.Add(archivedDirectory);
        }
    }

    /// <summary>
    /// Rewrites test-result.txt with <paramref name="outcome"/> in every directory registered for
    /// <paramref name="testId"/>, then forgets them. A failed rewrite is logged and never changes
    /// the test's own outcome.
    /// </summary>
    public static void Rewrite(string testId, string outcome, string testName, string? message, string? stackTrace)
    {
        if (!Pending.TryRemove(testId, out var directories))
        {
            return;
        }

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                RunDirectoryArchive.WriteTestResult(directory, outcome, testName, message, stackTrace);
            }
            catch (IOException ex)
            {
                TestContext.Out.WriteLine($"PendingArchivedOutcomes: failed to rewrite '{directory}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                TestContext.Out.WriteLine($"PendingArchivedOutcomes: failed to rewrite '{directory}': {ex.Message}");
            }
        }
    }
}

/// <summary>
/// Applies to every test in this assembly. Once a test ends, rewrites the test-result.txt files that
/// <see cref="ModelTestingResultsArchiver"/> wrote from the test body while the outcome was still
/// Unfinished. Uses ITestAction (not TearDown) so every fixture that archives from its test body is
/// covered without touching each class; AfterTest runs after TearDown so the outcome includes
/// teardown failures. Tests.ErrorVariants has its own copy as it has its own archiver.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RewriteBodyArchivedOutcomeAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
    }

    public void AfterTest(ITest test)
    {
        var result = TestContext.CurrentContext.Result;
        PendingArchivedOutcomes.Rewrite(test.Id, result.Outcome.ToString(), test.FullName, result.Message, result.StackTrace);
    }
}
