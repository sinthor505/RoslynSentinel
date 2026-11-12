using System.Security.Cryptography;
using System.Text;

namespace RoslynSentinel.Common.AgentLoop;

/// <summary>
/// Parsed error-policy Markdown file. The file holds one <c>### &lt;wire name&gt;</c> section per
/// <see cref="ErrorPolicyKey"/> (for example <c>### FileNotFound.Edit</c>); the section body is the
/// guidance text for that key. Text before the first section is a preamble and is ignored. A key
/// with no section is not an error: <see cref="MissingKeys"/> lists it and the filter adds nothing
/// for it.
/// </summary>
public sealed class ErrorPolicyDocument
{
    /// <summary>Length of the lowercase hex prefix returned by <see cref="ContentHash"/>.</summary>
    public const int ShortHashLength = 12;

    /// <summary>
    /// Prefix of the rendered section heading. The later pointer sentence in the agent prompt
    /// names the same string, so both sides must use <see cref="RenderedHeading"/>.
    /// </summary>
    public const string HeadingPrefix = "Error handling: ";

    private const string SectionMarker = "### ";

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private ErrorPolicyDocument(
        IReadOnlyDictionary<ErrorPolicyKey, string> entries,
        IReadOnlyList<ErrorPolicyKey> missingKeys,
        string contentHash)
    {
        Entries = entries;
        MissingKeys = missingKeys;
        ContentHash = contentHash;
    }

    /// <summary>Section bodies for the keys present in the file, trimmed, with line breaks as \n.</summary>
    public IReadOnlyDictionary<ErrorPolicyKey, string> Entries { get; }

    /// <summary>Keys from <see cref="ErrorPolicyKeys.All"/> that have no section, in All order.</summary>
    public IReadOnlyList<ErrorPolicyKey> MissingKeys { get; }

    /// <summary>First <see cref="ShortHashLength"/> lowercase hex characters of the SHA-256 of the exact file bytes.</summary>
    public string ContentHash { get; }

    /// <summary>Parses Markdown text. The hash is computed over the UTF-8 bytes of <paramref name="markdown"/>.</summary>
    /// <exception cref="FormatException">A heading is unknown, duplicated, or has an empty body.</exception>
    public static ErrorPolicyDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        byte[] bytes = Encoding.UTF8.GetBytes(markdown);
        return Build(markdown, HashBytes(bytes));
    }

    /// <summary>
    /// Decodes UTF-8 bytes and parses them. A leading BOM is stripped for parsing only; the hash is
    /// computed over the original bytes, BOM included.
    /// </summary>
    /// <exception cref="FormatException">A heading is unknown, duplicated, or has an empty body.</exception>
    public static ErrorPolicyDocument ParseBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        bool hasBom = bytes.Length >= Utf8Bom.Length
            && bytes[0] == Utf8Bom[0]
            && bytes[1] == Utf8Bom[1]
            && bytes[2] == Utf8Bom[2];
        int offset = hasBom ? Utf8Bom.Length : 0;
        string text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        return Build(text, HashBytes(bytes));
    }

    /// <summary>Reads the file at <paramref name="path"/> and parses its bytes. IO exceptions propagate.</summary>
    /// <exception cref="FormatException">A heading is unknown, duplicated, or has an empty body.</exception>
    public static ErrorPolicyDocument Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ParseBytes(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Heading text rendered for a key, for example "Error handling: FileNotFound.Edit". This is the
    /// shared string that the agent prompt's pointer sentence names.
    /// </summary>
    public static string RenderedHeading(ErrorPolicyKey key) => HeadingPrefix + ErrorPolicyKeys.ToWireName(key);

    private static string HashBytes(byte[] bytes)
    {
        byte[] digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant()[..ShortHashLength];
    }

    private static ErrorPolicyDocument Build(string text, string contentHash)
    {
        string normalised = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalised.Split('\n');

        var entries = new Dictionary<ErrorPolicyKey, string>();
        var headingLines = new Dictionary<ErrorPolicyKey, int>();

        int currentKeyLine = 0;
        ErrorPolicyKey? currentKey = null;
        int bodyStart = 0;

        for (int i = 0; i <= lines.Length; i++)
        {
            bool isHeading = i < lines.Length && lines[i].StartsWith(SectionMarker, StringComparison.Ordinal);
            bool isEnd = i == lines.Length;
            if (!isHeading && !isEnd)
            {
                continue;
            }

            if (currentKey is ErrorPolicyKey finishedKey)
            {
                string body = string.Join("\n", lines, bodyStart, i - bodyStart).Trim();
                if (body.Length == 0)
                {
                    throw new FormatException(
                        $"Error-policy heading '{SectionMarker}{ErrorPolicyKeys.ToWireName(finishedKey)}' on line {currentKeyLine} has an empty body. Add guidance text under it or remove the section.");
                }

                entries.Add(finishedKey, body);
            }

            if (isEnd)
            {
                break;
            }

            string name = lines[i].Substring(SectionMarker.Length).TrimEnd();
            int lineNumber = i + 1;
            if (!ErrorPolicyKeys.TryParseWireName(name, out ErrorPolicyKey key))
            {
                throw new FormatException(
                    $"Unknown error-policy heading '{SectionMarker}{name}' on line {lineNumber}. Valid headings are: {ValidHeadingList()}.");
            }

            if (headingLines.TryGetValue(key, out int firstLine))
            {
                throw new FormatException(
                    $"Duplicate error-policy heading '{SectionMarker}{name}' on line {lineNumber}; the same heading first appears on line {firstLine}.");
            }

            headingLines[key] = lineNumber;
            currentKey = key;
            currentKeyLine = lineNumber;
            bodyStart = i + 1;
        }

        var missing = ErrorPolicyKeys.All.Where(key => !entries.ContainsKey(key)).ToList();
        return new ErrorPolicyDocument(entries, missing.AsReadOnly(), contentHash);
    }

    private static string ValidHeadingList()
    {
        return string.Join(", ", ErrorPolicyKeys.All.Select(key => SectionMarker + ErrorPolicyKeys.ToWireName(key)));
    }
}
