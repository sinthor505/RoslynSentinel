using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice C2 of docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md:
/// <see cref="RoslynFormattingHelper.NormalizeNewChildWhitespace{T}"/> and
/// <see cref="RoslynFormattingHelper.NormalizeNewRootWhitespace"/> with the shared EnsureNodeIsNew guard.
/// The "SafeSite_" fixtures mirror how the engines build nodes at the call sites the proposal lists as
/// safe (NewChild/NewRoot); the "KnownFalsePositive_" tests pin where the content check (4) rejects such a
/// site today (proposal open question 1). They document behaviour and must be revisited when that
/// question is decided; they are not an endorsement of the rejection.
/// </summary>
public class RoslynFormattingHelperNormalizeNewTests
{
    private const string ReplaceHint = "ReplaceNodeAndFormatReplacementAsync";
    private const string AnnotateHint = "FormatAnnotatedSpansAsync";

    private static CompilationUnitSyntax Parse(string source, string path = "")
        => CSharpSyntaxTree.ParseText(source, path: path).GetCompilationUnitRoot();

    private static ClassDeclarationSyntax ParseClass(string source)
        => Parse(source).DescendantNodes().OfType<ClassDeclarationSyntax>().First();

    private static MethodDeclarationSyntax NewVoidMethod()
        => MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Run")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithBody(Block(ExpressionStatement(InvocationExpression(IdentifierName("Go")))));

    private static CompilationUnitSyntax NewRoot()
        => CompilationUnit().AddMembers(
            ClassDeclaration("Gen").AddModifiers(Token(SyntaxKind.PublicKeyword)).AddMembers(NewVoidMethod()));

    private static void AssertContentCheckRejected(Action act, string because)
    {
        var ex = Assert.Throws<ArgumentException>(() => act(), because);
        Assert.That(ex!.Message, Does.Match(@"contains \d+ piece\(s\) of non-elastic"), because);
        Assert.That(ex.Message, Does.Contain(ReplaceHint));
        Assert.That(ex.ParamName, Is.EqualTo("node"));
    }

    // ---- Check 1: CompilationUnitSyntax passed to the child method ----

