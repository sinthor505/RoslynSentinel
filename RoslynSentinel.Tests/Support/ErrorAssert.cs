using NUnit.Framework;
using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Support;

/// <summary>
/// Asserts the stable code, and optionally a distinguishing fragment of the message, of an <see cref="IOperationError"/>
/// (a ToolException or a wire ResultError). Pin a short fragment that identifies the failure (a name, a number, a flag
/// name), never a sentence of the production wording.
/// </summary>
public static class ErrorAssert
{
    /// <summary>
    /// Asserts that <paramref name="error"/> is not null, that its ErrorCode equals <paramref name="expectedCode"/>
    /// (use the <see cref="ToolErrorCode"/> constants), and, when <paramref name="distinguishingFragment"/> is given,
    /// that its Message contains it (ordinal comparison).
    /// </summary>
    public static void Is(IOperationError? error, string expectedCode, string? distinguishingFragment = null)
    {
        Assert.That(error, Does.Not.Null, "Expected an IOperationError with code '" + expectedCode + "', but none was given.");
        var actualCode = error!.ErrorCode;
        var message = error.Message;
        Assert.That(
            actualCode,
            NUnit.Framework.Is.EqualTo(expectedCode),
            "Error code mismatch. expected: '" + expectedCode + "', actual: '" + actualCode + "'. Error message: " + message);
        if (distinguishingFragment is null)
        {
            return;
        }

        Assert.That(
            message.Contains(distinguishingFragment, StringComparison.Ordinal),
            NUnit.Framework.Is.True,
            "Error message does not contain fragment '" + distinguishingFragment + "'. Actual message: " + message);
    }
}
