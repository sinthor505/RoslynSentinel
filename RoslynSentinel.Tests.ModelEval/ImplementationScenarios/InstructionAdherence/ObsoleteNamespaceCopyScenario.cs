using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common.AgentLoop;
using RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.InstructionAdherence;

/// <summary>
/// Byte-exact scenario: copy a file with a UTF-8 BOM and CRLF line endings into a new namespace, and mark every top-level type of
/// the original [System.Obsolete("Use ...")] below its XML doc comment, without adding a using and without disturbing the layout.
/// </summary>
internal static class ObsoleteNamespaceCopyScenario
{
    private const string CoreFolder = "ContosoOrders.Core/Evals/ObsoleteNamespaceCopy/";
    private const string TestFolder = "ContosoOrders.Tests/Evals/ObsoleteNamespaceCopy/";
    private const string LegacyPath = CoreFolder + "Legacy/Orders.cs";
    private const string ModernPath = CoreFolder + "Modern/Orders.cs";
    private const string ConsumerPath = CoreFolder + "Consumer.cs";
    private const string TestsPath = TestFolder + "OrdersTests.cs";
    private const string MigrationTestsPath = TestFolder + "ObsoleteMigrationTests.cs";

    private const string ModernNamespace = "ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern";

    // File.WriteAllText of a string that starts with U+FEFF writes the UTF-8 BOM bytes EF BB BF.
    private const string Bom = "﻿";

    private const string LegacyBody = """
namespace ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;

/// <summary>A customer order.</summary>
public sealed class Order
{
    public Order(string id, decimal total)
    {
        Id = id;
        Total = total;
    }

    public string Id { get; }

    public decimal Total { get; }
}

/// <summary>Order status.</summary>
public enum OrderStatus
{
    Open,
    Shipped,
    Cancelled,
}

/// <summary>Computes order totals.</summary>
public static class OrderMath
{
    /// <summary>Sums the totals of the given orders.</summary>
    public static decimal Sum(IEnumerable<Order> orders) => orders.Sum(order => order.Total);
}
""";

    private const string GoldenLegacyBody = """
namespace ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;

/// <summary>A customer order.</summary>
[System.Obsolete("Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.Order")]
public sealed class Order
{
    public Order(string id, decimal total)
    {
        Id = id;
        Total = total;
    }

    public string Id { get; }

    public decimal Total { get; }
}

/// <summary>Order status.</summary>
[System.Obsolete("Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.OrderStatus")]
public enum OrderStatus
{
    Open,
    Shipped,
    Cancelled,
}

/// <summary>Computes order totals.</summary>
[System.Obsolete("Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.OrderMath")]
public static class OrderMath
{
    /// <summary>Sums the totals of the given orders.</summary>
    public static decimal Sum(IEnumerable<Order> orders) => orders.Sum(order => order.Total);
}
""";

    private const string GoldenModernBody = """
namespace ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern;

/// <summary>A customer order.</summary>
public sealed class Order
{
    public Order(string id, decimal total)
    {
        Id = id;
        Total = total;
    }

    public string Id { get; }

    public decimal Total { get; }
}

/// <summary>Order status.</summary>
public enum OrderStatus
{
    Open,
    Shipped,
    Cancelled,
}

/// <summary>Computes order totals.</summary>
public static class OrderMath
{
    /// <summary>Sums the totals of the given orders.</summary>
    public static decimal Sum(IEnumerable<Order> orders) => orders.Sum(order => order.Total);
}
""";

    private const string ConsumerSource = """
using ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;

namespace ContosoOrders.Evals.ObsoleteNamespaceCopy;

/// <summary>Uses the legacy types.</summary>
public static class Consumer
{
    public static decimal Total(IEnumerable<Order> orders) => OrderMath.Sum(orders);

    public static OrderStatus Status(Order order) => order.Total > 0 ? OrderStatus.Open : OrderStatus.Cancelled;
}
""";

