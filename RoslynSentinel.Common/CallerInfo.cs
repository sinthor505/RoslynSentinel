namespace RoslynSentinel.Common;

public record CallerInfo(
    string CallerMethod,
    string CallerType,
    string FilePath,
    int Line,
    string CodeSnippet
);
