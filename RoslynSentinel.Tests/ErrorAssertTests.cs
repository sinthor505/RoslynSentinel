using NUnit.Framework;
using RoslynSentinel.Common;
using RoslynSentinel.Tests.Support;

namespace RoslynSentinel.Tests;

/// <summary>
/// Pins the behaviour of <see cref="ErrorAssert.Is"/>: code first, then the optional message fragment, for both a
/// ToolException and a wire ResultError.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ErrorAssertTests
{
    [Test]
    public void Is_ToolNotFoundExceptionWithCodeAndFragment_Passes()
    {
        var error = new ToolNotFoundException("Class 'Widget' was not found in the solution.");

        ErrorAssert.Is(error, ToolErrorCode.NotFound, "Widget");
    }

    [Test]
    public void Is_CodeDiffersButFragmentMatches_FailsNamingBothCodes()
    {
        var error = new ToolNotFoundException("Class 'Widget' was not found in the solution.");

        var ex = Assert.Throws<AssertionException>(() => ErrorAssert.Is(error, ToolErrorCode.Ambiguous, "Widget"));

        Assert.That(ex!.Message, Does.Contain("'" + ToolErrorCode.Ambiguous + "'"));
        Assert.That(ex.Message, Does.Contain("'" + ToolErrorCode.NotFound + "'"));
    }

    [Test]
    public void Is_MissingFragment_Fails()
    {
        var error = new ToolNotFoundException("Class 'Widget' was not found in the solution.");

        Assert.Throws<AssertionException>(() => ErrorAssert.Is(error, ToolErrorCode.NotFound, "Gadget"));
    }

    [Test]
    public void Is_NullError_Fails()
    {
        Assert.Throws<AssertionException>(() => ErrorAssert.Is(null, ToolErrorCode.NotFound));
    }

    [Test]
    public void Is_ResultErrorWithCodeAndFragment_Passes()
    {
        var error = new ResultError(ToolErrorCode.NotFound, "Member 'Frobnicate' was not found.");

        ErrorAssert.Is(error, ToolErrorCode.NotFound, "Frobnicate");
    }
}
