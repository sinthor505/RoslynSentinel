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
    public void Enter_SearchSymbolMode_SetsTerm_OtherModesAndInspectSymbolDoNot()
    {
        var index = new EditEffectIndex();

        EnterAs("Search", index, ("mode", "symbol"), ("query", "Bar"));
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.EqualTo("Bar"));

        EnterAs("Search", index, ("mode", "text"), ("query", "Bar"));
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.Null);
        Assert.That(ToolCallContext.Current.EditEffects, Is.Null);

        EnterAs("InspectSymbol", index, ("filePath", "Foo.cs"), ("contextSnippet", "public void Bar()"));
        Assert.That(ToolCallContext.Current!.MissingTerm, Is.Null);
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
