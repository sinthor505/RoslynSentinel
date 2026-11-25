using System;
using System.Globalization;
using System.IO;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// evalqueue (scripts/roslynsentinel-evalqueue.ps1) watches the marker file this class writes. It means
/// "SetUp is complete and the agent run is starting", so a test longer than -NoRunFolderMinutes is not
/// flagged SetupStalled. Written once per process per job id.
/// </summary>
[StartupSettingGroup("Eval queue")]
public static class EvalQueueSetupMarker
{
    /// <summary>Environment variable the evalqueue sets to the job id it is running.</summary>
    public const string JobEnvironmentVariable = "ROSLYNSENTINEL_EVALQUEUE_JOB";

    public static readonly StartupSetting<string?> JobSetting = StartupSetting.String(null, JobEnvironmentVariable, null, "Job id the evalqueue script is running; when present, the process writes the setup-complete marker.", SettingScope.AgentLoop, SettingKind.Live);

    private static readonly object Gate = new object();
    private static string? lastWrittenJobId;

    /// <summary>Returns the marker path for a job id under the given scratch root.</summary>
    public static string PathFor(string scratchRoot, string jobId)
    {
        return Path.Combine(scratchRoot, "evalqueue-setup-complete-" + jobId + ".marker");
    }

    /// <summary>
    /// Writes the marker when this process runs under evalqueue; does nothing otherwise. Best effort: it never throws.
    /// </summary>
    public static void WriteIfQueued()
    {
        try
        {
            string? jobId = JobSetting.Value;
            if (string.IsNullOrEmpty(jobId))
            {
                return;
            }

            lock (Gate)
            {
                if (string.Equals(lastWrittenJobId, jobId, StringComparison.Ordinal))
                {
                    return;
                }

                string root = ScratchRoot.Resolve();
                Directory.CreateDirectory(root);
                File.WriteAllText(
                    PathFor(root, jobId),
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine);
                lastWrittenJobId = jobId;
            }
        }
        catch (Exception)
        {
            // Best effort: a marker that cannot be written must never fail or stall the agent run.
        }
    }
}
