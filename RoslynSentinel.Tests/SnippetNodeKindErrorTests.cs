using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common.SnippetMatching;

namespace RoslynSentinel.Tests;

public class SnippetNodeKindErrorTests
{
    private static (SourceText Text, SyntaxNode Root, SnippetMatch Match) Locate(string source, string snippet, int line)
    {
        var text = SourceText.From(source);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var start = source.IndexOf(snippet, StringComparison.Ordinal);
        var match = new SnippetMatch(start, line, snippet.Length, WhitespaceTolerance.Exact, "test");
        return (text, root, match);
    }

    [Test]
    public void Describe_WrongNodeKind_NamesLineExpectedNodeAndInnermostKind()
    {
        const string source = "class C\n{\n    void M()\n    {\n        int x = 1;\n    }\n}\n";
        var (text, root, match) = Locate(source, "int x = 1;", 5);

        var message = SnippetNodeKindError.Describe(text, root, match, "switch statement", "contains a switch statement");

        Assert.That(message, Does.Contain("matched at line 5"));
        Assert.That(message, Does.Contain("no switch statement"));
        Assert.That(message, Does.Contain("LocalDeclarationStatement"));
    }

    [Test]
    public void Describe_LongFirstLine_QuotesOnlySixtyCharacters()
    {
        var line = "int " + new string('v', 100) + " = 1;";
        var source = "class C\n{\n    void M()\n    {\n        " + line + "\n    }\n}\n";
        var (text, root, match) = Locate(source, line, 5);

        var message = SnippetNodeKindError.Describe(text, root, match, "switch statement", "contains a switch statement");

        Assert.That(message, Does.Contain("(`" + line[..60] + "`)"));
        Assert.That(message, Does.Not.Contain(line));
    }

    [Test]
    public void Describe_SnippetLabelOverride_AppearsInMessage()
    {
        const string source = "class C\n{\n    void M()\n    {\n        int x = 1;\n    }\n}\n";
        var (text, root, match) = Locate(source, "int x = 1;", 5);

        var message = SnippetNodeKindError.Describe(text, root, match, "switch statement", "contains a switch statement", "oldContent");

        Assert.That(message, Does.StartWith("oldContent matched at line 5"));
        Assert.That(message, Does.Contain("Pass a oldContent that contains a switch statement."));
        Assert.That(message, Does.Not.Contain("contextSnippet"));
    }
}
