namespace RoslynSentinel.Engines.Basic;

public record PublicApiMember(string Kind, string ContainingType, string Signature, string? FilePath, int Line);
