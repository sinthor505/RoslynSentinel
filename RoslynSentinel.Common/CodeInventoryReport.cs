namespace RoslynSentinel.Common;

public record CodeInventoryReport(
    FilePath filePath,
    List<string> Namespaces,
    List<string> Classes,
    List<string> Interfaces,
    List<string> Methods,
    List<string> Properties
);
