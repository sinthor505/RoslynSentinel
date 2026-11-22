using NUnit.Framework;
using NUnit.Framework.Interfaces;
using RoslynSentinel.Common.AgentLoop;

[assembly: RoslynSentinel.Tests.ErrorVariants.RewriteBodyArchivedOutcome]

namespace RoslynSentinel.Tests.ErrorVariants;

/// <summary>
/// Applies to every test in this assembly. Once a test ends, rewrites the test-result.txt files that
/// <see cref="ModelTestingResultsArchiver"/> wrote from the test body while the outcome was still
/// Unfinished. Uses ITestAction (not TearDown) so every fixture that archives from its test body is
/// covered without touching each class; AfterTest runs after TearDown so the outcome includes
/// teardown failures. The rewrite logic lives in RoslynSentinel.Common.AgentLoop.PendingArchivedOutcomes.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RewriteBodyArchivedOutcomeAttribute : Attribute, ITestAction
{
    public ActionTargets Targets => ActionTargets.Test;

    public void BeforeTest(ITest test)
    {
    }

    public void AfterTest(ITest test)
    {
        var result = TestContext.CurrentContext.Result;
        PendingArchivedOutcomes.Rewrite(test.Id, result.Outcome.ToString(), test.FullName, result.Message, result.StackTrace, TestContext.Out.WriteLine);
    }
}
