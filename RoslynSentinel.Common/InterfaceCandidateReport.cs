namespace RoslynSentinel.Common;

public record InterfaceCandidateReport(FilePath filePath, string ClassName, List<string> PublicMethods);
