using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;

namespace RoslynSentinel.Tests.Tools.Advanced;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class ExtractMembersOverloadRefusalTests
{
    private static string SourceFixture() => """
        public class Source
        {
            public int Get() => 1;
            public int Save(int a) => a;
            public int Save(string s) => s.Length;
        }
        """.ReplaceLineEndings("\r\n");

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_BareNameWithOverloads_ReturnsAmbiguousAndWritesNothing()
    {
        var original = SourceFixture();
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Save"]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(int)"));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(string)"));
        Assert.That(result.ErrorData?.Message, Does.Contain("MoveMember"));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
        Assert.That(() => workspace.ReadText("Source.Partial.cs"), Throws.TypeOf<FileNotFoundException>(), "A refused call must not create the partial file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_SignatureEntry_ReturnsInvalidArgumentAndWritesNothing()
    {
        var original = SourceFixture();
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Save(int)"]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
        Assert.That(() => workspace.ReadText("Source.Partial.cs"), Throws.TypeOf<FileNotFoundException>(), "A refused call must not create the partial file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_NonOverloadedBareName_StillExtractsToPartial()
    {
        using var workspace = InMemoryWorkspace.Create(("Source.cs", SourceFixture()));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Get"]);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Source.Partial.cs"), Does.Contain("Get"));
        Assert.That(workspace.ReadText("Source.cs"), Does.Not.Contain("public int Get()"));
    }
}
