namespace RoslynSentinel.Common;

public static class EolWritePolicy
{
    /// <summary>
    /// Computes the bytes to write for a file edit. Three behaviours: (1) exactRestore passes the
    /// content through unchanged (undo writes the pre-image byte-for-byte); (2) otherwise the whole
    /// file is converted to the EolResolver target, so mixed, CR-only, no-newline and new files
    /// converge; (3) the converted text must be a single line-ending style, or the call is refused.
    /// </summary>
    public static bool TryComputeContentToWrite(EolMode mode, string filePath, string? preImage, string newContent, string? editorConfigEol, bool exactRestore, out string contentToWrite, out string? refusalMessage)
    {
        if (exactRestore)
        {
            contentToWrite = newContent;
            refusalMessage = null;
            return true;
        }

        var target = EolResolver.Resolve(mode, preImage, editorConfigEol);
        var converted = EolUtilities.NormalizeEol(newContent, target);
        var counts = EolChangeGuard.Count(converted);
        if (counts.Total == 0 || counts.IsSingleStyle)
        {
            contentToWrite = converted;
            refusalMessage = null;
            return true;
        }

        // Unreachable with the current NormalizeEol; guards against a future regression that leaves mixed styles.
        contentToWrite = newContent;
        refusalMessage = $"EOL change refused for '{filePath}': the converted content is not a single line-ending style ({counts.Describe()}).";
        return false;
    }
}
