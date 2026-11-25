using System.Reflection;
using System.Runtime.InteropServices;
using LibGit2Sharp;

namespace RoslynSentinel.Common;

/// <summary>
/// The one libgit2 call LibGit2Sharp does not expose: <c>git_reference_create_matching</c>, a compare-and-swap ref update (<c>Repository.Commit</c> moves the branch with a plain
/// <c>git_reference_set_target</c>, so two committers can orphan each other's commit; probed 2026-11-11, 111 of 600 commits lost). Bound by function pointer, with no
/// <c>DllImport</c>, to the SAME native library LibGit2Sharp loaded, so no library name or version is hard-coded here: the name is the const
/// <c>LibGit2Sharp.Core.NativeDllName.Name</c> of the LibGit2Sharp assembly in use (read by reflection; for 0.32.0 <c>git2-5853918</c>), loaded the way LibGit2Sharp's
/// own resolver loads it (<see cref="NativeLibrary.TryLoad(string, Assembly, DllImportSearchPath?, out IntPtr)"/> for that assembly), else from
/// <c>runtimes/&lt;rid&gt;/native</c> next to the entry assembly or LibGit2Sharp. A package change that renames or moves any of it fails the type initializer
/// (<see cref="DllNotFoundException"/> / <see cref="EntryPointNotFoundException"/>, cached as <see cref="TypeInitializationException"/>), which
/// <see cref="FallbackGitReader"/> counts as a failure and answers from the CLI; <c>GitCasCommitNativeTests</c> pins the resolution so such a change is also a test failure.
/// </summary>
internal static class Libgit2Native
{
    /// <summary>libgit2 <c>GIT_ENOTFOUND</c>.</summary>
    internal const int ErrorNotFound = -3;

    /// <summary>libgit2 <c>GIT_ELOCKED</c>: a lock file (here the ref's <c>.lock</c>) exists.</summary>
    internal const int ErrorLocked = -14;

    /// <summary>libgit2 <c>GIT_EMODIFIED</c>: the ref's current value is not the expected one.</summary>
    internal const int ErrorModified = -15;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RepositoryOpen(out IntPtr repository, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RepositoryFree(IntPtr repository);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReferenceCreateMatching(
        out IntPtr reference, IntPtr repository, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] id, int force, byte[] currentId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string logMessage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReferenceFree(IntPtr reference);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ErrorLast();

    private static readonly RepositoryOpen OpenRepository;
    private static readonly RepositoryFree FreeRepository;
    private static readonly ReferenceCreateMatching CreateMatching;
    private static readonly ReferenceFree FreeReference;
    private static readonly ErrorLast LastErrorPointer;

    /// <summary>The native library file name LibGit2Sharp binds to (without directory), read from the assembly in use.</summary>
    internal static readonly string LibraryName;

    static Libgit2Native()
    {
        // LibGit2Sharp's NativeMethods static constructor registers its resolver and calls git_libgit2_init (and registers the matching shutdown);
        // our own git_repository_open below relies on that initialisation having happened.
        _ = GlobalSettings.Version;

        LibraryName = ReadLibraryName();
        var library = Load(LibraryName);
        OpenRepository = Bind<RepositoryOpen>(library, "git_repository_open");
        FreeRepository = Bind<RepositoryFree>(library, "git_repository_free");
        CreateMatching = Bind<ReferenceCreateMatching>(library, "git_reference_create_matching");
        FreeReference = Bind<ReferenceFree>(library, "git_reference_free");
        LastErrorPointer = Bind<ErrorLast>(library, "git_error_last");
    }

    /// <summary>
    /// <c>git_reference_create_matching(force: 1, current_id: <paramref name="expectedOldId"/>)</c> in a native repository opened on <paramref name="gitDirectory"/>:
    /// moves <paramref name="referenceName"/> to <paramref name="newId"/> only if it currently is <paramref name="expectedOldId"/> (all zero bytes: only if it does not exist),
    /// under the ref's own <c>.lock</c>, writing the reflog entry <paramref name="logMessage"/> (libgit2 also mirrors it to HEAD's reflog when HEAD points at the ref).
    /// Returns libgit2's return code (0 = moved; see the Error constants) and its error text.
    /// </summary>
    internal static (int Code, string Message) CompareAndSwapReference(string gitDirectory, string referenceName, byte[] newId, byte[] expectedOldId, string logMessage)
    {
        var code = OpenRepository(out var repository, gitDirectory);
        if (code != 0)
        {
            return (code, LastError());
        }

        try
        {
            code = CreateMatching(out var reference, repository, referenceName, newId, 1, expectedOldId, logMessage);
            if (code != 0)
            {
                return (code, LastError());
            }

            FreeReference(reference);
            return (0, string.Empty);
        }
        finally
        {
            FreeRepository(repository);
        }
    }

    /// <summary><c>git_error_last()</c> is a pointer to <c>{ char *message; int klass; }</c>, valid until the next libgit2 call on this thread.</summary>
    private static string LastError()
    {
        var error = LastErrorPointer();
        if (error == IntPtr.Zero)
        {
            return "(no libgit2 error text)";
        }

        var text = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error));
        return string.IsNullOrEmpty(text) ? "(no libgit2 error text)" : text;
    }

    private static string ReadLibraryName()
    {
        var holder = typeof(Repository).Assembly.GetType("LibGit2Sharp.Core.NativeDllName")
            ?? throw new DllNotFoundException("LibGit2Sharp.Core.NativeDllName is not in the LibGit2Sharp assembly; the package layout changed, so the native library name cannot be read.");
        var field = holder.GetField("Name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return field?.GetRawConstantValue() as string
            ?? throw new DllNotFoundException("LibGit2Sharp.Core.NativeDllName.Name is not a string constant; the package layout changed, so the native library name cannot be read.");
    }

    private static IntPtr Load(string name)
    {
        // The exact resolution LibGit2Sharp's own P/Invokes get (its resolver, then the default probing): the handle is the one it already holds.
        if (NativeLibrary.TryLoad(name, typeof(Repository).Assembly, null, out var handle))
        {
            return handle;
        }

        var directories = new List<string>();
        foreach (var root in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(Repository).Assembly.Location) })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            directories.Add(root);
            var runtimes = Path.Combine(root, "runtimes");
            if (Directory.Exists(runtimes))
            {
                directories.AddRange(Directory.EnumerateDirectories(runtimes).Select(rid => Path.Combine(rid, "native")));
            }
        }

        foreach (var directory in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var pattern in new[] { name + ".*", "lib" + name + ".*" })
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                {
                    if (NativeLibrary.TryLoad(file, out handle))
                    {
                        return handle;
                    }
                }
            }
        }

        throw new DllNotFoundException($"The libgit2 native library '{name}' that LibGit2Sharp uses could not be loaded (not resolved by LibGit2Sharp's resolver, nor found under runtimes/*/native).");
    }

    private static T Bind<T>(IntPtr library, string exportName)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(
            NativeLibrary.TryGetExport(library, exportName, out var address)
                ? address
                : throw new EntryPointNotFoundException($"libgit2 does not export '{exportName}'."));
}
