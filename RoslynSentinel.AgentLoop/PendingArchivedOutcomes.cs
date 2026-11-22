using System.Collections.Concurrent;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Tracks run directories (and their archived copies) that a test body archived while its
/// outcome was still Inconclusive, so test-result.txt can be rewritten with the real outcome
/// once the test ends. Keyed by the NUnit test id. This is the single shared implementation for
/// Tests.ModelEval and Tests.ErrorVariants; each keeps only a thin NUnit ITestAction shim.
/// </summary>
public static class PendingArchivedOutcomes
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
    /// <paramref name="testId"/>, then forgets them. A failed rewrite is reported through
    /// <paramref name="log"/> (when given) and never changes the test's own outcome.
    /// </summary>
    public static void Rewrite(string testId, string outcome, string testName, string? message, string? stackTrace, Action<string>? log = null)
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
                log?.Invoke($"PendingArchivedOutcomes: failed to rewrite '{directory}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                log?.Invoke($"PendingArchivedOutcomes: failed to rewrite '{directory}': {ex.Message}");
            }
        }
    }
}
