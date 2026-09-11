namespace RoslynSentinel.Common;

public record DeadCodeReport(FilePathWrapper filePath, string SymbolName, int Line, int Column, string Type);
