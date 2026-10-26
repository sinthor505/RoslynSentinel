namespace RoslynSentinel.Common;

/// <summary>
/// Pure line-ending resolution: the terminator a write should use. Never reads
/// <see cref="EolPolicyOptions.Mode"/>, files or the environment; <see cref="Environment.NewLine"/>
/// is only the last fallback.
/// </summary>
public static class EolResolver
{
    /// <summary>
    /// Returns "\r\n" or "\n" (never "\r"). Crlf and Lf ignore the other inputs. Auto uses a
    /// single-style pre-image, then <paramref name="editorConfigEndOfLine"/> ("crlf" or "lf"; any
    /// other value is ignored), then <see cref="Environment.NewLine"/>.
    /// </summary>
    public static string Resolve(EolMode mode, string? preImageText, string? editorConfigEndOfLine)
    {
        if (mode == EolMode.Crlf)
        {
            return "\r\n";
        }

        if (mode == EolMode.Lf)
        {
            return "\n";
        }

        var preImageStyle = DetectSingleStyle(preImageText);
        if (preImageStyle is not null)
        {
            return preImageStyle;
        }

        var editorConfig = editorConfigEndOfLine?.Trim();
        if (string.Equals(editorConfig, "crlf", StringComparison.OrdinalIgnoreCase))
        {
            return "\r\n";
        }

        if (string.Equals(editorConfig, "lf", StringComparison.OrdinalIgnoreCase))
        {
            return "\n";
        }

        return Environment.NewLine;
    }

    // TODO consolidate with EolUtilities.DetectDominantEol (plan_eol_policy_and_write_chokepoint.md Step 4 slice C)
    /// <summary>Returns "\r\n" for CRLF-only text, "\n" for LF-only text, otherwise null (no newlines, mixed, or CR-only).</summary>
    private static string? DetectSingleStyle(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
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
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        if (crlf > 0 && lf == 0 && cr == 0)
        {
            return "\r\n";
        }

        if (lf > 0 && crlf == 0 && cr == 0)
        {
            return "\n";
        }

        return null;
    }
}
