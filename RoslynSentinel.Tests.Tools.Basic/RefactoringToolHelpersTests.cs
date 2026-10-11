using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for RefactoringToolHelpers: ErrorCodeFor overloads and RequireUpdatedText behavior with ErrorCode precedence.
/// </summary>
[TestFixture]
[Category("RefactoringToolHelpers")] // sentinel:auto-category
public class RefactoringToolHelpersTests
{
    [Test]
    public void RequireUpdatedText_WithErrorCodeSet_ReturnsErrorWithPrecedence()
    {
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.CannotEdit, "X.cs") { ErrorCode = ToolErrorCode.Ambiguous, Message = "m" },
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(!result!.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
    }

    [Test]
    public void RequireUpdatedText_OutcomeTargetNotFound_ReturnsNotFound()
    {
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.TargetNotFound, "X.cs") { Message = "m" },
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(!result!.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public void RequireUpdatedText_OutcomeAlreadyInTargetState_IsSuccessWithNoWrite()
    {
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.AlreadyInTargetState, "X.cs") { Message = "m" },
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Null);
        Assert.That(result.Code, Is.Null);
        Assert.That(result.StatusMessage, Does.Contain("already in the requested state"));
        var summary = result.SuccessData as AppliedChangeSummary;
        Assert.That(summary, Is.Not.Null);
        Assert.That(summary!.AffectedFiles, Is.Empty, "an already-in-state result must not name a file to write");
        Assert.That(summary.Description, Does.Contain("already in the requested state"));
    }

    [Test]
    public void RequireUpdatedText_AlreadyInTargetStateWithUnchangedText_IsStillSuccessWithNoWrite()
    {
        // Some engines (GenerateConstructor) return AlreadyInTargetState together with the unchanged text.
        // The old guard saw non-empty text and let the caller write it; the outcome must win.
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.AlreadyInTargetState, "X.cs", string.Empty, false, "class C { }"),
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.False);
        Assert.That((result.SuccessData as AppliedChangeSummary)!.AffectedFiles, Is.Empty);
    }

    [Test]
    public void ToSummaryResult_AlreadyInStateGuard_KeepsSuccessAndSummary()
    {
        var guard = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.AlreadyInTargetState, "X.cs") { Message = "m" },
            "test",
            "X.cs")!;

        var typed = RefactoringToolHelpers.ToSummaryResult(guard);

        Assert.That(typed.IsError, Is.False);
        Assert.That(typed.SuccessData, Is.Not.Null);
        Assert.That(typed.StatusMessage, Does.Contain("already in the requested state"));
    }

    [Test]
    public void ToSummaryResult_ErrorGuard_KeepsErrorCode()
    {
        var guard = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.TargetNotFound, "X.cs") { Message = "m" },
            "test",
            "X.cs")!;

        var typed = RefactoringToolHelpers.ToSummaryResult(guard);

        Assert.That(typed.IsError, Is.True);
        Assert.That(typed.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    // ---- GuardEngineResult: engines report facts, the tool passes every word of guidance -------------------

    private const string Hint = "Use GetFileOutline to verify the method exists.";
    private const string SuccessText = "No lock statements found in 'M'.";
    private const string ErrorLead = "op: method 'M' not found or not eligible.";

    private static DocumentEditResult Edit(EditOutcome outcome, string? updatedText = null, string message = "") =>
        new(outcome, "X.cs", string.Empty, false, updatedText) { Message = message };

    private static SentinelCallToolResult<object>? Guard(DocumentEditResult result, NoOpHandling noOp) =>
        RefactoringToolHelpers.GuardEngineResult(result, "op", "method 'M'", Hint, noOp);

    [Test]
    public void GuardEngineResult_ModifiedWithText_ReturnsNull()
    {
        Assert.That(Guard(Edit(EditOutcome.Modified, "class C { }"), NoOpHandling.Success(SuccessText)), Is.Null);
    }

    [Test]
    public void GuardEngineResult_ModifiedWithoutText_FollowsTheToolsNoOpHandling()
    {
        // Pre-guard behaviour: an empty UpdatedText reached the tool's own empty-text branch whatever the outcome.
        var asSuccess = Guard(Edit(EditOutcome.Modified), NoOpHandling.Success(SuccessText));
        Assert.That(asSuccess!.IsError, Is.False);
        Assert.That(asSuccess.SuccessData, Is.EqualTo(SuccessText));

        var asError = Guard(Edit(EditOutcome.Modified), NoOpHandling.Error(ErrorLead));
        Assert.That(asError!.IsError, Is.True);
        Assert.That(asError.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Exception), "the old generic code, there is no engine code on a Modified result");
    }

    [Test]
    public void GuardEngineResult_AlreadyInTargetState_IsSuccessWithMessage()
    {
        var result = Guard(Edit(EditOutcome.AlreadyInTargetState, "class C { }"), NoOpHandling.Error(ErrorLead));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.False);
        Assert.That(result.Code, Is.Null);
        Assert.That(result.SuccessData as string, Does.Contain("already in the requested state"));
    }

    [TestCase(EditOutcome.TargetNotFound)]
    [TestCase(EditOutcome.NoCandidatesFound)]
    [TestCase(EditOutcome.CannotEdit)]
    [TestCase(EditOutcome.CannotOptimize)]
    [TestCase(EditOutcome.CannotMove)]
    [TestCase(EditOutcome.CannotRemove)]
    [TestCase(EditOutcome.CannotConvert)]
    public void GuardEngineResult_NothingToDo_SuccessHandling_ReturnsExactlyTheToolsMessage(EditOutcome outcome)
    {
        // A whole source file in the engine's Message must never reach the agent on the success path either.
        var result = Guard(Edit(outcome, message: "// ERROR: boom\nclass Secret { }"), NoOpHandling.Success(SuccessText));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.False, "a nothing-to-do result stays a success");
        Assert.That(result.SuccessData, Is.EqualTo(SuccessText));
    }

    [Test]
    public void GuardEngineResult_NothingToDo_SuccessHandling_NonEmptyTextProceeds()
    {
        // Pre-guard behaviour for the success-style tools: any non-empty UpdatedText was used by the tool.
        Assert.That(Guard(Edit(EditOutcome.CannotConvert, "class C { }"), NoOpHandling.Success(SuccessText)), Is.Null);
    }

    [TestCase(EditOutcome.TargetNotFound, ToolErrorCode.NotFound)]
    [TestCase(EditOutcome.NoCandidatesFound, ToolErrorCode.NotFound)]
    [TestCase(EditOutcome.CannotConvert, ToolErrorCode.TargetIneligible)]
    [TestCase(EditOutcome.CannotOptimize, ToolErrorCode.TargetIneligible)]
    public void GuardEngineResult_NothingToDo_ErrorHandling_UsesEngineCodeAndToolWording(EditOutcome outcome, string expectedCode)
    {
        var result = Guard(Edit(outcome, message: "// ERROR: Method 'M' not found in X.cs.\nclass Secret { }"), NoOpHandling.Error(ErrorLead));

        Assert.That(result!.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(expectedCode));
        Assert.That(result.ErrorData.Message, Does.StartWith(ErrorLead + " " + Hint));
        Assert.That(result.ErrorData.Message, Does.Contain("Engine detail: Method 'M' not found in X.cs."));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("Secret"), "the source after the first line must never be forwarded");
    }

    [Test]
    public void GuardEngineResult_NothingToDo_ErrorHandling_IgnoresTextAndQuotesTheReasonThePlaceTheEngineHidIt()
    {
        // OptimizeToValueTask: Outcome CannotOptimize, Message empty, the reason is the first line of UpdatedText
        // ahead of the whole file. The old tool shipped that text as the new file.
        var result = Guard(
            Edit(EditOutcome.CannotOptimize, "// WARNING: Cannot safely convert to ValueTask: method has 2 await expressions.\nclass Secret { }"),
            NoOpHandling.Error(ErrorLead));

        Assert.That(result!.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData.Message, Does.Contain(Hint));
        Assert.That(result.ErrorData.Message, Does.Contain("Cannot safely convert to ValueTask: method has 2 await expressions."));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("Secret"));
    }

    [Test]
    public void GuardEngineResult_NothingToDo_ErrorHandling_PlainFileInUpdatedTextIsNotQuotedAsAReason()
    {
        // ConvertStaticToExtension: TargetNotFound with the unchanged file as UpdatedText.
        var result = Guard(Edit(EditOutcome.TargetNotFound, "// Copyright header\nclass Secret { }"), NoOpHandling.Error(ErrorLead));

        Assert.That(result!.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Is.EqualTo(ErrorLead + " " + Hint));
    }

    [Test]
    public void GuardEngineResult_NothingToDo_ErrorUnlessText_ProceedsWithTextAndErrorsWithout()
    {
        Assert.That(Guard(Edit(EditOutcome.NoCandidatesFound, "class C { }", "// Info: No fields."), NoOpHandling.ErrorUnlessText(ErrorLead)), Is.Null);

        var result = Guard(Edit(EditOutcome.TargetNotFound, message: "// ErrorDetails: Class 'C' not found."), NoOpHandling.ErrorUnlessText(ErrorLead));
        Assert.That(result!.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData.Message, Does.Contain("Engine detail: Class 'C' not found."));
    }

    [TestCase(EditOutcome.Error, ToolErrorCode.Exception)]
    [TestCase(EditOutcome.Unset, ToolErrorCode.Exception)]
    [TestCase(EditOutcome.SourceInvalid, ToolErrorCode.InvalidArgument)]
    [TestCase(EditOutcome.FeatureDisabled, ToolErrorCode.FeatureDisabled)]
    [TestCase(EditOutcome.DocumentNotFound, ToolErrorCode.NotFound)]
    public void GuardEngineResult_GenuineFailure_UsesEngineCodeOneLineReasonAndToolHint(EditOutcome outcome, string expectedCode)
    {
        // A genuine failure is an error even when the tool's no-op handling is a success, and even when text came back.
        var result = Guard(Edit(outcome, "class Secret { }", "// ErrorDetails: File not found.\nclass Secret { }"), NoOpHandling.Success(SuccessText));

        Assert.That(result!.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(expectedCode));
        Assert.That(result.ErrorData.Message, Is.EqualTo($"op failed for method 'M' ({outcome}): File not found. {Hint}"));
    }

    [Test]
    public void GuardEngineResult_GenuineFailure_EngineErrorCodeWinsOverTheOutcomeMapping()
    {
        var edit = new DocumentEditResult(EditOutcome.Error, "X.cs") { ErrorCode = ToolErrorCode.Ambiguous, Message = "Two overloads match" };

        var result = Guard(edit, NoOpHandling.Success(SuccessText));

        Assert.That(result!.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData.Message, Does.Contain("Two overloads match."));
    }

    [Test]
    public void GuardEngineResult_GenuineFailure_WithoutAReasonStillExplainsAndHints()
    {
        var result = Guard(Edit(EditOutcome.Error), NoOpHandling.Success(SuccessText));

        Assert.That(result!.ErrorData!.Message, Is.EqualTo($"op failed for method 'M' (Error). {Hint}"));
    }

    [Test]
    public void GuardEngineResult_GenuineFailure_CapsAnEnormousReasonAndNeverLeaksTheSource()
    {
        var result = Guard(Edit(EditOutcome.Error, message: new string('x', 5000) + "\nclass Secret { }"), NoOpHandling.Success(SuccessText));

        Assert.That(result!.ErrorData!.Message.Length, Is.LessThan(400));
        Assert.That(result.ErrorData.Message, Does.Contain("..."));
        Assert.That(result.ErrorData.Message, Does.Not.Contain("Secret"));
    }

    // ---- SummarizeEngineMessage --------------------------------------------------------------------------

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   \r\n  \n")]
    public void SummarizeEngineMessage_NullEmptyOrBlank_IsEmpty(string? message)
    {
        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage(message), Is.Empty);
    }

    [Test]
    public void SummarizeEngineMessage_ErrorMarkerFollowedByWholeSourceFile_KeepsOnlyTheReason()
    {
        var message = "// ERROR: Method 'Foo' not found in X.cs.\nusing System;\n\nnamespace N\n{\n    public class C { }\n}\n";

        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage(message), Is.EqualTo("Method 'Foo' not found in X.cs."));
    }

    [TestCase("// ErrorDetails: File 'x.cs' not found.", "File 'x.cs' not found.")]
    [TestCase("// Info: Constructor already exists.", "Constructor already exists.")]
    [TestCase("// WARNING: This method implements interface(s): IFoo.\nclass C { }", "This method implements interface(s): IFoo.")]
    [TestCase("// Document not found.", "Document not found.")]
    [TestCase("Target not found in file: C:\\x\\X.cs", "Target not found in file: C:\\x\\X.cs")]
    public void SummarizeEngineMessage_StripsLeadingCommentMarkers(string message, string expected)
    {
        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage(message), Is.EqualTo(expected));
    }

    [Test]
    public void SummarizeEngineMessage_MarkerAloneThenCode_IsEmptyRatherThanTheFirstLineOfCode()
    {
        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage("// ERROR\nclass Secret { }"), Is.Empty);
    }

    [Test]
    public void SummarizeEngineMessage_SkipsLeadingBlankLinesAndHandlesCrLf()
    {
        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage("\r\n\r\n  the reason  \r\nclass Secret { }"), Is.EqualTo("the reason"));
    }

    [Test]
    public void SummarizeEngineMessage_LongSingleLine_IsCappedWithAnEllipsis()
    {
        var summary = RefactoringToolHelpers.SummarizeEngineMessage(new string('a', 1000));

        Assert.That(summary.Length, Is.EqualTo(200));
        Assert.That(summary, Does.EndWith("..."));
    }

    [Test]
    public void SummarizeEngineMessage_HonoursACustomCap()
    {
        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage("abcdefghij", 8), Is.EqualTo("abcde..."));
        Assert.That(RefactoringToolHelpers.SummarizeEngineMessage("abcdefghij", 10), Is.EqualTo("abcdefghij"));
    }

    [Test]
    public void RequireUpdatedText_ErrorCodeWinsOverOutcome()
    {
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.TargetNotFound, "X.cs") { ErrorCode = ToolErrorCode.TargetIneligible, Message = "m" },
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(!result!.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }
}
