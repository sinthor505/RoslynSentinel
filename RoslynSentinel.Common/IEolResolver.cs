namespace RoslynSentinel.Common;

public interface IEolResolver
{
    /// <summary>Returns the line terminator the write chokepoint would use for this file under the active EOL policy (mode override, then --eol, then the .editorconfig end_of_line, then the platform default); engines use it instead of detecting the EOL from the source file.</summary>
    string ResolveEol(string filePath, string? preImageText);
}
