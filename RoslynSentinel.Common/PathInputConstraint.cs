namespace RoslynSentinel.Common;

public record PathInputConstraint(
    string ParameterName,
    string ConstraintDescription,
    string? SuggestedValue);
