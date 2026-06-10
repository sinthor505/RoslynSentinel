namespace RoslynSentinel.Common;

public record DeadCodeReport(FilePath filePath, string SymbolName, int Line, int Column, string Type);
