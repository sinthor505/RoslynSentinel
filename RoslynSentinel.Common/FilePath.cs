using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

[JsonConverter(typeof(FilePathJsonConverter))]
public readonly struct FilePath : IEquatable<FilePath>, IComparable<FilePath>
{
    public readonly bool Validated;  // whether the path has been validated as absolute and normalized

    public string Absolute { get; } = string.Empty;
    public string Relative { get; } = string.Empty;

    public FilePath(string path, string? solutionRoot = "", bool validated = false)
    {
        Absolute = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        Relative = string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(solutionRoot) ? string.Empty : Path.GetRelativePath(solutionRoot, path);
        Validated = validated || File.Exists(Absolute);
    }

    // construct from whatever the wire sent, against the known root
    public static FilePath FromWire(string pathArg, string? solutionRoot)
    {
        var clean = NormalizeWirePath(pathArg);

        if (string.IsNullOrWhiteSpace(clean))
        {
            return new FilePath(string.Empty, solutionRoot);
        }

        if (Path.IsPathRooted(clean))
        {
            return new FilePath(Path.GetFullPath(clean), solutionRoot, validated: true);
        }

        // PersistentWorkspaceManager.GetSolutionRoot() returns null whenever no solution is
        // loaded, or the loaded solution is in-memory and has no file path. Tools call FromWire
        // before their own try/catch, so combining against a null root threw a raw
        // ArgumentNullException straight out of the MCP boundary. Keep the caller's relative
        // path instead — resolving it against the process working directory would silently
        // produce a path that points nowhere near the solution.
        if (string.IsNullOrWhiteSpace(solutionRoot))
        {
            return new FilePath(clean, solutionRoot);
        }

        return new FilePath(Path.GetFullPath(Path.Combine(solutionRoot, clean)), solutionRoot, validated: true);
    }

    // Agents sometimes pass path arguments wrapped in stray quotes (straight or smart) or
    // whitespace picked up from shell-quoted examples or markdown, e.g. "'./Foo/Foo.sln'".
    // Strip those iteratively so the literal wrapping characters don't end up baked into a
    // resolved path (and cause File.Exists/Directory.Exists to fail on an otherwise-valid path).
    private static readonly char[] PathWrapChars = ['\'', '"', '‘', '’', '“', '”', ' ', '\t', '\r', '\n'];

    // Collapse repeated backslashes introduced by JSON double-encoding (e.g. c:\\\\foo → c:\foo),
    // and strip stray wrapping quotes/whitespace. Preserves the leading \\ of UNC paths.
    public static string NormalizeWirePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

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

    public string RelativeTo(string solutionRoot)
    {
        return Path.GetRelativePath(solutionRoot, this.Absolute);
    }

    public override string ToString() => Absolute ?? string.Empty;
    public override bool Equals(object? obj)
    {
        return obj is FilePath other && string.Equals(Absolute, other.Absolute, StringComparison.OrdinalIgnoreCase);
    }

    // compare to string for convenience
    public bool Equals(string? other)
    {
        return string.Equals(Absolute, other, StringComparison.OrdinalIgnoreCase);
    }

    // implicit conversion from string to FilePath for convenience
    public static implicit operator FilePath(string path) => new FilePath(path);

    //implicit conversion from filePath to string for convenience
    public static implicit operator string(FilePath filePath) => filePath.Absolute;

    //Equality operators for convenience
    public static bool operator ==(FilePath left, FilePath right) => left.Equals(right);
    public static bool operator !=(FilePath left, FilePath right) => !left.Equals(right);

    // string equality operators for Windows
    public static bool operator ==(FilePath left, string? right) => left.Equals(right);
    public static bool operator !=(FilePath left, string? right) => !left.Equals(right);
    public static bool operator ==(string? left, FilePath right) => right.Equals(left);
    public static bool operator !=(string? left, FilePath right) => !right.Equals(left);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Absolute);

    // startswith for convenience
    public bool StartsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Absolute.StartsWith(value, comparisonType);

    //endswith for convenience
    public bool EndsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Absolute.EndsWith(value, comparisonType);

    public static bool operator <(FilePath left, string right) => !left.Absolute.StartsWith(right, StringComparison.OrdinalIgnoreCase);

    public static bool operator >(FilePath left, string right) => left.Absolute.StartsWith(right, StringComparison.OrdinalIgnoreCase);

    // contains operator for convenience
    public bool Contains(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Absolute.Contains(value, comparisonType);

    public bool Equals(FilePath other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(Absolute, other.Absolute);
    }

    public int CompareTo(FilePath other)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(Absolute, other.Absolute);
    }
}

/// <summary>
/// Enables System.Text.Json to serialize <see cref="FilePath"/> both as a plain JSON string
/// and as a dictionary property name (required for Dictionary&lt;FilePath, ...&gt; serialization).
/// </summary>
public sealed class FilePathJsonConverter : JsonConverter<FilePath>
{
    public override FilePath Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new FilePath(FilePath.NormalizeWirePath(reader.GetString()!));

    public override void Write(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    // Required for Dictionary<FilePath, TValue> key serialization
    public override FilePath ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new FilePath(FilePath.NormalizeWirePath(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}
