namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Shared static helpers for the refactoring tool classes: file-content preview truncation, the
/// empty-updated-text guard, and the error-code mapping it depends on. Extracted verbatim from
/// RefactoringStructuralTools (see docs/current/plans/plan_split_workspace_refactoring_tools_for_di.md,
/// Decision 7 step 1) - all three were already private static methods with no instance state, so
/// this is a pure relocation. ErrorCodeFor moved alongside RequireUpdatedText (not itself named in
/// the plan) because RequireUpdatedText calls it and it cannot stay private on the god-class without
/// blocking this move; it has one other caller (RefactoringStructuralTools.Member) which now calls it
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
    public static string ErrorCodeFor(EditOutcome outcome) => outcome.ToErrorCode() ?? ToolErrorCode.Exception;

    /// <summary>
    /// Maps a document-edit result to the error code the agent sees, preferring ErrorCode if set.
    /// </summary>
    public static string ErrorCodeFor(DocumentEditResult result) => ErrorCodeFor(result.Outcome);

    /// <summary>
    /// Guards against staging an unintended empty-file overwrite: when a document-edit engine
    /// method can't locate its target (wrong name, wrong attribute/modifier, etc.), it returns
    /// Outcome != Modified and leaves UpdatedText empty (a non-nullable string) -> so skipping this check would silently
    /// propose replacing the whole file with nothing.
    /// Returns null when updated.UpdatedText is safe to use as the new file content.
    /// </summary>
    /// <param name="recoveryHint">Optional guidance appended to the failure message only (not the AlreadyInTargetState success).</param>
    /// <remarks>
    /// The error code is derived from the outcome rather than always being
    /// <see cref="ToolErrorCode.Exception"/>: a target the engine simply couldn't find is an
    /// ordinary, caller-correctable condition, and reporting it as an exception told the agent
    /// the server had faulted. Run 20260910-013550-398 hit this via a generic containerName ->
    /// see docs/current/feedback_agent_friendly_error_messages.md.
    /// <para>
    /// <see cref="EditOutcome.AlreadyInTargetState"/> and <see cref="EditOutcome.NoCandidatesFound"/> are the
    /// two outcomes that are a success without a write. AlreadyInTargetState means the engine positively verified
    /// the code already has the requested shape; NoCandidatesFound means it found nothing to change. Both are checked
    /// first, whatever <c>UpdatedText</c> holds (some engines return the unchanged text with it), and
    /// returns a non-error result whose <c>SuccessData</c> is an <see cref="AppliedChangeSummary"/> with
    /// no affected files, so a caller must return the guard result as-is and never read
    /// <c>UpdatedText</c> afterwards. A caller whose return type is
    /// <c>SentinelCallToolResult&lt;AppliedChangeSummary&gt;</c> converts it with <see cref="ToSummaryResult"/>.
    /// Every other outcome keeps its previous behaviour: non-empty text proceeds, empty text is an error.
    /// </para>
    /// </remarks>
    public static SentinelCallToolResult<object>? RequireUpdatedText(DocumentEditResult updated, string operationName, FilePathWrapper filePath, string? recoveryHint = null)
    {
        if (updated.Outcome == EditOutcome.AlreadyInTargetState)
        {
            var description = AlreadyInStateMessage(operationName, $"'{filePath}'", SummarizeEngineMessage(updated.Message, MaxEngineGuidanceLength));
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new AppliedChangeSummary(ChangeId: null, AffectedFiles: [], Description: description, DryRun: false),
                StatusMessage = description
            };
        }

        if (updated.Outcome == EditOutcome.NoCandidatesFound)
        {
            var nothingDone = $"{operationName}: nothing to do for '{filePath}' - no change was needed, nothing was written.";
            var detail = SummarizeEngineMessage(updated.Message, MaxEngineGuidanceLength);
            var nothingDoneText = string.IsNullOrEmpty(detail) ? nothingDone : nothingDone + " " + detail;
            return new SentinelCallToolResult<object>
            {
                IsError = false,
                SuccessData = new AppliedChangeSummary(ChangeId: null, AffectedFiles: [], Description: nothingDoneText, DryRun: false),
                StatusMessage = nothingDoneText
            };
        }

        if (!string.IsNullOrEmpty(updated.UpdatedText))
        {
            return null;
        }

        return new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(ErrorCodeFor(updated),
                $"{operationName}: no change produced for '{filePath}' ({updated.Outcome}). {SummarizeEngineMessage(updated.Message, MaxEngineGuidanceLength)}{(string.IsNullOrEmpty(recoveryHint) ? string.Empty : " " + recoveryHint)}", StructuredDetail: ToolErrorMapper.ToStructuredDetail(updated.Candidates))
        };
    }

    /// <summary>
    /// Re-types a <see cref="RequireUpdatedText"/> guard result for a tool that returns
    /// <c>SentinelCallToolResult&lt;AppliedChangeSummary&gt;</c>: an error keeps its <c>ErrorData</c>, the
    /// already-in-state success keeps its summary and status message.
    /// </summary>
    public static SentinelCallToolResult<AppliedChangeSummary> ToSummaryResult(SentinelCallToolResult<object> guardResult) =>
        guardResult.IsError
            ? new SentinelCallToolResult<AppliedChangeSummary> { IsError = true, ErrorData = guardResult.ErrorData }
            : new SentinelCallToolResult<AppliedChangeSummary>
            {
                IsError = false,
                SuccessData = guardResult.SuccessData as AppliedChangeSummary,
                StatusMessage = guardResult.StatusMessage
            };

    /// <summary>
    /// Outcome-based gate for tools that branch on an engine result before using its text (the
    /// <c>CodeTransformTools</c> family). Decides from <c>Outcome</c>, not from whether
    /// <c>UpdatedText</c> happens to be empty - an empty-text check reports an engine error as a no-op
    /// success and cannot tell "already in the requested state" from a failure.
    /// Engines report facts only (outcome, optional code, a short reason); every word of guidance the agent
    /// reads comes from the tool, passed in here. Returns:
    /// <list type="bullet">
    /// <item>null for <see cref="EditOutcome.Modified"/> with text (the caller continues with its success path);</item>
    /// <item>a non-error result with a string message and no write for <see cref="EditOutcome.AlreadyInTargetState"/>;</item>
    /// <item>for the "nothing to do" family (NoCandidatesFound, and Modified with no text) whatever
    /// <paramref name="noOp"/> says: the tool's own success message, or an error carrying the engine's code
    /// (<see cref="EngineResultBase.Code"/>) with the tool's own wording;</item>
    /// <item>an error for every other outcome (Error, Unset, SourceInvalid, FeatureDisabled, DocumentNotFound),
    /// carrying the engine's code, the engine's reason reduced by <see cref="SummarizeEngineMessage"/>, and
    /// <paramref name="hint"/>. An unclassified outcome (Unset, Error) is therefore an error, never a silent success.</item>
    /// </list>
    /// </summary>
    /// <param name="result">The engine result.</param>
    /// <param name="operationName">The transform or kind name, used to prefix the message.</param>
    /// <param name="subject">What was targeted, already quoted, e.g. <c>method 'Foo' in 'C:\x.cs'</c>.</param>
    /// <param name="hint">The tool's guidance for the agent (what to verify or do next), appended to failures.</param>
    /// <param name="noOp">What this tool says when the engine found nothing to do.</param>
    public static SentinelCallToolResult<object>? GuardEngineResult(DocumentEditResult result, string operationName, string subject, string hint, NoOpHandling noOp)
    {
        var hasText = !string.IsNullOrEmpty(result.UpdatedText);
        switch (result.Outcome)
        {
            case EditOutcome.Modified when hasText:
                return null;
            case EditOutcome.AlreadyInTargetState:
                var message = AlreadyInStateMessage(operationName, subject, result.Message);
                return new SentinelCallToolResult<object> { IsError = false, SuccessData = message, StatusMessage = message };
            case EditOutcome.Modified:
            case EditOutcome.NoCandidatesFound:
                return GuardNothingToDo(result, hint, noOp, hasText);
            default:
                return GuardEngineFailure(result, operationName, subject, hint);
        }
    }

    private static SentinelCallToolResult<object>? GuardNothingToDo(DocumentEditResult result, string hint, NoOpHandling noOp, bool hasText)
    {
        if (noOp.ProceedWhenTextPresent && hasText)
        {
            return null;
        }

        if (noOp.IsSuccess)
        {
            return new SentinelCallToolResult<object> { IsError = false, SuccessData = noOp.Message };
        }

        var reason = EngineReason(result);
        var message = reason.Length > 0 ? $"{noOp.Message} {hint} Engine detail: {reason}" : $"{noOp.Message} {hint}";
        return new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(result.Code ?? ToolErrorCode.Exception, message.TrimEnd(), StructuredDetail: ToolErrorMapper.ToStructuredDetail(result.Candidates))
        };
    }

    private static SentinelCallToolResult<object> GuardEngineFailure(DocumentEditResult result, string operationName, string subject, string hint)
    {
        var reason = EngineReason(result);
        var head = $"{operationName} failed for {subject} ({result.Outcome})";
        var detail = reason.Length > 0 ? $"{head}: {EndSentence(reason)}" : $"{head}.";
        return new SentinelCallToolResult<object>
        {
            IsError = true,
            ErrorData = new ResultError(result.Code ?? ToolErrorCode.Exception, $"{detail} {hint}".TrimEnd(), StructuredDetail: ToolErrorMapper.ToStructuredDetail(result.Candidates))
        };
    }

    private static string EndSentence(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    private const int MaxEngineReasonLength = 200;

    // Single-line engine guidance (recovery instructions) must survive intact; the first-line rule, not the cap, is what stops source dumps.
    private const int MaxEngineGuidanceLength = 1000;

    private static readonly System.Text.RegularExpressions.Regex CommentMarkerPrefix = new(
        @"^//+\s*(?:(?:ErrorDetails|Error|Info|Warning)\b\s*:?\s*)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex DiagnosticComment = new(
        @"^//\s*(?:ErrorDetails|Error|Warning)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Reduces whatever an engine put in <c>Message</c> to one short reason line that is safe to show an agent.
    /// Engines sometimes put a whole source file in <c>Message</c> (a "// ERROR: ..." line followed by the file)
    /// or a raw exception message, so only the first non-blank line is kept, a leading comment marker
    /// ("// ErrorDetails:", "// ERROR:", "// Info:", "// WARNING:") is stripped, and the result is capped at
    /// <paramref name="maxLength"/> characters. Never returns more than one line, never the source that follows it.
    /// </summary>
    public static string SummarizeEngineMessage(string? message, int maxLength = MaxEngineReasonLength)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var firstLine = message.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        var line = CommentMarkerPrefix.Replace(firstLine, string.Empty).Trim();
        return line.Length > maxLength ? line[..Math.Max(0, maxLength - 3)].TrimEnd() + "..." : line;
    }

    // The engine's reason: its Message, or - for the engines that report the reason as a "// WARNING: ..." first
    // line of UpdatedText instead (OptimizeToValueTask) - that line. A plain source file in UpdatedText never matches.
    // Keep this UpdatedText read until OptimizeToValueTask moves its reason to Message; dropping it first loses the reason.
    private static string EngineReason(DocumentEditResult result)
    {
        var reason = SummarizeEngineMessage(result.Message);
        if (reason.Length > 0)
        {
            return reason;
        }

        var text = result.UpdatedText?.TrimStart();
        return !string.IsNullOrEmpty(text) && DiagnosticComment.IsMatch(text) ? SummarizeEngineMessage(text) : string.Empty;
    }

    private static string AlreadyInStateMessage(string operationName, string subject, string? engineMessage)
    {
        var message = $"{operationName}: {subject} is already in the requested state - nothing to change, nothing was written.";
        return string.IsNullOrWhiteSpace(engineMessage) ? message : $"{message} {engineMessage}";
    }
}

