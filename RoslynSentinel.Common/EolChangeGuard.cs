namespace RoslynSentinel.Common;

/// <summary>Counts of each physical line-ending style in a piece of text.</summary>
/// <param name="Crlf">Number of CR+LF pairs.</param>
/// <param name="Lf">Number of lone LF characters.</param>
/// <param name="Cr">Number of lone CR characters.</param>
public readonly record struct EolCounts(int Crlf, int Lf, int Cr)
{
    public int Total => Crlf + Lf + Cr;

    /// <summary>True when exactly one style occurs (and at least one line break exists).</summary>
    public bool IsSingleStyle => (Crlf > 0 ? 1 : 0) + (Lf > 0 ? 1 : 0) + (Cr > 0 ? 1 : 0) == 1;

    /// <summary>The most frequent style; ties break CRLF, then LF, then CR. Empty when there are no line breaks.</summary>
    public string Dominant
    {
        get
        {
            if (Total == 0)
            {
                return "none";
            }

            if (Crlf >= Lf && Crlf >= Cr)
            {
                return "CRLF";
            }

            return Lf >= Cr ? "LF" : "CR";
        }
    }

    /// <summary>The line terminator for the dominant style: the platform default when there are no line breaks.</summary>
    public string DominantTerminator => Total == 0
        ? Environment.NewLine
        : Dominant switch
        {
            "CRLF" => "\r\n",
            "LF" => "\n",
            _ => "\r",
        };

    /// <summary>Human-readable style, e.g. "CRLF" or "mixed (CRLF x 40, LF x 2)".</summary>
    public string Describe()
    {
        if (Total == 0)
        {
            return "no line breaks";
        }

        if (IsSingleStyle)
        {
            return Dominant;
        }

        var parts = new List<string>();
        if (Crlf > 0)
        {
            parts.Add($"CRLF x {Crlf}");
        }

        if (Lf > 0)
        {
            parts.Add($"LF x {Lf}");
        }

        if (Cr > 0)
        {
            parts.Add($"CR x {Cr}");
        }

        return $"mixed ({string.Join(", ", parts)})";
    }
}

/// <summary>Counts line-ending styles in text (see EolCounts); the former refusing guard was removed when the chokepoint started converting to the resolved EOL.</summary>
public static class EolChangeGuard
{
    public static EolCounts Count(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        return new EolCounts(crlf, lf, cr);
    }
}
