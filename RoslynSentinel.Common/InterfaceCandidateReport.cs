namespace RoslynSentinel.Common;

public record InterfaceCandidateReport(FilePathWrapper filePath, string ClassName, List<string> PublicMethods);
