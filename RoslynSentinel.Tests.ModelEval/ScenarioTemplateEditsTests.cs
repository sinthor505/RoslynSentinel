using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Tests.ModelEval.ImplementationScenarios;

namespace RoslynSentinel.Tests.ModelEval;

/// <summary>
/// No-model unit tests for <see cref="ScenarioTemplateEdits"/>: string inputs only, no files.
/// </summary>
[TestFixture]
public class ScenarioTemplateEditsTests
{
    [Test]
    public void ReplaceExactly_ReplacesAndKeepsCrlf()
    {
        const string crlfText = "a\r\nb Old c\r\nd\r\n";

        var crlfResult = ScenarioTemplateEdits.ReplaceExactly(crlfText, "b Old c\nd", "b New c\ne");

        Assert.That(crlfResult, Is.EqualTo("a\r\nb New c\r\ne\r\n"));
        Assert.That(crlfResult.Replace("\r\n", string.Empty, StringComparison.Ordinal), Does.Not.Contain("\n"));

        const string lfText = "a\nb Old c\nd\n";

        var lfResult = ScenarioTemplateEdits.ReplaceExactly(lfText, "b Old c\nd", "b New c\ne");

        Assert.That(lfResult, Is.EqualTo("a\nb New c\ne\n"));
        Assert.That(lfResult, Does.Not.Contain("\r"));
    }

    [Test]
    public void ReplaceExactly_ThrowsWhenCountDiffers()
    {
        Assert.Throws<InvalidOperationException>(() => ScenarioTemplateEdits.ReplaceExactly("a\nb\n", "zzz", "y"));
        Assert.Throws<InvalidOperationException>(() => ScenarioTemplateEdits.ReplaceExactly("x\nx\n", "x", "y"));

        var result = ScenarioTemplateEdits.ReplaceExactly("x\r\nx\r\n", "x", "y", expectedCount: 2);

        Assert.That(result, Is.EqualTo("y\r\ny\r\n"));
    }

    [Test]
    public void RemoveMethods_RemovesOnlyNamedAndThrowsOnMissing()
    {
        var source = string.Join(
            "\r\n",
            "using System;",
            "",
            "namespace Sample;",
            "",
            "public class Holder",
            "{",
            "    public void A()",
            "    {",
            "    }",
            "",
            "    /// <summary>Doc for B.</summary>",
            "    [Obsolete(\"gone\")]",
            "    public void B()",
            "    {",
            "    }",
            "",
            "    public void C()",
            "    {",
            "    }",
            "}",
            "");

        var result = ScenarioTemplateEdits.RemoveMethods(source, "B");

        var remainingMethods = CSharpSyntaxTree.ParseText(result).GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Select(method => method.Identifier.ValueText)
            .ToList();
        Assert.That(remainingMethods, Is.EqualTo(new[] { "A", "C" }));
        Assert.That(result, Does.Not.Contain("B("));
        Assert.That(result, Does.Not.Contain("Doc for B."));

        var errors = CSharpSyntaxTree.ParseText(result).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.That(errors, Is.Empty);
        Assert.That(result.Replace("\r\n", string.Empty, StringComparison.Ordinal), Does.Not.Contain("\n"));

        Assert.Throws<InvalidOperationException>(() => ScenarioTemplateEdits.RemoveMethods(source, "Z"));

        var overloads = string.Join(
            "\r\n",
            "public class Overloads",
            "{",
            "    public void Over(int x)",
            "    {",
            "    }",
            "",
            "    public void Over(string s)",
            "    {",
            "    }",
            "}",
            "");
        Assert.Throws<InvalidOperationException>(() => ScenarioTemplateEdits.RemoveMethods(overloads, "Over"));
    }
}
