namespace RoslynSentinel.Common;

/// <summary>
/// Thin chokepoint for filesystem mutations and mutation-adjacent reads. Each method is a direct
/// System.IO passthrough plus a <see cref="FilePathLock"/> acquisition around writes/deletes (and
/// the reads that specifically need to observe a consistent state relative to those writes), so
/// every call that can race a concurrent write to the same path goes through one place. Not a
/// general-purpose File.* replacement — existence/metadata checks and read-only helpers unrelated
/// to the write path (solution parsing, config lookups, etc.) still call System.IO directly.
/// </summary>
public static class FileIoHelper
{
    /// <summary>Reads a file's full text, holding the per-path lock so the read can't observe a partially-written file.</summary>
    public static async Task<string> ReadAllTextAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        using (await FilePathLock.AcquireAsync(filePath, cancellationToken))
        {
            return await File.ReadAllTextAsync(filePath.Absolute, cancellationToken);
        }
    }

    /// <summary>Reads a file's full text if it exists, or returns null. Holds the per-path lock for the duration.</summary>
    public static async Task<string?> ReadAllTextIfExistsAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        using (await FilePathLock.AcquireAsync(filePath, cancellationToken))
        {
            return File.Exists(filePath.Absolute) ? await File.ReadAllTextAsync(filePath.Absolute, cancellationToken) : null;
        }
    }

    /// <summary>Writes text to a file, creating the parent directory if needed. Holds the per-path lock for the duration of the write.</summary>
    public static async Task WriteAllTextAsync(FilePath filePath, string content, CancellationToken cancellationToken = default)
    {
        using (await FilePathLock.AcquireAsync(filePath, cancellationToken))
        {
            var directory = Path.GetDirectoryName(filePath.Absolute);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(filePath.Absolute, content, cancellationToken);
        }
    }

    /// <summary>Deletes a file if it exists. Holds the per-path lock for the duration.</summary>
    public static async Task DeleteAsync(FilePath filePath, CancellationToken cancellationToken = default)
    {
        using (await FilePathLock.AcquireAsync(filePath, cancellationToken))
        {
            if (File.Exists(filePath.Absolute))
            {
                File.Delete(filePath.Absolute);
            }
        }
    }

    /// <summary>True if a write/delete to this path is currently in flight through this helper.</summary>
    public static bool IsLocked(FilePath filePath) => FilePathLock.IsLocked(filePath);
}
