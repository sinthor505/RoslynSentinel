using Microsoft.CodeAnalysis;

namespace RoslynSentinel.Server;

public record DiagnosticReport(
    bool Success,
    List<DiagnosticInfo> Diagnostics
);

public record DiagnosticInfo(
    string Id,
    string Severity,
    string Message,
    string FilePath,
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
}
