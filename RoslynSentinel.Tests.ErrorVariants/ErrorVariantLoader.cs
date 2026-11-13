using System.Security.Cryptography;
using System.Text;

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Loads error-message variants from ErrorVariants/&lt;errorCode&gt;/&lt;id&gt;.md (copied to the output folder).
/// File format: a "## message" section (required) and an optional "## trailing" section.
/// </summary>
public static class ErrorVariantLoader
{
    private const string HeadingPrefix = "## ";
    private const string MessageSection = "message";
    private const string TrailingSection = "trailing";

    public static ErrorVariant Parse(string errorCode, string id, string markdown)
    {
        var lines = NormalizeLineEndings(markdown).Split('\n');
        var sections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                var name = line[HeadingPrefix.Length..].Trim();
                if (name != MessageSection && name != TrailingSection)
                {
                    throw new FormatException(
                        $"Variant '{id}': unknown section '## {name}'. Allowed sections are '## {MessageSection}' and '## {TrailingSection}'.");
                }

                if (sections.ContainsKey(name))
                {
                    throw new FormatException($"Variant '{id}': duplicate section '## {name}'.");
                }

                current = new List<string>();
                sections[name] = current;
                continue;
            }

            if (current is null)
            {
                if (line.Trim().Length > 0)
                {
                    throw new FormatException(
                        $"Variant '{id}': text before the first '## ' heading. The file must start with '## {MessageSection}'.");
                }

                continue;
            }

            current.Add(line);
        }

        if (!sections.TryGetValue(MessageSection, out var messageLines))
        {
            throw new FormatException($"Variant '{id}': missing '## {MessageSection}' section.");
        }

        var message = BodyOf(messageLines);
        if (message.Length == 0)
        {
            throw new FormatException($"Variant '{id}': '## {MessageSection}' section is empty.");
        }

        string? trailing = sections.TryGetValue(TrailingSection, out var trailingLines)
            ? BodyOf(trailingLines)
            : null;

        return new ErrorVariant(id, errorCode, message, trailing, HashOf(markdown));
    }

    // Deliberate copy of the error-policy hash idea: ModelEval's/Common's ErrorPolicyDocument hashes raw
    // bytes; this one normalizes line endings first so CRLF and LF checkouts agree.
    // TODO unify with ErrorPolicyDocument.ContentHash
    public static string HashOf(string markdown)
    {
        var normalized = NormalizeLineEndings(markdown).TrimEnd();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant()[..12];
    }

    public static ErrorVariant LoadFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var errorCode = Path.GetFileName(Path.GetDirectoryName(fullPath)) ?? string.Empty;
        var id = Path.GetFileNameWithoutExtension(fullPath);
        var markdown = File.ReadAllText(fullPath, Encoding.UTF8);
        return Parse(errorCode, id, markdown);
    }

    public static ErrorVariant Load(string errorCode, string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ErrorVariants", errorCode, id + ".md");
        if (!File.Exists(path))
        {
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            var message = Directory.Exists(folder)
                ? $"Error variant '{id}' not found at '{path}'. Ids available for '{errorCode}': {ListedIds(errorCode)}."
                : $"Error variant '{id}' not found at '{path}': the folder for error code '{errorCode}' is missing.";
            throw new FileNotFoundException(message, path);
        }

        return LoadFile(path);
    }

    public static IReadOnlyList<string> ListIds(string errorCode)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "ErrorVariants", errorCode);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder, "*.md")
            .Select(file => Path.GetFileNameWithoutExtension(file) ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string ListedIds(string errorCode)
    {
        var ids = ListIds(errorCode);
        return ids.Count == 0 ? "none" : string.Join(", ", ids);
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    // Drops leading whitespace-only lines and trailing whitespace; an all-blank body yields "".
    private static string BodyOf(List<string> lines)
    {
        var start = 0;
        while (start < lines.Count && lines[start].Trim().Length == 0)
        {
            start++;
        }

        return string.Join("\n", lines.Skip(start)).TrimEnd();
    }
}
