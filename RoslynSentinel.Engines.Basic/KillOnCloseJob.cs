using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// A Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: when the last handle to the job closes
/// (including when the OS closes it because the owning process died, even by a hard kill) Windows terminates every
/// process assigned to it. Used so test-instance children started by <see cref="TestInstanceLauncher"/> do not
/// outlive a hard-killed owner. Grandchildren started by an assigned child (for example a <c>dotnet build</c>
/// spawned by a test instance) inherit job membership automatically, so they are covered too; a child that spawned
/// something before assignment completes is the only gap (assignment happens immediately after
/// <see cref="Process.Start()"/>). On non-Windows platforms every operation is a no-op or reports unavailable.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // One job for the whole process; never disposed (see TryAssignToProcessWideJob).
    private static readonly Lazy<(KillOnCloseJob? Job, string? Error)> ProcessWide = new(CreateProcessWide);

    private readonly SafeHandle _handle;

    private KillOnCloseJob(SafeHandle handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Assigns <paramref name="process"/> to the single process-wide kill-on-close job, creating it on first use.
    /// Returns true on success and on non-Windows platforms (no-op, <paramref name="warning"/> null). On any failure
    /// returns false with a short warning naming the Win32 error; never throws. A missing job must not fail the
    /// launch: the caller just reports the warning. If this process is itself inside a job, Windows 8+ supports
    /// nested jobs, so a failure here is unexpected and is simply reported.
    /// </summary>
    internal static bool TryAssignToProcessWideJob(Process process, out string? warning)
    {
        warning = null;
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            // The job instance is deliberately kept for the process lifetime and never disposed: closing its handle,
            // including by the OS when this process dies, is what kills the members.
            var (job, createError) = ProcessWide.Value;
            if (job is null)
            {
                warning = createError ?? "kill-on-close job unavailable";
                return false;
            }

            if (job.TryAssign(process, out var assignError))
            {
                return true;
            }

            warning = assignError;
            return false;
        }
        catch (Exception ex)
        {
            warning = $"kill-on-close job failed: {ex.GetType().Name}";
            return false;
        }
    }

    /// <summary>Test seam: creates a separate (not process-wide) job. Null plus <paramref name="error"/> on failure or non-Windows.</summary>
    internal static KillOnCloseJob? CreateIsolated(out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "kill-on-close jobs are only available on Windows";
            return null;
        }

        try
        {
            var created = Create();
            error = created.Error;
            return created.Job;
        }
        catch (Exception ex)
        {
            error = $"kill-on-close job failed: {ex.GetType().Name}";
            return null;
        }
    }

    /// <summary>Assigns <paramref name="process"/> (using its <see cref="Process.SafeHandle"/>) to this job.</summary>
    internal bool TryAssign(Process process, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "kill-on-close jobs are only available on Windows";
            return false;
        }

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, process.SafeHandle))
            {
                error = Describe("AssignProcessToJobObject", Marshal.GetLastWin32Error());
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"kill-on-close job failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Closes the job handle, which kills every process assigned to it. Only meaningful for isolated instances.</summary>
    public void Dispose()
    {
        _handle.Dispose();
    }

    private static (KillOnCloseJob? Job, string? Error) CreateProcessWide()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, "kill-on-close jobs are only available on Windows");
        }

        try
        {
            return Create();
        }
        catch (Exception ex)
        {
            return (null, $"kill-on-close job failed: {ex.GetType().Name}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (KillOnCloseJob? Job, string? Error) Create()
    {
        var raw = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (raw == IntPtr.Zero)
        {
            return (null, Describe("CreateJobObject", Marshal.GetLastWin32Error()));
        }

        var handle = new JobHandle(raw);
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, buffer, (uint)size))
            {
                var error = Describe("SetInformationJobObject", Marshal.GetLastWin32Error());
                handle.Dispose();
                return (null, error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return (new KillOnCloseJob(handle), null);
    }

    private static string Describe(string api, int win32Error)
    {
        return $"kill-on-close job: {api} failed (Win32 error {win32Error}: {new Win32Exception(win32Error).Message}); "
            + "a hard kill of the server will not stop this child";
    }

    [SupportedOSPlatform("windows")]
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle(IntPtr existingHandle) : base(ownsHandle: true)
        {
            SetHandle(existingHandle);
        }

        protected override bool ReleaseHandle()
        {
            return NativeMethods.CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeHandle job, int infoClass, IntPtr info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeHandle job, SafeHandle process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
