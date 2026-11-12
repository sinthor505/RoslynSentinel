using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Asyncify;

/// <summary>
/// AsyncifyTools.ClassifyUpliftSkipReason: how an <see cref="UpliftSkippedInfo"/> becomes an (outcome, failure reason).
/// The caller-file-missing skip carries a typed Classification; every other skip is classified from its reason text.
/// The real producer (AsyncBatchEngine.RunUpliftBatchAsync, "Caller file not found on disk.") is not reachable from a test:
/// the caller list comes from workspace documents, so a caller always has a workspace document and the missing-file branch
/// cannot be provoked without stubbing the (non-virtual) caller finder. These tests pin the classification of the skip it builds.
/// </summary>
[TestFixture]
[Category("AsyncBatchEngine")]
public class UpliftSkipClassificationTests
{
    private static UpliftSkippedInfo Skip(string reason, FailureReason? classification = null) =>
        new("C:\\repo\\Caller.cs", "Caller", reason, new List<DiagnosticInfo>(), Classification: classification);

    [Test]
    public void Classify_TypedSymbolNotResolved_UnrelatedReasonText_IsFailedSymbolNotResolved()
    {
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip("something unrelated happened", FailureReason.SymbolNotResolved));

        Assert.That(result, Is.EqualTo((ItemOutcome.Failed, FailureReason.SymbolNotResolved)));
    }

    [Test]
    public void Classify_TypedSymbolNotResolved_WithProducerText_IsFailedSymbolNotResolved()
    {
        // Exactly what RunUpliftBatchAsync builds for a caller file that is neither in the solution nor on disk.
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip("Caller file not found on disk.", FailureReason.SymbolNotResolved));

        Assert.That(result, Is.EqualTo((ItemOutcome.Failed, FailureReason.SymbolNotResolved)));
    }

    [TestCase("Caller file not found on disk.")]
    [TestCase("Bridge pre-condition: file not found while bridging.")]
    [TestCase("Body rewrite failed: File Not Found")]
    public void Classify_NoTypedSignal_FileNotFoundText_FallsBackToSymbolNotResolved(string reason)
    {
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip(reason));

        Assert.That(result, Is.EqualTo((ItemOutcome.Failed, FailureReason.SymbolNotResolved)));
    }

    [TestCase("Bridge pre-condition: File 'C:\\repo\\Caller.cs' not found in the loaded solution. Ensure LoadSolution has been called.")]
    [TestCase("Bridge pre-condition: Method 'Caller' not found in 'C:\\repo\\Caller.cs'. Names are case-sensitive.")]
    [TestCase("Document not found in workspace for semantic body rewrite.")]
    public void Classify_NoTypedSignal_ExceptionOriginatedNotFoundText_StaysUnknown(string reason)
    {
        // Pins that these texts never matched the file-not-found check: typing them would widen the classification.
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip(reason));

        Assert.That(result, Is.EqualTo((ItemOutcome.Failed, FailureReason.Unknown)));
    }

    [TestCase("Bridge pre-condition: Method 'X' is already async - it cannot be converted to a bridge.")]
    [TestCase("Method already has CancellationToken parameter")]
    public void Classify_AlreadyAsyncText_IsAlreadySatisfied(string reason)
    {
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip(reason));

        Assert.That(result, Is.EqualTo((ItemOutcome.AlreadySatisfied, FailureReason.AlreadyAsync)));
    }

    [Test]
    public void Classify_AsyncOverloadAlreadyExistsText_IsBlockedOverloadAlreadyExists()
    {
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip("Async overload 'LoadAsync' already exists."));

        Assert.That(result, Is.EqualTo((ItemOutcome.Blocked, FailureReason.OverloadAlreadyExists)));
    }

    [TestCase("Validation produced 2 compiler error(s) - [CS0103] The name 'x' does not exist; flagged")]
    [TestCase("the transform left a compiler error behind")]
    public void Classify_ValidationProducedOrCompilerErrorText_IsCompilerErrorAfterTransform(string reason)
    {
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip(reason));

        Assert.That(result, Is.EqualTo((ItemOutcome.Failed, FailureReason.CompilerErrorAfterTransform)));
    }

    [TestCase("Unexpected bridge error: boom")]
    [TestCase("")]
    public void Classify_UnrecognizedText_IsFailedUnknown(string reason)
    {
        var result = AsyncifyTools.ClassifyUpliftSkipReason(Skip(reason));

        Assert.That(result, Is.EqualTo((ItemOutcome.Failed, FailureReason.Unknown)));
    }
}
