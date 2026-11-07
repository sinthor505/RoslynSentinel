namespace RoslynSentinel.Tests.Basic;

public class AgentIdentityHookTransformTests
{
    [Test]
    public void SubagentInput_CarriesAgentIdAndAgentType()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"agent_id\":\"sub-7\",\"agent_type\":\"implementer\",\"tool_input\":{\"path\":\"a.cs\"}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.That(updated.GetProperty("_agentId").GetString(), Is.EqualTo("sub-7"));
        Assert.That(updated.GetProperty("_agentType").GetString(), Is.EqualTo("implementer"));
    }

    [Test]
    public void MainSessionInput_WithoutAgentId_IsMainAndOmitsAgentType()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"tool_input\":{\"path\":\"a.cs\"}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.That(updated.GetProperty("_agentId").GetString(), Is.EqualTo("main"));
        Assert.That(updated.TryGetProperty("_agentType", out _), Is.False);
    }

    [Test]
    public void ToolInputProperties_SurviveInOriginalOrder()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"agent_id\":\"sub-2\",\"tool_input\":{\"text\":\"h\\u00e9llo \\\"q\\\"\\nline\",\"nested\":{\"a\":1},\"list\":[1,\"two\",true],\"num\":42.5,\"flag\":true,\"nothing\":null}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        string[] names = updated.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "text", "nested", "list", "num", "flag", "nothing", "_agentId" }));
        Assert.That(updated.GetProperty("text").GetString(), Is.EqualTo("h\u00e9llo \"q\"\nline"));
        Assert.That(updated.GetProperty("nested").GetRawText(), Is.EqualTo("{\"a\":1}"));
        Assert.That(updated.GetProperty("list").GetRawText(), Is.EqualTo("[1,\"two\",true]"));
        Assert.That(updated.GetProperty("num").GetDouble(), Is.EqualTo(42.5));
        Assert.That(updated.GetProperty("flag").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.True));
        Assert.That(updated.GetProperty("nothing").ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
    }

    [Test]
    public void ModelSuppliedIdentityFields_AreReplacedAndAppearOnce()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"agent_id\":\"sub-1\",\"tool_input\":{\"_agentId\":\"forged\",\"x\":1,\"_agentType\":\"fake\"}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        string[] names = updated.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "x", "_agentId" }));
        Assert.That(updated.GetProperty("_agentId").GetString(), Is.EqualTo("sub-1"));
    }

    [Test]
    public void Output_HasPreToolUseEventUnderHookSpecificOutput()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"tool_input\":{\"a\":1}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        Assert.That(document.RootElement.EnumerateObject().Select(property => property.Name).ToArray(), Is.EqualTo(new[] { "hookSpecificOutput" }));
        System.Text.Json.JsonElement hookOutput = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.That(hookOutput.EnumerateObject().Select(property => property.Name).ToArray(), Is.EqualTo(new[] { "hookEventName", "updatedInput" }));
        Assert.That(hookOutput.GetProperty("hookEventName").GetString(), Is.EqualTo("PreToolUse"));
    }

    [Test]
    public void PassThrough_ReturnsNullForUnusableInput()
    {
        string[] inputs = { "{not json", "", "[1,2]", "{\"tool_name\":\"x\"}", "{\"tool_input\":\"text\"}" };

        foreach (string json in inputs)
        {
            byte[]? result = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(System.Text.Encoding.UTF8.GetBytes(json));
            Assert.That(result, Is.Null, json);
        }
    }

    [Test]
    public void SessionId_IsWrittenToUpdatedInput()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"session_id\":\"s1\",\"tool_input\":{\"path\":\"a.cs\"}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.That(updated.GetProperty("_sessionId").GetString(), Is.EqualTo("s1"));
    }

    [Test]
    public void MissingSessionId_OmitsKey()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"tool_input\":{\"path\":\"a.cs\"}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        Assert.That(updated.TryGetProperty("_sessionId", out _), Is.False);
    }

    [Test]
    public void ModelSuppliedSessionId_IsReplaced()
    {
        byte[] input = System.Text.Encoding.UTF8.GetBytes("{\"session_id\":\"s1\",\"tool_input\":{\"_sessionId\":\"fake\",\"x\":1}}");

        byte[]? output = RoslynSentinel.Utilities.AgentIdentityHook.AgentIdentityHookTransform.Transform(input);

        Assert.That(output, Is.Not.Null);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(output!);
        System.Text.Json.JsonElement updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedInput");
        string[] names = updated.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.That(names, Is.EqualTo(new[] { "x", "_agentId", "_sessionId" }));
        Assert.That(updated.GetProperty("_sessionId").GetString(), Is.EqualTo("s1"));
    }
}
