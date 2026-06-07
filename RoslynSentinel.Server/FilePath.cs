namespace RoslynSentinel.Server;

public readonly struct FilePath : IEquatable<FilePath>
{
    public string? Path
    {
        get;
    }

    public FilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(path));
        }
        Path = path;
    }
    public override string ToString() => Path;
    public override bool Equals(object? obj)
    {
        return obj is FilePath other && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
    }

    // implicit conversion from string to FilePath for convenience
    public static implicit operator FilePath(string path) => new FilePath(path);

    //implicit conversion from filePath to string for convenience
    public static implicit operator string(FilePath filePath) => filePath.Path!;

    //Equality operators for convenience
    public static bool operator ==(FilePath left, FilePath right) => left.Equals(right);
    public static bool operator !=(FilePath left, FilePath right) => !left.Equals(right);

    // string equality operators for Windows
    public static bool operator ==(FilePath left, string right) => left.Equals(new FilePath(right));
    public static bool operator !=(FilePath left, string right) => !left.Equals(new FilePath(right));
    public static bool operator ==(string left, FilePath right) => new FilePath(left).Equals(right);
    public static bool operator !=(string left, FilePath right) => !new FilePath(left).Equals(right);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path ?? string.Empty);

    // startswith for convenience
    public bool StartsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Path?.StartsWith(value, comparisonType) ?? false;

    //endswith for convenience
    public bool EndsWith(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Path?.EndsWith(value, comparisonType) ?? false;

    public static bool operator <(FilePath left, string right) => !(left.Path?.StartsWith(right, StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool operator >(FilePath left, string right) => left.Path?.StartsWith(right, StringComparison.OrdinalIgnoreCase) ?? false;

    // contains operator for convenience
    public bool Contains(string value, StringComparison comparisonType = StringComparison.OrdinalIgnoreCase) => this.Path?.Contains(value, comparisonType) ?? false;

    public bool Equals(FilePath other)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path);
    }
}
