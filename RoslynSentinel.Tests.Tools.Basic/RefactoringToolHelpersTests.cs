using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Common;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for RefactoringToolHelpers: ErrorCodeFor overloads and RequireUpdatedText behavior with ErrorCode precedence.
/// </summary>
[TestFixture]
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
        Assert.That(result!.IsSuccess, Is.False);
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
        Assert.That(result!.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    public void RequireUpdatedText_OutcomeNoChange_ReturnsException()
    {
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.NoChange, "X.cs") { Message = "m" },
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.Exception));
    }

    [Test]
    public void RequireUpdatedText_ErrorCodeWinsOverOutcome()
    {
        var result = RefactoringToolHelpers.RequireUpdatedText(
            new DocumentEditResult(EditOutcome.TargetNotFound, "X.cs") { ErrorCode = ToolErrorCode.TargetIneligible, Message = "m" },
            "test",
            "X.cs");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsSuccess, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
    }
}
