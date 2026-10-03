using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

public static class EolUtilities
{
    /// <summary>Detects the dominant line-ending style in a string: CRLF (\r\n), LF (\n), or CR (\r).</summary>
    public static string DetectDominantEol(string content)
    {
        int crlfCount = 0, lfCount = 0, crCount = 0;
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] == '\r')
            {
                if (i + 1 < content.Length && content[i + 1] == '\n')
                {
                    crlfCount++;
                    i++; // skip the \n
                }
                else
                {
                    crCount++;
                }
            }
            else if (content[i] == '\n')
            {
                lfCount++;
            }
        }

        // Tie-break: CRLF, then LF, then CR
        if (crlfCount > 0 && crlfCount >= lfCount && crlfCount >= crCount)
        {
            return "\r\n";
        }

        if (lfCount > 0)
        {
            return "\n";
        }

        if (crCount > 0)
        {
            return "\r";
        }

        return Environment.NewLine; // no line breaks found
    }

    public static string DetectDominantEol(SourceText sourceText)
    {
        var endings = sourceText.Lines
            .Select(l => sourceText.GetSubText(TextSpan.FromBounds(l.End, l.EndIncludingLineBreak)).ToString())
            .ToList();
        return endings
            .Where(e => e.Length > 0)
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? Environment.NewLine;
    }

    public static string NormalizeEol(string content, string dominantEol)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return dominantEol == "\n" ? normalized : normalized.Replace("\n", dominantEol);
    }
}
