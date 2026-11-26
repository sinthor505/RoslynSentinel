using System.Text.RegularExpressions;

namespace RoslynSentinel.Common;

/// <summary>
/// Helpers for the think tags that some models wrap their reasoning in inside message text.
/// </summary>
internal static class ThinkTagText
{
    /// <summary>The opening think tag.</summary>
    public const string OpenTag = "<think>";

    /// <summary>The closing think tag.</summary>
    public const string CloseTag = "</think>";

    private static readonly Regex TagPattern = new(
        Regex.Escape(OpenTag) + "|" + Regex.Escape(CloseTag),
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes every open and close think tag (case-insensitive) and trims the result.
    /// </summary>
    /// <param name="text">Text that may contain think tags.</param>
    /// <returns>The text without think tags, trimmed.</returns>
    public static string RemoveTags(string text)
    {
        return TagPattern.Replace(text, string.Empty).Trim();
    }

    /// <summary>
    /// Reports whether the text contains the close tag (case-insensitive).
    /// </summary>
    /// <param name="text">Text to check; null or empty gives false.</param>
    /// <returns>True when the text contains <see cref="CloseTag"/>.</returns>
    public static bool HasCloseTag(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(CloseTag, StringComparison.OrdinalIgnoreCase);
    }
}