/// <summary>
/// What a tool says when its engine reports "nothing to do / not found / not eligible" (see
/// <see cref="RefactoringToolHelpers.GuardEngineResult(DocumentEditResult, string, string, string, NoOpHandling)"/>).
/// The wording is the tool's, never the engine's.
/// </summary>
/// <param name="Message">For a success: the whole message returned. For an error: the lead sentence; the guard appends the hint and a sanitised engine detail.</param>
/// <param name="IsSuccess">True when "nothing to do" is a successful result (nothing written).</param>
/// <param name="ProceedWhenTextPresent">True to keep the pre-guard behaviour of treating a non-empty <c>UpdatedText</c> as a result to use, whatever the outcome.</param>
public sealed record NoOpHandling(string Message, bool IsSuccess, bool ProceedWhenTextPresent)
{
    /// <summary>"Nothing to do" is a success returning <paramref name="message"/>; a non-empty <c>UpdatedText</c> still proceeds.</summary>
    public static NoOpHandling Success(string message) => new(message, true, true);

    /// <summary>"Nothing to do" is an error opening with <paramref name="lead"/>; <c>UpdatedText</c> is ignored (an engine may stuff a reason or the unchanged file into it).</summary>
    public static NoOpHandling Error(string lead) => new(lead, false, false);

    /// <summary>"Nothing to do" is an error opening with <paramref name="lead"/> unless <c>UpdatedText</c> is non-empty, which proceeds.</summary>
    public static NoOpHandling ErrorUnlessText(string lead) => new(lead, false, true);
}
