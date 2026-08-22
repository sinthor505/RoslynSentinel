namespace RoslynSentinel.Common;

/// <summary>
/// Base for exceptions that name their own <see cref="ToolErrorCode"/> at the throw site, where
/// the real cause is already known, instead of leaving the tool-layer catch block to guess it
/// from the exception's runtime type or message text. Stock BCL exception types (the previous
/// approach) don't work for this: many unrelated failures throw the same
/// <see cref="InvalidOperationException"/>, so a catch block matching on type alone can't tell
/// "solution not loaded" apart from "hunk anchor not found" apart from "member name ambiguous."
/// Each subclass below corresponds to a category actually observed across the engine layer (see
/// docs/TODO.md's error-wrapper entries) — this is not a speculative taxonomy.
/// </summary>
public abstract class ToolException : Exception
{
    public abstract string ErrorCode
    {
        get;
    }

    protected ToolException(string message) : base(message)
    {
    }
}

/// <summary>
/// No solution is loaded (<see cref="IWorkspaceManager.CurrentSolution"/> is null) where
/// an operation requires one. Maps to <see cref="ToolErrorCode.SolutionNotLoaded"/>.
/// </summary>
public sealed class SolutionNotLoadedException : ToolException
{
    public override string ErrorCode => ToolErrorCode.SolutionNotLoaded;

    public SolutionNotLoadedException(string message = "No solution is loaded. Call LoadSolution with a .sln or .csproj path.")
        : base(message)
    {
    }
}

/// <summary>
/// A named file, symbol, type, member, project, or context snippet does not exist where the
/// caller said it would. Distinct from a confirmed "zero results" answer — the lookup never ran
/// because its input didn't resolve. Maps to <see cref="ToolErrorCode.NotFound"/>.
/// </summary>
public sealed class ToolNotFoundException : ToolException
{
    public override string ErrorCode => ToolErrorCode.NotFound;

    public ToolNotFoundException(string message) : base(message)
    {
    }
}

/// <summary>
/// A caller-supplied name or snippet matches more than one candidate and needs a disambiguating
/// argument (contextSnippet, lineBefore/lineAfter, containingType, etc.) — distinct from
/// <see cref="ToolNotFoundException"/> because the content DOES exist, just not uniquely. Maps to
/// <see cref="ToolErrorCode.Ambiguous"/>.
/// </summary>
public sealed class ToolAmbiguousMatchException : ToolException
{
    public override string ErrorCode => ToolErrorCode.Ambiguous;

    public ToolAmbiguousMatchException(string message) : base(message)
    {
    }
}

/// <summary>
/// A unified diff's hunk could not be applied — its declared position and content didn't match
/// the file even after <see cref="DiffEngine"/>'s re-anchoring search. The remediation (regenerate
/// the diff against current content) is specific enough to warrant its own code rather than
/// folding into <see cref="ToolErrorCode.NotFound"/>. Maps to <see cref="ToolErrorCode.DiffApplyFailed"/>.
/// </summary>
public sealed class DiffApplyException : ToolException
{
    public override string ErrorCode => ToolErrorCode.DiffApplyFailed;

    public DiffApplyException(string message) : base(message)
    {
    }
}

/// <summary>
/// Builds a <see cref="ResultError"/> from a caught exception without asserting a cause that may
/// not be true. Replaces the old per-tool pattern of appending a fixed "Check that the solution is
/// loaded and the file path is valid" sentence to every exception regardless of type. Call this
/// from every MCP tool method's catch block instead of hand-rolling the mapping.
/// </summary>
public static class ToolErrorMapper
{
    /// <param name="ex">The caught exception.</param>
    /// <param name="workspaceManager">
    /// Used as a fallback signal only: if a not-yet-migrated call site throws a plain
    /// <see cref="InvalidOperationException"/> for a "no solution loaded" reason without using
    /// <see cref="SolutionNotLoadedException"/>, checking <see cref="IWorkspaceManager.CurrentSolution"/>
    /// directly still catches it correctly instead of guessing from the exception type.
    /// </param>
    /// <param name="context">Short label prefixed to the message (e.g. "ApplyDiff diff apply for 'Foo.cs'").</param>
    public static ResultError ToResultError(Exception ex, IWorkspaceManager workspaceManager, string context)
    {
        var (code, message) = ToCodeAndMessage(ex, workspaceManager, context);
        return new ResultError(code, message, ex.Message);
    }

    /// <summary>
    /// Same mapping as <see cref="ToResultError"/>, for the handful of tool methods that return a
    /// bare <see cref="string"/> instead of a <see cref="ToolResult{T}"/> (e.g. <c>Produces(DataTag.ResultOnly)</c>
    /// methods in <c>SentinelGenerationTools</c>) and so have nowhere to put a structured <see cref="ResultError"/>.
    /// </summary>
    public static string ToErrorMessage(Exception ex, IWorkspaceManager workspaceManager, string context)
    {
        var (_, message) = ToCodeAndMessage(ex, workspaceManager, context);
        return message;
    }

    private static (string Code, string Message) ToCodeAndMessage(Exception ex, IWorkspaceManager workspaceManager, string context)
    {
        if (ex is ToolException toolEx)
        {
            return (toolEx.ErrorCode, $"{context} failed: {toolEx.Message}");
        }

        if (workspaceManager.CurrentSolution == null)
        {
            return (ToolErrorCode.SolutionNotLoaded, $"{context} failed: no solution is loaded. Call LoadSolution first. Details: {ex.Message}");
        }

        return (ToolErrorCode.Exception, $"{context} failed unexpectedly ({ex.GetType().Name}). Details: {ex.Message}");
    }
}
