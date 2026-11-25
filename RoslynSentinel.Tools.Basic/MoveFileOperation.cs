namespace RoslynSentinel.Tools.Basic;

/// <summary>The operation name MoveFile writes under, and the pattern of the operation blobs that name produces.</summary>
internal static class MoveFileOperation
{
    /// <summary>Operation name of a MoveFile write: it names the operation blob and prefixes the compile-gate error text.</summary>
    internal const string Name = "move_file";

    /// <summary>File-name pattern of the operation blobs written under <see cref="Name"/>.</summary>
    internal const string BlobSearchPattern = Name + "_*.json";
}
