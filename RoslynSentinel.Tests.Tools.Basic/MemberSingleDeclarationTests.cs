using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member(replace/addMember) take exactly one member declaration in newMemberSource. A source with
/// several members used to be rejected by replace as "not a valid member declaration" (the engine's
/// ContainsDiagnostics check fired before its multi-declaration check, and the tool layer then
/// overwrote the engine message with a hardcoded generic string) - which sent agents off rewriting a
/// perfectly valid member instead of splitting the call. These tests pin the specific message: how
/// many members were found, their names, that the operation takes exactly one, and the recovery path.
/// About the tool's code-correctness, so it runs on an InMemoryWorkspace (no temp directory, MSBuild
/// load or disk write). See ModifyAttributeBatchTests for the pattern.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public class MemberSingleDeclarationTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberSingleDeclarationFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class SingleDeclTarget
    {
        public int Alpha() => 1;

        public int Beta() => 2;
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        return new RefactoringStructuralTools(new RefactoringStructuralImpl(
            new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config),
            new MemberRefactoringEngine(workspaceManager, new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance), new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance), config),
            new StructuralRefinementEngine(workspaceManager, config),
            new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance),
            workspaceManager,
            new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance),
            NullLogger<RefactoringStructuralImpl>.Instance));
    }

    [Test]
    public async Task Replace_WithThreeMembers_ReportsCountNamesAndRecoveryInsteadOfInvalidDeclarationAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.Member(
            reason: "test multi member replace",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Alpha",
            newMemberSource: "public int Alpha() => 10;\n\npublic int Gamma() => 3;\n\nprivate string _delta = \"d\";");

        Assert.That(result.IsSuccess, Is.False);
        var message = result.ErrorData!.Message;
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(message, Does.Contain("3 member declarations"));
            Assert.That(message, Does.Contain("Method 'Alpha'"));
            Assert.That(message, Does.Contain("Method 'Gamma'"));
            Assert.That(message, Does.Contain("Field '_delta'"));
            Assert.That(message, Does.Contain("replace takes exactly one member"));
            Assert.That(message, Does.Contain("Member(operation: replace) once per existing member"));
            Assert.That(message, Does.Contain("Member(operation: addMember"));
            Assert.That(message, Does.Not.Contain("not a valid member declaration"),
                "The multi-member case must not be misreported as a malformed declaration.");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected replace must not write anything.");
    }

    [Test]
    public async Task Replace_WithSingleMalformedMember_StillReportsInvalidDeclarationAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.Member(
            reason: "test malformed single replace",
            operation: MemberAction.replace,
            filePath: workspace.PathOf(FixtureRelativePath),
            memberName: "Alpha",
            newMemberSource: "public int Alpha() { return ; + }");

        Assert.That(result.IsSuccess, Is.False);
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData.Message, Does.Contain("not a valid member declaration"));
            Assert.That(result.ErrorData.Message, Does.Not.Contain("member declarations ("));
        });
    }

    [TestCase(null)]
    [TestCase("after:Alpha")]
    [TestCase("before:Beta")]
    public async Task AddMember_WithTwoMembers_ReportsCountNamesAndOneCallPerMemberAsync(string? position)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.Member(
            reason: "test multi member add",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "SingleDeclTarget",
            position: position,
            newMemberSource: "public int Gamma() => 3;\n\npublic string Name { get; set; } = \"\";");

        Assert.That(result.IsSuccess, Is.False);
        var message = result.ErrorData!.Message;
        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorData.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(message, Does.Contain("2 member declarations"));
            Assert.That(message, Does.Contain("Method 'Gamma'"));
            Assert.That(message, Does.Contain("Property 'Name'"));
            Assert.That(message, Does.Contain("addMember takes exactly one member per call"));
            Assert.That(message, Does.Contain("Member(operation: addMember) once per member"));
        });
    }
}
