using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.AmbiguityResolution;

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
    private const string CycleBreakTestsPath = TestFolder + "CycleBreakTests.cs";

    private const string PolicyTypeName = "ContosoOrders.Evals.BreakCycleWithForwarders.ReplayPolicy";

    private const string ClientTypeName = "ContosoOrders.Evals.BreakCycleWithForwarders.AgentClient";

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

    private const string CycleBreakTestsSource = """
using System.Reflection;
using ContosoOrders.Evals.BreakCycleWithForwarders;
using Xunit;

namespace ContosoOrders.Tests.Evals.BreakCycleWithForwarders;

public class CycleBreakTests
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

    private const string CannedReport = "DONE: moved the parsing, the two constants and the two-argument drop count into ReplayPolicy; AgentClient forwards to them with its names unchanged and ReplayPolicy no longer uses AgentClient.";

    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/BreakCycleWithForwarders, ReplayPolicy.cs and AgentClient.cs use each other. " +
            "Make the dependency one-way: AgentClient may depend on ReplayPolicy, but ReplayPolicy must not depend on AgentClient. " +
            "Find every AgentClient member that ReplayPolicy uses, move the definitions of those members into ReplayPolicy " +
            "(it already has members of its own, so pick names that do not clash), and leave in AgentClient a one-line forwarder " +
            "for each moved member with the same name, signature and accessibility (a constant forwards to the constant). " +
            "The overload that only calls another AgentClient overload stays as it is.",
        Files: "ReplayPolicy.cs and AgentClient.cs in ContosoOrders.Core/Evals/BreakCycleWithForwarders. No test file, no other file.",
        Conditions: "Nothing in ReplayPolicy.cs (code, `<see cref>` or using directive) may resolve to a member of AgentClient. " +
            "The tests keep using the AgentClient names and pass unchanged; behaviour does not change.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including ClientTests and CycleBreakTests."));

    // The contract placement is read once per process, when this Definition initialises (StepPrompt renders then); one process runs one arm.
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
            [CycleBreakTestsPath] = CycleBreakTestsSource,
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
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckCycleBroken(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "ClientTests", "CycleBreakTests" }),
        },
        PreferredHost: "113",
        PrimaryAxis: EvalAxis.AmbiguityResolution);

    /// <summary>
    /// ReplayPolicy.cs has no name, cref or using directive that binds to AgentClient or one of its members (an unqualified use
    /// through `using static` binds too, so it fails; a plain comment naming AgentClient is invisible to the compiler and passes).
    /// Each AgentClient forwarder is one expression (or one return) whose symbol is a ReplayPolicy member, whatever the spelling
    /// (qualified, `using static` or alias); the two constants are initialised from a ReplayPolicy member. Behaviour, names and
    /// signatures are decided by ClientTests and CycleBreakTests, the scope by the shared check, not here.
    /// </summary>
    private static string? CheckCycleBroken(ImplementationScenarioCheckContext ctx)
    {
        var compilation = ctx.Compile(CoreFolder.TrimEnd('/'));
        var policy = compilation.Type(PolicyTypeName);
        var client = compilation.Type(ClientTypeName);
        if (policy is null || client is null)
        {
            return "ReplayPolicy or AgentClient is missing from ContosoOrders.Core/Evals/BreakCycleWithForwarders";
        }

        var policyModel = compilation.ModelFor(PolicyPath);
        var policyRoot = policyModel.SyntaxTree.GetRoot();
        foreach (var name in policyRoot.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (BindsToClient(policyModel.GetSymbolInfo(name), client))
            {
                return $"ReplayPolicy.cs still depends on AgentClient ({DescribeUse(name)})";
            }
        }

        // Crefs live in doc-comment trivia, so they are visited separately; the cref symbol is the member it names.
        foreach (var cref in policyRoot.DescendantNodes(descendIntoTrivia: true).OfType<CrefSyntax>())
        {
            if (BindsToClient(policyModel.GetSymbolInfo(cref), client))
            {
                return $"ReplayPolicy.cs still depends on AgentClient ({DescribeUse(cref)})";
            }
        }

        var clientModel = compilation.ModelFor(ClientPath);
        var clientRoot = clientModel.SyntaxTree.GetRoot();
        foreach (var constant in new[] { "ReasoningReplayTurns", "ReasoningWindowVariable" })
        {
            var variable = clientRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == constant);
            if (variable?.Initializer is null || !IsPolicyMember(clientModel.GetSymbolInfo(variable.Initializer.Value).Symbol, policy))
            {
                return $"AgentClient.{constant} must be a constant initialised from a ReplayPolicy member";
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
            if (expression is null && method.Body is { Statements: [ReturnStatementSyntax returned] })
            {
                expression = returned.Expression;
            }

            if (expression is null || !IsPolicyMember(clientModel.GetSymbolInfo(expression).Symbol, policy))
            {
                return $"AgentClient.{method.Identifier.ValueText} must be a one-expression forwarder whose call or member access is a ReplayPolicy member";
            }
        }

        return null;
    }

    // True when the binding (or any candidate of a failed overload resolution) is AgentClient itself, a member of it, or an alias of it.
    private static bool BindsToClient(SymbolInfo info, INamedTypeSymbol client) =>
        (info.Symbol is null ? info.CandidateSymbols : info.CandidateSymbols.Add(info.Symbol))
            .Any(symbol => IsClientSymbol(symbol is IAliasSymbol alias ? alias.Target : symbol, client));

    private static bool IsClientSymbol(ISymbol symbol, INamedTypeSymbol client) =>
        SymbolEqualityComparer.Default.Equals(symbol, client) || SymbolEqualityComparer.Default.Equals(symbol.ContainingType, client);

    private static bool IsPolicyMember(ISymbol? symbol, INamedTypeSymbol policy) =>
        symbol is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, policy);

    private static string DescribeUse(SyntaxNode node) =>
        $"'{node}' on line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

    /// <summary>
    /// The executable outcome table (Spec S12): each variant is an overlay of ReplayPolicy.cs and/or AgentClient.cs (the golden
    /// plus one edit) on step 1 (golden) with a DONE: final message. Every non-passing row claims DONE, so its class is SilentIncomplete.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Edits are written with \n; the template's line endings are kept. Throws when a template no longer contains the text to
        // edit, so a template change cannot silently turn a variant into the golden.
        static string Edited(string text, params (string OldText, string NewText)[] edits)
        {
            var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach (var (oldText, newText) in edits)
            {
                if (!normalized.Contains(oldText, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"BreakCycleWithForwarders variant edit: '{oldText}' is not in the template.");
                }

                normalized = normalized.Replace(oldText, newText, StringComparison.Ordinal);
            }

            return normalized.Replace("\n", eol, StringComparison.Ordinal);
        }

        const string Done = "DONE: ReplayPolicy no longer depends on AgentClient.";

        // AgentClient forwards through `using static ...ReplayPolicy;` instead of qualifying each call.
        var usingStaticClient = Edited(
            GoldenClientSource,
            ("namespace ContosoOrders.Evals.BreakCycleWithForwarders;\n", "using static ContosoOrders.Evals.BreakCycleWithForwarders.ReplayPolicy;\n\nnamespace ContosoOrders.Evals.BreakCycleWithForwarders;\n"),
            ("= ReplayPolicy.ReplayTurns;", "= ReplayTurns;"),
            ("= ReplayPolicy.WindowVariable;", "= WindowVariable;"),
            ("=> ReplayPolicy.ParseWindowOverride(text);", "=> ParseWindowOverride(text);"),
            ("=> ReplayPolicy.DroppedTurnsForWindow(assistantTurns, window);", "=> DroppedTurnsForWindow(assistantTurns, window);"));

        // The doc comment of ReplayPolicy points at AgentClient with a cref (it binds, so it is a dependency).
        var crefKept = Edited(
            GoldenPolicySource,
            ("/// <summary>Decides how many of the oldest assistant turns lose their reasoning text.</summary>\n",
             "/// <summary>Decides how many of the oldest assistant turns lose their reasoning text for <see cref=\"AgentClient\"/>.</summary>\n"));

        // A plain comment naming AgentClient: no symbol, so no dependency.
        var commentOnly = Edited(
            GoldenPolicySource,
            ("    private readonly int _window;\n", "    // AgentClient forwards to the members below.\n    private readonly int _window;\n"));

        return new[]
        {
            new ScenarioVariant(
                Name: "UsingStaticForwarder",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ClientPath] = usingStaticClient },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "CrefKept",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [PolicyPath] = crefKept },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "still depends on AgentClient"),
            // The definitions are in ReplayPolicy (golden) but AgentClient keeps its own copies (seed) instead of forwarding.
            new ScenarioVariant(
                Name: "DefinitionsCopied",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [ClientPath] = SeedClientSource },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "must be a constant initialised from a ReplayPolicy member"),
            new ScenarioVariant(
                Name: "CommentOnly",
                BaseStep: 1,
                Files: new Dictionary<string, string> { [PolicyPath] = commentOnly },
                FinalMessage: Done,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
        };
    }
}
