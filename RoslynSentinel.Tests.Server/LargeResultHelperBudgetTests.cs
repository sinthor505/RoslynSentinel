using System.Reflection;
using System.Text.Json.Nodes;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for the inline budget classes: the configurable default budget, the Whole class floor and
/// multiplier, the offload threshold, the startup setting's minimum, the class a tool method declares, the
/// error-only body detection, and <see cref="IFindingsEnvelope.HasSuccessData"/>. Tests that set the process-wide
/// budget setting are <see cref="NonParallelizableAttribute"/> and <see cref="TearDown"/> restores the stock budget.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LargeResultHelperBudgetTests
{
    [TearDown]
    public void TearDown()
    {
        InlineBudgetOptions.Configure([]);
    }

    private static void ConfigureBudget(int value)
    {
        InlineBudgetOptions.Configure([$"{InlineBudgetOptions.BudgetCharsSetting.Flag}={value}"]);
    }

    [InlineBudget(InlineBudgetClass.Whole)]
    private static void WholeTaggedStub()
    {
    }

    private static void PlainStub()
    {
    }

    private static MethodInfo StubMethod(string name) =>
        typeof(LargeResultHelperBudgetTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Stub method '{name}' not found.");

    [Test]
    public void DefaultFollowsSetting()
    {
        var configured = 2 * InlineBudgetOptions.StockBudgetChars;
        ConfigureBudget(configured);

        Assert.That(LargeResultHelper.DefaultBudgetChars, Is.EqualTo(configured));
    }

    [Test]
    public void WholeIsMaxOfFloorAndDouble()
    {
        InlineBudgetOptions.Configure([]);
        Assert.That(LargeResultHelper.BudgetChars(InlineBudgetClass.Whole), Is.EqualTo(LargeResultHelper.WholeFloorChars),
            "At the stock budget the Whole floor wins over the doubled default.");

        var configured = 2 * LargeResultHelper.WholeFloorChars;
        ConfigureBudget(configured);
        Assert.That(LargeResultHelper.BudgetChars(InlineBudgetClass.Whole),
            Is.EqualTo(LargeResultHelper.WholeMultiplier * configured),
            "Above the floor the Whole budget is the multiplier times the configured default.");
    }

    [Test]
    public void ThresholdIsBudgetTimesTolerance()
    {
        foreach (var budgetClass in new[] { InlineBudgetClass.Default, InlineBudgetClass.Whole })
        {
            Assert.That(LargeResultHelper.OffloadThresholdChars(budgetClass),
                Is.EqualTo((int)(LargeResultHelper.BudgetChars(budgetClass) * LargeResultHelper.ToleranceFactor)),
                $"Threshold for {budgetClass} must be its budget times the tolerance factor.");
        }
    }

    [Test]
    public void SettingBelowMinimumIsRejected()
    {
        var read = InlineBudgetOptions.BudgetCharsSetting.Read(StartupArgs.Empty, _ => (InlineBudgetOptions.MinimumBudgetChars - 1).ToString());

        Assert.That(read.Value, Is.EqualTo(InlineBudgetOptions.StockBudgetChars));
        Assert.That(read.Source, Is.EqualTo(SettingSource.Invalid));
        Assert.That(read.Problem, Is.Not.Null);
    }

    [Test]
    public void ClassForAttributedMethodIsWhole()
    {
        Assert.That(LargeResultHelper.ClassFor(StubMethod(nameof(WholeTaggedStub)), errorOnly: false), Is.EqualTo(InlineBudgetClass.Whole));
    }

    [Test]
    public void ClassForErrorOnlyIsWhole()
    {
        Assert.That(LargeResultHelper.ClassFor(StubMethod(nameof(PlainStub)), errorOnly: true), Is.EqualTo(InlineBudgetClass.Whole));
    }

    [Test]
    public void ClassForPlainMethodIsDefault()
    {
        Assert.That(LargeResultHelper.ClassFor(StubMethod(nameof(PlainStub)), errorOnly: false), Is.EqualTo(InlineBudgetClass.Default));
    }

    [TestCase("{\"isError\":true,\"errorData\":{}}", true, TestName = "IsErrorOnlyBody_ErrorWithoutSuccessData_IsTrue")]
    [TestCase("{\"isError\":true,\"errorData\":{},\"successData\":null}", true, TestName = "IsErrorOnlyBody_ErrorWithNullSuccessData_IsTrue")]
    [TestCase("{\"isError\":true,\"errorData\":{},\"successData\":{}}", false, TestName = "IsErrorOnlyBody_ErrorWithObjectSuccessData_IsFalse")]
    [TestCase("{\"isError\":false,\"successData\":{}}", false, TestName = "IsErrorOnlyBody_Success_IsFalse")]
    [TestCase("[]", false, TestName = "IsErrorOnlyBody_NonObject_IsFalse")]
    public void IsErrorOnlyBody_Classifies(string json, bool expected)
    {
        var root = JsonNode.Parse(json);

        Assert.That(LargeResultHelper.IsErrorOnlyBody(root), Is.EqualTo(expected));
    }

    [Test]
    public void HasSuccessDataReflectsSuccessData()
    {
        var failure = new SentinelCallToolResult<string> { IsError = true };
        var success = new SentinelCallToolResult<string> { IsError = false, SuccessData = "payload" };

        Assert.That(((IFindingsEnvelope)failure).HasSuccessData, Is.False, "A failure with no success data reports none.");
        Assert.That(((IFindingsEnvelope)success).HasSuccessData, Is.True, "A success carrying data reports it.");
    }
}
