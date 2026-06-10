namespace RoslynSentinel.Common;

public record PathDrivenTestReport(
    string MethodName,
    FilePath filePath,
    string ClassName,
    int PathCount,
    List<PathDrivenTestCase> TestCases,
    string GeneratedTestCode,
    string Note = "Generated stubs are starting points — input constraints are inferred heuristically and may not precisely trigger each intended path.");