    private const string TestsSource = """
#pragma warning disable CS0618
using ContosoOrders.Evals.ObsoleteNamespaceCopy;
using ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;
using Xunit;

namespace ContosoOrders.Tests.Evals.ObsoleteNamespaceCopy;

public class OrdersTests
{
    [Fact]
    public void Consumer_SumsOrderTotals()
    {
        var orders = new[] { new Order("a", 2m), new Order("b", 3m) };
        Assert.Equal(5m, Consumer.Total(orders));
    }

    [Fact]
    public void Consumer_ReportsStatus()
    {
        Assert.Equal(OrderStatus.Open, Consumer.Status(new Order("a", 1m)));
        Assert.Equal(OrderStatus.Cancelled, Consumer.Status(new Order("b", 0m)));
    }
}
""";

    // Visible acceptance tests (Spec S14). Reflection by name, so the seed (no Modern namespace yet) builds with 0 errors and these are red until the work is done.
    private const string MigrationTestsSource = """
#pragma warning disable CS0618
using System.Reflection;
using ContosoOrders.Evals.ObsoleteNamespaceCopy;
using Xunit;
using Legacy = ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;

namespace ContosoOrders.Tests.Evals.ObsoleteNamespaceCopy;

public class ObsoleteMigrationTests
{
    private const string ModernNamespace = "ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern";
    private const string Prefix = "Use " + ModernNamespace + ".";

    private static readonly Assembly Core = typeof(Consumer).Assembly;

    private static Type ModernType(string name)
    {
        var type = Core.GetType(ModernNamespace + "." + name);
        Assert.True(type is not null, $"The type {ModernNamespace}.{name} does not exist yet.");
        return type!;
    }

    [Theory]
    [InlineData(typeof(Legacy.Order), "Order")]
    [InlineData(typeof(Legacy.OrderStatus), "OrderStatus")]
    [InlineData(typeof(Legacy.OrderMath), "OrderMath")]
    public void LegacyTypesCarryObsoleteWithTheModernName(Type type, string name)
    {
        var attribute = type.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(Prefix + name, attribute!.Message);
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("OrderStatus")]
    [InlineData("OrderMath")]
    public void ModernTypesAreNotObsoleteAndLiveInTheModernNamespace(string name)
    {
        var type = ModernType(name);
        Assert.Null(type.GetCustomAttribute<ObsoleteAttribute>());
        Assert.Equal(ModernNamespace, type.Namespace);
    }

    [Fact]
    public void ModernCopyWorks()
    {
        var orderType = ModernType("Order");
        var orders = Array.CreateInstance(orderType, 2);
        orders.SetValue(Activator.CreateInstance(orderType, "a", 2m), 0);
        orders.SetValue(Activator.CreateInstance(orderType, "b", 3m), 1);

        var sum = ModernType("OrderMath").GetMethod("Sum")!.Invoke(null, new object[] { orders });
        Assert.Equal(5m, sum);
    }
}
""";

    private static string StepPrompt => EvalContractOptions.Render(new ScenarioTask(
        Goal: "In ContosoOrders.Core/Evals/ObsoleteNamespaceCopy the file Legacy/Orders.cs declares three top-level types in the namespace " +
            "`ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy`. Two files change. " +
            "1. New file Modern/Orders.cs (same folder tree): a copy of Legacy/Orders.cs in the namespace `ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern`; " +
            "it keeps the doc comments and CRLF line endings, has no byte order mark requirement and carries no Obsolete attribute. " +
            "2. Existing file Legacy/Orders.cs: every top-level type gets the attribute `[System.Obsolete(\"Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.<Type>\")]`, " +
            "where `<Type>` is that type's name, written fully qualified as `System.Obsolete` and placed below the type's XML doc comment. " +
            "The attribute points at the Modern type and goes on the Legacy type.",
        Files: "Legacy/Orders.cs and the new Modern/Orders.cs under ContosoOrders.Core/Evals/ObsoleteNamespaceCopy. No other file.",
        Conditions: "Legacy/Orders.cs keeps its UTF-8 byte order mark, its CRLF line endings and its doc comments exactly as they are. " +
            "Add no using directive. Consumer.cs and the tests are not edited.",
        IfFails: EvalContractOptions.PlainIfFails,
        DoneWhen: "The solution builds with 0 errors; all tests in ContosoOrders.Tests pass, including OrdersTests and ObsoleteMigrationTests."));

