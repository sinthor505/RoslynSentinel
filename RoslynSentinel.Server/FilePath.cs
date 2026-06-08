using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynSentinel.Server;

[JsonConverter(typeof(FilePathJsonConverter))]
public readonly struct FilePath : IEquatable<FilePath>
{
    private readonly string absolute;   // canonical internal form
    private readonly bool validated;  // whether the path has been validated as absolute and normalized

    public string? Absolute
    {
        get
        {
            if (field == null)
            {
                throw new InvalidOperationException("FilePath was default-constructed and has no value.");
            }
            return field;
        }
    }

    public FilePath(string path, bool validated = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(path));
        }
        absolute = path;
    }

    // construct from whatever the wire sent, against the known root
    public static FilePath FromWire(string pathArg, string solutionRoot)
    {
        if (string.IsNullOrWhiteSpace(pathArg))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(pathArg));
        }
        string abs = Path.IsPathRooted(pathArg)
            ? Path.GetFullPath(pathArg)
            : Path.GetFullPath(Path.Combine(solutionRoot, pathArg));
        return new FilePath(abs, validated: true);
    }

    public string RelativeTo(string solutionRoot)
    {
        return Path.GetRelativePath(solutionRoot, this.absolute);
    }

    public override string ToString() => absolute;
    public override bool Equals(object? obj)
    {
        return obj is FilePath other && string.Equals(absolute, other.absolute, StringComparison.OrdinalIgnoreCase);
    }

    // implicit conversion from string to FilePath for convenience
    public static implicit operator FilePath(string path) => new FilePath(path);

    //implicit conversion from filePath to string for convenience
    public static implicit operator string(FilePath filePath) => filePath.absolute!;

    //Equality operators for convenience
    public static bool operator ==(FilePath left, FilePath right) => left.Equals(right);
    public static bool operator !=(FilePath left, FilePath right) => !left.Equals(right);

    // string equality operators for Windows
    public static bool operator ==(FilePath left, string right) => left.Equals(new FilePath(right));
    public static bool operator !=(FilePath left, string right) => !left.Equals(new FilePath(right));
    public static bool operator ==(string left, FilePath right) => new FilePath(left).Equals(right);
    public static bool operator !=(string left, FilePath right) => !new FilePath(left).Equals(right);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(absolute ?? string.Empty);

    // startswith for convenience
    public bool StartsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.absolute?.StartsWith(value, comparisonType) ?? false;

    //endswith for convenience
    public bool EndsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.absolute?.EndsWith(value, comparisonType) ?? false;

    public static bool operator <(FilePath left, string right) => !(left.absolute?.StartsWith(right, StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool operator >(FilePath left, string right) => left.absolute?.StartsWith(right, StringComparison.OrdinalIgnoreCase) ?? false;

    // contains operator for convenience
    public bool Contains(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.absolute?.Contains(value, comparisonType) ?? false;

    public bool Equals(FilePath other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(absolute, other.absolute);
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
