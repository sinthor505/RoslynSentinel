using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Common;
using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Wiring coverage for the edit effect note: the lookup-term map for symbol tools with no file path, and a not-found error
/// after a recorded rename gaining the EditEffect finding through both the marshaller (returned errors) and the failure
/// handler (thrown errors).
/// </summary>
[TestFixture]
public class EditEffectNoteWiringTests
{
    private static readonly MethodInfo AnyMethod = typeof(EditEffectNoteWiringTests).GetMethod(nameof(AnyMethodTarget), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AnyMethodTarget()
    {
    }

    [TearDown]
    public void TearDown()
    {
        ToolCallContext.Set(null);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static Dictionary<string, JsonElement> Args(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => Json(p.Value));

    // A rename of Foo.Bar to Foo.Baz recorded by session s1 / agent a1, committed to the returned index as the write chokepoint would.
    private static EditEffectIndex IndexWithRename()
    {
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("a1", null, "RenameSymbol", "rename", 0) { SessionId = "s1" };
        writer.AssignToolCallId("a76e9f1c");
        writer.RecordEffect(EditEffectKind.Renamed, "Foo.Bar", "M:Foo.Bar", "Bar", "Baz", null, "Foo.cs");
        writer.CommitPendingEffects(index);
        return index;
    }

    private static void EnterAs(string tool, EditEffectIndex index, params (string Name, string Value)[] pairs)
    {
        var arguments = Args([.. pairs, (AgentIdentityCapture.AgentIdKey, "a1"), (AgentIdentityCapture.SessionIdKey, "s1")]);
        AgentIdentityCapture.Enter(tool, arguments, () => 0, () => "C:\\root", () => index, () => null);
    }

    private static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [TestCase("LocateSymbol", "symbolName", "Bar")]
    [TestCase("RenameSymbol", "docCommentId", "M:Foo.Bar")]
    [TestCase("MoveMember", "className", "Foo")]
    public void Enter_SymbolTool_SetsTermAndIndex_ButNoTargetFile(string tool, string argument, string value)
    {
        var index = new EditEffectIndex();

        // filePath is passed on purpose: the evidence text note must still see no target file for these tools.
        EnterAs(tool, index, (argument, value), ("filePath", "Foo.cs"));

        var context = ToolCallContext.Current!;
        Assert.That(context.MissingTerm, Is.EqualTo(value));
        Assert.That(context.TargetFilePath, Is.Null);
        Assert.That(context.SolutionRoot, Is.EqualTo("C:\\root"));
        Assert.That(context.EditEffects, Is.SameAs(index));
        // Compared as a bool: Assert.That(Func<...>, constraint) would call the accessor and test its result (null here).
        Assert.That(context.CurrentSolution is not null, Is.True);
    }

    [Test]
    public void Enter_SearchSymbolMode_SetsTerm_OtherModesDoNot()
    {
        var index = new EditEffectIndex();

        EnterAs("Search", index, ("mode", "symbol"), ("query", "Bar"));
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Bar"));

        EnterAs("Search", index, ("mode", "text"), ("query", "Bar"));
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.Null);
        Assert.That(ToolCallContext.Current.EditEffects, Is.Null);
    }

    [Test]
    public void Enter_InspectSymbol_TermIsTheSnippetIdentifierWithAnEffect()
    {
        EnterAs("InspectSymbol", IndexWithRename(), ("filePath", "Foo.cs"), ("contextSnippet", "public int Bar(int x)"));

        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Bar"));
        Assert.That(ToolCallContext.Current.TargetFilePath, Is.EqualTo("Foo.cs"));
    }

    [Test]
    public void Enter_InspectSymbol_WithNoEffectForAnyIdentifier_HasNoTerm()
    {
        EnterAs("InspectSymbol", IndexWithRename(), ("filePath", "Foo.cs"), ("contextSnippet", "public int Quux(int x)"));

        Assert.That(ToolCallContext.Current!.MissingTerm, Is.Null);
        Assert.That(ToolCallContext.Current.TargetFilePath, Is.EqualTo("Foo.cs"));
    }

    [Test]
    public void Enter_FileTool_StillMapsFileAndTerm_AndGetsTheIndex()
    {
        var index = new EditEffectIndex();

        EnterAs("GetMethodSource", index, ("filePath", "Foo.cs"), ("methodName", "Bar"));

        var context = ToolCallContext.Current!;
        Assert.That(context.TargetFilePath, Is.EqualTo("Foo.cs"));
        Assert.That(context.MissingTerm, Is.EqualTo("Bar"));
        Assert.That(context.EditEffects, Is.SameAs(index));
    }

    [Test]
    public void Enter_GetFileOutline_SetsTargetFileAndIndex_ButNoTerm()
    {
        var index = new EditEffectIndex();

        EnterAs("GetFileOutline", index, ("filePath", "Foo.cs"));

        var context = ToolCallContext.Current!;
        Assert.That(context.TargetFilePath, Is.EqualTo("Foo.cs"));
        Assert.That(context.MissingTerm, Is.Null);
        Assert.That(context.EditEffects, Is.SameAs(index));
    }

    [Test]
    public void Enter_ReadFileWithoutMemberName_SetsTargetFileAndIndex()
    {
        var index = new EditEffectIndex();

        EnterAs("ReadFile", index, ("filePath", "Foo.cs"));

        var context = ToolCallContext.Current!;
        Assert.That(context.TargetFilePath, Is.EqualTo("Foo.cs"));
        Assert.That(context.MissingTerm, Is.Null);
        Assert.That(context.EditEffects, Is.SameAs(index));
    }

    // A MoveMember call as the filter sees it: className plus a memberNames array (the array is not a string, so Args cannot build it).
    private static void EnterMoveMember(EditEffectIndex? index, string className, string[] memberNames)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["className"] = Json(className),
            ["memberNames"] = JsonDocument.Parse(JsonSerializer.Serialize(memberNames)).RootElement.Clone(),
            [AgentIdentityCapture.AgentIdKey] = Json("a1"),
            [AgentIdentityCapture.SessionIdKey] = Json("s1"),
        };
        AgentIdentityCapture.Enter("MoveMember", arguments, () => 0, () => "C:\\root", () => index, () => null);
    }

    // A MoveMember of Foo.Bar into Target by session s1 / agent a1, committed to the returned index.
    private static EditEffectIndex IndexWithMove(string session = "s1")
    {
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("a1", null, "MoveMember", "move", 0) { SessionId = session };
        writer.AssignToolCallId("b17c0d2e");
        writer.RecordEffect(EditEffectKind.Moved, "Foo.Bar", null, "Bar", null, "Target", "Target.cs");
        writer.CommitPendingEffects(index);
        return index;
    }

    [Test]
    public void Enter_MoveMember_TermIsTheMemberWithAnEffect_QualifiedByClassName()
    {
        EnterMoveMember(IndexWithMove(), "Foo", ["Zed", "Bar"]);

        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Foo.Bar"));
        Assert.That(ToolCallContext.Current.TargetFilePath, Is.Null);
    }

    [Test]
    public void Enter_MoveMember_NoMatchingEffect_UsesTheFirstMember_AndAnotherSessionsEffectDoesNotCount()
    {
        EnterMoveMember(new EditEffectIndex(), "Foo", ["Zed", "Bar"]);
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Foo.Zed"));

        EnterMoveMember(IndexWithMove(session: "s2"), "Foo", ["Zed", "Bar"]);
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Foo.Zed"));

        EnterMoveMember(null, "Foo", ["Zed", "Bar"]);
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Foo.Zed"));
    }

    [Test]
    public void Enter_MoveMember_OverloadSignatureAndRenamedClass_StillMatch()
    {
        // Member passed as a signature: the parameter list is dropped by the term parser.
        EnterMoveMember(IndexWithMove(), "Foo", ["Bar(int)"]);
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Foo.Bar(int)"));
        Assert.That(EditEffectEvidence.HasEffectFor(IndexWithMove(), ToolCallContext.Current.MissingTerm!, "s1", ServerInstanceIdentity.Id), Is.True);

        // No member has an effect but the class was renamed: className alone is the term.
        var index = new EditEffectIndex();
        var writer = new ToolCallContext("a1", null, "RenameSymbol", "rename", 0) { SessionId = "s1" };
        writer.RecordEffect(EditEffectKind.Renamed, "Foo", "T:Foo", "Foo", "Foo2", null, "Foo.cs");
        writer.CommitPendingEffects(index);
        EnterMoveMember(index, "Foo", ["Zed"]);
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Foo"));
    }

    [Test]
    public void FailureHandler_MoveMemberNotFoundAfterMove_CarriesMovedNote()
    {
        EnterMoveMember(IndexWithMove(), "Foo", ["Bar"]);

        CallToolResult result = CallToolFailureHandler.ToFailureResult(new ToolNotFoundException("no such member"), "MoveMember");

        JsonObject body = JsonNode.Parse(TextOf(result))!.AsObject();
        Assert.That(body["findings"]![0]!["source"]!.GetValue<string>(), Is.EqualTo("EditEffect"));
        Assert.That(body["findings"]![0]!["message"]!.GetValue<string>(), Does.EndWith("moved `Foo.Bar` to `Target` in `Target.cs`."));
    }

    [Test]
    public void Enter_IndexLookupThrows_KeepsIdentityAndTerm()
    {
        var arguments = Args(("symbolName", "Bar"), (AgentIdentityCapture.AgentIdKey, "a1"));

        Assert.DoesNotThrow(() => AgentIdentityCapture.Enter("LocateSymbol", arguments, () => 0, null, () => throw new InvalidOperationException("boom")));

        Assert.That(ToolCallContext.Current!.AgentId, Is.EqualTo("a1"));
        Assert.That(ToolCallContext.Current.MissingTerm, Is.EqualTo("Bar"));
        Assert.That(ToolCallContext.Current.EditEffects, Is.Null);
    }

    [Test]
    public async Task Marshaller_LocateSymbolNotFoundAfterRename_CarriesEditEffectFinding()
    {
        EnterAs("LocateSymbol", IndexWithRename(), ("symbolName", "Bar"));
        var marshaller = new ToolResultMarshaller(NullLogger<ToolResultMarshaller>.Instance);
        var result = new SentinelCallToolResult<object> { IsError = true, ErrorData = new ResultError("NotFound", "No symbol 'Bar'.") };

        object? marshalled = await marshaller.MarshalAsync(
            "LocateSymbol", AnyMethod, result, typeof(object), McpJsonUtilities.DefaultOptions, TestContext.CurrentContext.CancellationToken);

        JsonObject finding = JsonNode.Parse((string)marshalled!)!["findings"]![0]!.AsObject();
        Assert.That(finding["source"]!.GetValue<string>(), Is.EqualTo("EditEffect"));
        Assert.That(finding["message"]!.GetValue<string>(), Does.StartWith("`Bar` was not found. Call `a76e9f1c` (`RenameSymbol`, this agent, "));
        Assert.That(finding["message"]!.GetValue<string>(), Does.EndWith("renamed `Foo.Bar` to `Foo.Baz`."));
    }

    [Test]
    public void FailureHandler_ThrownNotFoundAfterRename_CarriesEditEffectFinding()
    {
        EnterAs("RenameSymbol", IndexWithRename(), ("docCommentId", "M:Foo.Bar"), ("projectName", "P"), ("newName", "Qux"));

        CallToolResult result = CallToolFailureHandler.ToFailureResult(new ToolNotFoundException("no such symbol"), "RenameSymbol");

        JsonObject body = JsonNode.Parse(TextOf(result))!.AsObject();
        Assert.That(body["findings"]![0]!["source"]!.GetValue<string>(), Is.EqualTo("EditEffect"));
        Assert.That(body["findings"]![0]!["message"]!.GetValue<string>(), Does.Contain("renamed `Foo.Bar` to `Foo.Baz`"));
        Assert.That(body["errorData"]!["errorCode"]!.GetValue<string>(), Is.EqualTo("NotFound"));
    }

    [Test]
    public void FailureHandler_OtherSessionOrOtherCode_AddsNoEffectFinding()
    {
        var arguments = Args(("symbolName", "Bar"), (AgentIdentityCapture.AgentIdKey, "a1"), (AgentIdentityCapture.SessionIdKey, "s2"));
        AgentIdentityCapture.Enter("LocateSymbol", arguments, () => 0, null, IndexWithRename);

        JsonObject otherSession = JsonNode.Parse(TextOf(CallToolFailureHandler.ToFailureResult(new ToolNotFoundException("no such symbol"), "LocateSymbol")))!.AsObject();
        Assert.That(otherSession["findings"]!.AsArray(), Is.Empty);

        EnterAs("LocateSymbol", IndexWithRename(), ("symbolName", "Bar"));
        JsonObject otherCode = JsonNode.Parse(TextOf(CallToolFailureHandler.ToFailureResult(new InvalidOperationException("boom"), "LocateSymbol")))!.AsObject();
        Assert.That(otherCode["findings"]!.AsArray(), Is.Empty);
    }
}
