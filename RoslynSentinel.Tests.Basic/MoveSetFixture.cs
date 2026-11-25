using System.Text;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Real-file scaffolding for the MoveSetPlanner tests: a unique folder per test under the scratch root, text, binary and
/// non-UTF-8 writers, and a byte snapshot for proving the disk was not touched.
/// </summary>
internal static class MoveSetFixture
{
    /// <summary>Creates a fresh folder for one test: Combine(scratch root, "MoveSetPlanner", testName, new guid).</summary>
    public static string CreateRoot(string testName)
    {
        var root = Path.Combine(ScratchRoot.Resolve(), "MoveSetPlanner", testName, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Deletes the folder made by <see cref="CreateRoot"/>; a missing folder is not an error.</summary>
    public static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Writes UTF-8 text without a byte order mark, creating parent folders.</summary>
    public static string WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>Writes a file with a NUL byte inside its first 8000 bytes, so the planner classes it as binary.</summary>
    public static string WriteBinary(string path)
    {
        return WriteBytes(path, 0x50, 0x4B, 0x00, 0x03, 0x04);
    }

    /// <summary>Writes a file holding the invalid UTF-8 sequence 0xC3 0x28, so the planner classes it as not UTF-8.</summary>
    public static string WriteNonUtf8(string path)
    {
        return WriteBytes(path, 0x41, 0xC3, 0x28, 0x42);
    }

    /// <summary>Maps every file under the root to its exact bytes.</summary>
    public static Dictionary<string, byte[]> Snapshot(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => File.ReadAllBytes(path), StringComparer.OrdinalIgnoreCase);
    }

    private static string WriteBytes(string path, params byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
