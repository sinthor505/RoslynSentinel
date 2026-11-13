using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Error-policy route tests for RefactoringToolHelpers.GuardEngineResult: the nothing-to-do and engine-failure
/// guards record no missing-target kind on the ambient ToolCallContext, and the error text is unchanged.
/// </summary>
[TestFixture]
[Category("RefactoringToolHelpers")]
public class ErrorPolicyGuardRouteTests
{
    private const string Hint = "Use GetFileOutline to verify the method exists.";
    private const string NothingMessage = "nothing";

    [TearDown]
    public void TearDown() => ToolCallContext.Set(null);

    private static DocumentEditResult Edit(EditOutcome outcome, string? updatedText = null, string message = "") =>
        new(outcome, "X.cs", string.Empty, false, updatedText) { Message = message };

    [Test]
    public void GuardEngineResult_NoCandidatesFoundWithErrorNoOp_RecordsNoMissingTargetKind()
    {
        var ctx = new ToolCallContext(null, null, "Member", null, 0);
        ToolCallContext.Set(ctx);

        var result = RefactoringToolHelpers.GuardEngineResult(
            Edit(EditOutcome.NoCandidatesFound),
            "op",
            "method 'M'",
            Hint,
            NoOpHandling.Error(NothingMessage));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.True);
        Assert.That(ctx.MissingTargetKind, Is.Null, "the guard route must not record a missing-target kind");
    }

    [Test]
    public void GuardEngineResult_UndefinedOutcome_RecordsNoMissingTargetKind()
    {
        var ctx = new ToolCallContext(null, null, "Member", null, 0);
        ToolCallContext.Set(ctx);

        var result = RefactoringToolHelpers.GuardEngineResult(
            Edit((EditOutcome)99),
            "op",
            "method 'M'",
            Hint,
            NoOpHandling.Error(NothingMessage));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.True);
        Assert.That(ctx.MissingTargetKind, Is.Null, "an engine-failure outcome must not record a missing-target kind");
    }

    [Test]
    public void GuardEngineResult_NoCandidatesFound_ErrorTextIsUnchanged()
    {
        var ctx = new ToolCallContext(null, null, "Member", null, 0);
        ToolCallContext.Set(ctx);

        var result = RefactoringToolHelpers.GuardEngineResult(
            Edit(EditOutcome.NoCandidatesFound),
            "op",
            "method 'M'",
            Hint,
            NoOpHandling.Error(NothingMessage));

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Is.EqualTo(NothingMessage + " " + Hint));
    }
}
