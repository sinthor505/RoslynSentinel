namespace RoslynSentinel.Common;

public record ReferenceInfo(
    FilePathWrapper filePath,
    int Line,
    int Column,
    string Preview
);
