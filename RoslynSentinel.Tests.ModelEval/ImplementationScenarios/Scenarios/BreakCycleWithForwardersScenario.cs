using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

/// <summary>
/// Make a two-file dependency one-way: ReplayPolicy uses four AgentClient members (one of them unqualified through
/// `using static`), and already has a property named DroppedTurns, so the moved member needs a different name. AgentClient keeps
/// one-expression forwarders with unchanged names and signatures.
/// </summary>
internal static class BreakCycleWithForwardersScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/BreakCycleWithForwarders/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/BreakCycleWithForwarders/";
    private const string PolicyPath = CoreFolder + "ReplayPolicy.cs";
    private const string ClientPath = CoreFolder + "AgentClient.cs";
    private const string ClientTestsPath = TestFolder + "ClientTests.cs";

    private const string SeedPolicySource = """
using static ContosoOrders.Evals.BreakCycleWithForwarders.AgentClient;

namespace ContosoOrders.Evals.BreakCycleWithForwarders;

/// <summary>Decides how many of the oldest assistant turns lose their reasoning text.</summary>
public sealed class ReplayPolicy
{
    private readonly int _window;
    private readonly int _assistantTurns;

    private ReplayPolicy(int window, int assistantTurns)
    {
        _window = window;
        _assistantTurns = assistantTurns;
    }

    /// <summary>Name of the environment variable that overrides the window.</summary>
    public static string VariableName => ReasoningWindowVariable;

    /// <summary>How many of the oldest assistant turns lose their reasoning text.</summary>
    public int DroppedTurns => AgentClient.ReasoningDroppedTurns(_assistantTurns, _window);

    public bool ReplaysReasoning(int turnIndex) => turnIndex >= DroppedTurns;

    public static ReplayPolicy FromEnvironment(string? variableText, int assistantTurns)
    {
        var window = AgentClient.ParseReasoningWindowOverride(variableText) ?? AgentClient.ReasoningReplayTurns;
        return new ReplayPolicy(window, assistantTurns);
    }
}
""";

    private const string GoldenPolicySource = """
namespace ContosoOrders.Evals.BreakCycleWithForwarders;

/// <summary>Decides how many of the oldest assistant turns lose their reasoning text.</summary>
public sealed class ReplayPolicy
{
    /// <summary>Number of most recent assistant turns that keep their reasoning text.</summary>
    public const int ReplayTurns = 10;

    /// <summary>Environment variable that overrides the replay window.</summary>
    public const string WindowVariable = "REPLAY_WINDOW";

    private readonly int _window;
    private readonly int _assistantTurns;

    private ReplayPolicy(int window, int assistantTurns)
    {
        _window = window;
        _assistantTurns = assistantTurns;
    }

    /// <summary>Name of the environment variable that overrides the window.</summary>
    public static string VariableName => WindowVariable;

    /// <summary>How many of the oldest assistant turns lose their reasoning text.</summary>
    public int DroppedTurns => DroppedTurnsForWindow(_assistantTurns, _window);

    public bool ReplaysReasoning(int turnIndex) => turnIndex >= DroppedTurns;

    public static ReplayPolicy FromEnvironment(string? variableText, int assistantTurns)
    {
        var window = ParseWindowOverride(variableText) ?? ReplayTurns;
        return new ReplayPolicy(window, assistantTurns);
    }

    /// <summary>Parses the override; null or blank text means no override.</summary>
    public static int? ParseWindowOverride(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, out var window) || window < 0)
        {
            throw new FormatException($"{WindowVariable} must be a non-negative integer, got '{text}'.");
        }

        return window;
    }

    /// <summary>Number of leading turns that lose their reasoning for a window size.</summary>
    public static int DroppedTurnsForWindow(int assistantTurns, int window) => Math.Max(0, assistantTurns - window);
}
""";

    private const string SeedClientSource = """
namespace ContosoOrders.Evals.BreakCycleWithForwarders;

/// <summary>Client of the agent endpoint; builds a replay policy for its conversation.</summary>
public sealed class AgentClient
{
    /// <summary>Number of most recent assistant turns that keep their reasoning text.</summary>
    public const int ReasoningReplayTurns = 10;

    /// <summary>Environment variable that overrides the replay window.</summary>
    public const string ReasoningWindowVariable = "REPLAY_WINDOW";

    public int AssistantTurns { get; set; }

    /// <summary>Parses the override; null or blank text means no override.</summary>
    public static int? ParseReasoningWindowOverride(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, out var window) || window < 0)
        {
            throw new FormatException($"{ReasoningWindowVariable} must be a non-negative integer, got '{text}'.");
        }

        return window;
    }

    /// <summary>Dropped turns for the default window.</summary>
    public static int ReasoningDroppedTurns(int assistantTurns) => ReasoningDroppedTurns(assistantTurns, ReasoningReplayTurns);

    /// <summary>Number of leading turns that lose their reasoning for a window size.</summary>
    public static int ReasoningDroppedTurns(int assistantTurns, int window) => Math.Max(0, assistantTurns - window);

    public ReplayPolicy CurrentPolicy(string? variableText) => ReplayPolicy.FromEnvironment(variableText, AssistantTurns);
}
""";

    private const string GoldenClientSource = """
namespace ContosoOrders.Evals.BreakCycleWithForwarders;

/// <summary>Client of the agent endpoint; builds a replay policy for its conversation.</summary>
public sealed class AgentClient
{
    /// <summary>Number of most recent assistant turns that keep their reasoning text.</summary>
    public const int ReasoningReplayTurns = ReplayPolicy.ReplayTurns;

    /// <summary>Environment variable that overrides the replay window.</summary>
    public const string ReasoningWindowVariable = ReplayPolicy.WindowVariable;

    public int AssistantTurns { get; set; }

    /// <summary>Parses the override; null or blank text means no override.</summary>
    public static int? ParseReasoningWindowOverride(string? text) => ReplayPolicy.ParseWindowOverride(text);

    /// <summary>Dropped turns for the default window.</summary>
    public static int ReasoningDroppedTurns(int assistantTurns) => ReasoningDroppedTurns(assistantTurns, ReasoningReplayTurns);

    /// <summary>Number of leading turns that lose their reasoning for a window size.</summary>
    public static int ReasoningDroppedTurns(int assistantTurns, int window) => ReplayPolicy.DroppedTurnsForWindow(assistantTurns, window);

    public ReplayPolicy CurrentPolicy(string? variableText) => ReplayPolicy.FromEnvironment(variableText, AssistantTurns);
}
""";

    private const string ClientTestsSource = """
using ContosoOrders.Evals.BreakCycleWithForwarders;
using Xunit;

namespace ContosoOrders.Tests.Evals.BreakCycleWithForwarders;

public class ClientTests
{
    [Fact]
    public void ReplayTurnsConstant_IsTen() => Assert.Equal(10, AgentClient.ReasoningReplayTurns);

    [Fact]
    public void WindowVariable_IsReplayWindow() => Assert.Equal("REPLAY_WINDOW", AgentClient.ReasoningWindowVariable);

    [Fact]
    public void Parse_BlankIsNullAndNumberIsParsed()
    {
        Assert.Null(AgentClient.ParseReasoningWindowOverride(null));
        Assert.Null(AgentClient.ParseReasoningWindowOverride("  "));
        Assert.Equal(3, AgentClient.ParseReasoningWindowOverride("3"));
    }

    [Fact]
    public void Parse_InvalidThrowsWithTheVariableName()
    {
        var ex = Assert.Throws<FormatException>(() => AgentClient.ParseReasoningWindowOverride("x"));
        Assert.Contains("REPLAY_WINDOW", ex.Message);
    }

    [Fact]
    public void DroppedTurns_OneAndTwoArguments()
    {
        Assert.Equal(2, AgentClient.ReasoningDroppedTurns(12));
        Assert.Equal(7, AgentClient.ReasoningDroppedTurns(10, 3));
        Assert.Equal(0, AgentClient.ReasoningDroppedTurns(2, 5));
    }

    [Fact]
    public void CurrentPolicy_UsesTheOverride()
    {
        var client = new AgentClient { AssistantTurns = 12 };
        Assert.Equal(2, client.CurrentPolicy(null).DroppedTurns);
        Assert.Equal(9, client.CurrentPolicy("3").DroppedTurns);
        Assert.False(client.CurrentPolicy(null).ReplaysReasoning(1));
        Assert.True(client.CurrentPolicy(null).ReplaysReasoning(2));
        Assert.Equal("REPLAY_WINDOW", ReplayPolicy.VariableName);
    }
}
""";

    private const string HiddenTestSource = """
using System.Reflection;
using ContosoOrders.Evals.BreakCycleWithForwarders;
using Xunit;

namespace ContosoOrders.Tests.Evals.BreakCycleWithForwarders;

public class BreakCycleWithForwardersStep1Tests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void ReplayPolicyOwnsTheDefinitions()
    {
        var literals = typeof(ReplayPolicy).GetFields(Declared).Where(field => field.IsLiteral).ToList();
        Assert.Contains(literals, field => field.FieldType == typeof(int) && (int)field.GetRawConstantValue()! == 10);
        Assert.Contains(literals, field => field.FieldType == typeof(string) && (string)field.GetRawConstantValue()! == "REPLAY_WINDOW");

        var methods = typeof(ReplayPolicy).GetMethods(Declared).ToList();
        Assert.Contains(methods, method => method.ReturnType == typeof(int?)
            && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string) }));
        Assert.Contains(methods, method => method.ReturnType == typeof(int)
            && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(int), typeof(int) }));
    }

    [Fact]
    public void AgentClientKeepsNamesAndSignatures()
    {
        Assert.NotNull(typeof(AgentClient).GetMethod("ParseReasoningWindowOverride", new[] { typeof(string) }));
        Assert.NotNull(typeof(AgentClient).GetMethod("ReasoningDroppedTurns", new[] { typeof(int), typeof(int) }));
        Assert.NotNull(typeof(AgentClient).GetMethod("ReasoningDroppedTurns", new[] { typeof(int) }));
        Assert.True(typeof(AgentClient).GetField("ReasoningReplayTurns")!.IsLiteral);
        Assert.True(typeof(AgentClient).GetField("ReasoningWindowVariable")!.IsLiteral);
    }

    [Fact]
    public void ReplayPolicyKeepsItsDroppedTurnsProperty()
    {
        var property = typeof(ReplayPolicy).GetProperty("DroppedTurns")!;
        Assert.Equal(typeof(int), property.PropertyType);
    }

    [Fact]
    public void BehaviourIsUnchanged()
    {
        var client = new AgentClient { AssistantTurns = 15 };
        Assert.Equal(5, client.CurrentPolicy(null).DroppedTurns);
        Assert.Equal(12, client.CurrentPolicy("3").DroppedTurns);
        Assert.Equal(4, AgentClient.ReasoningDroppedTurns(7, 3));
        Assert.Throws<FormatException>(() => AgentClient.ParseReasoningWindowOverride("-1"));
    }
}
""";

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/BreakCycleWithForwarders, ReplayPolicy.cs and AgentClient.cs use each other. Make the dependency
one-way: AgentClient may depend on ReplayPolicy, but ReplayPolicy must not mention AgentClient anywhere (not even in a using
directive). Find every AgentClient member that ReplayPolicy uses, move the definitions of those members into ReplayPolicy
(ReplayPolicy already has members of its own, so pick names that do not clash), and leave in AgentClient a one-line forwarder
for each moved member with the same name, signature and accessibility (a constant forwards to the constant). The overload that
only calls another AgentClient overload stays as it is. Do not edit the test file or any other file; the tests keep using the
AgentClient names and must pass unchanged, and behaviour must not change.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "BreakCycleWithForwarders",
        "Break a two-file reference cycle by moving definitions and leaving forwarders",
        "ambiguity, recovery",
        "ModelEval-Scenario-Ideas 12, commit 945170d9",
        new Dictionary<string, string>
        {
            [PolicyPath] = SeedPolicySource,
            [ClientPath] = SeedClientSource,
            [ClientTestsPath] = ClientTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [PolicyPath] = GoldenPolicySource,
                    [ClientPath] = GoldenClientSource,
                },
                AllowedPaths: new List<string> { PolicyPath, ClientPath },
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckCycleBroken(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "113");

    /// <summary>ReplayPolicy no longer names AgentClient; the definitions moved; AgentClient only forwards; the seed tests are untouched.</summary>
    private static string? CheckCycleBroken(ImplementationScenarioCheckContext ctx)
    {
        if (!ctx.IsUnchanged(ClientTestsPath))
        {
            return "ClientTests.cs changed but must stay as it was";
        }

        var policyRoot = ctx.Syntax(PolicyPath).GetRoot();
        var mentions = policyRoot.DescendantTokens()
            .Where(token => token.Kind() == SyntaxKind.IdentifierToken && token.ValueText == "AgentClient")
            .ToList();
        if (mentions.Count > 0)
        {
            var line = mentions[0].GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return $"ReplayPolicy.cs still mentions AgentClient ({mentions.Count} identifier(s), first on line {line})";
        }

        if (!policyRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.ValueText == "DroppedTurns"))
        {
            return "the DroppedTurns property of ReplayPolicy is gone";
        }

        if (!ctx.ReadText(PolicyPath).Contains("int.TryParse"))
        {
            return "the parsing code (int.TryParse) is not in ReplayPolicy.cs: the definition was not moved there";
        }

        if (ctx.ReadText(ClientPath).Contains("int.TryParse"))
        {
            return "AgentClient.cs still contains the parsing code; it should only forward";
        }

        var clientRoot = ctx.Syntax(ClientPath).GetRoot();
        foreach (var name in new[] { "ReasoningReplayTurns", "ReasoningWindowVariable" })
        {
            var variable = clientRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == name);
            if (variable?.Initializer is null || !variable.Initializer.Value.ToString().Contains("ReplayPolicy"))
            {
                return $"AgentClient.{name} must be a constant initialised from ReplayPolicy";
            }
        }

        var forwarders = clientRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "ParseReasoningWindowOverride"
                || (m.Identifier.ValueText == "ReasoningDroppedTurns" && m.ParameterList.Parameters.Count == 2))
            .ToList();
        if (forwarders.Count != 2)
        {
            return "AgentClient lost ParseReasoningWindowOverride or the two-argument ReasoningDroppedTurns";
        }

        foreach (var method in forwarders)
        {
            ExpressionSyntax? expression = method.ExpressionBody?.Expression;
            if (expression is null && method.Body is not null && method.Body.Statements.Count == 1 && method.Body.Statements[0] is ReturnStatementSyntax returned)
            {
                expression = returned.Expression;
            }

            if (expression is not InvocationExpressionSyntax || !expression.ToString().StartsWith("ReplayPolicy.", StringComparison.Ordinal))
            {
                return $"AgentClient.{method.Identifier.ValueText} must be a one-expression forwarder that calls ReplayPolicy";
            }
        }

        return null;
    }
}
