using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Pure-syntax tests for MoveMemberTextEdits.BuildForwarder and the kept-members overload of
/// BuildSourceEdits (MoveMember keep modes, slice 4). No workspace or compilation is involved.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")]
public class MoveMemberForwarderTests
{
    private const string QualifiedTarget = "global::Ns.Target";
    private const string TargetDisplayName = "Target";

    private static MethodDeclarationSyntax ParseMethod(string member, string eol = "\n")
    {
        var code = "public class Src" + eol + "{" + eol + "    " + member + eol + "}" + eol;
        return CSharpSyntaxTree.ParseText(code).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
    }

    private static string Forwarder(string member, bool deprecated = false, string eol = "\n") =>
        MoveMemberTextEdits.BuildForwarder(ParseMethod(member, eol), QualifiedTarget, deprecated, TargetDisplayName, eol);

    [Test]
    public void VoidReturn_ExpressionBodiedForwarder()
    {
        var text = Forwarder("public void M(int a) { }");
        Assert.That(text, Is.EqualTo("public void M(int a) => global::Ns.Target.M(a);"));
    }

    [Test]
    public void ValueReturn_ExpressionBodiedForwarder()
    {
        var text = Forwarder("public string Name() => \"x\";");
        Assert.That(text, Is.EqualTo("public string Name() => global::Ns.Target.Name();"));
    }

    [Test]
    public void RefOutInParams_AreMirroredAtTheCall()
    {
        var text = Forwarder("public bool Pick(ref int a, out int b, in int c, params int[] d) { b = 0; return true; }");
        Assert.That(text, Is.EqualTo(
            "public bool Pick(ref int a, out int b, in int c, params int[] d) => global::Ns.Target.Pick(ref a, out b, in c, d);"));
    }

    [Test]
    public void OptionalParameters_KeepTheirDefaults()
    {
        var text = Forwarder("public void Log(string text, int level = 2, string tag = \"app\") { }");
        Assert.That(text, Is.EqualTo(
            "public void Log(string text, int level = 2, string tag = \"app\") => global::Ns.Target.Log(text, level, tag);"));
    }

    [Test]
    public void ExpressionBodiedSource_ProducesSameForwarderShape()
    {
        var text = Forwarder("public int Twice(int a) => a * 2;");
        Assert.That(text, Is.EqualTo("public int Twice(int a) => global::Ns.Target.Twice(a);"));
    }

    [Test]
    public void BlockBodiedSource_BodyIsNotCopied()
    {
        var text = Forwarder("public int Twice(int a) { var b = a * 2; return b; }");
        Assert.That(text, Is.EqualTo("public int Twice(int a) => global::Ns.Target.Twice(a);"));
    }

    [Test]
    public void AsyncModifier_IsDropped_ReturnTypeKept()
    {
        var text = Forwarder("public async Task<int> Load(int id) { await Task.Yield(); return id; }");
        Assert.That(text, Is.EqualTo("public Task<int> Load(int id) => global::Ns.Target.Load(id);"));
    }

    [Test]
    public void StaticModifier_IsKept()
    {
        var text = Forwarder("public static int Count() { return 0; }");
        Assert.That(text, Is.EqualTo("public static int Count() => global::Ns.Target.Count();"));
    }

    [Test]
    public void MemberAttributes_AreDropped()
    {
        var text = Forwarder("[System.Obsolete(\"old\")] [Description(\"d\")] public int Size() { return 1; }");
        Assert.That(text, Is.EqualTo("public int Size() => global::Ns.Target.Size();"));
    }

    [Test]
    public void ParameterAttributes_AreDropped()
    {
        var text = Forwarder("public int Pair([Description(\"first\")] int a, int b) { return a + b; }");
        Assert.That(text, Is.EqualTo("public int Pair(int a, int b) => global::Ns.Target.Pair(a, b);"));
    }

    [Test]
    public void DeprecatedMode_EmitsExactObsoleteLiteral_LF()
    {
        var text = Forwarder("public static int M() { return 1; }", deprecated: true, eol: "\n");
        Assert.That(text, Is.EqualTo(
            "[Obsolete(\"Deprecated wrapper: call Target.M instead.\", DiagnosticId = \"RSDEPRECATED\")]\n" +
            "    public static int M() => global::Ns.Target.M();"));
    }

    [Test]
    public void DeprecatedMode_HonoursCRLF()
    {
        var text = Forwarder("public static int M() { return 1; }", deprecated: true, eol: "\r\n");
        Assert.That(text, Is.EqualTo(
            "[Obsolete(\"Deprecated wrapper: call Target.M instead.\", DiagnosticId = \"RSDEPRECATED\")]\r\n" +
            "    public static int M() => global::Ns.Target.M();"));
    }

    [Test]
    public void NonDeprecatedMode_IsSingleLine_EvenWithCRLF()
    {
        var text = Forwarder("public static int M() { return 1; }", deprecated: false, eol: "\r\n");
        Assert.That(text, Is.EqualTo("public static int M() => global::Ns.Target.M();"));
    }

    [Test]
    public void BuildSourceEdits_KeptMemberReplacedOverSpan_MovedMemberRemoved()
    {
        const string code =
            "public class Src\n" +
            "{\n" +
            "    /// <summary>Doc.</summary>\n" +
            "    public static int Keep() { return 1; }\n" +
            "    public static int Moved() { return 2; }\n" +
            "}\n";
        var sourceText = SourceText.From(code);
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var keep = methods.Single(m => m.Identifier.ValueText == "Keep");
        var moved = methods.Single(m => m.Identifier.ValueText == "Moved");

        var membersToMove = new List<MemberDeclarationSyntax> { keep, moved };
        var keptMembers = new HashSet<MemberDeclarationSyntax> { keep };

        var edits = MoveMemberTextEdits.BuildSourceEdits(
            sourceText,
            membersToMove,
            new List<TextChange>(),
            keptMembers,
            m => MoveMemberTextEdits.BuildForwarder((MethodDeclarationSyntax)m, QualifiedTarget, false, TargetDisplayName, "\n"),
            out var insideMovedMemberEdits);

        Assert.That(insideMovedMemberEdits, Is.Empty);
        Assert.That(edits.Count, Is.EqualTo(2));

        // The kept member is replaced over its Span, so the leading XML doc comment is not part of the edit.
        var keepEdit = edits.Single(e => e.Span == keep.Span);
        Assert.That(keepEdit.NewText, Is.EqualTo("public static int Keep() => global::Ns.Target.Keep();"));
        Assert.That(sourceText.ToString(keepEdit.Span), Does.StartWith("public static int Keep()"));

        // The non-kept moved member is removed as before (empty replacement covering its span).
        var removal = edits.Single(e => e.NewText == string.Empty);
        Assert.That(removal.Span.Contains(moved.Span), Is.True);
    }
}
