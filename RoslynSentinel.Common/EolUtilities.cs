using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Common;

public static class EolUtilities
{
    // Added by AddMember (expected - used for diagnostics)
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
    // Added by InsertMemberAfter (expected - used for diagnostics)
    public static string NormalizeEol(string content, string dominantEol)
    {
        var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
        return dominantEol == "\n" ? normalized : normalized.Replace("\n", dominantEol);
    }
}
