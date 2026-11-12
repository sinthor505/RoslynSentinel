using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// The field-name miss of MsToolAugmentEngine.EncapsulateFieldSafeAsync (plan_not_found_error_orientation, Step 5e slice 4). Engine-direct:
/// no tool hosts EncapsulateFieldSafeAsync (Search references finds callers only in tests). A near field name gives the ranked retry, an
/// unrelated name gives the no-similar-name fact and a GetFileOutline option; the failed result carries the NotFound code and no new text.
/// </summary>
[TestFixture]
[Category("MsToolAugmentEngine")] // sentinel:auto-category
public class MsToolAugmentEncapsulateFieldMissTests
{
    private const string Source = """
        public class Counter
        {
            private int _count;
            public int Total;
        }
        """;

    private PersistentWorkspaceManager _workspaceManager;
    private MsToolAugmentEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = Battery.Basic.TestEngines.MsToolAugmentEngine(_workspaceManager);
        _workspaceManager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Entity.cs", Source)]));
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    private static void AssertFactsAndOptionsOnly(string message)
    {
        Assert.That(message, Does.Not.Contain("tell the user"));
        Assert.That(message, Does.Not.Contain("You MUST"));
        Assert.That(message, Does.Not.Contain("not found."), "No bare 'not found.' sentence.");
    }

    private async Task<string> RunMissAsync(string fieldName)
    {
        var result = await _engine.EncapsulateFieldSafeAsync("Entity.cs", fieldName);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.UpdatedContent, Is.Null, "nothing was generated or written");
        return result.Error!;
    }

    [Test]
    public async Task EncapsulateField_NearFieldName_OffersTheRankedRetryAsync()
    {
        var message = await RunMissAsync("Totl");

        Assert.That(message, Does.Contain("Field 'Totl' was not found in 'Entity.cs'."));
        Assert.That(message, Does.Contain("Did you mean 'Total'? Retry with fieldName: 'Total'."));
        AssertFactsAndOptionsOnly(message);
    }

    [Test]
    public async Task EncapsulateField_NoSimilarFieldName_StatesTheFactAndOffersTheOutlineAsync()
    {
        var message = await RunMissAsync("Qzxvbnm");

        Assert.That(message, Does.Contain("No field declared in 'Entity.cs' has a similar name to 'Qzxvbnm'."));
        Assert.That(message, Does.Contain("GetFileOutline("));
        Assert.That(message, Does.Not.Contain("Did you mean"));
        AssertFactsAndOptionsOnly(message);
    }
}
