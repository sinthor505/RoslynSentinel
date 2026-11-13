using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;

namespace RoslynSentinel.Tests.ModelEval.LiveModel;

/// <summary>
/// Runs once before any test in this assembly. Each fixture's own <c>[SetUp]</c> builds a real
/// <c>LmStudioAgentClient</c> and only checks that <see cref="LlmOptions.Model"/> is non-empty -
/// none of them touch the network before starting a model-eval run, so a genuinely unreachable LM
/// Studio server surfaces only via the per-fixture <c>HttpClient</c>'s own multi-minute timeout
/// (<c>Math.Max(LlmOptions.TimeoutSeconds * 4, 600)</c> seconds) or the 120s
/// <see cref="LlmOptions.StreamIdleTimeoutSeconds"/> retry cycle, deep inside the first generation
/// call - indistinguishable from a server that is merely slow to respond.
/// <para>
/// This adds a fast, deliberately short-timeout reachability check, separate from those generation
/// timeouts, so an unreachable server fails in seconds with a clear reason instead of minutes with
/// an ambiguous one. All the fixtures in this assembly are also marked <c>[Explicit]</c> so they
/// never run as part of an ordinary full-suite pass - this preflight is a safety net for when they
/// ARE run explicitly without a live server up, not the primary gate.
/// </para>
/// </summary>
[SetUpFixture]
public class ModelEvalAssemblySetUp
{
    private static readonly TimeSpan ReachabilityTimeout = TimeSpan.FromSeconds(5);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        LlmOptions.Configure([]);
        // The in-process eval server never runs ServerStdio/ServerHttp, so read the experimental server flags'
        // env vars here (ROSLYNSENTINEL_LIFT_RENAMES, ROSLYNSENTINEL_LOOP_LATCH); unset leaves them off.
        LiftRenamesOptions.Configure([]);
        LoopLatchOptions.Configure([]);
        // Reads ROSLYNSENTINEL_ERROR_POLICY_MODE / _FILE; unset leaves the policy Off. For Pointer/Inline with no file,
        // the harness defaults the file to ErrorPolicyFile.OutputPath (deviation from production startup, harness convenience only).
        ErrorPolicyOptions.Configure(ErrorPolicyFile.ResolveConfigureArgs(
            Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ERROR_POLICY_MODE"),
            Environment.GetEnvironmentVariable("ROSLYNSENTINEL_ERROR_POLICY_FILE")));
        ModelAgentRunner.ProcessStamp = BuildProcessStamp(ReadWorkingTreeStamp(AppContext.BaseDirectory));

        if (string.IsNullOrEmpty(LlmOptions.Model))
        {
            Assert.Ignore(
                "ROSLYNSENTINEL_LLM_MODEL is not set - model-eval tests require a real LM Studio " +
                "server with a loaded model and are skipped rather than failed when unconfigured.");
        }

        using var probeClient = new HttpClient { Timeout = ReachabilityTimeout };
        var modelsUrl = LlmOptions.BaseUrl.TrimEnd('/') + "/models";

        try
        {
            using var response = await probeClient.GetAsync(modelsUrl, TestContext.CurrentContext.CancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Assert.Ignore(
                $"LLM server at {LlmOptions.BaseUrl} did not respond within {ReachabilityTimeout.TotalSeconds}s " +
                $"({ex.GetType().Name}: {ex.Message}) - is LM Studio running with a model loaded?");
        }
    }

    /// <summary>
    /// Adds the error policy mode (lowercase name) and policy file content hash (empty when no policy file) to a git stamp.
    /// Null in, null out: with no git stamp the run stays unstamped. A malformed or unreadable policy file throws from
    /// <see cref="ErrorPolicyDocument.Load"/> on purpose, so a bad configuration fails the run loudly.
    /// </summary>
    internal static (string CommitHash, bool Dirty, string ErrorPolicyMode, string ErrorPolicyHash)? BuildProcessStamp(
        (string CommitHash, bool Dirty)? gitStamp)
    {
        // TODO error-policy arm D: temporary A/B switch; remove after Step 11c (ship the line in CodingAgent if D wins)
        // The arm D suffix lands in the errorPolicyMode csv column and the transcript; the string column is free-form, nothing parses it.
        var stamp = BuildProcessStamp(gitStamp, ErrorPolicyOptions.Mode, ErrorPolicyOptions.PolicyFilePath);
        if (stamp is not { } s)
        {
            return null;
        }

        return (s.CommitHash, s.Dirty, EvalExitLine.StampMode(s.ErrorPolicyMode, EvalExitLine.IsEnabled()), s.ErrorPolicyHash);
    }

    /// <summary>
    /// Same as the one-argument form, but takes the error policy mode and file explicitly so callers need no static
    /// <see cref="ErrorPolicyOptions"/> state. Null git stamp still gives null; a null or empty file gives an empty hash.
    /// </summary>
    internal static (string CommitHash, bool Dirty, string ErrorPolicyMode, string ErrorPolicyHash)? BuildProcessStamp(
        (string CommitHash, bool Dirty)? gitStamp, ErrorPolicyMode mode, string? policyFile)
    {
        if (gitStamp is not { } git)
        {
            return null;
        }

        var hash = string.IsNullOrEmpty(policyFile) ? string.Empty : ErrorPolicyDocument.Load(policyFile).ContentHash;
        return (git.CommitHash, git.Dirty, mode.ToString().ToLowerInvariant(), hash);
    }

    /// <summary>
    /// True when any tracked path changed, or an untracked .cs or .csproj exists. Untracked docs and other
    /// files do not count: other sessions leave untracked notes that would make every run dirty.
    /// </summary>
    internal static bool IsDirty(IReadOnlyList<WorkingTreeChange> changes) =>
        changes.Any(change => change.WorkTreeState != '?'
            || change.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || change.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the git HEAD and dirty flag of the repo holding <paramref name="startDirectory"/>, so each
    /// model-eval transcript records which code it ran against. Returns null when no repo root is found,
    /// git yields nothing, or anything throws.
    /// </summary>
    internal static (string CommitHash, bool Dirty)? ReadWorkingTreeStamp(string startDirectory)
    {
        try
        {
            // Copy of ModelTestingResultsArchiver.FindRepoRoot: that one is private and throws when no root is found.
            var dir = new DirectoryInfo(startDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RoslynSentinel.slnx")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                return null;
            }

            var root = dir.FullName;

            // libgit2 calls cannot be timed out and a stamp must never hang an eval run (a 600 s stall was
            // seen when other sessions load the box); the abandoned task only holds a background thread.
            var task = Task.Run<(string CommitHash, bool Dirty)?>(() =>
            {
                var sha = GitReaders.Default.GetHeadSha(root, shortForm: false, TimeSpan.FromSeconds(10))?.Trim();
                var changes = GitReaders.Default.GetStatus(root, null, TimeSpan.FromSeconds(10));
                if (string.IsNullOrEmpty(sha) || changes is null)
                {
                    return null;
                }

                return (sha, IsDirty(changes));
            });

            if (!task.Wait(TimeSpan.FromSeconds(30)))
            {
                return null;
            }

            return task.Result;
        }
        catch (Exception)
        {
            // A stamp failure must never fail an eval run; the transcript just omits the stamp.
            return null;
        }
    }
}
