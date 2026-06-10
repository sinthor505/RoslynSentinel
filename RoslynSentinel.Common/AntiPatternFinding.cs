using System.Text.Json.Serialization;

namespace RoslynSentinel.Common;

public record AntiPatternFinding(
    [property: JsonPropertyName("patternType")] string Pattern,
    string Description,
    string Severity,
    FilePath FilePath,
    int Line,
    string Snippet,
    string Remediation = ""
);
