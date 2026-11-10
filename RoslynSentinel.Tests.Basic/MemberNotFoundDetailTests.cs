using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// A member-not-found error from the shared member read (ReadFile with memberName, Member view) states what was searched,
/// lists the file's own members by kind with a similar name first, says when the name is declared in another file, and
/// names the next call. Driven by a run where ReadFile(memberName: CalculateDiscountedTotal) was asked before the method
/// was renamed from CalcDisc and got only "Member 'CalculateDiscountedTotal' not found".
/// </summary>
[TestFixture]
[Category("SymbolNavigationEngine")]
public class MemberNotFoundDetailTests
{
    private IWorkspaceManager _workspaceManager;
    private SymbolNavigationEngine _symbolNavigationEngine;

    private const string CalcSource = """
        public class OrderPricingCalculator
        {
            private int _unrelatedField;
            public decimal CalcDisc(decimal x) => x;
            public string DescribeOrder() => "";
            public string SummarizeShipping() => "";
        }
        """;

    private const string OtherSource = """
        public class Helper
        {
            public decimal CalculateDiscountedTotal(decimal x) => x;
        }
        """;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _symbolNavigationEngine = Battery.Basic.TestEngines.SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Calc.cs", CalcSource), ("Other.cs", OtherSource)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private async Task<string> MessageForMissingAsync(string memberName, string? containerName = null, bool strict = false)
    {
        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _symbolNavigationEngine.GetMemberSourceAsync("Calc.cs", memberName, containerName, null, null, null, strict));
        Assert.That(ex!.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        return ex.Message;
    }

    [Test]
    public async Task MissingMemberWithASimilarOne_ListsMembersByKind_SimilarFirst_AndNamesTheNextCallAsync()
    {
        var message = await MessageForMissingAsync("CalculateDiscountedTotal2");

        Assert.That(message, Does.StartWith("Member 'CalculateDiscountedTotal2' not found in 'Calc.cs' (searched every method"));
        Assert.That(message, Does.Contain("Closest names: 'CalcDisc' (method)"));
        Assert.That(message, Does.Contain("methods: CalcDisc, DescribeOrder, SummarizeShipping"));
        Assert.That(message, Does.Contain("fields: _unrelatedField"));
        Assert.That(message, Does.Contain("ReadFile(").And.Contain("without memberName"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }

    [Test]
    public async Task MissingMemberDeclaredInAnotherFile_SaysWhichFileAsync()
    {
        // Other.cs declares Helper.CalculateDiscountedTotal; Calc.cs does not.
        var message = await MessageForMissingAsync("CalculateDiscountedTotal");

        Assert.That(message, Does.Contain("'CalculateDiscountedTotal' is declared in "));
        Assert.That(message, Does.Contain("Other.cs"));
        Assert.That(message, Does.Contain("(method of Helper)"));
        Assert.That(message, Does.Contain("memberName: \"CalculateDiscountedTotal\""), "the next call reads it from the other file");
    }

    [Test]
    public async Task WrongContainerNameWithoutStrictFlag_NamesTheTypesTheFileDeclaresAsync()
    {
        var message = await MessageForMissingAsync("NoSuchMember", "Missing");

        Assert.That(message, Does.Contain("containerName 'Missing'"));
        Assert.That(message, Does.Contain("Type 'Missing' is not declared in this file (types declared here: OrderPricingCalculator)"));
        Assert.That(message, Does.Contain("methods: CalcDisc"));
    }

    [Test]
    public async Task RightContainerMissingMember_ListsOnlyThatTypesMembersAsync()
    {
        var message = await MessageForMissingAsync("NoSuchMember", "OrderPricingCalculator");

        Assert.That(message, Does.Contain("Members declared in type 'OrderPricingCalculator' of 'Calc.cs'"));
        Assert.That(message, Does.Not.Contain("is not declared in this file"));
    }

    [Test]
    public async Task WrongContainerNameWithStrictFlag_KeepsTheContainerMissTextAndAddsTheMemberListAsync()
    {
        var message = await MessageForMissingAsync("NoSuchMember", "Missing", strict: true);

        Assert.That(message, Does.Contain("Missing"));
        Assert.That(message, Does.Contain("methods: CalcDisc, DescribeOrder, SummarizeShipping"));
        Assert.That(message, Does.Contain("GetFileOutline("));
    }
}
