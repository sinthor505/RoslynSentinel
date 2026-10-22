namespace RoslynSentinel.Common;

/// <summary>
/// One hunk that a unified diff could not apply, carried on <see cref="DiffApplyException.FailedHunks"/>.
/// Line is 1-based. Expected and Actual are the diff's expected line and the file's actual line when the
/// failure is a content mismatch; they are null otherwise. Wire-safe: no paths and no stack traces.
/// </summary>
public sealed record DiffHunkFailure(string HunkHeader, int Line, string Reason, string? Expected = null, string? Actual = null);
