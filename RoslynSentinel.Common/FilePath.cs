using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

[JsonConverter(typeof(FilePathJsonConverter))]
public readonly struct FilePath : IEquatable<FilePath>, IComparable<FilePath>
{
    public readonly bool Validated;  // whether the path has been validated as absolute and normalized

    public string Absolute { get; } = string.Empty;
    public string Relative { get; } = string.Empty;

    public FilePath(string path, bool validated = false)
    {
        Absolute = string.IsNullOrWhiteSpace(path) ? string.Empty : path;
        Validated = validated || File.Exists(Absolute);
    }

    // construct from whatever the wire sent, against the known root
    public static FilePath FromWire(string pathArg, string solutionRoot)
    {
        string abs = Path.IsPathRooted(pathArg)
            ? Path.GetFullPath(pathArg)
            : Path.GetFullPath(Path.Combine(solutionRoot, pathArg));
        return new FilePath(abs, validated: true);
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
    public static bool operator ==(FilePath left, string right) => left.Equals(right);
    public static bool operator !=(FilePath left, string right) => !left.Equals(right);
    public static bool operator ==(string left, FilePath right) => right.Equals(left);
    public static bool operator !=(string left, FilePath right) => !right.Equals(left);

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
        => new FilePath(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());

    // Required for Dictionary<FilePath, TValue> key serialization
    public override FilePath ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new FilePath(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, FilePath value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.ToString());
}
