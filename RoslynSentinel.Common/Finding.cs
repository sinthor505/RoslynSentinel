namespace RoslynSentinel.Common;

public sealed record Finding(string Source, string Message, FindingSeverity Severity = FindingSeverity.Info);
// Added by AddTopLevelType (expected - used for diagnostics)
public enum FindingSeverity
{
    Info, Caution, Warning
}
