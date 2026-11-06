namespace RoslynSentinel.Engines.Basic;
public record PublicApiMember(string Kind, string ContainingType, string Signature, string? FilePath, int Line);
public record BreakingChange(string ChangeKind, string Description, string AffectedMember, string? FilePath, int Line);