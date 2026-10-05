using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

public class NamedArgumentsEngineTests
{
    private static CSharpCompilation Compile(SyntaxTree tree)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("NamedArgsTest", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static string[] Errors(CSharpCompilation compilation)
    {
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
    }

    /// <summary>Rewrites <paramref name="code"/>, asserting both the input and the output compile cleanly.</summary>
    private static NamedArgumentsRewriteResult Rewrite(string code, NamedArgumentsOptions? options = null, string? targetDocCommentId = null)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = Compile(tree);
        Assert.That(Errors(compilation), Is.Empty, "test input must compile");

        var result = NamedArgumentsEngine.Rewrite(compilation.GetSemanticModel(tree), tree.GetRoot(), options ?? new NamedArgumentsOptions(), targetDocCommentId);

        var rewrittenTree = CSharpSyntaxTree.ParseText(result.Root.ToFullString());
        Assert.That(Errors(Compile(rewrittenTree)), Is.Empty, "rewritten output must compile");
        return result;
    }

    private static string RewriteText(string code, NamedArgumentsOptions? options = null)
    {
        return Rewrite(code, options).Root.ToFullString();
    }

    [Test]
    [Category("NamedArgumentsRewriteResult")] // sentinel:auto-category
    public void Rewrite_PositionalCall_NamesAllArguments()
    {
        var result = Rewrite("class C { void M(int a, int b) { } void N() { M(1, 2); } }");

        Assert.That(result.Root.ToFullString(), Is.EqualTo("class C { void M(int a, int b) { } void N() { M(a: 1, b: 2); } }"));
        Assert.That(result.CallSitesConverted, Is.EqualTo(1));
        Assert.That(result.ArgumentsNamed, Is.EqualTo(2));
    }

    [Test]
    public void Rewrite_MixedNamedAndPositional_NamesOnlyThePositionalOnes()
    {
        var text = RewriteText("class C { void M(int a, int b) { } void N() { M(1, b: 2); } }");

        Assert.That(text, Is.EqualTo("class C { void M(int a, int b) { } void N() { M(a: 1, b: 2); } }"));
    }

    [Test]
    [Category("NamedArgumentsRewriteResult")] // sentinel:auto-category
    public void Rewrite_AlreadyNamed_ReportsNoChange()
    {
        var result = Rewrite("class C { void M(int a, int b) { } void N() { M(a: 1, b: 2); } }");

        Assert.That(result.CallSitesConverted, Is.EqualTo(0));
    }

    [Test]
    [Category("NamedArgumentsOptions")] // sentinel:auto-category
    public void Rewrite_SingleParameterMethod_SkippedByDefault()
    {
        var code = "class C { void M(int a) { } void N() { M(1); } }";

        Assert.That(RewriteText(code), Is.EqualTo(code));
        Assert.That(RewriteText(code, new NamedArgumentsOptions(MinParameters: 1)), Is.EqualTo("class C { void M(int a) { } void N() { M(a: 1); } }"));
    }

    [Test]
    public void Rewrite_ParamsMethod_IsLeftAlone()
    {
        var code = "class C { void M(int a, params int[] rest) { } void N() { M(1, 2, 3); } }";

        Assert.That(RewriteText(code), Is.EqualTo(code));
    }

    [Test]
    [Category("NamedArgumentsOptions")] // sentinel:auto-category
    public void Rewrite_LiteralArgumentsOnly_NamesJustTheLiterals()
    {
        var code = "class C { void M(int a, int b, string c) { } void N(int x) { M(x, 2, \"s\"); } }";

        var text = RewriteText(code, new NamedArgumentsOptions(LiteralArgumentsOnly: true));

        Assert.That(text, Is.EqualTo("class C { void M(int a, int b, string c) { } void N(int x) { M(x, b: 2, c: \"s\"); } }"));
    }

    [Test]
    public void Rewrite_ConstructorAndImplicitNew_AreRewritten()
    {
        var code = "class C { public C(int a, int b) { } static void N() { var x = new C(1, 2); C y = new(3, 4); } }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("class C { public C(int a, int b) { } static void N() { var x = new C(a: 1, b: 2); C y = new(a: 3, b: 4); } }"));
    }

