using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Scenarios;

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

    private const string HiddenTestSource = """
#pragma warning disable CS0618
using System.Reflection;
using Xunit;
using Legacy = ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy;
using Modern = ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern;

namespace ContosoOrders.Tests.Evals.ObsoleteNamespaceCopy;

public class ObsoleteNamespaceCopyStep1Tests
{
    private const string Prefix = "Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.";

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
    [InlineData(typeof(Modern.Order))]
    [InlineData(typeof(Modern.OrderStatus))]
    [InlineData(typeof(Modern.OrderMath))]
    public void ModernTypesAreNotObsoleteAndLiveInTheModernNamespace(Type type)
    {
        Assert.Null(type.GetCustomAttribute<ObsoleteAttribute>());
        Assert.Equal("ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern", type.Namespace);
    }

    [Fact]
    public void ModernCopyWorks()
    {
        var orders = new[] { new Modern.Order("a", 2m), new Modern.Order("b", 3m) };
        Assert.Equal(5m, Modern.OrderMath.Sum(orders));
    }
}
""";

    private const string StepPrompt = """
In ContosoOrders.Core/Evals/ObsoleteNamespaceCopy the file Legacy/Orders.cs declares three top-level types in the namespace
ContosoOrders.Evals.ObsoleteNamespaceCopy.Legacy. Create Modern/Orders.cs (same folder tree) as a copy of it in the namespace
ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern, and mark every top-level type of the legacy file with the attribute
[System.Obsolete("Use ContosoOrders.Evals.ObsoleteNamespaceCopy.Modern.<Type>")], where <Type> is that type's name; write the
attribute fully qualified as System.Obsolete and place it below the type's XML doc comment. Legacy/Orders.cs must keep its UTF-8 byte order mark, CRLF line endings and doc comments exactly as they are.
Modern/Orders.cs keeps CRLF line endings and the doc comments; it has no byte order mark requirement. Do not add a using
directive, do not edit Consumer.cs or any test, and leave the copy without any Obsolete attribute.
""";

    public static ImplementationScenario Definition { get; } = new ImplementationScenario(
        "ObsoleteNamespaceCopy",
        "Copy a BOM + CRLF file into a new namespace and mark the original types Obsolete, byte-exactly",
        "adherence, byte-exact",
        "ModelEval-Scenario-Ideas 17, commit 98e77c6e (reframed for a tool user)",
        new Dictionary<string, string>
        {
            [LegacyPath] = Bom + LegacyBody,
            [ConsumerPath] = ConsumerSource,
            [TestsPath] = TestsSource,
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
                HiddenTestSource: HiddenTestSource,
                ExtraCheck: static ctx => Task.FromResult<string?>(CheckBytesAndAttributes(ctx)),
                CannedFinalMessage: null),
        },
        PreferredHost: "112");

    /// <summary>Byte checks (BOM on Legacy, zero or one on Modern, uniform CRLF, no added using), attribute placement and text, copy fidelity, untouched consumer and tests.</summary>
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

            if (Regex.IsMatch(text, @"^\s*using\s+System\s*;", RegexOptions.Multiline))
            {
                return $"{path} gained a 'using System;'";
            }
        }

        var legacyText = ctx.ReadText(LegacyPath);
        var modernText = ctx.ReadText(ModernPath);
        if (modernText.StartsWith('﻿'))
        {
            modernText = modernText.Substring(1);
        }
        var seedBody = ImplementationScenario.NormalizeToCrlf(LegacyBody).TrimEnd();

        // Removing the three attribute lines from the legacy file must give back the original text.
        var withoutAttributes = Regex.Replace(legacyText, @"^\[System\.Obsolete\([^\r\n]*\)\]\r\n", string.Empty, RegexOptions.Multiline).TrimEnd();
        if (withoutAttributes != seedBody)
        {
            return "Legacy/Orders.cs differs from the original in more than the added [System.Obsolete] lines";
        }

        var legacyRoot = ctx.Syntax(LegacyPath).GetRoot();
        var legacyTypes = legacyRoot.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(type => type.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .ToList();
        if (legacyTypes.Count != 3)
        {
            return $"Legacy/Orders.cs has {legacyTypes.Count} top-level types, expected 3";
        }

        foreach (var type in legacyTypes)
        {
            var obsolete = type.AttributeLists.SelectMany(list => list.Attributes)
                .FirstOrDefault(attribute => attribute.Name.ToString().EndsWith("Obsolete", StringComparison.Ordinal));
            if (obsolete is null)
            {
                return $"{type.Identifier.ValueText} has no Obsolete attribute";
            }

            if (obsolete.Name.ToString() != "System.Obsolete")
            {
                return $"{type.Identifier.ValueText}: the attribute must be written as System.Obsolete, found {obsolete.Name}";
            }

            var expected = $"Use {ModernNamespace}.{type.Identifier.ValueText}";
            if (obsolete.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal || literal.Token.ValueText != expected)
            {
                return $"{type.Identifier.ValueText}: the Obsolete message must be exactly \"{expected}\"";
            }

            var docBeforeAttribute = type.AttributeLists[0].GetLeadingTrivia().Any(trivia => trivia.Kind() == SyntaxKind.SingleLineDocumentationCommentTrivia);
            if (!docBeforeAttribute)
            {
                return $"{type.Identifier.ValueText}: the XML doc comment must stay above the Obsolete attribute";
            }
        }

        // The copy is the original with only the namespace changed.
        var expectedModern = seedBody.Replace("ObsoleteNamespaceCopy.Legacy;", "ObsoleteNamespaceCopy.Modern;");
        if (modernText.TrimEnd() != expectedModern)
        {
            return "Modern/Orders.cs is not an exact copy of the original apart from the namespace";
        }

        return null;
    }
}
