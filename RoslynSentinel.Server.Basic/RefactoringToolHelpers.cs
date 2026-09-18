namespace RoslynSentinel.Server.Basic;

/// <summary>
/// Shared static helpers for the refactoring tool classes: file-content preview truncation, the
/// empty-updated-text guard, and the error-code mapping it depends on. Extracted verbatim from
/// SentinelRefactoringTools (see docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md,
/// Decision 7 step 1) - all three were already private static methods with no instance state, so
/// this is a pure relocation. ErrorCodeFor moved alongside RequireUpdatedText (not itself named in
/// the plan) because RequireUpdatedText calls it and it cannot stay private on the god-class without
/// blocking this move; it has one other caller (SentinelRefactoringTools.Member) which now calls it
/// here instead.
/// </summary>
public static class RefactoringToolHelpers
{
    public static string PreviewFileContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= 20)
        {
            return content;
        }

        var head = lines.Take(10);
        var tail = lines.TakeLast(10);
        return string.Join("\n", head) + "\n// ... (truncated)\n" + string.Join("\n", tail);
    }

    /// <summary>
    /// Maps a document-edit outcome to the error code the agent sees. Shared so the
    /// <c>Member(replace)</c> path, which builds its own message, cannot drift from
    /// <see cref="RequireUpdatedText"/> on the code.
    /// </summary>
    public static string ErrorCodeFor(EditOutcome outcome) => outcome switch
    {
        EditOutcome.TargetNotFound or EditOutcome.DocumentNotFound => ToolErrorCode.NotFound,
        EditOutcome.SourceInvalid => ToolErrorCode.InvalidArgument,
        _ => ToolErrorCode.Exception
    };

    /// <summary>
    /// Guards against staging an unintended empty-file overwrite: when a document-edit engine
    /// method can't locate its target (wrong name, wrong attribute/modifier, etc.), it returns
    /// Outcome != Modified and leaves UpdatedText at its string.Empty default rather than null ->
    /// so skipping this check would silently propose replacing the whole file with nothing.
    /// Returns null when updated.UpdatedText is safe to use as the new file content.
    /// </summary>
    /// <remarks>
    /// The error code is derived from the outcome rather than always being
    /// <see cref="ToolErrorCode.Exception"/>: a target the engine simply couldn't find is an
    /// ordinary, caller-correctable condition, and reporting it as an exception told the agent
    /// the server had faulted. Run 20260910-013550-398 hit this via a generic containerName ->
    /// see docs/current/feedback_agent_friendly_error_messages.md.
    /// </remarks>
    public static ToolResult<object>? RequireUpdatedText(DocumentEditResult updated, string operationName, FilePathWrapper filePath)
    {
        if (!string.IsNullOrEmpty(updated.UpdatedText))
        {
            return null;
        }

        return new ToolResult<object>
        {
            Success = false,
            Error = new ResultError(ErrorCodeFor(updated.Outcome),
                $"{operationName}: no change produced for '{filePath}' ({updated.Outcome}). {updated.Message}")
        };
    }
}