    public static ImplementationScenario Definition
    {
        get;
    } = new ImplementationScenario(
        "ObsoleteNamespaceCopy",
        "Copy a BOM + CRLF file into a new namespace and mark the original types Obsolete, byte-exactly",
        "adherence, byte-exact",
        "ModelEval-Scenario-Ideas 17, commit 98e77c6e (reframed for a tool user)",
        new Dictionary<string, string>
        {
            [LegacyPath] = Bom + LegacyBody,
            [ConsumerPath] = ConsumerSource,
            [TestsPath] = TestsSource,
            [MigrationTestsPath] = MigrationTestsSource,
        },
        new[]
        {
            new ImplementationScenarioStep(
                Prompt: StepPrompt,
                GoldenFiles: new Dictionary<string, string>
                {
                    [LegacyPath] = Bom + GoldenLegacyBody,
                    [ModernPath] = Bom + GoldenModernBody,
                },
                AllowedPaths: new List<string> { LegacyPath, ModernPath },
                HiddenTestSource: null,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckBytesAndAttributes(ctx)),
                CannedFinalMessage: CannedReport,
                Variants: BuildVariants(),
                DoneWhenTests: new[] { "OrdersTests", "ObsoleteMigrationTests" }),
        },
        PreferredHost: "113", // 112 truncated after 42 min (whole-file CreateFile turns are long); 113 passed after the cap
        PrimaryAxis: EvalAxis.InstructionAdherence); // 112 truncated after 42 min (whole-file CreateFile turns are long); 113 passed after the cap

    /// <summary>
    /// Byte checks stay (BOM on Legacy, zero or one BOM on Modern, uniform CRLF: the brief states them), then compiled checks: the
    /// consumer and tests are untouched; both files keep the seed's using directives; each top-level type of Legacy carries exactly
    /// one attribute list holding one attribute whose symbol is System.ObsoleteAttribute with the message "Use ...Modern.&lt;Type&gt;",
    /// between the doc comment and the declaration whatever the whitespace; Legacy without those attribute lists is equivalent to the
    /// seed; Modern is equivalent to the seed apart from the namespace; the doc comments of both equal the seed's.
    /// </summary>
    private static string? CheckBytesAndAttributes(ImplementationScenarioCheckContext ctx)
    {
        foreach (var path in new[] { ConsumerPath, TestsPath })
        {
            if (!ctx.IsUnchanged(path))
            {
                return $"changed but must stay as it was: {path}";
            }
        }

        foreach (var path in new[] { LegacyPath, ModernPath })
        {
            if (!ctx.Exists(path))
            {
                return $"{path} does not exist";
            }

            var bytes = ctx.ReadBytes(path);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (path == LegacyPath && !hasBom)
            {
                return $"{path} does not start with the UTF-8 BOM";
            }

            // The new file may carry zero or one BOM; a second BOM is rejected below.
            var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            if (text.Contains('﻿'))
            {
                return $"{path} contains a second BOM";
            }

            var lf = text.Count(c => c == '\n');
            var cr = text.Count(c => c == '\r');
            var crlf = Regex.Matches(text, "\r\n").Count;
            if (lf != crlf || cr != crlf)
            {
                return $"{path} does not use uniform CRLF line endings ({crlf} CRLF, {lf - crlf} bare LF, {cr - crlf} bare CR)";
            }
        }

        var compilation = ctx.Compile(CoreFolder.TrimEnd('/'));
        var legacyModel = compilation.ModelFor(LegacyPath);
        var legacyRoot = legacyModel.SyntaxTree.GetRoot();
        var modernRoot = compilation.ModelFor(ModernPath).SyntaxTree.GetRoot();
        var seedLegacyRoot = CSharpSyntaxTree.ParseText(ImplementationScenario.NormalizeToCrlf(LegacyBody)).GetRoot();
        var seedModernRoot = CSharpSyntaxTree.ParseText(
            ImplementationScenario.NormalizeToCrlf(LegacyBody.Replace("ObsoleteNamespaceCopy.Legacy;", "ObsoleteNamespaceCopy.Modern;"))).GetRoot();

        // The using directives of each file equal the seed's set (the seed declares none, so any added using, System or not, fails).
        var seedUsings = UsingDirectives(seedLegacyRoot);
        foreach (var (path, root) in new[] { (LegacyPath, legacyRoot), (ModernPath, modernRoot) })
        {
            var usings = UsingDirectives(root);
            if (!usings.SequenceEqual(seedUsings))
            {
                return $"{path}: the using directives differ from the original ([{string.Join(", ", usings)}] instead of [{string.Join(", ", seedUsings)}])";
            }
        }

        var legacyTypes = TopLevelTypes(legacyRoot);
        if (legacyTypes.Count != 3)
        {
            return $"Legacy/Orders.cs has {legacyTypes.Count} top-level types, expected 3";
        }

        foreach (var type in legacyTypes)
        {
            var name = type.Identifier.ValueText;
            if (type.AttributeLists.Count != 1 || type.AttributeLists[0].Attributes.Count != 1)
            {
                return $"{name} must carry exactly one attribute list with one attribute (it has {type.AttributeLists.Count} attribute list(s))";
            }

            var attribute = type.AttributeLists[0].Attributes[0];
            if (legacyModel.GetSymbolInfo(attribute).Symbol is not IMethodSymbol { ContainingType: { } attributeType }
                || attributeType.ToDisplayString() != "System.ObsoleteAttribute")
            {
                return $"{name}: the attribute must resolve to System.ObsoleteAttribute, found {attribute.Name}";
            }

            var expected = $"Use {ModernNamespace}.{name}";
            var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
            if (argument is null || legacyModel.GetConstantValue(argument.Expression).Value as string != expected)
            {
                return $"{name}: the Obsolete message must be exactly \"{expected}\"";
            }

            // The doc comment is the leading trivia of the attribute list's first token when the attribute sits below it, on its own line or not.
            if (!type.AttributeLists[0].GetLeadingTrivia().Any(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)))
            {
                return $"{name}: the XML doc comment must stay above the Obsolete attribute";
            }
        }

        // Legacy without the attribute lists is the seed; Modern is the seed with the other namespace.
        var legacyWithoutAttributes = legacyRoot.RemoveNodes(legacyTypes.SelectMany(type => type.AttributeLists), SyntaxRemoveOptions.KeepNoTrivia);
        if (legacyWithoutAttributes is null || !legacyWithoutAttributes.IsEquivalentTo(seedLegacyRoot))
        {
            return "Legacy/Orders.cs differs from the original in more than the added [System.Obsolete] attribute lists";
        }

        if (!modernRoot.IsEquivalentTo(seedModernRoot))
        {
            return "Modern/Orders.cs is not a copy of the original apart from the namespace";
        }

        var seedDocComments = DocComments(seedLegacyRoot);
        if (!DocComments(legacyRoot).SequenceEqual(seedDocComments) || !DocComments(modernRoot).SequenceEqual(seedDocComments))
        {
            return "a doc comment in Legacy/Orders.cs or Modern/Orders.cs differs from the original";
        }

        return null;
    }

    private static List<string> UsingDirectives(SyntaxNode root) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(directive => directive.ToString()).Order(StringComparer.Ordinal).ToList();

    private static List<BaseTypeDeclarationSyntax> TopLevelTypes(SyntaxNode root) =>
        root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(type => type.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .ToList();

    // Doc comment texts in file order (the trailing line break trimmed); the same call on the seed gives the expected list.
    private static List<string> DocComments(SyntaxNode root) =>
        root.DescendantTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(trivia => trivia.ToString().Trim())
            .ToList();

    private const string CannedReport = "DONE: Modern/Orders.cs is the copy in the Modern namespace; the three Legacy types carry [System.Obsolete] pointing at the Modern types.";

    /// <summary>
    /// The executable outcome table (Spec S14): each variant is a one-file overlay of Legacy/Orders.cs (the golden plus one edit) on
    /// step 1 (golden) with a DONE: final message; every non-passing row claims DONE, so its class is SilentIncomplete. The overlay
    /// writer writes the text it is given (a leading U+FEFF becomes the BOM bytes), so BomDropped is an overlay without the BOM.
    /// </summary>
    private static IReadOnlyList<ScenarioVariant> BuildVariants()
    {
        // Edits are written with \n; the template's line endings are kept. Throws when a template no longer contains the text to
        // edit, so a template change cannot silently turn a variant into the golden.
        static string Edited(string text, string oldText, string newText)
        {
            var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!normalized.Contains(oldText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"ObsoleteNamespaceCopy variant edit: '{oldText}' is not in the template.");
            }

            return normalized.Replace(oldText, newText, StringComparison.Ordinal).Replace("\n", eol, StringComparison.Ordinal);
        }

        static IReadOnlyDictionary<string, string> Overlay(string legacy) =>
            new Dictionary<string, string> { [LegacyPath] = legacy };

        const string NamespaceLine = "namespace ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;\n";

        // The attribute on the declaration line (below the doc comment, no line break before the declaration).
        var attributeSameLine = Edited(
            Edited(
                Edited(
                    GoldenLegacyBody,
                    "Modern.Order\")]\npublic sealed class Order",
                    "Modern.Order\")] public sealed class Order"),
                "Modern.OrderStatus\")]\npublic enum OrderStatus",
                "Modern.OrderStatus\")] public enum OrderStatus"),
            "Modern.OrderMath\")]\npublic static class OrderMath",
            "Modern.OrderMath\")] public static class OrderMath");

        // An extra using that the brief forbids.
        var extraUsing = Edited(GoldenLegacyBody, NamespaceLine, "using System.Linq;\n\n" + NamespaceLine);

        // The other way to satisfy the brief's attribute: add using System and write [Obsolete] instead of [System.Obsolete].
        var obsoleteWithUsingSystem = Edited(
            Edited(GoldenLegacyBody, "[System.Obsolete(", "[Obsolete("),
            NamespaceLine,
            "using System;\n\n" + NamespaceLine);

        return new[]
        {
            new ScenarioVariant(
                Name: "AttributeSameLine",
                BaseStep: 1,
                Files: Overlay(Bom + attributeSameLine),
                FinalMessage: CannedReport,
                ExpectedClass: EvalOutcomeClass.CorrectDone,
                ExpectedOutcome: ImplementationScenarioOutcome.Pass),
            new ScenarioVariant(
                Name: "ExtraUsing",
                BaseStep: 1,
                Files: Overlay(Bom + extraUsing),
                FinalMessage: CannedReport,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "the using directives differ"),
            new ScenarioVariant(
                Name: "BomDropped",
                BaseStep: 1,
                Files: Overlay(GoldenLegacyBody),
                FinalMessage: CannedReport,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "does not start with the UTF-8 BOM"),
            new ScenarioVariant(
                Name: "ObsoleteWithUsingSystem",
                BaseStep: 1,
                Files: Overlay(Bom + obsoleteWithUsingSystem),
                FinalMessage: CannedReport,
                ExpectedClass: EvalOutcomeClass.SilentIncomplete,
                ExpectedOutcome: ImplementationScenarioOutcome.CheckFailed,
                DetailContains: "the using directives differ"),
        };
    }
}
