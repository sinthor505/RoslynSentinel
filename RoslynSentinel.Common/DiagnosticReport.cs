using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Common;

public record DiagnosticReport(
    bool Success,
    List<DiagnosticInfo> Diagnostics
);

public static class DiagnosticReportExtensions
{
    public static string ToInfo(this IEnumerable<DiagnosticInfo> diagnostics)
    {
        return string.Join(", ", diagnostics.Select(d => d.Message.ToString()));
    }

    public static string ToJson(this IEnumerable<DiagnosticInfo> diagnostics)
    {
        return System.Text.Json.JsonSerializer.Serialize(diagnostics, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
    }
}

public record DiagnosticInfo(
    string Id,
    string Severity,
    string Message,
    FilePath FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn
);

public static class DiagnosticExtensions
{
    public static DiagnosticInfo ToInfo(this Diagnostic diagnostic)
    {
        var lineSpan = diagnostic.Location.GetLineSpan();
        return new DiagnosticInfo(
            diagnostic.Id,
            diagnostic.Severity.ToString(),
            diagnostic.GetMessage(),
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            lineSpan.EndLinePosition.Line + 1,
            lineSpan.EndLinePosition.Character + 1
        );
    }

    /// <summary>
    /// Returns a copy of this <see cref="DiagnosticInfo"/> with the file path made relative
    /// to <paramref name="solutionDir"/>, keeping output compact without losing location info.
    /// </summary>
    public static DiagnosticInfo WithRelativePath(this DiagnosticInfo info, string solutionDir)
    {
        if (string.IsNullOrEmpty(info.FilePath) || string.IsNullOrEmpty(solutionDir))
        {
            return info;
        }

        if (info.FilePath.StartsWith(solutionDir, StringComparison.OrdinalIgnoreCase))
        {
            return info with { FilePath = info.FilePath.ToString()[solutionDir.Length..].TrimStart('\\', '/') };
        }

        return info with { FilePath = Path.GetFileName(info.FilePath) };
    }
}

public record DiagnosticGroupSummary(
    string DiagnosticId,
    string Severity,
    string MessageTemplate,
    int Count,
    List<string> Locations
);

public record DiagnosticsSummaryResult(
    int TotalIssues,
    int Errors,
    int Warnings,
    List<DiagnosticGroupSummary> TopIssues
);
