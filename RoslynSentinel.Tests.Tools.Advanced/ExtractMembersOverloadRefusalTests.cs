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
        Assert.That(result.ErrorData?.Message, Does.Contain("ExtractMembers"));
        Assert.That(result.ErrorData?.Message, Does.Not.Contain("MoveMember"));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
        Assert.That(() => workspace.ReadText("Source.Partial.cs"), Throws.TypeOf<FileNotFoundException>(), "A refused call must not create the partial file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_SignatureEntry_ExtractsOnlyThatOverload()
    {
        using var workspace = InMemoryWorkspace.Create(("Source.cs", SourceFixture()));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Save(int)"]);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var partial = workspace.ReadText("Source.Partial.cs");
        Assert.That(partial, Does.Contain("Save(int a)"));
        Assert.That(partial, Does.Not.Contain("Save(string"));
        var source = workspace.ReadText("Source.cs");
        Assert.That(source, Does.Not.Contain("Save(int a)"));
        Assert.That(source, Does.Contain("Save(string s)"));
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

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_AmbiguousCandidateFromMessage_RetrySucceeds()
    {
        using var workspace = InMemoryWorkspace.Create(("Source.cs", SourceFixture()));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var refused = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Save"]);
        Assert.That(refused.IsError, Is.True);
        var candidate = System.Text.RegularExpressions.Regex.Match(refused.ErrorData?.Message ?? string.Empty, @"Save\([^)]*\)");
        Assert.That(candidate.Success, Is.True, $"no candidate printed in: {refused.ErrorData?.Message}");
        Assert.That(candidate.Value, Is.EqualTo("Save(int)"));
        var retry = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: [candidate.Value]);
        Assert.That(retry.IsError, Is.False, $"error: {retry.ErrorData?.Message}");
        var partial = workspace.ReadText("Source.Partial.cs");
        Assert.That(partial, Does.Contain("Save(int a)"));
        Assert.That(partial, Does.Not.Contain("Save(string"));
        Assert.That(workspace.ReadText("Source.cs"), Does.Contain("Save(string s)"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_TypoSignature_ReturnsNotFoundAndWritesNothing()
    {
        var original = SourceFixture();
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Save(long)"]);
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
        Assert.That(() => workspace.ReadText("Source.Partial.cs"), Throws.TypeOf<FileNotFoundException>(), "A refused call must not create the partial file.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_SignatureAndPropertyTogether_ExtractsBoth()
    {
        var original = """
            public class Source
            {
                public int Get() => 1;
                public int Count { get; set; }
                public int Save(int a) => a;
                public int Save(string s) => s.Length;
            }
            """.ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Source.cs"), "Source", ExtractAsType.partialClass, memberNames: ["Save(int)", "Count"]);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var partial = workspace.ReadText("Source.Partial.cs");
        Assert.That(partial, Does.Contain("Save(int a)"));
        Assert.That(partial, Does.Contain("Count"));
        Assert.That(partial, Does.Not.Contain("Save(string"));
        var source = workspace.ReadText("Source.cs");
        Assert.That(source, Does.Not.Contain("Save(int a)"));
        Assert.That(source, Does.Not.Contain("public int Count"));
        Assert.That(source, Does.Contain("Save(string s)"));
    }

    // A path that is not a document of the workspace must be refused as NotFound (naming the path) for both extract kinds, on
    // a dry run and on apply; it used to come back as dry_run_ok / no_changes because the engine returned an empty change set.
    [TestCase(ExtractAsType.partialClass, true)]
    [TestCase(ExtractAsType.partialClass, false)]
    [TestCase(ExtractAsType.@interface, true)]
    [TestCase(ExtractAsType.@interface, false)]
    [Category("AdvancedRefactoringTools")]
    public async Task ExtractMembers_MissingSourceFile_ReturnsNotFoundAndWritesNothing(ExtractAsType newType, bool dryRun)
    {
        var original = SourceFixture();
        using var workspace = InMemoryWorkspace.Create(("Source.cs", original));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var missing = workspace.PathOf("Missing.cs");
        var result = await tools.ExtractMembersTool(reason: "test message", missing, "Source", newType, newTypeName: newType == ExtractAsType.@interface ? "ISource" : null, memberNames: newType == ExtractAsType.partialClass ? ["Get"] : null, dryRun: dryRun);
        Assert.That(result.IsError, Is.True, "A missing source file must be an error, not dry_run_ok / no_changes.");
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("Missing.cs"));
        Assert.That(workspace.ReadText("Source.cs"), Is.EqualTo(original), "A refused call must not write.");
        Assert.That(() => workspace.ReadText("Source.Partial.cs"), Throws.TypeOf<FileNotFoundException>(), "A refused call must not create the partial file.");
    }
}
