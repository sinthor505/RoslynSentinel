using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Basic;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveMemberOverloadSelectionTests
{
    private static string SaveFixture() => """
        #nullable enable
        public static class Source
        {
            public static int Save(int a) => a;
            public static int Save(string s) => s.Length;
            public static int Save(ref int a) => a;
            public static int Save(System.Collections.Generic.List<string> items) => items.Count;
            public static int Save<T>(T value) => 0;
            public static int Save(string? s, params int[] values) => values.Length;
        }

        public static class Target
        {
        }
        """.ReplaceLineEndings("\r\n");

    private static string SourcePart(string text) => text[..text.IndexOf("public static class Target", StringComparison.Ordinal)];

    private static string TargetPart(string text) => text[text.IndexOf("public static class Target", StringComparison.Ordinal)..];

    private static int CountOf(string text, string token) => (text.Length - text.Replace(token, string.Empty, StringComparison.Ordinal).Length) / token.Length;

    // The printed signatures sit between "in class 'Source': " and ". A bare name"; signatures never contain "; ".
    private static List<string> PrintedSignatures(string message)
    {
        const string marker = "in class 'Source': ";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = message.IndexOf(". A bare name", start, StringComparison.Ordinal);
        return message[start..end].Split("; ").ToList();
    }

    // The example signature the ambiguity message tells the caller to pass: the text in quotes after "for example ".
    private static string QuotedExample(string message)
    {
        const string marker = "for example \"";
        var start = message.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return message[start..message.IndexOf('"', start)];
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_BareNameWithTwoOverloads_ReturnsAmbiguousListingSignaturesAndWritesNothing()
    {
        var original = SaveFixture();
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(int)"));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(string)"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_AmbiguousError_PrintedExampleSignatureRoundTrips()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", SaveFixture()));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var refusal = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save"], "Target");
        Assert.That(refusal.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        var message = refusal.ErrorData?.Message ?? string.Empty;
        var example = QuotedExample(message);
        Assert.That(PrintedSignatures(message), Does.Contain(example));
        var moved = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", [example], "Target");
        Assert.That(moved.IsError, Is.False, $"error: {moved.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(CountOf(TargetPart(text), "Save"), Is.EqualTo(1), "Exactly one overload should move into Target.");
        Assert.That(CountOf(SourcePart(text), "Save"), Is.EqualTo(5), "The other five overloads should stay in Source.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_EveryPrintedSignature_RoundTripsAndMovesExactlyOneOverload()
    {
        using var probe = InMemoryWorkspace.Create(("Refactor.cs", SaveFixture()));
        var probeTools = Battery.Basic.TestTools.CoreRefactoringTools(probe.Manager);
        var refusal = await probeTools.MoveMemberTool(reason: "test message", probe.PathOf("Refactor.cs"), "Source", ["Save"], "Target");
        var signatures = PrintedSignatures(refusal.ErrorData?.Message ?? string.Empty);
        Assert.That(signatures, Has.Count.EqualTo(6));
        foreach (var signature in signatures)
        {
            using var workspace = InMemoryWorkspace.Create(("Refactor.cs", SaveFixture()));
            var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
            var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", [signature], "Target");
            Assert.That(result.IsError, Is.False, $"{signature}: {result.ErrorData?.Message}");
            var text = workspace.ReadText("Refactor.cs");
            Assert.That(CountOf(TargetPart(text), "Save"), Is.EqualTo(1), $"{signature} should move exactly one overload into Target.");
            Assert.That(CountOf(SourcePart(text), "Save"), Is.EqualTo(5), $"{signature} should leave five overloads in Source.");
        }
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_SignatureSelectsOneOverload_MovesOnlyThatOverload()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", SaveFixture()));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save(int)"], "Target");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(TargetPart(text), Does.Contain("Save(int a)"));
        Assert.That(TargetPart(text), Does.Not.Contain("Save(string s)"));
        Assert.That(SourcePart(text), Does.Contain("Save(string s)"));
        Assert.That(SourcePart(text), Does.Not.Contain("Save(int a)"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_AllSignaturesListed_MovesWholeGroup()
    {
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", SaveFixture()));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save(int)", "Save(string)"], "Target");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(TargetPart(text), Does.Contain("Save(int a)"));
        Assert.That(TargetPart(text), Does.Contain("Save(string s)"));
        Assert.That(SourcePart(text), Does.Not.Contain("Save(int a)"));
        Assert.That(SourcePart(text), Does.Not.Contain("Save(string s)"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_BareNameSingleMethod_StillMovesByName()
    {
        var original = "public static class Source\r\n{\r\n    public static int Only() => 1;\r\n    public static int Other() => 2;\r\n}\r\n\r\npublic static class Target\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Only"], "Target");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(TargetPart(text), Does.Contain("Only()"));
        Assert.That(SourcePart(text), Does.Not.Contain("Only()"));
        Assert.That(SourcePart(text), Does.Contain("Other()"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_BareNameFieldPropertyAndNestedType_StillMoveByName()
    {
        var original = "public static class Source\r\n{\r\n    public static int Count = 1;\r\n    public static int Prop => 2;\r\n    public static class Nested\r\n    {\r\n    }\r\n}\r\n\r\npublic static class Target\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Count", "Prop", "Nested"], "Target");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(TargetPart(text), Does.Contain("Count = 1"));
        Assert.That(TargetPart(text), Does.Contain("Prop =>"));
        Assert.That(TargetPart(text), Does.Contain("class Nested"));
        Assert.That(SourcePart(text), Does.Not.Contain("Count = 1"));
        Assert.That(SourcePart(text), Does.Not.Contain("Prop =>"));
        Assert.That(SourcePart(text), Does.Not.Contain("class Nested"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_SignatureMatchesNothing_ReturnsNotFoundListingSignaturesAndWritesNothing()
    {
        var original = SaveFixture();
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save(long)"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(long)"));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(int)"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_MixedValidAndUnmatchedSignature_ReturnsNotFoundAndMovesNothing()
    {
        var original = SaveFixture();
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save(int)", "Save(long)"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(result.ErrorData?.Message, Does.Contain("Save(long)"));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "The valid half must not move when the other half is unmatched.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_SignatureWhitespaceIsNormalized_SelectsSameOverloads()
    {
        var original = "using System.Collections.Generic;\r\n\r\npublic static class Source\r\n{\r\n    public static int Save(int a) => a;\r\n    public static int Lookup(Dictionary<string, int> map) => map.Count;\r\n}\r\n\r\npublic static class Target\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save( int )", "Lookup(Dictionary< string , int >)"], "Target");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Refactor.cs");
        Assert.That(TargetPart(text), Does.Contain("Save(int a)"));
        Assert.That(TargetPart(text), Does.Contain("Lookup(Dictionary<string, int> map)"));
        Assert.That(SourcePart(text), Does.Not.Contain("Save(int a)"));
        Assert.That(SourcePart(text), Does.Not.Contain("Lookup("));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_QualifiedTypeName_DoesNotMatchShortTypeAndWritesNothing()
    {
        var original = "public static class Source\r\n{\r\n    public static int Save(int a) => a;\r\n}\r\n\r\npublic static class Target\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save(System.Int32)"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_NullableAnnotationAsWritten_SelectsOnlyMatchingAnnotation()
    {
        var original = "#nullable enable\r\npublic static class Source\r\n{\r\n    public static int Save(string? s) => s?.Length ?? 0;\r\n}\r\n\r\npublic static class Target\r\n{\r\n}\r\n";
        using var nonNullWorkspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(nonNullWorkspace.Manager);
        var nonNull = await tools.MoveMemberTool(reason: "test message", nonNullWorkspace.PathOf("Refactor.cs"), "Source", ["Save(string)"], "Target");
        Assert.That(nonNull.IsError, Is.True);
        Assert.That(nonNull.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(nonNullWorkspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");

        using var nullableWorkspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var nullableTools = Battery.Basic.TestTools.CoreRefactoringTools(nullableWorkspace.Manager);
        var nullable = await nullableTools.MoveMemberTool(reason: "test message", nullableWorkspace.PathOf("Refactor.cs"), "Source", ["Save(string?)"], "Target");
        Assert.That(nullable.IsError, Is.False, $"error: {nullable.ErrorData?.Message}");
        Assert.That(TargetPart(nullableWorkspace.ReadText("Refactor.cs")), Does.Contain("Save(string? s)"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task MoveMember_BareAndSignatureOfSameMethodMixed_ReturnsAmbiguousAndWritesNothing()
    {
        var original = SaveFixture();
        using var workspace = InMemoryWorkspace.Create(("Refactor.cs", original));
        var tools = Battery.Basic.TestTools.CoreRefactoringTools(workspace.Manager);
        var result = await tools.MoveMemberTool(reason: "test message", workspace.PathOf("Refactor.cs"), "Source", ["Save", "Save(int)"], "Target");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(workspace.ReadText("Refactor.cs"), Is.EqualTo(original), "A refused call must not write.");
    }
}
