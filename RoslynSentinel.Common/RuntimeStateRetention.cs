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

    /// <summary>
    /// Sweeps the operation blobs (*.json, top directory only) in &lt;solutionRoot&gt;/.roslynsentinel/operations.
    /// Blobs younger than OperationsProtectedAge are never deleted. Blobs older than maxAge are deleted; if more than
    /// maxCount remain, the oldest unprotected survivors are deleted until maxCount remain. A deleted blob also
    /// deletes its .undone marker, and a marker with no blob beside it that is older than maxAge is deleted.
    /// Never throws. Returns the number of blobs deleted.
    /// </summary>
    public static int SweepOperations(string solutionRoot, TimeSpan maxAge, int maxCount, DateTime? utcNow = null)
    {
        var count = 0;
        try
        {
            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "operations");
            if (!Directory.Exists(dir)) { return 0; }
            var now = utcNow ?? DateTime.UtcNow;
            var cutoff = now - maxAge;
            var protectedCutoff = now - OperationsProtectedAge;

            var blobs = ListFilesWithExtension(dir, "*.json", ".json");
            var survivors = new List<(string Path, DateTime WrittenUtc)>();
            foreach (var blob in blobs)
            {
                // Age below the protected age (written after protectedCutoff) is never deleted.
                var isProtected = blob.WrittenUtc > protectedCutoff;
                if (!isProtected && blob.WrittenUtc < cutoff && TryDeleteBlob(blob.Path))
                {
                    count++;
                }
                else
                {
                    survivors.Add(blob);
                }
            }

            var excess = survivors.Count - Math.Max(0, maxCount);
            if (excess > 0)
            {
                foreach (var blob in survivors.OrderBy(s => s.WrittenUtc).ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
                {
                    if (excess <= 0) { break; }
                    if (blob.WrittenUtc > protectedCutoff) { break; } // oldest first: everything after is protected too
                    if (TryDeleteBlob(blob.Path))
                    {
                        count++;
                        excess--;
                    }
                }
            }

            foreach (var marker in ListFilesWithExtension(dir, "*" + OperationBlobWriter.UndoneMarkerExtension, OperationBlobWriter.UndoneMarkerExtension))
            {
                if (marker.WrittenUtc >= cutoff) { continue; }
                if (File.Exists(Path.ChangeExtension(marker.Path, ".json"))) { continue; }
                TryDeleteFile(marker.Path);
            }
        }
        catch (Exception)
        {
            // Retention is best effort and must never break the caller (enumeration, bad path, security errors).
        }
        return count;

        static bool TryDeleteBlob(string blobPath)
        {
            if (!TryDeleteFile(blobPath)) { return false; }
            TryDeleteFile(Path.ChangeExtension(blobPath, OperationBlobWriter.UndoneMarkerExtension));
            return true;
        }
    }

    /// <summary>
    /// Deletes validation_*.json files directly inside &lt;solutionRoot&gt;/.roslynsentinel/validation whose
    /// LastWriteTimeUtc is older than maxAge. Non-recursive. Never throws; a file held by another session is skipped.
    /// Returns the number of files deleted.
    /// </summary>
    public static int SweepValidation(string solutionRoot, TimeSpan maxAge, DateTime? utcNow = null)
    {
        var count = 0;
        try
        {
            var dir = Path.Combine(solutionRoot, ".roslynsentinel", "validation");
            if (!Directory.Exists(dir)) { return 0; }
            var cutoff = (utcNow ?? DateTime.UtcNow) - maxAge;
            foreach (var file in ListFilesWithExtension(dir, "validation_*.json", ".json"))
            {
                if (file.WrittenUtc < cutoff && TryDeleteFile(file.Path)) { count++; }
            }
        }
        catch (Exception)
        {
            // Retention is best effort and must never break the caller (enumeration, bad path, security errors).
        }
        return count;
    }

    /// <summary>
    /// Deletes each immediate subfolder of buildRoot (except the one named currentInstanceId) whose newest write time
    /// is older than maxAge. The newest write time is scanned two levels deep (the folder, its entries, and the entries
    /// of its child folders), because a rebuild changes files deep in bin/obj without touching the instance folder.
    /// Loose files in buildRoot (such as .gitignore) are never touched. Never throws; a folder that cannot be deleted
    /// is skipped and not counted. Returns the number of folders deleted.
    /// </summary>
    public static int SweepBuildInstances(string buildRoot, string currentInstanceId, TimeSpan maxAge, DateTime? utcNow = null)
    {
        var count = 0;
        try
        {
            if (!Directory.Exists(buildRoot)) { return 0; }
            var cutoff = (utcNow ?? DateTime.UtcNow) - maxAge;
            foreach (var folder in Directory.EnumerateDirectories(buildRoot))
            {
                if (string.Equals(Path.GetFileName(folder), currentInstanceId, StringComparison.OrdinalIgnoreCase)) { continue; }
                try
                {
                    if (NewestWriteUtc(folder) >= cutoff) { continue; }
                    Directory.Delete(folder, true);
                    count++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception)
        {
            // Retention is best effort and must never break the caller (enumeration, bad path, security errors).
        }
        return count;
    }

    /// <summary>
    /// Sweeps {fileStem}*.log files directly inside logDirectory. Never touches currentLogPath (null or empty means
    /// no current file), a file named crash.log, or a file whose name does not start with fileStem. Files older than
    /// maxAge are deleted; then, if the remaining files plus the current log exceed maxFiles (values of 0 or less
    /// count as 1), the oldest remaining files are deleted until they no longer do. Never throws; a file held by
    /// another process is skipped and not counted. Returns the number of files deleted.
    /// </summary>
    public static int SweepLogs(string logDirectory, string fileStem, string currentLogPath, int maxFiles, TimeSpan maxAge, DateTime? utcNow = null)
    {
        var count = 0;
        try
        {
            if (string.IsNullOrEmpty(fileStem) || !Directory.Exists(logDirectory)) { return 0; }
            var cutoff = (utcNow ?? DateTime.UtcNow) - maxAge;
            var currentFullPath = string.IsNullOrEmpty(currentLogPath) ? null : Path.GetFullPath(currentLogPath);

            var others = new List<(string Path, DateTime WrittenUtc)>();
            foreach (var log in ListFilesWithExtension(logDirectory, fileStem + "*.log", ".log"))
            {
                var name = Path.GetFileName(log.Path);
                if (!name.StartsWith(fileStem, StringComparison.OrdinalIgnoreCase)) { continue; }
                if (string.Equals(name, "crash.log", StringComparison.OrdinalIgnoreCase)) { continue; }
                if (currentFullPath != null && string.Equals(Path.GetFullPath(log.Path), currentFullPath, StringComparison.OrdinalIgnoreCase)) { continue; }
                others.Add(log);
            }

            var remaining = new List<(string Path, DateTime WrittenUtc)>();
            foreach (var log in others)
            {
                if (log.WrittenUtc < cutoff && TryDeleteFile(log.Path)) { count++; }
                else { remaining.Add(log); }
            }

            // The +1 is the current log, which counts toward the limit but is never deleted.
            var excess = remaining.Count + 1 - Math.Max(1, maxFiles);
            if (excess > 0)
            {
                foreach (var log in remaining.OrderBy(l => l.WrittenUtc).ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
                {
                    if (excess <= 0) { break; }
                    if (TryDeleteFile(log.Path))
                    {
                        count++;
                        excess--;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Retention is best effort and must never break the caller (enumeration, bad path, security errors).
        }
        return count;
    }

    /// <summary>Deletes one file; returns false when it is held by another process or access is denied.</summary>
    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Returns the newest LastWriteTimeUtc of the folder, its entries, and the entries of its child folders (two levels).</summary>
    private static DateTime NewestWriteUtc(string folder)
    {
        var newest = File.GetLastWriteTimeUtc(folder);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
        {
            var entryTime = File.GetLastWriteTimeUtc(entry);
            if (entryTime > newest) { newest = entryTime; }
            if (!Directory.Exists(entry)) { continue; }
            foreach (var child in Directory.EnumerateFileSystemEntries(entry))
            {
                var childTime = File.GetLastWriteTimeUtc(child);
                if (childTime > newest) { newest = childTime; }
            }
        }
        return newest;
    }

    /// <summary>Lists files in a folder (top directory only) whose name ends with the extension, with their last write times.</summary>
    private static List<(string Path, DateTime WrittenUtc)> ListFilesWithExtension(string folder, string searchPattern, string extension)
    {
        var files = new List<(string Path, DateTime WrittenUtc)>();
        foreach (var file in Directory.EnumerateFiles(folder, searchPattern, SearchOption.TopDirectoryOnly))
        {
            if (!file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) { continue; }
            try
            {
                files.Add((file, File.GetLastWriteTimeUtc(file)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files;
    }
}
