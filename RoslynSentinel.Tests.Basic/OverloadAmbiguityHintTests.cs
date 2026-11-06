using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

// The ambiguity message for plain overloads/constructors names the Member call that works. Member compares the bare identifier
// (a signature in memberName matches nothing), so the call selects the overload by its declaration line as contextSnippet.
[TestFixture]
public class OverloadAmbiguityHintTests
{
    private const string Path = "C:\\x\\C.cs";

    private const string TwoOverloads =
        "public class C\n" +
        "{\n" +
        "    public void Foo(int x)\n" +
        "    {\n" +
        "    }\n" +
        "\n" +
        "    public void Foo(string s)\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    private static string AmbiguityMessage(string source, string name, string operation, string? containerName = null)
    {
        var text = SourceText.From(source);
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        // Same narrowing as ReplaceMemberAsync/RemoveMemberAsync: a constructor's name is also its class's name, so type kinds are dropped.
        var members = SyntaxTargetResolver.ResolveCandidates(root, text, name)
            .Where(c => c.Kind is not (CandidateKind.Class or CandidateKind.Interface or CandidateKind.Struct or CandidateKind.Record or CandidateKind.Enum or CandidateKind.EnumMember))
            .ToList();
        var candidates = SyntaxTargetResolver.FilterByContainingType(members, containerName);
        var error = Assert.Throws<ToolAmbiguousMatchException>(() => SyntaxTargetResolver.ResolveBySnippetOrThrow(
            candidates, text, contextSnippet: null, lineBefore: null, lineAfter: null,
            (c, m, mode) => SyntaxTargetResolver.BuildOverloadHintForCandidates(c, m, mode, text, Path, operation, name), filePath: Path));
        return error!.Message;
    }

    [Test]
    public void PlainOverloads_NameTheMemberCallWithTheDeclarationLineAsContextSnippet()
    {
        var message = AmbiguityMessage(TwoOverloads, "Foo", "replace");

        Assert.That(message, Does.Contain("differ by signature"));
        Assert.That(message, Does.Contain("A signature in memberName is not matched"));
        Assert.That(message, Does.Contain("Next: Member("));
        Assert.That(message, Does.Contain("operation: replace"));
        Assert.That(message, Does.Contain("memberName: \"Foo\""));
        Assert.That(message, Does.Contain("contextSnippet: \"public void Foo(int x)\""));
        Assert.That(message, Does.Contain("filePath: \"C:\\\\x\\\\C.cs\""));
    }

    [Test]
    public void RemoveOperation_IsCarriedIntoTheNamedCall()
    {
        var message = AmbiguityMessage(TwoOverloads, "Foo", "remove");

        Assert.That(message, Does.Contain("operation: remove"));
    }

    [Test]
    public void TheNamedContextSnippet_SelectsExactlyOneOverload()
    {
        var text = SourceText.From(TwoOverloads);
        var root = CSharpSyntaxTree.ParseText(TwoOverloads).GetRoot();
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Foo");

        var picked = SyntaxTargetResolver.ResolveBySnippetOrThrow(
            candidates, text, "public void Foo(int x)", null, null, SyntaxTargetResolver.BuildHintForCandidates);

        Assert.That(picked, Is.Not.Null);
        Assert.That(picked!.StartLine, Is.EqualTo(3));
    }

    [Test]
    public void Constructors_GetTheSameHint()
    {
        const string source =
            "public class C\n" +
            "{\n" +
            "    public C(int x)\n" +
            "    {\n" +
            "    }\n" +
            "\n" +
            "    public C(string s, int n)\n" +
            "    {\n" +
            "    }\n" +
            "}\n";

        var message = AmbiguityMessage(source, "C", "replace");

        Assert.That(message, Does.Contain("overloads of 'C'"));
        Assert.That(message, Does.Contain("contextSnippet: \"public C(int x)\""));
    }

    [Test]
    public void SameNameInDifferentTypes_KeepsThePlainHint()
    {
        const string source =
            "public class A\n{\n    public void Foo(int x)\n    {\n    }\n}\n\n" +
            "public class B\n{\n    public void Foo(int x)\n    {\n    }\n}\n";

        var message = AmbiguityMessage(source, "Foo", "replace");

        Assert.That(message, Does.Not.Contain("differ by signature"));
        Assert.That(message, Does.Not.Contain("Next: Member("));
        Assert.That(message, Does.Contain("Provide a more specific contextSnippet"));
    }

    [Test]
    public void NotFoundOutcome_KeepsThePlainHint()
    {
        var text = SourceText.From(TwoOverloads);
        var root = CSharpSyntaxTree.ParseText(TwoOverloads).GetRoot();
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Foo");

        var hint = SyntaxTargetResolver.BuildOverloadHintForCandidates(candidates, [], "not found", text, Path, "replace", "Foo");

        Assert.That(hint, Is.EqualTo(SyntaxTargetResolver.BuildHintForCandidates(candidates, [], "not found")));
    }
}
