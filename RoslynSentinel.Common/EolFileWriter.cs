using System.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// Writes a workspace file that cannot go through PersistentWorkspaceManager.ApplyProposedChangesAsync (no solution loaded, or a file a CLI tool just created) with the EOL the resolver picks, so the Step 6 bare-write ratchet has no workspace-file exception. Returns null on success or a refusal message.
/// </summary>
public static class EolFileWriter
{
    /// <summary>Writes content to a file with the EOL the resolver picks for it, preserving a UTF-8 BOM already on disk. Returns null on success or a refusal message.</summary>
    public static async Task<string?> WriteAllTextResolvedAsync(string filePath, string content, string? preImage, CancellationToken cancellationToken = default)
    {
        if (!EolWritePolicy.TryComputeContentToWrite(EolPolicyOptions.Mode, filePath, preImage, content, EditorConfigEolReader.Read(filePath), exactRestore: false, out var contentToWrite, out var refusal))
        {
            return refusal;
        }

        Encoding? encoding = HasUtf8Bom(filePath) ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: true) : null;
        await FileIoHelper.WriteAllTextAsync((FilePathWrapper)filePath, contentToWrite, encoding, cancellationToken);
        return null;
    }

    /// <summary>Re-reads a file a CLI tool just created and rewrites it with the resolved EOL. Returns null on success or a refusal message.</summary>
    public static async Task<string?> NormalizeNewFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(filePath, cancellationToken);
        return await WriteAllTextResolvedAsync(filePath, text, preImage: null, cancellationToken);
    }

    private static bool HasUtf8Bom(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            using var stream = File.OpenRead(filePath);
            var buffer = new byte[3];
            return stream.Read(buffer, 0, 3) == 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
