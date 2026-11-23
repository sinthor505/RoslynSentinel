using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Support;

/// <summary>
/// Shared text builders and assertions for formatting, line-ending and whole-output tests.
/// </summary>
public static class FormatKit
{
    /// <summary>Joins lines with LF, then converts to CRLF when requested.</summary>
    public static string Src(bool crlf, params string[] lines)
    {
        var text = string.Join("\n", lines);
        return crlf ? text.Replace("\n", "\r\n") : text;
    }

    /// <summary>Replaces the anchor line's LF with CRLF, leaving exactly one stray CRLF in the text.</summary>
    public static string WithStrayCrlf(string lfText, string anchorLine)
    {
        var anchor = anchorLine + "\n";
        Assert.That(lfText, Does.Contain(anchor), "Precondition: the anchor line is present.");
        var mixed = lfText.Replace(anchor, anchorLine + "\r\n");
        Assert.That(mixed.Count(c => c == '\r'), Is.EqualTo(1), "Precondition: exactly one stray CRLF.");
        return mixed;
    }

    /// <summary>Asserts exact equality (including CR, LF and tabs); a failure shows those characters as visible markers. Use for tests whose subject is formatting or line endings.</summary>
    public static void AssertByteExact(string actual, string expected, string? because = null)
    {
        var header = because ?? "Byte-exact mismatch.";
        var message = header + "\n  expected: " + Visible(expected) + "\n  actual:   " + Visible(actual);
        Assert.That(actual, Is.EqualTo(expected), message);
    }

    /// <summary>Asserts equality after collapsing every whitespace run to one space and trimming the ends. Use for every other test that compares a whole output text.</summary>
    public static void AssertSameIgnoringWhitespace(string actual, string expected, string? because = null)
    {
        var normalisedActual = Normalise(actual);
        var normalisedExpected = Normalise(expected);
        var header = because ?? "Text differs beyond whitespace.";
        var message = header + "\n  expected: " + normalisedExpected + "\n  actual:   " + normalisedActual;
        Assert.That(normalisedActual, Is.EqualTo(normalisedExpected), message);
    }

    private static string Normalise(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string Visible(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
