using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests;

/// <summary>
/// Coverage for <see cref="DiagnosticExtensions.ToInfo(Diagnostic, bool)"/>'s opt-in SourceLine field:
/// the trimmed text of the diagnostic's start line, capped at 160 characters plus "...".
/// </summary>
[TestFixture]
public class DiagnosticExtensionsTests
{
    private static Diagnostic CompileUndefinedNameDiagnostic(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "DiagnosticExtensionsTests",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        return compilation.GetDiagnostics().Single(d => d.Id == "CS0103");
    }

    [Test]
    public void ToInfo_IncludeSourceLine_ReturnsTrimmedLine()
    {
        var source = "class C\n{\n    void M()\n    {\n        var x = undefinedName;\n    }\n}\n";
        var diagnostic = CompileUndefinedNameDiagnostic(source);

        var info = diagnostic.ToInfo(includeSourceLine: true);

        Assert.That(info.SourceLine, Is.EqualTo("var x = undefinedName;"),
            "the diagnostic's start line should be returned with its leading indentation trimmed");
    }

    [Test]
    public void ToInfo_LineOver160Chars_IsCappedWithEllipsis()
    {
        var longLiteral = new string('a', 200);
        var trimmedLine = $"var x = undefinedName + \"{longLiteral}\";";
        var source = $"class C\n{{\n    void M()\n    {{\n        {trimmedLine}\n    }}\n}}\n";
        var diagnostic = CompileUndefinedNameDiagnostic(source);

        var info = diagnostic.ToInfo(includeSourceLine: true);

        Assert.That(trimmedLine.Length, Is.GreaterThan(160), "test precondition: the line must exceed the cap");
        Assert.That(info.SourceLine, Is.EqualTo(trimmedLine[..160] + "..."),
            "a line over 160 chars should be cut to its first 160 chars followed by \"...\"");
        Assert.That(info.SourceLine!.Length, Is.EqualTo(163));
    }

    [Test]
    public void ToInfo_Default_LeavesSourceLineNull()
    {
        var source = "class C\n{\n    void M()\n    {\n        var x = undefinedName;\n    }\n}\n";
        var diagnostic = CompileUndefinedNameDiagnostic(source);

        var info = diagnostic.ToInfo();

        Assert.That(info.SourceLine, Is.Null,
            "SourceLine is opt-in; the default ToInfo() call must not read the source text");
    }
}
