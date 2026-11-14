using System.Text.RegularExpressions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Member(addMember) with N >= 1 complete member declarations in newMemberSource (MemberRefactoringEngine.AddMembersAsync
/// and the insertAfterMemberName / insertBeforeMemberName variants). The block is one write with one changeId: source order,
/// one blank line between members, the container otherwise unchanged. Also covers the compile gate on a caller declared
/// before its helper in one call, the dryRun envelope, the N = 1 regression through the same path, and the null-parse texts.
/// Runs on an InMemoryWorkspace, so the file on disk is the workspace text.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")]
public class MemberAddManyTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberAddManyFixture.cs";

    private const string FixtureFileName = "MemberAddManyFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class SingleDeclTarget
    {
        public int Alpha() => 1;

        public int Beta() => 2;
    }
    """;

    private const string ShippingSource = """
    namespace ContosoOrders.Core;

    public class ShippingRules
    {
        public int Quote() => 1;

        public int Label() => 2;
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    private static Task<SentinelCallToolResult<object>> AddAsync(
        RefactoringStructuralTools tools,
        InMemoryWorkspace workspace,
        string containerName,
        string newMemberSource,
        string? insertAfterMemberName = null,
        string? insertBeforeMemberName = null,
        bool dryRun = false) =>
        tools.MemberTool(
            reason: "test multi member add",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: containerName,
            newMemberSource: newMemberSource,
            insertAfterMemberName: insertAfterMemberName,
            insertBeforeMemberName: insertBeforeMemberName,
            dryRun: dryRun);

    private static void AssertInOrder(string text, params string[] fragments)
    {
        var last = -1;
        foreach (var fragment in fragments)
        {
            var index = text.IndexOf(fragment, StringComparison.Ordinal);
            Assert.That(index, Is.GreaterThan(last), $"'{fragment}' must be present and follow the previous fragment. actual:\n{text}");
            last = index;
        }
    }

    [Test]
    public async Task AddMember_TwoDeclarationsNoAnchor_AppendsBlockInSourceOrderAfterLastMemberAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget",
            "public int Gamma() => 3;\n\npublic int Delta() => 4;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var after = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 2 members: Method 'Gamma', Method 'Delta' to 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(after, Does.Contain("public int Alpha() => 1;"), "existing members must survive");
            Assert.That(Regex.IsMatch(after, @"Beta\(\) => 2;\r?\n\r?\n[ \t]+public int Gamma\(\) => 3;\r?\n\r?\n[ \t]+public int Delta\(\) => 4;\r?\n\}"),
                Is.True, "the block must follow Beta with one blank line between members and close the class after Delta. actual:\n" + after);
        });
        AssertInOrder(after, "Alpha() => 1;", "Beta() => 2;", "Gamma() => 3;", "Delta() => 4;");
    }

    [Test]
    public async Task AddMember_ThreeDeclarationsNoAnchor_AppendsBlockInSourceOrderAfterLastMemberAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget",
            "public int X() => 5;\n\npublic int Y() => 6;\n\npublic int Z() => 7;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var after = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 3 members: Method 'X', Method 'Y', Method 'Z' to 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(after, Does.Contain("public int Alpha() => 1;"), "existing members must survive");
            Assert.That(Regex.IsMatch(after, @"Beta\(\) => 2;\r?\n\r?\n[ \t]+public int X\(\) => 5;\r?\n\r?\n[ \t]+public int Y\(\) => 6;\r?\n\r?\n[ \t]+public int Z\(\) => 7;\r?\n\}"),
                Is.True, "the block must follow Beta with one blank line between each member and close the class after Z. actual:\n" + after);
        });
        AssertInOrder(after, "Alpha() => 1;", "Beta() => 2;", "X() => 5;", "Y() => 6;", "Z() => 7;");
    }

    [Test]
    public async Task AddMember_CallerBeforeHelperInOneCall_PassesCompileGateAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget",
            "public int Run() => Helper() + 1;\n\nprivate int Helper() => 41;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 2 members: Method 'Run', Method 'Helper' to 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("public int Run() => Helper() + 1;"));
        });
    }

    [Test]
    public async Task AddMember_CallerAloneWithoutHelper_IsRefusedByCompileGateAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget", "public int Run() => Helper() + 1;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a caller with no helper in the file must fail the compile gate");
            Assert.That(result.ErrorData?.Message, Does.Contain("introduces new compiler errors"));
            Assert.That((result.ErrorData?.Detail ?? string.Empty) + string.Concat(result.ErrorData?.StructuredDetail?.Select(d => d.ToString() ?? string.Empty) ?? []), Does.Contain("CS0103"),
                "the compile gate must carry CS0103 in the refusal's diagnostics (Detail or StructuredDetail)");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a refused add must not write");
    }

    [Test]
    public async Task AddMember_TwoDeclarationsAfterAnchor_InsertsContiguousBlockBetweenAnchorAndNextMemberAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget",
            "public int Gamma() => 3;\n\npublic int Delta() => 4;", insertAfterMemberName: "Alpha");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var after = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 2 members: Method 'Gamma', Method 'Delta' after 'Alpha' in 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(Regex.IsMatch(after, @"Alpha\(\) => 1;\r?\n\r?\n[ \t]+public int Gamma\(\) => 3;\r?\n\r?\n[ \t]+public int Delta\(\) => 4;\r?\n\r?\n[ \t]+public int Beta\(\) => 2;"),
                Is.True, "the block must sit between Alpha and Beta, contiguous, with one blank line between members. actual:\n" + after);
        });
        AssertInOrder(after, "Alpha() => 1;", "Gamma() => 3;", "Delta() => 4;", "Beta() => 2;");
    }

    [Test]
    public async Task AddMember_TwoDeclarationsBeforeAnchor_InsertsContiguousBlockBetweenPreviousMemberAndAnchorAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget",
            "public int Gamma() => 3;\n\npublic int Delta() => 4;", insertBeforeMemberName: "Beta");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var after = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 2 members: Method 'Gamma', Method 'Delta' before 'Beta' in 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(Regex.IsMatch(after, @"Alpha\(\) => 1;\r?\n\r?\n[ \t]+public int Gamma\(\) => 3;\r?\n\r?\n[ \t]+public int Delta\(\) => 4;\r?\n\r?\n[ \t]+public int Beta\(\) => 2;"),
                Is.True, "the block must sit between Alpha and Beta, contiguous, with one blank line between members. actual:\n" + after);
        });
        AssertInOrder(after, "Alpha() => 1;", "Gamma() => 3;", "Delta() => 4;", "Beta() => 2;");
    }

    [Test]
    public async Task AddMember_ShippingKindThreeMethodsAfterAnchor_InsertsBlockInSourceOrderAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, ShippingSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "ShippingRules",
            "public bool IsKnown(string code) => code == \"A\";\n\n" +
            "public bool IsExpedited(string code) => code == \"X\";\n\n" +
            "public string Normalize(string code) => code.Trim().ToUpperInvariant();",
            insertAfterMemberName: "Quote");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var after = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 3 members: Method 'IsKnown', Method 'IsExpedited', Method 'Normalize' after 'Quote' in 'ShippingRules' in MemberAddManyFixture.cs."));
            Assert.That(after, Does.Contain("public int Label() => 2;"), "the member after the anchor must survive");
        });
        AssertInOrder(after, "Quote() => 1;", "IsKnown(string code)", "IsExpedited(string code)", "Normalize(string code)", "Label() => 2;");
    }

    [Test]
    public async Task AddMember_TwoDeclarationsDryRun_ReturnsSameEnvelopeAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget",
            "public int Gamma() => 3;\n\npublic int Delta() => 4;", dryRun: true);

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var summary = (AppliedChangeSummary)result.SuccessData!;
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added 2 members: Method 'Gamma', Method 'Delta' to 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(summary.DryRun, Is.True);
            Assert.That(summary.Status, Is.EqualTo("dry_run_ok"));
            Assert.That(summary.Diff, Is.Not.Null.And.Not.Empty);
            Assert.That(summary.ChangedContent, Is.Null, "a dry run must not echo the whole file");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a dry run must not write");
    }

    [Test]
    public async Task AddMember_SingleDeclaration_KeepsExistingAddedMessageAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget", "public int X() => 5;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(result.StatusMessage, Is.EqualTo("Added Method 'X' to 'SingleDeclTarget' in MemberAddManyFixture.cs."));
            Assert.That(workspace.ReadText(FixtureRelativePath), Does.Contain("public int X() => 5;"));
        });
    }

    [TestCase(null, null, "Failed to parse new member.", TestName = "NullParse_NoAnchor_KeepsAddPathText")]
    [TestCase("Alpha", null, "Cannot edit: invalid member source.", TestName = "NullParse_InsertAfter_KeepsAnchorPathText")]
    [TestCase(null, "Beta", "Cannot edit: invalid member source.", TestName = "NullParse_InsertBefore_KeepsAnchorPathText")]
    public async Task AddMember_UnparsableSource_KeepsExistingTextAndWritesNothingAsync(string? insertAfterMemberName, string? insertBeforeMemberName, string expectedMessage)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await AddAsync(tools, workspace, "SingleDeclTarget", "   ",
            insertAfterMemberName: insertAfterMemberName, insertBeforeMemberName: insertBeforeMemberName);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an unparsable source must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain(expectedMessage));
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("member declarations"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a rejected add must not write");
    }
}
