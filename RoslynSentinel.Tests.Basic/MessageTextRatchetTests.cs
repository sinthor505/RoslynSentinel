using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace RoslynSentinel.Tests.Basic;

// Ratchet for message-text pins. An Assert.That whose actual value is a message-like member (Message, Hint, Detail, ...) and
// whose constraint contains Does.Contain of a 35-character-or-longer literal pins wording. The pins are counted per file name
// in docs/known-long-message-pins.txt: growth fails (use ErrorAssert.Is with a code and a short fragment), a decrease passes
// with a warning to lower the baseline. Classes and methods marked [Category("MessageTextSubject")] are exempt.
[TestFixture]
[Category("Ratchet")]
[Category("MessageTextRatchet")]
public class MessageTextRatchetTests
{
    private const string BaselineRelativePath = "docs/known-long-message-pins.txt";
    private const string UpdateEnvVar = "ROSLYNSENTINEL_UPDATE_LONG_MESSAGE_BASELINE";
    private const string ExemptCategory = "MessageTextSubject";
    private const string TestProjectPrefix = "RoslynSentinel.Tests";
    private const int MinimumLiteralLength = 35;

    private static readonly string[] MessageMemberNames =
    {
        "Message", "ErrorMessage", "StatusMessage", "Hint", "Detail", "Details", "Reason", "Next", "Error",
    };

    [Test]
    public void LongMessagePins_NeverExceedTheBaseline()
    {
        var repo = RepoRootLocator.Find(AppContext.BaseDirectory);
        var found = ScanTestProjects(repo);
        var baselinePath = Path.Combine(repo, BaselineRelativePath);

        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            WriteBaseline(baselinePath, found);
            TestContext.Out.WriteLine($"baseline files: {found.Count}; total pins: {found.Values.Sum()}");
            return;
        }

        var baseline = LoadBaseline(baselinePath);
        var problems = new List<string>();
        var lower = new List<string>();
        foreach (var file in found.Keys.Union(baseline.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            found.TryGetValue(file, out var count);
            baseline.TryGetValue(file, out var allowed);
            if (count > allowed)
            {
                problems.Add(
                    $"NEW long message pin in {file}: {count} pins, baseline {allowed}; use ErrorAssert.Is(error, ToolErrorCode.X, \"<short fragment>\") for a failure with a code, or pin a short fragment.");
            }
            else if (count < allowed)
            {
                lower.Add($"{file} to {count}");
            }
        }

        Assert.That(problems, Is.Empty, string.Join(Environment.NewLine, problems));
        if (lower.Count > 0)
        {
            Assert.Warn($"lower the baseline in {BaselineRelativePath} for {string.Join("; ", lower)} (the ratchet only shrinks)");
        }
    }

