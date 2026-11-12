namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Lookup misses of SyntaxUpgradeEngine reached through CodeTransformTools (ConvertSwitchToExpression, UpgradeToPrimaryConstructor and
/// UseExceptionExpressions, plan_not_found_error_orientation Step 5e): a near name gives the ranked retry, no near name gives the
/// no-similar-name fact and a GetFileOutline option. The error code stays NotFound and nothing is transformed.
/// </summary>
[TestFixture]
[Category("CodeTransformTools")]
public class SyntaxUpgradeEngineMissTests : CodeTransformMissFixtureBase
{
    private const string CounterWithSwitch = """
        public class Counter
        {
            public int Pick(int n)
            {
                switch (n)
                {
                    case 1: return 10;
                    default: return 0;
                }
            }

            public int Plain() { return 1; }
        }
        """;

    [Test]
    public async Task ConvertSwitchToExpression_NearMethodName_OffersTheRankedRetry()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Pik", "convert_switch_to_expression"));

        Assert.That(message, Does.Contain("Method 'Pik' was not found in"));
        Assert.That(message, Does.Contain("Did you mean 'Pick'? Retry with methodName: 'Pick'."));
    }

    [Test]
    public async Task ConvertSwitchToExpression_NoSimilarMethodName_StatesTheFactAndOffersTheOutline()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Qzxvbnm", "convert_switch_to_expression"));

        Assert.That(message, Does.Contain("No method declared in"));
        Assert.That(message, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }

    [Test]
    public async Task ConvertSwitchToExpression_MethodWithoutSwitch_StatesTheMethodFactAndOffersTheSource()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Plain", "convert_switch_to_expression"));

        Assert.That(message, Does.Contain("No switch statement is declared in method 'Plain'"));
        Assert.That(message, Does.Contain("GetMethodSource("));
    }

    [Test]
    public async Task UpgradeToPrimaryConstructor_NearClassName_OffersTheRankedRetry()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyClassCodeTransform("miss test", path, "Cunter", "upgrade_to_primary_constructor"));

        Assert.That(message, Does.Contain("Class 'Cunter' was not found in"));
        Assert.That(message, Does.Contain("Did you mean 'Counter'? Retry with className: 'Counter'."));
    }

    [Test]
    public async Task UpgradeToPrimaryConstructor_NoSimilarClassName_StatesTheFactAndOffersTheOutline()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyClassCodeTransform("miss test", path, "Qzxvbnm", "upgrade_to_primary_constructor"));

        Assert.That(message, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }

    [Test]
    public async Task UseExceptionExpressions_NearMethodName_OffersTheRankedRetry()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Pik", "use_exception_expressions"));

        Assert.That(message, Does.Contain("Did you mean 'Pick'? Retry with methodName: 'Pick'."));
    }

    [Test]
    public async Task UseExceptionExpressions_NoSimilarMethodName_StatesTheFactAndOffersTheOutline()
    {
        var path = SetSource("Counter.cs", CounterWithSwitch);

        var message = AssertNotFound(await Tools.ApplyMethodCodeTransform("miss test", path, "Qzxvbnm", "use_exception_expressions"));

        Assert.That(message, Does.Contain("has a similar name to 'Qzxvbnm'"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }
}
