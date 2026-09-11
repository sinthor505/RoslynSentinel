namespace RoslynSentinel.Common;

public record CodeInventoryReport(
    FilePathWrapper filePath,
    List<string> Namespaces,
    List<string> Classes,
    List<string> Interfaces,
    List<string> Methods,
    List<string> Properties
);
