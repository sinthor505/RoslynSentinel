namespace RoslynSentinel.Common;

/// <summary>Age and count limits for runtime state, and the sweeps that enforce them.</summary>
public static class RuntimeStateRetention
{
    public static readonly TimeSpan OperationsMaxAge = TimeSpan.FromDays(14);
    public const int OperationsMaxCount = 5000;
    public static readonly TimeSpan OperationsProtectedAge = TimeSpan.FromDays(1);
    public static readonly TimeSpan ValidationMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan BuildInstanceMaxAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan LogMaxAge = TimeSpan.FromDays(14);
    public const int LogMaxFiles = 50;

    /// <summary>Deletes operation ledgers older than maxAge or beyond maxCount; returns the number removed.</summary>
    public static int SweepOperations(string solutionRoot, TimeSpan maxAge, int maxCount, DateTime? utcNow = null)
    {
        throw new NotImplementedException(); // TODO implement (Step 12)
    }

    /// <summary>Deletes validation folders older than maxAge; returns the number removed.</summary>
    public static int SweepValidation(string solutionRoot, TimeSpan maxAge, DateTime? utcNow = null)
    {
        throw new NotImplementedException(); // TODO implement (Step 12)
    }

    /// <summary>Deletes build instance folders older than maxAge, except currentInstanceId; returns the number removed.</summary>
    public static int SweepBuildInstances(string buildRoot, string currentInstanceId, TimeSpan maxAge, DateTime? utcNow = null)
    {
        throw new NotImplementedException(); // TODO implement (Step 12)
    }

    /// <summary>Deletes log files for fileStem beyond maxFiles or older than maxAge, never currentLogPath; returns the number removed.</summary>
    public static int SweepLogs(string logDirectory, string fileStem, string currentLogPath, int maxFiles, TimeSpan maxAge, DateTime? utcNow = null)
    {
        throw new NotImplementedException(); // TODO implement (Step 12)
    }
}
