namespace RoslynSentinel.Common;

public record PathDrivenTestCase(
    string TestMethodName,
    string ScenarioDescription,
    List<PathInputConstraint> InputConstraints,
    string ExpectedOutcome,
    string ArrangeCode,
    string ActCode,
    string AssertCode,
    string? Notes = null);
