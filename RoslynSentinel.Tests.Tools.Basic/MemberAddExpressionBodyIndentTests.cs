namespace RoslynSentinel.Tests.Battery.Basic;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

/// <summary>
/// Reproduction attempt for finding_member_add_member_flattens_expression_body_continuation_indent.md:
/// Member(addMember) / InsertMemberAfter of a two-line expression-bodied method (body on its own, deeper-indented
/// continuation line) into a CRLF class whose siblings use the same style must keep the continuation indent and
/// leave the siblings untouched. Runs on an InMemoryWorkspace; assertions are on the engine's UpdatedText.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MemberAddExpressionBodyIndentTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/ExprBodyIndentFixture.cs";

    private static string Fixture(params string[] members) =>
    string.Join("\r\n", new[] { "namespace ContosoOrders.Core;", "", "public class ExprBodyTarget", "{" }
        .Concat(members).Concat(new[] { "}", "" }));

    [Test]
    public async Task AddMember_AppendTwoLineExpressionBody_KeepsContinuationIndentAndSiblingsAsync()
    {
        const string alpha = "    public int Alpha() =>\r\n        1 + 1;";
        const string beta = "    public int Beta() =>\r\n        2 + 2;";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, Fixture(alpha, "", beta)));
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, TestEngines.ValidationEngine(workspace.Manager));

        var result = await engine.AddMemberAsync(workspace.PathOf(FixtureRelativePath), "ExprBodyTarget", "public int X() =>\r\n        1 + 2;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Fixture(alpha, "", beta, "", "    public int X() =>\r\n        1 + 2;")));
    }

    [Test]
    public async Task InsertMemberAfter_TwoLineExpressionBody_KeepsContinuationIndentAndSiblingsAsync()
    {
        const string alpha = "    public int Alpha() =>\r\n        1 + 1;";
        const string beta = "    public int Beta() =>\r\n        2 + 2;";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, Fixture(alpha, "", beta)));
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        var engine = TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, TestEngines.ValidationEngine(workspace.Manager));

        var result = await engine.InsertMemberAfterAsync(workspace.PathOf(FixtureRelativePath), "ExprBodyTarget", "Alpha", "public int X() =>\r\n        1 + 2;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        Assert.That(result.UpdatedText, Is.EqualTo(Fixture(alpha, "", "    public int X() =>\r\n        1 + 2;", "", beta)));
    }
}