    [Test]
    public void Rewrite_ExtensionMethodInReducedForm_UsesParametersAfterThis()
    {
        var code = "static class E { public static void Ext(this string s, int a, int b) { } } class C { void N() { \"x\".Ext(1, 2); } }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("static class E { public static void Ext(this string s, int a, int b) { } } class C { void N() { \"x\".Ext(a: 1, b: 2); } }"));
    }

    [Test]
    public void Rewrite_NestedCalls_BothLevelsAreRewritten()
    {
        var code = "class C { int M(int a, int b) => a; void N() { M(1, M(3, 4)); } }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("class C { int M(int a, int b) => a; void N() { M(a: 1, b: M(a: 3, b: 4)); } }"));
    }

    [Test]
    public void Rewrite_ReservedKeywordParameterName_IsEscaped()
    {
        var text = RewriteText("class C { void M(int @event, int b) { } void N() { M(1, 2); } }");

        Assert.That(text, Is.EqualTo("class C { void M(int @event, int b) { } void N() { M(@event: 1, b: 2); } }"));
    }

    [Test]
    public void Rewrite_InsideExpressionTree_IsLeftAlone()
    {
        var code = "using System; using System.Linq.Expressions; class C { int M(int a, int b) => a; void N() { Expression<Func<int>> e = () => M(1, 2); } }";

        Assert.That(RewriteText(code), Is.EqualTo(code));
    }

    [Test]
    public void Rewrite_AttributeArguments_NamesConstructorArgumentsButNotPropertySetters()
    {
        var code = "using System; class MyAttribute : Attribute { public string P { get; set; } = \"\"; public MyAttribute(int a, string b) { } } [My(1, \"x\", P = \"p\")] class C { }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("using System; class MyAttribute : Attribute { public string P { get; set; } = \"\"; public MyAttribute(int a, string b) { } } [My(a: 1, b: \"x\", P = \"p\")] class C { }"));
    }

    [Test]
    public void Rewrite_ConstructorInitializer_IsRewritten()
    {
        var code = "class B { public B(int a, int b) { } } class D : B { public D() : base(1, 2) { } }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("class B { public B(int a, int b) { } } class D : B { public D() : base(a: 1, b: 2) { } }"));
    }

    [Test]
    public void Rewrite_PrimaryConstructorBaseType_IsRewritten()
    {
        var code = "class B { public B(int a, int b) { } } class D(int x) : B(x, 2) { }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("class B { public B(int a, int b) { } } class D(int x) : B(a: x, b: 2) { }"));
    }

    [Test]
    [Category("NamedArgumentsRewriteResult")] // sentinel:auto-category
    public void Rewrite_TargetSymbol_RewritesOnlyCallsOfThatMethod()
    {
        var code = "class C { void M(int a, int b) { } void K(int a, int b) { } void N() { M(1, 2); K(3, 4); } }";

        var result = Rewrite(code, targetDocCommentId: "M:C.M(System.Int32,System.Int32)");

        Assert.That(result.Root.ToFullString(), Is.EqualTo("class C { void M(int a, int b) { } void K(int a, int b) { } void N() { M(a: 1, b: 2); K(3, 4); } }"));
        Assert.That(result.CallSitesConverted, Is.EqualTo(1));
    }

    [Test]
    public void Rewrite_DelegateInvocation_IsLeftAlone()
    {
        var code = "using System; class C { void N(Func<int, int, int> f) { f(1, 2); } }";

        Assert.That(RewriteText(code), Is.EqualTo(code));
    }

    [Test]
    public void Rewrite_MultiLineArguments_PreserveLayoutAndComments()
    {
        var code = "class C { void M(int a, int b) { } void N() {\n    M(\n        1, // first\n        2);\n} }";

        var text = RewriteText(code);

        Assert.That(text, Is.EqualTo("class C { void M(int a, int b) { } void N() {\n    M(\n        a: 1, // first\n        b: 2);\n} }"));
    }
}
