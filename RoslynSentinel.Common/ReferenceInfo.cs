namespace RoslynSentinel.Common;

public record ReferenceInfo(
    FilePath filePath,
    int Line,
    int Column,
    string Preview
);
