namespace RoslynSentinel.Engines.Basic;

public record BreakingChange(string ChangeKind, string Description, string AffectedMember, string? FilePath, int Line);
