namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Lookup misses of ThreadSafetyEngine reached through CodeTransformTools (make_method_thread_safe and convert_lock_to_semaphore_slim,
/// plan_not_found_error_orientation Step 5e): a near method name gives the ranked retry, no near name gives the no-similar-name fact and a
/// GetFileOutline option, and an expression-bodied method states the body fact. The code stays NotFound and nothing is transformed.
/// The "containing type not found" sites (:46 and :123) are not reachable: a method declaration always sits inside a type.
/// </summary>
[TestFixture]
[Category("CodeTransformTools")]
public class ThreadSafetyEngineMissTests : CodeTransformMissFixtureBase
{
    private const string Counter = """
        public class Counter
        {
            private readonly object _gate = new();
            private int _n;

            public void Increment() { lock (_gate) { _n++; } }

            public int Read() => _n;
        }
        """;

    [Test]
    public async Task MakeMethodThreadSafe_NearMethodName_OffersTheRankedRetry()
    {
        var path = SetSource("Counter.cs", Counter);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Incrment", "make_method_thread_safe"));

        Assert.That(message, Does.Contain("Method 'Incrment' was not found in"));
        Assert.That(message, Does.Contain("Did you mean 'Increment'? Retry with methodName: 'Increment'."));
    }

    [Test]
    public async Task MakeMethodThreadSafe_NoSimilarMethodName_StatesTheFactAndOffersTheOutline()
    {
        var path = SetSource("Counter.cs", Counter);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Qzxvbnm", "make_method_thread_safe"));

        Assert.That(message, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_NearMethodName_OffersTheRankedRetry()
    {
        var path = SetSource("Counter.cs", Counter);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Incrment", "convert_lock_to_semaphore_slim"));

        Assert.That(message, Does.Contain("Method 'Incrment' was not found in"));
        Assert.That(message, Does.Contain("Did you mean 'Increment'? Retry with methodName: 'Increment'."));
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_NoSimilarMethodName_StatesTheFactAndOffersTheOutline()
    {
        var path = SetSource("Counter.cs", Counter);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Qzxvbnm", "convert_lock_to_semaphore_slim"));

        Assert.That(message, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }

    [Test]
    public async Task ConvertLockToSemaphoreSlim_ExpressionBodiedMethod_StatesTheBodyFactAndOffersTheSource()
    {
        var path = SetSource("Counter.cs", Counter);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Read", "convert_lock_to_semaphore_slim"));

        Assert.That(message, Does.Contain("Method 'Read' in"));
        Assert.That(message, Does.Contain("has no block body"));
        Assert.That(message, Does.Contain("GetMethodSource("));
    }
}
