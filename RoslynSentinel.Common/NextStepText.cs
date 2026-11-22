namespace RoslynSentinel.Common;

/// <summary>Reads the "Next step: " hint that a tool error text carries.</summary>
public static class NextStepText
{
    /// <summary>The marker that introduces the next step in a tool error text.</summary>
    public const string Marker = "Next step: ";

    /// <summary>The text after "Next step: " to the end of its line, verbatim; null when the error has none.</summary>
    public static string? Extract(string errorText)
    {
        var start = errorText.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += Marker.Length;
        var end = errorText.IndexOfAny(['\r', '\n'], start);
        var step = (end < 0 ? errorText[start..] : errorText[start..end]).Trim();
        return step.Length == 0 ? null : step;
    }
}
