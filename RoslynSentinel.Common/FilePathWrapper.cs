using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

/// <summary>
/// Why a <see cref="FilePathWrapper"/> failed to validate. <c>Validated == false</c> can mean either
/// the path argument itself was empty/invalid, or no solution was loaded at all (no root to resolve
/// against) -> callers need to tell these apart instead of assuming "path is invalid" when the real
/// problem is "load a solution first."
/// </summary>
public enum FilePathFailureReason
{
    /// <summary>Validation succeeded, or was never attempted.</summary>
    None = 0,

    /// <summary>No solution is loaded, so there was no solution root to resolve against.</summary>
    NoSolutionLoaded,

    /// <summary>A solution is loaded, but the path argument itself was null/empty/whitespace.</summary>
    PathInvalid,
}

[JsonConverter(typeof(FilePathJsonConverter))]
public readonly struct FilePathWrapper : IEquatable<FilePathWrapper>, IComparable<FilePathWrapper>
{
    public readonly bool Validated;  // whether the path has been validated as absolute and normalized

    /// <summary>
    /// Why <see cref="Validated"/> is false. Branch on this instead of assuming the path was bad.
    /// </summary>
    public readonly FilePathFailureReason FailureReason;

    public string Absolute { get; } = string.Empty;
    public string Relative { get; } = string.Empty;

    public FilePathWrapper(string path, string? solutionRoot = "", bool validated = false, FilePathFailureReason failureReason = FilePathFailureReason.None)
    {
        Absolute = string.IsNullOrWhiteSpace(path) ? string.Empty : CanonicalizeSeparators(path);
        Relative = string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(solutionRoot) ? string.Empty : Path.GetRelativePath(solutionRoot, Absolute);
        Validated = validated || File.Exists(Absolute);
        FailureReason = Validated ? FilePathFailureReason.None : failureReason;
    }

    // Models frequently submit forward-slash paths (e.g. "C:/Users/.../Foo.cs") regardless of
    // platform. FileSystemWatcher's e.FullPath always reports backslashes on Windows, so an
    // uncanonicalized forward-slash FilePathWrapper used as a dictionary key (e.g. _internalChanges in
    // PersistentWorkspaceManager) can never match the watcher's own-write-suppression lookup ->
    // a deterministic miss, not a race. Canonicalize here so every construction path (bare
    // constructor, FromWire, JSON converter) agrees on separators. UNC prefix (\\) is preserved.
    private static string CanonicalizeSeparators(string path)
    {
        bool isUnc = path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return isUnc ? @"\\" + normalized.TrimStart(Path.DirectorySeparatorChar) : normalized;
    }

    // construct from whatever the wire sent, against the known root
    public static FilePathWrapper FromWire(string? pathArg, string? solutionRoot)
    {
        var clean = NormalizeWirePath(pathArg ?? string.Empty);

        if (string.IsNullOrWhiteSpace(clean))
        {
            return new FilePathWrapper(string.Empty, solutionRoot);
        }

        if (Path.IsPathRooted(clean))
        {
            return new FilePathWrapper(Path.GetFullPath(clean), solutionRoot, validated: true);
        }

        // PersistentWorkspaceManager.GetSolutionRoot() returns null whenever no solution is
        // loaded, or the loaded solution is in-memory and has no file path. Tools call FromWire
        // before their own try/catch, so combining against a null root threw a raw
        // ArgumentNullException straight out of the MCP boundary. Keep the caller's relative
        // path instead -> resolving it against the process working directory would silently
        // produce a path that points nowhere near the solution.
        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            return new FilePathWrapper(clean, solutionRoot);
        }

        return new FilePathWrapper(Path.GetFullPath(Path.Combine(solutionRoot, clean)), solutionRoot, validated: true);
    }

    // Agents sometimes pass path arguments wrapped in stray quotes (straight or smart) or
    // whitespace picked up from shell-quoted examples or markdown, e.g. "'./Foo/Foo.sln'".
    // Strip those iteratively so the literal wrapping characters don't end up baked into a
    // resolved path (and cause File.Exists/Directory.Exists to fail on an otherwise-valid path).
    private static readonly char[] PathWrapChars = ['\'', '"', '‘', '’', '“', '”', ' ', '\t', '\r', '\n'];

    // Collapse repeated backslashes introduced by JSON double-encoding (e.g. c:\\\\foo -> c:\foo),
    // and strip stray wrapping quotes/whitespace. Preserves the leading \\ of UNC paths.
    public static string NormalizeWirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var trimmed = path;
        string previous;
        do
        {
            previous = trimmed;
            trimmed = trimmed.Trim(PathWrapChars);
        } while (trimmed.Length != previous.Length);

        bool isUnc = trimmed.StartsWith(@"\\", StringComparison.Ordinal);
        string body = isUnc ? trimmed.Substring(2) : trimmed;
        body = body.Replace(@"\\", @"\");
        return isUnc ? @"\\" + body : body;
    }

    /// <summary>
    /// Wire-boundary construction used by <see cref="FilePathJsonConverter"/>: normalizes the raw string
    /// (<see cref="NormalizeWirePath"/>) and resolves a relative path against the ambient solution root
    /// when one is in scope (<see cref="UseSolutionRoot"/>).
    /// </summary>
    internal static FilePathWrapper FromWirePath(string? raw) => FromAmbientRoot(NormalizeWirePath(raw ?? string.Empty));

    public string RelativeTo(string solutionRoot)
    {
        return Path.GetRelativePath(solutionRoot, this.Absolute);
    }

    public override string ToString() => Absolute ?? string.Empty;
    public override bool Equals(object? obj)
    {
        return obj is FilePathWrapper other && string.Equals(Absolute, other.Absolute, StringComparison.OrdinalIgnoreCase);
    }

    // compare to string for convenience
    public bool Equals(string? other)
    {
        return string.Equals(Absolute, other, StringComparison.OrdinalIgnoreCase);
    }

    // The solution root of the tool call currently being served, or null outside one. AsyncLocal (not
    // a plain static) so concurrent calls and parallel tests never see each other's root: it is set
    // only by the server's request filter around a tool call (see UseSolutionRoot) and flows down the
    // call's async chain, including MCP argument binding (the JSON converter below). Code that runs
    // outside a scope sees null and behaves exactly as before.
    private static readonly AsyncLocal<string?> AmbientSolutionRoot = new();

    /// <summary>
    /// Makes <paramref name="solutionRoot"/> the root that the implicit <c>string -> FilePathWrapper</c>
    /// conversion and <see cref="FilePathJsonConverter"/> resolve RELATIVE paths against, until the returned
    /// scope is disposed. Without a root those two entry points build an unrooted wrapper whose
    /// <see cref="Absolute"/> is the relative string, which a tool that skips <see cref="FromWire"/> then
    /// feeds to a path lookup that can never match (e.g. <c>GetDiagnostics(scope: file)</c> with a
    /// solution-relative path). Rooted paths are never rewritten. Pass null/blank for no root.
    /// </summary>
    public static IDisposable UseSolutionRoot(string? solutionRoot)
    {
        var previous = AmbientSolutionRoot.Value;
        AmbientSolutionRoot.Value = string.IsNullOrWhiteSpace(solutionRoot) ? null : solutionRoot;
        return new SolutionRootScope(previous);
    }

    private sealed class SolutionRootScope(string? previous) : IDisposable
    {
        public void Dispose() => AmbientSolutionRoot.Value = previous;
    }

    // Builds a wrapper for a string that did not come through FromWire: a relative path is resolved
    // against the ambient solution root when one is set (marked validated, as FromWire does); every
    // other input keeps the original unrooted construction.
    private static FilePathWrapper FromAmbientRoot(string? path)
    {
        var root = AmbientSolutionRoot.Value;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return new FilePathWrapper(path ?? string.Empty);
        }

        try
        {
            return new FilePathWrapper(Path.GetFullPath(Path.Combine(root, CanonicalizeSeparators(path))), root, validated: true);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the OS cannot normalize is not this method's to reject: keep the old behavior.
            return new FilePathWrapper(path);
        }
    }

    // implicit conversion from string to FilePathWrapper for convenience. Resolves a relative path
    // against the ambient solution root when one is in scope (see UseSolutionRoot).
    public static implicit operator FilePathWrapper(string path) => FromAmbientRoot(path);

    //implicit conversion from filePath to string for convenience
    public static implicit operator string(FilePathWrapper filePath) => filePath.Absolute;

    //Equality operators for convenience
    public static bool operator ==(FilePathWrapper left, FilePathWrapper right) => left.Equals(right);
    public static bool operator !=(FilePathWrapper left, FilePathWrapper right) => !left.Equals(right);

    // string equality operators for Windows
    public static bool operator ==(FilePathWrapper left, string? right) => left.Equals(right);
    public static bool operator !=(FilePathWrapper left, string? right) => !left.Equals(right);
    public static bool operator ==(string? left, FilePathWrapper right) => right.Equals(left);
    public static bool operator !=(string? left, FilePathWrapper right) => !right.Equals(left);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Absolute);

    // startswith for convenience
    public bool StartsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Absolute.StartsWith(value, comparisonType);

    //endswith for convenience
    public bool EndsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Absolute.EndsWith(value, comparisonType);

    public static bool operator <(FilePathWrapper left, string right) => !left.Absolute.StartsWith(right, StringComparison.OrdinalIgnoreCase);

    public static bool operator >(FilePathWrapper left, string right) => left.Absolute.StartsWith(right, StringComparison.OrdinalIgnoreCase);

    // contains operator for convenience
    public bool Contains(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Absolute.Contains(value, comparisonType);

    public bool Equals(FilePathWrapper other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(Absolute, other.Absolute);
    }

    public int CompareTo(FilePathWrapper other)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(Absolute, other.Absolute);
    }
}

/// <summary>
/// Enables System.Text.Json to serialize <see cref="FilePathWrapper"/> both as a plain JSON string
/// and as a dictionary property name (required for Dictionary<FilePathWrapper, ...> serialization).
/// </summary>
public sealed class FilePathJsonConverter : JsonConverter<FilePathWrapper>
{
    public override FilePathWrapper Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"A file path parameter must be a string, but got a {reader.TokenType} value.");
        }

        // Relative paths resolve against the ambient solution root when a tool call has one in scope.
        return FilePathWrapper.FromWirePath(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, FilePathWrapper value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    // Required for Dictionary<FilePathWrapper, TValue> key serialization
    public override FilePathWrapper ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException(
                $"A file path dictionary key must be a string, but got a {reader.TokenType} value.");
        }

        return FilePathWrapper.FromWirePath(reader.GetString());
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, FilePathWrapper value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}
