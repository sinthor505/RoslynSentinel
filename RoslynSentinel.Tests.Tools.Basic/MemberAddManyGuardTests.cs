using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member(addMember) guards for N >= 1 declarations in newMemberSource (MemberRefactoringEngine.ParseNewMemberDeclarations,
/// AddMembersAsync and the insertAfterMemberName / insertBeforeMemberName anchor path). Each rejection writes nothing;
/// a multi-declaration source names the declaration that failed; one malformed declaration keeps the single-declaration
/// text; an anchor that names one of the members being added is refused as not found.
/// Runs on an InMemoryWorkspace, so the file on disk is the workspace text.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")]
public class MemberAddManyGuardTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberAddManyGuardFixture.cs";

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
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    private static Task<SentinelCallToolResult<object>> AddAsync(
        RefactoringStructuralTools tools,
        InMemoryWorkspace workspace,
        string newMemberSource,
        string? insertAfterMemberName = null,
        string? insertBeforeMemberName = null) =>
        tools.MemberTool(
            reason: "test multi member add guard",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "SingleDeclTarget",
            newMemberSource: newMemberSource,
            insertAfterMemberName: insertAfterMemberName,
            insertBeforeMemberName: insertBeforeMemberName);

    [Test]
    public async Task AddMember_SecondDeclarationParseError_NamesDeclarationTwoAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "public int Gamma() => 3;\n\npublic int Delta( => 4;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a malformed second declaration must be refused");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("declaration 2"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a refused add must not write, including the valid first declaration");
    }

    [Test]
    public async Task AddMember_SecondDeclarationFailsCompileGate_RefusesAndDoesNotWriteFirstMemberAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "public int Gamma() => 3;\n\npublic Missing M2() => null;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a second declaration with an undefined type must fail the compile gate");
            Assert.That(result.ErrorData?.Message, Does.Contain("introduces new compiler errors"));
            Assert.That((result.ErrorData?.Detail ?? string.Empty) + string.Concat(result.ErrorData?.StructuredDetail?.Select(d => d.ToString() ?? string.Empty) ?? []), Does.Contain("CS0246"),
                "the compile gate must carry CS0246 in the refusal's diagnostics (Detail or StructuredDetail)");
        });
        Assert.Multiple(() =>
        {
            Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a refused add must not write");
            Assert.That(workspace.ReadText(FixtureRelativePath), Does.Not.Contain("Gamma()"), "the valid first declaration must not be written on its own");
        });
    }

    [Test]
    public async Task AddMember_ValidThenMalformedSecond_ReportsDeclarationTwoAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "public int Gamma() => 3;\n\npublic int Delta( => 4;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("declaration 2"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "the valid first declaration must not be silently applied");
    }

    [Test]
    public async Task AddMember_ValidThenBodyFragment_RejectsWithoutNamingADeclarationAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "public int Gamma() => 3;\n\nreturn 1;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a body fragment after a valid declaration must not be silently dropped");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("newMemberSource is invalid:"));
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("declaration 2"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a rejected add must not write");
    }

    [Test]
    public async Task AddMember_TwoValidThenStrayBrace_NamesTheExtraBraceAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "public int Gamma() => 3;\n\npublic int Delta() => 4;\n}");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("extra or unbalanced '}'"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected addMember must not write anything.");
    }

    [Test]
    public async Task AddMember_SingleMalformedDeclaration_KeepsSingleDeclarationTextAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "public int Gamma( => 3;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True);
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("newMemberSource is invalid:"));
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("member declarations"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a rejected add must not write");
    }

    [TestCase("Gamma", null, "insertAfterMemberName", TestName = "AddMember_InsertAfterNameBeingAdded_IsNotFoundAndWritesNothing")]
    [TestCase(null, "Delta", "insertBeforeMemberName", TestName = "AddMember_InsertBeforeNameBeingAdded_IsNotFoundAndWritesNothing")]
    public async Task AddMember_AnchorNamesMemberBeingAdded_IsNotFoundAndWritesNothingAsync(string? insertAfterMemberName, string? insertBeforeMemberName, string anchorParam)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);
        var anchorName = insertAfterMemberName ?? insertBeforeMemberName!;

        var result = await AddAsync(tools, workspace, "public int Gamma() => 3;\n\npublic int Delta() => 4;",
            insertAfterMemberName: insertAfterMemberName, insertBeforeMemberName: insertBeforeMemberName);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an anchor naming a member being added must be refused");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain($"member '{anchorName}' ({anchorParam}) not found in container 'SingleDeclTarget'"));
            Assert.That(result.ErrorData?.Message, Does.Contain("Anchors available:"));
            Assert.That(result.ErrorData?.Message, Does.Contain("Alpha").And.Contains("Beta"));
            Assert.That(result.ErrorData?.Message, Does.Contain($"'{anchorName}' is declared in newMemberSource"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a refused add must not write");
    }
}
