using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RoslynSentinel.Common;

/// <summary>
/// Content rule for the text of a failed multi-edit batch (ReplaceSnippet, ModifyModifier, ModifyAttribute and
/// ModifyBaseType): the first <see cref="FullDetailCount"/> entries are shown in full, every later entry is reduced
/// to one line that keeps its batchEdits index, and the whole text stays under <see cref="TotalCapChars"/>.
/// It bounds the content by counts and characters; it never measures a serialized response.
/// </summary>
public static class BatchFailureMessage
{
    public const int FullDetailCount = 3;
    public const int TotalCapChars = 8000;
    public const int OneLineMaxChars = 200;
    public const int MinFullEntryChars = 400;

    private const int SuffixReserveChars = 40;

    public static string Compose(string header, IReadOnlyList<string> failures)
    {
        if (failures is null || failures.Count == 0)
        {
            return header;
        }

        int count = failures.Count;
        if (count <= FullDetailCount && FitsWhole(header, failures))
        {
            return header + "\n" + string.Join("\n", failures);
        }

        int fullCount = Math.Min(count, FullDetailCount);
        var oneLines = new List<string>();
        for (int i = fullCount; i < count; i++)
        {
            oneLines.Add(FirstLine(failures[i]));
        }

        string tail = oneLines.Count == 0
            ? string.Empty
            : string.Join("\n", oneLines) + "\n" + NoteFor(count - FullDetailCount);
        int tailLength = tail.Length == 0 ? 0 : tail.Length + 1;

        int budget = Math.Max(MinFullEntryChars * fullCount, TotalCapChars - header.Length - tailLength);
        int perEntry = budget / fullCount;

        var full = new List<string>(fullCount);
        for (int i = 0; i < fullCount; i++)
        {
            full.Add(CutEntry(failures[i], perEntry));
        }

        string result = header + "\n" + string.Join("\n", full);
        return tail.Length == 0 ? result : result + "\n" + tail;
    }

    private static bool FitsWhole(string header, IReadOnlyList<string> failures)
    {
        int total = header.Length + 1;
        foreach (string entry in failures)
        {
            total += entry.Length + 1;
        }

        return total <= TotalCapChars;
    }

    private static string FirstLine(string entry)
    {
        int end = entry.IndexOfAny(new[] { '\n', '\r' });
        string line = (end < 0 ? entry : entry.Substring(0, end)).Trim();
        if (line.Length <= OneLineMaxChars)
        {
            return line;
        }

        return line.Substring(0, OneLineMaxChars - 3) + "...";
    }

    private static string CutEntry(string entry, int perEntry)
    {
        if (entry.Length <= perEntry)
        {
            return entry;
        }

        int keep = Math.Max(0, perEntry - SuffixReserveChars);
        int omitted = entry.Length - keep;
        return entry.Substring(0, keep) + " ... [" + omitted + " chars omitted]";
    }

    private static string NoteFor(int moreCount)
    {
        return "(" + moreCount + " more failing edit(s) shown as one line each; fix the detailed ones above and re-run to see the rest in full.)";
    }
}
