namespace RoslynSentinel.Common;

public sealed record Finding(string Source, string Message, FindingSeverity Severity = FindingSeverity.Info);

public enum FindingSeverity
{
    Info, Caution, Warning
}
