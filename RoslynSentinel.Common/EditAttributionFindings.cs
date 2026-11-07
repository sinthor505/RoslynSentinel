namespace RoslynSentinel.Common;

public static class EditAttributionFindings
{
    /// <summary>
    /// Builds Info findings for distinct paths whose latest edit attribution ledger entry was written by an agent other than the caller.
    /// Returns an empty list when the caller id is null or when reporting fails; reporting never fails a call.
    /// </summary>
    public static IReadOnlyList<Finding> ForPaths(EditAttributionLedger ledger, string? callerAgentId, IEnumerable<string> paths, DateTime utcNow)
    {
        if (callerAgentId is null)
        {
            return Array.Empty<Finding>();
        }

        try
        {
            const int maxFileFindings = 5;
            var findings = new List<Finding>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var foreignCount = 0;
            foreach (var path in paths)
            {
                if (!seen.Add(path))
                {
                    continue;
                }

                var entry = ledger.GetLatest(path)
                    ?? ledger.GetLatest(path.Replace('/', '\\'))
                    ?? ledger.GetLatest(path.Replace('\\', '/'));
                if (entry is null || entry.AgentId is null || string.Equals(entry.AgentId, callerAgentId, StringComparison.Ordinal))
                {
                    continue;
                }

                foreignCount++;
                if (findings.Count < maxFileFindings)
                {
                    var message = $"{path} was last changed by agent '{entry.AgentId}' (type {entry.AgentType ?? "unknown"}) via {entry.Tool ?? "unknown"}, {FormatAge(utcNow - entry.UtcTime)} ago: {entry.Reason ?? "no reason recorded"}";
                    findings.Add(new Finding("EditAttribution", message, FindingSeverity.Info));
                }
            }

            if (foreignCount > maxFileFindings)
            {
                findings.Add(new Finding("EditAttribution", $"(+{foreignCount - maxFileFindings} more files last changed by other agents)", FindingSeverity.Info));
            }

            return findings;
        }
        catch (Exception)
        {
            return Array.Empty<Finding>();
        }
    }

    /// <summary>
    /// Short age text such as 20s, 5m or 2h; a negative age reads as 0s.
    /// </summary>
    internal static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m";
        }

        return $"{(int)age.TotalHours}h";
    }

    /// <summary>
    /// Reports foreign edits for the paths, using the agent id of the current tool call and the workspace manager's edit attribution ledger.
    /// Returns an empty list when no caller identity or no ledger is available.
    /// </summary>
    public static IReadOnlyList<Finding> ForCurrentCaller(object? workspaceManager, IEnumerable<string> paths)
    {
        var callerId = ToolCallContext.Current?.AgentId;
        var ledger = (workspaceManager as IEditAttributionLedgerProvider)?.EditAttributions;
        if (callerId is null || ledger is null)
        {
            return Array.Empty<Finding>();
        }

        return ForPaths(ledger, callerId, paths, DateTime.UtcNow);
    }
}
