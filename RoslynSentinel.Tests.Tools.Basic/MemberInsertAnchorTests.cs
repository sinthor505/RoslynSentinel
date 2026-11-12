using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Tool-level tests for the addMember insert anchors (insertAfterMemberName / insertBeforeMemberName) that
/// replaced the old position string. Covers the InvalidArgument validation (both anchors, anchor on a non-addMember
/// operation, anchor on addTopLevelType), the not-found and ambiguous anchor outcomes on a class and an enum, the
/// literal-name rule ("after:X" is a name), the happy paths and append. Every failure must write nothing.
/// Runs on an InMemoryWorkspace, so the file on disk is the workspace text.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")] // sentinel:auto-category


public class MemberInsertAnchorTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberInsertAnchorFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public enum OrderState
    {
        Pending,
        Shipped
    }

    public class AnchorTarget
    {
        public int Existing() => 1;

        public int Save(int value) => value;

        public int Save(string value) => value.Length;

        public static AnchorTarget operator +(AnchorTarget a, AnchorTarget b) => a;
    }
    """;

    private static RefactoringStructuralTools BuildTools(IWorkspaceManager workspaceManager)
    {
        return TestTools.RefactoringStructuralTools(workspaceManager);
    }

    [Test]
    public async Task Member_AddWithBothAnchors_ReturnsInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test both insert anchors outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Added() => 3;",
            insertAfterMemberName: "Existing",
            insertBeforeMemberName: "Save");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "two anchors must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("cannot both be set"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a rejected add must not write");
    }

    [TestCase(MemberAction.view)]
    [TestCase(MemberAction.remove)]
    [TestCase(MemberAction.replace)]
    public async Task Member_InsertAnchorOnNonAddOperation_ReturnsInvalidArgumentNamingOperationAndWritesNothingAsync(MemberAction operation)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test insert anchor on non-add operation outcome",
            operation: operation,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            memberName: "Existing",
            newMemberSource: "public int Existing() => 2;",
            insertAfterMemberName: "Save");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an insert anchor on a non-addMember operation must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain($"'{operation}'"), "the message must name the operation");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a rejected call must not write");
    }

    [Test]
    public async Task Member_AddTopLevelTypeWithAnchor_ReturnsInvalidArgumentAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test addTopLevelType with insert anchor outcome",
            operation: MemberAction.addTopLevelType,
            filePath: workspace.PathOf(FixtureRelativePath),
            newMemberSource: "public class Extra { }",
            insertAfterMemberName: "Existing");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an anchor on addTopLevelType must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
            Assert.That(result.ErrorData?.Message, Does.Contain("'addTopLevelType'"), "the message must name the operation");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a rejected call must not write");
    }

    [Test]
    public async Task Member_AddWithUnknownAnchor_ReturnsNotFoundNamingAnchorContainerAndHintAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test unknown insert anchor outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Added() => 3;",
            insertAfterMemberName: "Missing");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an anchor that does not exist must be an error, not an append");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("'Missing'"), "the message must name the anchor");
            Assert.That(result.ErrorData?.Message, Does.Contain("'AnchorTarget'"), "the message must name the container");
            Assert.That(result.ErrorData?.Message, Does.Contain("cannot be an anchor"), "operators, indexers, events and delegates are named as non-anchors");
            Assert.That(result.ErrorData?.Message, Does.Contain("Member("), "the message must give the next listing call");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found anchor must not write");
    }

    [Test]
    public async Task Member_AddWithTypoAnchor_SuggestsSimilarMemberAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test typo insert anchor candidate outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Added() => 3;",
            insertAfterMemberName: "Existng");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a typo anchor must be an error, not an append");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("'Existng'"), "the message must name the anchor");
            Assert.That(result.ErrorData?.Message, Does.Contain("Did you mean 'Existing'"), "the similar member must be suggested");
            Assert.That(result.ErrorData?.Message, Does.Contain("insertAfterMemberName"), "the suggestion must name the parameter to retry with");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found anchor must not write");
    }

    [Test]
    public async Task Member_AddWithAnchorWithNoSimilarMember_SaysNoCandidateAndTellsUserAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test no-candidate insert anchor outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Added() => 3;",
            insertAfterMemberName: "Archive");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an anchor with no similar member must be an error, not an append");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("has a similar name to 'Archive'"), "the message must say no similar member exists");
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("Did you mean"), "no suggestion when nothing is similar");
            Assert.That(result.ErrorData?.Message, Does.Contain("tell the user"), "the message must offer the report-it-does-not-exist option");
            Assert.That(result.ErrorData?.Message, Does.Contain("Member("), "the message must give the next listing call");
            Assert.That(result.ErrorData?.Message, Does.Not.Contain("You MUST"), "the message must not use mandatory wording");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found anchor must not write");
    }

    [Test]
    public async Task Member_AddWithTypoContainer_SuggestsTypeAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test typo container candidate outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTargt",
            newMemberSource: "public int Added() => 3;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a typo container must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("Did you mean 'AnchorTarget'"), "the similar type must be suggested");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found container must not write");
    }

    [Test]
    public async Task Member_AddWithUnknownContainer_ListsDeclaredTypesAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test unknown container with no similar type outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "ShippingLabelPrinter",
            newMemberSource: "public int Added() => 3;");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an unknown container must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("No type declared in this file has a similar name"), "the message must say no similar type exists");
            Assert.That(result.ErrorData?.Message, Does.Contain("GetFileOutline"), "the message must give the listing call");
            Assert.That(result.ErrorData?.Message, Does.Contain("tell the user"), "the message must offer the report-it-does-not-exist option");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found container must not write");
    }

    [Test]
    public async Task Member_AddWithPrefixedAnchorValue_TreatsValueAsLiteralNameAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test prefixed anchor value outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Added() => 3;",
            insertAfterMemberName: "after:Existing");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "a prefixed value is a literal name and must not resolve to Existing");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("'after:Existing'"), "the message must quote the value as the literal name");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found anchor must not write");
    }

    [Test]
    public async Task Member_AddWithOverloadedAnchor_ReturnsAmbiguousListingBothSignaturesAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test overloaded insert anchor outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Added() => 3;",
            insertAfterMemberName: "Save");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an overloaded anchor must be an error");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
            Assert.That(result.ErrorData?.Message, Does.Contain("matches 2 members"));
            Assert.That(result.ErrorData?.Message, Does.Contain("Save(int value)"), "the first overload must be listed");
            Assert.That(result.ErrorData?.Message, Does.Contain("Save(string value)"), "the second overload must be listed");
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "an ambiguous anchor must not write");
    }

    [Test]
    public async Task Member_AddToEnumWithUnknownAnchor_ReturnsNotFoundAndWritesNothingAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);
        var before = workspace.ReadText(FixtureRelativePath);

        var result = await tools.MemberTool(
            reason: "test enum container unknown anchor outcome",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "OrderState",
            newMemberSource: "Cancelled",
            insertAfterMemberName: "Missing");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsError, Is.True, "an unknown enum anchor must be an error, not an append");
            Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
            Assert.That(result.ErrorData?.Message, Does.Contain("not found in enum"));
            Assert.That(result.ErrorData?.Message, Does.Contain("'OrderState'"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "a not-found enum anchor must not write");
    }

    [Test]
    public async Task Member_AddWithInsertAfterAnchor_PlacesMemberAfterAnchorAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test insertAfterMemberName happy path",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int After() => 4;",
            insertAfterMemberName: "Existing");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText(FixtureRelativePath);
        Assert.Multiple(() =>
        {
            Assert.That(text.IndexOf("public int After()", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("public int Existing()", StringComparison.Ordinal)));
            Assert.That(text.IndexOf("public int After()", StringComparison.Ordinal), Is.LessThan(text.IndexOf("public int Save(int value)", StringComparison.Ordinal)));
        });
    }

    [Test]
    public async Task Member_AddWithInsertBeforeAnchor_PlacesMemberBeforeAnchorAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test insertBeforeMemberName happy path",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Before() => 5;",
            insertBeforeMemberName: "Existing");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText(FixtureRelativePath);
        Assert.That(text.IndexOf("public int Before()", StringComparison.Ordinal), Is.LessThan(text.IndexOf("public int Existing()", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Member_AddWithNoAnchor_AppendsToEndOfContainerAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var result = await tools.MemberTool(
            reason: "test addMember without anchors appends",
            operation: MemberAction.addMember,
            filePath: workspace.PathOf(FixtureRelativePath),
            containerName: "AnchorTarget",
            newMemberSource: "public int Tail() => 6;");

        Assert.That(result.IsError, Is.False, result.ErrorData?.Message);
        var text = workspace.ReadText(FixtureRelativePath);
        Assert.That(text.IndexOf("public int Tail()", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("operator +", StringComparison.Ordinal)));
    }
}
