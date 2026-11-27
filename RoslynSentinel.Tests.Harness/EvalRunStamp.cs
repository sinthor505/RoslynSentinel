using RoslynSentinel.Common;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Git;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>Model-free run-stamp helpers (git HEAD and dirty flag plus error policy mode and hash) shared by the live probe and the Harness tests.</summary>
public static class EvalRunStamp
{
    /// <summary>
    /// Adds the error policy mode (lowercase name) and policy file content hash (empty when no policy file) to a git stamp.
    /// Null in, null out: with no git stamp the run stays unstamped. A malformed or unreadable policy file throws from
    /// <see cref="ErrorPolicyDocument.Load"/> on purpose, so a bad configuration fails the run loudly.
    /// </summary>
    public static (string CommitHash, bool Dirty, string ErrorPolicyMode, string ErrorPolicyHash)? BuildProcessStamp(
        (string CommitHash, bool Dirty)? gitStamp)
    {
        var stamp = BuildProcessStamp(gitStamp, ErrorPolicyOptions.Mode, ErrorPolicyOptions.PolicyFilePath);
        if (stamp is not { } s)
        {
            return null;
        }

        // TODO symbol member inventory untested (plan_error_policy_pointer arm I never reached a container); decide default after an eval that exercises containingType/filePath misses
        // The +inventory suffix in the errorPolicyMode column marks the symbol member inventory switch.
        var mode = NotFoundInventoryOptions.StampMode(s.ErrorPolicyMode, NotFoundInventoryOptions.Enabled);
        return (s.CommitHash, s.Dirty, mode, s.ErrorPolicyHash);
    }

    /// <summary>
    /// Same as the one-argument form, but takes the error policy mode and file explicitly so callers need no static
    /// <see cref="ErrorPolicyOptions"/> state. Null git stamp still gives null; a null or empty file gives an empty hash.
    /// </summary>
    public static (string CommitHash, bool Dirty, string ErrorPolicyMode, string ErrorPolicyHash)? BuildProcessStamp(
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
    public static (string CommitHash, bool Dirty)? ReadWorkingTreeStamp(string startDirectory)
    {
        try
        {
            var root = RepoRootLocator.TryFind(startDirectory);
            if (root is null)
            {
                return null;
            }

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