    [Test]
    public void Scan_ThirtyFiveCharacterLiteralCounts()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\")"));

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Scan_ThirtyFourCharacterLiteralDoesNotCount()
    {
        var literal = new string('a', 34);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\")"));

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Scan_MessageTextSubjectClassIsSkipped()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\")", classAttributes: "[Category(\"MessageTextSubject\")]"));

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Scan_MessageTextSubjectMethodIsSkipped()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\")", methodAttributes: "[Category(\"MessageTextSubject\")]"));

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Scan_NonMessageExpressionIsIgnored()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\")", member: "result.Status"));

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Scan_InterpolatedLiteralCounts()
    {
        var count = CountLongMessagePins(PinSource("Does.Contain($\"the value {name} is not allowed in this position\")"));

        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void Scan_DoesNotContainIsIgnored()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Not.Contain(\"" + literal + "\")"));

        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void Scan_AndChainCountsEachLiteral()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\").And.Contain(\"" + literal + "\")"));

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void Scan_NullForgivingAndConditionalAccessAreLookedThrough()
    {
        var literal = new string('a', 35);

        var count = CountLongMessagePins(PinSource("Does.Contain(\"" + literal + "\")", member: "error?.Message!"));

        Assert.That(count, Is.EqualTo(1));
    }

    internal static int CountLongMessagePins(string sourceText)
    {
        var root = CSharpSyntaxTree.ParseText(sourceText).GetRoot();
        var count = 0;
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsAssertThat(invocation))
            {
                continue;
            }

            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count < 2 || !IsMessageMember(arguments[0].Expression) || IsExempt(invocation))
            {
                continue;
            }

            count += CountContainPins(arguments[1].Expression);
        }

        return count;
    }

    private static string PinSource(string constraint, string classAttributes = "", string methodAttributes = "", string member = "error.Message") => $$"""
        using NUnit.Framework;

        {{classAttributes}}
        public class Sample
        {
            {{methodAttributes}}
            [Test]
            public void Runs()
            {
                Assert.That({{member}}, {{constraint}});
            }
        }
        """;

    private static SortedDictionary<string, int> ScanTestProjects(string repo)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var projectDir in Directory.EnumerateDirectories(repo, TestProjectPrefix + "*").OrderBy(dir => dir, StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
            {
                var segments = Path.GetRelativePath(repo, file).Split('\\', '/');
                if (segments.Any(segment => segment is "bin" or "obj" or "Worktree"))
                {
                    continue;
                }

                var pins = CountLongMessagePins(File.ReadAllText(file));
                if (pins == 0)
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                counts.TryGetValue(name, out var existing);
                counts[name] = existing + pins;
            }
        }

        return counts;
    }

    private static bool IsAssertThat(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax member)
        {
            return false;
        }

        return member.Expression is IdentifierNameSyntax target
            && target.Identifier.ValueText == "Assert"
            && member.Name.Identifier.ValueText == "That";
    }

    private static bool IsMessageMember(ExpressionSyntax expression)
    {
        var name = LastIdentifier(expression);
        return name is not null && MessageMemberNames.Contains(name);
    }

    private static string? LastIdentifier(ExpressionSyntax expression)
    {
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                    current = postfix.Operand;
                    break;
                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    break;
                case ConditionalAccessExpressionSyntax conditional:
                    current = conditional.WhenNotNull;
                    break;
                case MemberBindingExpressionSyntax binding:
                    return binding.Name.Identifier.ValueText;
                case MemberAccessExpressionSyntax access:
                    return access.Name.Identifier.ValueText;
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText;
                default:
                    return null;
            }
        }
    }

    private static int CountContainPins(ExpressionSyntax constraint)
    {
        // Any negated constraint is not a pin of wording: Does.Not.Contain and Is.Not.* are skipped as a whole.
        if (constraint.DescendantTokens().Any(token => token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == "Not"))
        {
            return 0;
        }

        var count = 0;
        foreach (var invocation in constraint.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (!IsNamed(invocation.Expression, "Contain") || invocation.ArgumentList.Arguments.Count == 0)
            {
                continue;
            }

            if (TextLength(invocation.ArgumentList.Arguments[0].Expression) >= MinimumLiteralLength)
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsNamed(ExpressionSyntax expression, string name) => expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == name,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText == name,
        _ => false,
    };

    // A plain literal counts its decoded value; an interpolated string counts the text between its quotes with holes as written.
    private static int TextLength(ExpressionSyntax argument) => argument switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText.Length,
        InterpolatedStringExpressionSyntax interpolated => InterpolatedInnerText(interpolated).Length,
        _ => 0,
    };

    private static string InterpolatedInnerText(InterpolatedStringExpressionSyntax interpolated)
    {
        var text = interpolated.ToString();
        var open = text.IndexOf('"');
        var close = text.LastIndexOf('"');
        return close > open ? text[(open + 1)..close] : string.Empty;
    }

    private static bool IsExempt(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is TypeDeclarationSyntax type && HasExemptCategory(type.AttributeLists))
            {
                return true;
            }

            if (current is MethodDeclarationSyntax method && HasExemptCategory(method.AttributeLists))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasExemptCategory(SyntaxList<AttributeListSyntax> lists) =>
        lists.SelectMany(list => list.Attributes).Any(IsExemptCategory);

    private static bool IsExemptCategory(AttributeSyntax attribute) =>
        ShortName(attribute.Name.ToString()) == "Category"
        && attribute.ArgumentList is { Arguments: { Count: > 0 } arguments }
        && arguments[0].Expression is LiteralExpressionSyntax literal
        && literal.Token.ValueText == ExemptCategory;

    private static string ShortName(string qualifiedName)
    {
        var dot = qualifiedName.LastIndexOf('.');
        return dot < 0 ? qualifiedName : qualifiedName[(dot + 1)..];
    }

    private static Dictionary<string, int> LoadBaseline(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.LastIndexOf(" | ", StringComparison.Ordinal);
            if (split < 0 || !int.TryParse(line[(split + 3)..], out var count))
            {
                throw new InvalidDataException("Malformed long-message-pin baseline line: " + line);
            }

            result[line[..split]] = count;
        }

        return result;
    }

    private static void WriteBaseline(string path, SortedDictionary<string, int> found)
    {
        var lines = new List<string>
        {
            "# Long message pins per test file (message-text ratchet baseline). Counts Assert.That(<message member>, Does.Contain(<35+ char literal>)).",
            "# Growth fails: use ErrorAssert.Is(error, ToolErrorCode.X, \"<short fragment>\") instead of a sentence. A decrease passes with a warning.",
            "# Classes and methods marked [Category(\"MessageTextSubject\")] are not counted.",
            "# Refresh after lowering pins: set ROSLYNSENTINEL_UPDATE_LONG_MESSAGE_BASELINE=1 and run",
            "# MessageTextRatchetTests.LongMessagePins_NeverExceedTheBaseline.",
            "# Line format: <File.cs> | <count> (file name, not path; equal names are summed)",
        };
        lines.AddRange(found.Select(pair => $"{pair.Key} | {pair.Value}"));
        File.WriteAllLines(path, lines);
    }
}