    [Test]
    public void NormalizeNewChild_RejectsCompilationUnit_AndPointsToRootMethod()
    {
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(CompilationUnit(), "\n"));
        Assert.That(ex!.Message, Does.Contain("NormalizeNewRootWhitespace"));
        Assert.That(ex.ParamName, Is.EqualTo("node"));
    }

    // ---- Check 2: attached to a tree ----

    [Test]
    public void NormalizeNewChild_RejectsNodeAttachedToTree()
    {
        var method = ParseClass("public class C { public void M() { } }").Members.OfType<MethodDeclarationSyntax>().First();
        Assert.That(method.Parent, Is.Not.Null);

        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(method, "\n"));

        // The method also carries non-elastic trivia, so this proves check 2 runs before check 4.
        Assert.That(ex!.Message, Does.Contain("attached to a syntax tree"));
        Assert.That(ex.Message, Does.Contain("ClassDeclaration"));
        Assert.That(ex.Message, Does.Contain(ReplaceHint));
        Assert.That(ex.Message, Does.Contain(AnnotateHint));
        Assert.That(ex.ParamName, Is.EqualTo("node"));
    }

    // ---- Check 3: tree has a file path ----

    [Test]
    public void NormalizeNewRoot_RejectsRootOfTreeWithFilePath_WithoutLeakingDirectory()
    {
        var root = Parse("public class C { }", @"C:\repo\Existing.cs");
        Assert.That(root.Parent, Is.Null);

        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(root, "\n"));

        // The root also carries non-elastic trivia, so this proves check 3 runs before check 4.
        Assert.That(ex!.Message, Does.Contain("Existing.cs"));
        Assert.That(ex.Message, Does.Contain("existing file"));
        Assert.That(ex.Message, Does.Not.Contain(@"C:\repo"));
        Assert.That(ex.Message, Does.Contain(ReplaceHint));
        Assert.That(ex.Message, Does.Contain(AnnotateHint));
    }

    [Test]
    public void NormalizeNewChild_RejectsParentlessNodeWhoseTreeHasFilePath()
    {
        var tree = CSharpSyntaxTree.Create(NewVoidMethod(), path: @"C:\repo\Existing.cs");
        var method = (MethodDeclarationSyntax)tree.GetRoot();
        Assert.That(method.Parent, Is.Null);

        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(method, "\n"));

        Assert.That(ex!.Message, Does.Contain("Existing.cs"));
        Assert.That(ex.Message, Does.Contain("existing file"));
    }

    [Test]
    public async Task NormalizeNewRoot_AdhocWorkspaceDocument_IsRejectedByCheck3()
    {
        // Proposal hypothesis for check 3: the file path may be empty for AdhocWorkspace documents.
        // Falsified for AddDocument(id, name, text): the tree gets a non-empty FilePath, so check 3 fires.
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(projectId, "Test", "Test", LanguageNames.CSharp);
        var documentId = DocumentId.CreateNewId(projectId);
        solution = solution.AddDocument(documentId, "Existing.cs", "public class C\n{\n}\n");
        var document = solution.GetDocument(documentId)!;
        var root = (CompilationUnitSyntax)(await document.GetSyntaxRootAsync())!;

        Assert.That(root.Parent, Is.Null);
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(root, "\n"));
        Assert.That(ex!.Message, Does.Contain("existing file"));
    }

    [Test]
    public void NormalizeNewRoot_ParsedRootWithoutFilePath_IsCaughtByContentCheck()
    {
        var root = Parse("public class C\n{\n}\n");
        Assert.That(root.SyntaxTree.FilePath, Is.Empty);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(root, "\n"), "parsed root with no path");
    }

    // ---- Check 4: content check ----

    [Test]
    public void ContentCheck_ReportsExactCount_ForCommentAndEndOfLine()
    {
        var node = NewVoidMethod().WithLeadingTrivia(Comment("// note"), CarriageReturnLineFeed);

        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));

        Assert.That(ex!.Message, Does.Contain("contains 2 piece(s)"));
        Assert.That(ex.Message, Does.Contain(ReplaceHint));
        Assert.That(ex.Message, Does.Contain(AnnotateHint));
    }

    [Test]
    public void ContentCheck_RejectsNonElasticWhitespace()
    {
        var node = NewVoidMethod().WithLeadingTrivia(Whitespace("    "));
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
        Assert.That(ex!.Message, Does.Contain("contains 1 piece(s)"));
    }

    [Test]
    public void ContentCheck_RejectsDocumentationComment()
    {
        var node = NewVoidMethod().WithLeadingTrivia(ParseLeadingTrivia("/// <summary>Runs.</summary>\n"));
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "doc comment");
    }

    [Test]
    public void KnownFalsePositive_ElasticWhitespaceWithText_IsNotAnnotatedElastic_AndIsRejected()
    {
        var probe = ElasticWhitespace("    ");
        var attached = NewVoidMethod().WithLeadingTrivia(probe).GetLeadingTrivia()[0];
        // Observed Roslyn behaviour: SyntaxFactory.ElasticWhitespace(text) does not carry
        // SyntaxAnnotation.ElasticAnnotation (only ElasticMarker and the cached elastic EOL/space do),
        // so the content check counts it. Pinned as a false positive, not endorsed.
        Assert.Multiple(() =>
        {
            Assert.That(probe.HasAnnotation(Microsoft.CodeAnalysis.SyntaxAnnotation.ElasticAnnotation), Is.False, "probe detached");
            Assert.That(attached.HasAnnotation(Microsoft.CodeAnalysis.SyntaxAnnotation.ElasticAnnotation), Is.False, "probe attached");
        });
        var node = NewVoidMethod().WithLeadingTrivia(ElasticWhitespace("    "));
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "ElasticWhitespace(text)");
    }

    [Test]
    public void ContentCheck_AcceptsElasticEndOfLine()
    {
        var node = NewVoidMethod().WithLeadingTrivia(ElasticCarriageReturnLineFeed);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void ContentCheck_AcceptsPureFactoryChildAndRoot()
    {
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), "\n"));
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(NewRoot(), "\n"));
    }

    [Test]
    public void KnownFalsePositive_ParseMemberDeclaration_IsRejected()
    {
        // Proposal hypothesis confirmed: a node parsed from a string keeps its source spacing, which the
        // content check cannot tell apart from user formatting.
        var node = ParseMemberDeclaration("public void Run() { }")!;
        Assert.That(node.Parent, Is.Null);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "parsed member");
    }

    // ---- EOL comes from the parameter ----

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void NormalizeNewChild_UsesEolFromParameter(string eol)
    {
        var text = RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), eol).ToFullString();
        AssertOnlyEol(text, eol);
    }

    [TestCase("\n")]
    [TestCase("\r\n")]
    public void NormalizeNewRoot_UsesEolFromParameter(string eol)
    {
        var text = RoslynFormattingHelper.NormalizeNewRootWhitespace(NewRoot(), eol).ToFullString();
        AssertOnlyEol(text, eol);
    }

    [Test]
    public void NormalizeNew_Methods_HaveNoDefaultForEol()
    {
        foreach (var name in new[] { nameof(RoslynFormattingHelper.NormalizeNewChildWhitespace), nameof(RoslynFormattingHelper.NormalizeNewRootWhitespace) })
        {
            var method = typeof(RoslynFormattingHelper).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
            var eol = method.GetParameters().Single(p => p.Name == "eol");
            Assert.That(eol.HasDefaultValue, Is.False, $"{name} must require eol.");
        }
    }

    private static void AssertOnlyEol(string text, string eol)
    {
        Assert.That(text, Does.Contain(eol), "Expected at least one line break.");
        var stripped = text.Replace(eol, string.Empty);
        Assert.That(stripped, Does.Not.Contain("\n"));
        Assert.That(stripped, Does.Not.Contain("\r"));
    }

    // ---- Safe-site fixtures (mirror the engines' construction) ----

    [Test]
    public void SafeSite_CodeGeneration_ImplementInterface_Property_997_IsAccepted()
    {
        var throwBody = Block(ThrowStatement(ObjectCreationExpression(ParseTypeName("NotImplementedException")).WithArgumentList(ArgumentList())));
        var accessors = new List<AccessorDeclarationSyntax>
        {
            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(throwBody),
            AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithBody(throwBody),
        };
        var node = (MemberDeclarationSyntax)PropertyDeclaration(ParseTypeName("string"), "Name")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithAccessorList(AccessorList(List(accessors)));

        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void SafeSite_CodeGeneration_ImplementInterface_Method_972_WithoutParameters_IsAccepted()
    {
        var node = (MemberDeclarationSyntax)BuildImplementInterfaceMethod(parameterCount: 0);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void KnownFalsePositive_CodeGeneration_ImplementInterface_Method_972_WithParameter_IsRejected()
    {
        // Cause: the site gives each parameter type SyntaxFactory.Space, a non-elastic single space.
        var node = BuildImplementInterfaceMethod(parameterCount: 1);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "parameter type with Space trivia");
    }

    private static MethodDeclarationSyntax BuildImplementInterfaceMethod(int parameterCount)
    {
        var methodParams = Enumerable.Range(0, parameterCount)
            .Select(i => Parameter(Identifier($"p{i}")).WithType(ParseTypeName("int").WithTrailingTrivia(Space)))
            .ToArray();
        var body = Block(ThrowStatement(ObjectCreationExpression(ParseTypeName("NotImplementedException")).WithArgumentList(ArgumentList())));
        return MethodDeclaration(ParseTypeName("string"), "Describe")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword)))
            .WithParameterList(ParameterList(SeparatedList(methodParams)))
            .WithBody(body);
    }

    [Test]
    public void SafeSite_Mapping_GenerateMapping_91_IsAccepted()
    {
        var assignments = new List<StatementSyntax>
        {
            ExpressionStatement(AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("dest"), IdentifierName("Name")),
                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("source"), IdentifierName("Name")))),
        };
        var mappingMethod = MethodDeclaration(ParseTypeName("void"), "MapOrderToOrderDto")
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(
                Parameter(Identifier("source")).WithType(ParseTypeName("Order")),
                Parameter(Identifier("dest")).WithType(ParseTypeName("OrderDto")))
            .WithBody(Block(assignments));

        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(mappingMethod, "\n"));
    }

    [Test]
    public void KnownFalsePositive_AdvancedRefactoring_SyncInterface_Method_335_IsRejected()
    {
        // Cause: ReturnType, ParameterList and constraint clauses are reused from the existing method,
        // so they carry the user's spacing ("string ", ", ", " where ").
        var method = ParseClass("public class Svc { public string Find<T>(int id, T hint) where T : class { return null; } }")
            .Members.OfType<MethodDeclarationSyntax>().First();
        var node = MethodDeclaration(method.ReturnType, method.Identifier)
            .WithParameterList(method.ParameterList)
            .WithTypeParameterList(method.TypeParameterList)
            .WithConstraintClauses(method.ConstraintClauses)
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .WithModifiers(TokenList());

        Assert.That(node.Parent, Is.Null);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "type taken from existing method");
    }

    [Test]
    public void KnownFalsePositive_AdvancedRefactoring_SyncInterface_Property_366_IsRejected()
    {
        // Cause: prop.Type and prop.Identifier are reused from the existing property and keep their trailing space.
        var prop = ParseClass("public class Svc { public int Count { get; set; } }").Members.OfType<PropertyDeclarationSyntax>().First();
        var accessors = new List<AccessorDeclarationSyntax>
        {
            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
        };
        var node = PropertyDeclaration(prop.Type, prop.Identifier)
            .WithAccessorList(AccessorList(List(accessors)))
            .WithModifiers(TokenList());

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "type taken from existing property");
    }

    [Test]
    public void KnownFalsePositive_MemberRefactoring_ExtractMethod_4569_IsRejected()
    {
        // Causes: parameter types carry SyntaxFactory.Space, and the extracted statements keep their interior spacing.
        var statement = ParseStatement("var total = a + b;");
        var bodyStatements = new List<StatementSyntax>
        {
            statement.WithoutLeadingTrivia().WithoutTrailingTrivia(),
            ReturnStatement(IdentifierName("total")),
        };
        var parameters = new[] { "a", "b" }
            .Select(n => Parameter(Identifier(n)).WithType(ParseTypeName("int").WithTrailingTrivia(Space)))
            .ToArray();
        var node = MethodDeclaration(ParseTypeName("int"), "ExtractedSum")
            .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.StaticKeyword)))
            .WithParameterList(ParameterList(SeparatedList(parameters)))
            .WithBody(Block(bodyStatements));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "extracted method");
    }

    [Test]
    public void KnownFalsePositive_MemberRefactoring_ExtractMethod_CallSite_4633_IsRejected()
    {
        // Cause: the site copies the original statement's leading trivia (the user's indentation) onto the call.
        var original = ParseStatement("        var total = a + b;\n");
        var node = BuildCallSite().WithLeadingTrivia(original.GetLeadingTrivia());
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "call site with copied indentation");
    }

    [Test]
    public void SafeSite_MemberRefactoring_ExtractMethod_CallSite_4633_WithoutCopiedTrivia_IsAccepted()
    {
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(BuildCallSite(), "\n"));
    }

    private static StatementSyntax BuildCallSite()
        => ExpressionStatement(InvocationExpression(
            IdentifierName("ExtractedSum"),
            ArgumentList(SeparatedList(new[] { Argument(IdentifierName("a")), Argument(IdentifierName("b")) }))));

    private static AttributeListSyntax ParseAttributeList(string attributeSource)
    {
        // Same construction as AttributeTextEditBuilder.ParseAttributeList.
        var snippet = ParseCompilationUnit($"[{attributeSource}]\npublic class __Dummy__ {{}}");
        return snippet.DescendantNodes().OfType<AttributeListSyntax>().First();
    }

    [Test]
    public void KnownFalsePositive_AttributeTextEditBuilder_49_ParsedListStillAttached_IsRejectedByCheck2()
    {
        var list = ParseAttributeList("Obsolete");
        Assert.That(list.Parent, Is.Not.Null);

        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(list, "\n"));
        Assert.That(ex!.Message, Does.Contain("attached to a syntax tree"));
    }

    [Test]
    public void SafeSite_AttributeTextEditBuilder_49_DetachedListWithoutArguments_IsAccepted()
    {
        var list = ParseAttributeList("Obsolete").WithoutTrivia();
        Assert.That(list.Parent, Is.Null);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(list, "\n"));
    }

    [Test]
    public void KnownFalsePositive_AttributeTextEditBuilder_49_DetachedListWithArguments_IsRejected()
    {
        // Cause: the parsed list keeps the user-style spacing inside its argument list (", ").
        var list = ParseAttributeList("Obsolete(\"use X\", true)").WithoutTrivia();
        Assert.That(list.Parent, Is.Null);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(list, "\n"), "parsed attribute with arguments");
    }

    [Test]
    public void KnownFalsePositive_AttributeTextEditBuilder_94_ParsedAttributeWithArguments_IsRejected()
    {
        var attribute = ParseAttributeList("Obsolete(\"use X\", true)").Attributes.First().WithoutTrivia();
        Assert.That(attribute.Parent, Is.Null);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(attribute, "\n"), "parsed attribute with arguments");
    }

    [Test]
    public void KnownFalsePositive_BasicRefactoring_ExtractInterface_795_NewRoot_IsRejected()
    {
        // Causes: the site deliberately sets non-elastic CRLF + indentation trivia on each interface member
        // (MemberTrivia) and reuses the original using directives and method parts with their own spacing.
        var root = Parse("using System;\nusing System.Linq;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a, int b) => a + b;\n}\n");
        var m = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        static SyntaxTriviaList MemberTrivia() => TriviaList(CarriageReturnLineFeed, Whitespace("    "));
        var ifaceMethod = (MemberDeclarationSyntax)MethodDeclaration(m.ReturnType.WithoutTrivia(), m.Identifier)
            .WithParameterList(m.ParameterList)
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .WithLeadingTrivia(MemberTrivia())
            .WithTrailingTrivia(TriviaList(CarriageReturnLineFeed));
        var iface = InterfaceDeclaration("ISvc").AddModifiers(Token(SyntaxKind.PublicKeyword)).AddMembers(ifaceMethod);
        var cleanUsings = List(root.Usings.Select(u => u.WithoutTrailingTrivia().WithTrailingTrivia(ElasticCarriageReturnLineFeed)));
        var ns = FileScopedNamespaceDeclaration(ParseName("Demo")).AddMembers(iface);
        var ifaceCompUnit = CompilationUnit().WithUsings(cleanUsings).AddMembers(ns);

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(ifaceCompUnit, "\r\n", elasticTrivia: false), "extract-interface new root");
    }

    [Test]
    public void KnownFalsePositive_AdvancedStructural_ExtractSuperclass_126_MixedRoot_IsRejected()
    {
        // Provenance of this site is unverified (proposal open question 4): the new root embeds the existing
        // class's property declarations, so the content check correctly sees user formatting here.
        var root = Parse("using System;\n\nnamespace Demo;\n\npublic class Customer\n{\n    public string Name { get; set; }\n    public int Age { get; set; }\n}\n");
        var properties = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members.OfType<PropertyDeclarationSyntax>().ToArray();
        var baseClass = ClassDeclaration("PersonBase")
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AbstractKeyword))
            .AddMembers(properties);
        var baseUnit = CompilationUnit().WithUsings(root.Usings)
            .AddMembers(FileScopedNamespaceDeclaration(ParseName("Demo")).AddMembers(baseClass));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(baseUnit, "\n"), "mixed new root");
    }
}
