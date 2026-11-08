namespace RoslynSentinel.Common;

/// <summary>
/// Inputs to <see cref="IdleRestartPolicy.FirstUnmetGuard"/>: a snapshot of the server's activity, halt, ledger and build
/// state plus the shared <c>latest.json</c> selection and this server's own build SHA.
/// </summary>
public sealed record IdleRestartCheckRequest(bool Enabled, DateTime NowUtc, DateTime LastActivityUtc, TimeSpan IdleThreshold, int InFlightCount, bool SessionHalted, string? OpenLedgerOperation, bool BuildGateBusy, LatestBuildRecord? Latest, string? DevHeadSha, string? OwnBuildSha);

/// <summary>
/// Pure decision for whether a stdio server may exit on its own so VS Code respawns it on a newer prebuilt binary.
/// A restart loses in-memory state (undo history, enabled toolsets), so it only happens when the server is idle and a
/// newer complete build is ready. Each guard exists for one reason:
/// disabled - the monitor is off unless configured; inFlight - a tool call is still running;
/// recentActivity - the server was used within the idle threshold; sessionHalted - the workspace is latched halted;
/// ledgerOpen - a multi-step edit is mid-flight; buildGateBusy - a build is holding the gate;
/// noLatestBuild - no usable shared build selection exists; latestBuildFailed - the newest build did not compile
/// (keep the server up on the older build); unknownHead - the dev repo HEAD or own build SHA is unknown;
/// prebuildPending - the prebuild for the current HEAD has not finished (keep the server up);
/// alreadyOnLatestBuild - this server already runs the selected build.
/// </summary>
public static class IdleRestartPolicy
{
    /// <summary>Guard name: the monitor is not enabled.</summary>
    public const string GuardDisabled = "disabled";

    /// <summary>Guard name: at least one tool call is in flight.</summary>
    public const string GuardInFlight = "inFlight";

    /// <summary>Guard name: the server was used more recently than the idle threshold.</summary>
    public const string GuardRecentActivity = "recentActivity";

    /// <summary>Guard name: the workspace session is halted.</summary>
    public const string GuardSessionHalted = "sessionHalted";

    /// <summary>Guard name: a scoped operation ledger has an open operation.</summary>
    public const string GuardLedgerOpen = "ledgerOpen";

    /// <summary>Guard name: the build gate is busy.</summary>
    public const string GuardBuildGateBusy = "buildGateBusy";

    /// <summary>Guard name: no usable <c>latest.json</c> selection exists.</summary>
    public const string GuardNoLatestBuild = "noLatestBuild";

    /// <summary>Guard name: the selected build failed to compile.</summary>
    public const string GuardLatestBuildFailed = "latestBuildFailed";

    /// <summary>Guard name: the dev repo HEAD or this server's build SHA is unknown.</summary>
    public const string GuardUnknownHead = "unknownHead";

    /// <summary>Guard name: the prebuild for the current dev repo HEAD has not finished.</summary>
    public const string GuardPrebuildPending = "prebuildPending";

    /// <summary>Guard name: this server already runs the selected build.</summary>
    public const string GuardAlreadyOnLatestBuild = "alreadyOnLatestBuild";

    /// <summary>
    /// Returns null when the server may exit now, otherwise the name of the first guard that blocks it, checked in the
    /// documented order. Guard names are the <c>Guard*</c> constants on this class.
    /// </summary>
    public static string? FirstUnmetGuard(IdleRestartCheckRequest r)
    {
        if (!r.Enabled)
        {
            return GuardDisabled;
        }

        if (r.InFlightCount > 0)
        {
            return GuardInFlight;
        }

        if (r.NowUtc - r.LastActivityUtc < r.IdleThreshold)
        {
            return GuardRecentActivity;
        }

        if (r.SessionHalted)
        {
            return GuardSessionHalted;
        }

        if (!string.IsNullOrEmpty(r.OpenLedgerOperation))
        {
            return GuardLedgerOpen;
        }

        if (r.BuildGateBusy)
        {
            return GuardBuildGateBusy;
        }

        var latest = r.Latest;
        if (latest is null || string.IsNullOrEmpty(latest.HeadSha) || string.IsNullOrEmpty(latest.BuildSha8))
        {
            return GuardNoLatestBuild;
        }

        if (latest.BuildFailed)
        {
            return GuardLatestBuildFailed;
        }

        if (string.IsNullOrEmpty(r.DevHeadSha) || string.IsNullOrEmpty(r.OwnBuildSha))
        {
            return GuardUnknownHead;
        }

        if (!string.Equals(latest.HeadSha, r.DevHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return GuardPrebuildPending;
        }

        if (r.OwnBuildSha.StartsWith(latest.BuildSha8, StringComparison.OrdinalIgnoreCase))
        {
            return GuardAlreadyOnLatestBuild;
        }

        return null;
    }
}
