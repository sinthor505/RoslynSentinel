using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Slice C2 of docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md:
/// <see cref="RoslynFormattingHelper.NormalizeNewChildWhitespace{T}"/> and
/// <see cref="RoslynFormattingHelper.NormalizeNewRootWhitespace"/> with the shared EnsureNodeIsNew guard.
/// The "SafeSite_" fixtures mirror how the engines build nodes at the call sites the proposal lists as
/// safe (NewChild/NewRoot). Check 4 exempts single-space whitespace trivia (slice C2b), so most of those
/// sites are accepted. The "PinnedNeedsC3_" tests pin the sites that are still rejected: each needs a
/// detach/parse step or elastic trivia at the call site in slice C3 (or, for the ElasticWhitespace(text)
/// case, cannot be told apart from user indentation). They document behaviour and must be revisited in C3.
/// </summary>
public class RoslynFormattingHelperNormalizeNewTests
{
    private const string ReplaceHint = "ReplaceNodeAndFormatReplacementAsync";
    private const string AnnotateHint = "Formatter.Annotation";

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

    private static void AssertContentCheckRejected(Action act, string because, string paramName = "node")
    {
        var ex = Assert.Throws<ArgumentException>(() => act(), because);
        Assert.That(ex!.Message, Does.Match(@"contains \d+ piece\(s\) of non-elastic"), because);
        Assert.That(ex.Message, Does.Contain(ReplaceHint));
        Assert.That(ex.ParamName, Is.EqualTo(paramName));
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
        Assert.That(ex!.Message, Does.Contain("has a file path"));
        Assert.That(ex.Message, Does.Not.Contain("Existing.cs"));
        Assert.That(ex.ParamName, Is.EqualTo("root"));
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

        Assert.That(ex!.Message, Does.Contain("has a file path"));
        Assert.That(ex.Message, Does.Not.Contain("Existing.cs"));
        Assert.That(ex.ParamName, Is.EqualTo("node"));
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
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(root, "\n"), "parsed root with no path", "root");
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
        // Deliberate trade-off: a synthesized node that carries generated documentation comments is rejected,
        // because check 4 cannot tell generated doc comments from a user's. A caller that needs generated docs
        // must add them after normalizing. An escape hatch (for example an opt-in flag) is to be decided later.
        var node = NewVoidMethod().WithLeadingTrivia(ParseLeadingTrivia("/// <summary>Runs.</summary>\n"));
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "doc comment");
    }

    [Test]
    public void PinnedNeedsC3_ElasticWhitespaceWithText_IsNotAnnotatedElastic_AndIsRejected()
    {
        // Not a false positive of the content check: ElasticWhitespace(text) is indistinguishable from
        // Whitespace(text) through the public API, so it cannot be exempted without also accepting real
        // indentation (see ContentCheck_RejectsNonElasticWhitespace). A call site that builds multi-character
        // whitespace with ElasticWhitespace(text) needs elastic trivia (ElasticSpace/ElasticMarker/
        // ElasticCarriageReturnLineFeed) or no trivia at all in slice C3.
        var probe = ElasticWhitespace("    ");
        var attached = NewVoidMethod().WithLeadingTrivia(probe).GetLeadingTrivia()[0];
        // Observed Roslyn behaviour: SyntaxFactory.ElasticWhitespace(text) does not carry
        // SyntaxAnnotation.ElasticAnnotation (only ElasticMarker and the cached elastic EOL/space do),
        // so the content check counts it. Pinned: needs a call-site change in C3.
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
    public void ParseMemberDeclaration_SingleLineSnippet_IsAccepted()
    {
        // A single-line snippet only carries single-space trivia, which check 4 exempts.
        var node = ParseMemberDeclaration("public void Run() { }")!;
        Assert.That(node.Parent, Is.Null);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void ParseMemberDeclaration_MultiLineSnippet_IsStillRejected()
    {
        // A snippet with newlines and indentation is what a hand-written or formatted tree carries.
        var node = ParseMemberDeclaration("public void Run()\n{\n    Go();\n}\n")!;
        Assert.That(node.Parent, Is.Null);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "parsed multi-line member");
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
    public void SafeSite_CodeGeneration_ImplementInterface_Method_972_WithParameter_IsAccepted()
    {
        // The site gives each parameter type SyntaxFactory.Space, a non-elastic single space that check 4 exempts.
        var node = BuildImplementInterfaceMethod(parameterCount: 1);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
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
    public void SafeSite_AdvancedRefactoring_SyncInterface_Method_335_ReusedSingleSpaceTrivia_IsAccepted()
    {
        // ReturnType, ParameterList and constraint clauses are reused from the existing method, so they carry
        // its single-space spacing ("string ", ", ", " where "), which check 4 exempts. A reused part with
        // newlines or indentation would still be rejected.
        var method = ParseClass("public class Svc { public string Find<T>(int id, T hint) where T : class { return null; } }")
            .Members.OfType<MethodDeclarationSyntax>().First();
        var node = MethodDeclaration(method.ReturnType, method.Identifier)
            .WithParameterList(method.ParameterList)
            .WithTypeParameterList(method.TypeParameterList)
            .WithConstraintClauses(method.ConstraintClauses)
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .WithModifiers(TokenList());

        Assert.That(node.Parent, Is.Null);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void SafeSite_AdvancedRefactoring_SyncInterface_Property_366_ReusedSingleSpaceTrivia_IsAccepted()
    {
        // prop.Type and prop.Identifier are reused from the existing property and keep their single trailing space.
        var prop = ParseClass("public class Svc { public int Count { get; set; } }").Members.OfType<PropertyDeclarationSyntax>().First();
        var accessors = new List<AccessorDeclarationSyntax>
        {
            AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
            AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
        };
        var node = PropertyDeclaration(prop.Type, prop.Identifier)
            .WithAccessorList(AccessorList(List(accessors)))
            .WithModifiers(TokenList());

        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void SafeSite_MemberRefactoring_ExtractMethod_4569_IsAccepted()
    {
        // Parameter types carry SyntaxFactory.Space, and the extracted statements keep their interior single-space spacing.
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

        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
    }

    [Test]
    public void PinnedNeedsC3_MemberRefactoring_ExtractMethod_CallSite_4633_CopiedIndentation_IsRejected()
    {
        // The site copies the original statement's leading trivia (the user's indentation) onto the call.
        // Rejection is correct; C3 must drop the copied trivia (see the SafeSite_ twin below).
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
    public void SafeSite_AttributeTextEditBuilder_49_ParsedAttachedList_BuildAddEdit_IsAcceptedAndUsesPassedEol()
    {
        // Slice C3-G1: the parsed list is still attached to the dummy class (the raw shape the pin used to reject);
        // the engine detaches it and strips the user's inner formatting before normalizing.
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"use X\",\r\n      true)");
        Assert.That(list, Is.Not.Null);
        Assert.That(list!.Parent, Is.Not.Null);

        var text = Microsoft.CodeAnalysis.Text.SourceText.From("public class C\r\n{\r\n}\r\n");
        var target = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        var edit = AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, "\r\n");

        Assert.That(edit.NewText, Is.EqualTo("[Obsolete(\"use X\", true)]\r\n"));
    }

    [Test]
    public void SafeSite_AttributeTextEditBuilder_94_ParsedAttachedAttribute_BuildReplaceEdit_IsAccepted()
    {
        // BuildReplaceEdit has no eol parameter: it derives the document's dominant EOL from the old attribute's tree.
        var text = Microsoft.CodeAnalysis.Text.SourceText.From("[Obsolete]\r\npublic class C\r\n{\r\n}\r\n");
        var oldAttribute = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<AttributeSyntax>().First();
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"y\",   true)")!.Attributes.First();
        Assert.That(newAttribute.Parent, Is.Not.Null);

        var edit = AttributeTextEditBuilder.BuildReplaceEdit(0, oldAttribute, newAttribute);

        Assert.That(edit.NewText, Is.EqualTo("Obsolete(\"y\", true)"));
        Assert.That(edit.Start, Is.EqualTo(oldAttribute.SpanStart));
        Assert.That(edit.End, Is.EqualTo(oldAttribute.Span.End));
    }

    [Test]
    public void Pinned_AttributeTextEditBuilder_AttributeWithInnerComment_IsRejectedByCheck4()
    {
        // Known edge after C3-G1: the guard keeps comments out of the "new node" contract, so a caller-supplied
        // attribute carrying an inner comment now throws ArgumentException instead of being rewritten with the
        // comment kept. The guard is deliberately not weakened; revisit if comments in attribute arguments matter.
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"x\" /* why */)");
        var text = Microsoft.CodeAnalysis.Text.SourceText.From("public class C\n{\n}\n");
        var target = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        var ex = Assert.Throws<ArgumentException>(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list!, text, "\n"));
        Assert.That(ex!.Message, Does.Match(@"contains \d+ piece\(s\) of non-elastic"));
    }

    [Test]
    public void SafeSite_AttributeTextEditBuilder_49_DetachedListWithoutArguments_IsAccepted()
    {
        var list = ParseAttributeList("Obsolete").WithoutTrivia();
        Assert.That(list.Parent, Is.Null);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(list, "\n"));
    }

    [Test]
    public void SafeSite_AttributeTextEditBuilder_49_DetachedListWithArguments_IsAccepted()
    {
        // Once detached, the parsed list only keeps single-space spacing inside its argument list (", ").
        var list = ParseAttributeList("Obsolete(\"use X\", true)").WithoutTrivia();
        Assert.That(list.Parent, Is.Null);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(list, "\n"));
    }

    [Test]
    public void SafeSite_AttributeTextEditBuilder_94_DetachedAttributeWithArguments_IsAccepted()
    {
        var attribute = ParseAttributeList("Obsolete(\"use X\", true)").Attributes.First().WithoutTrivia();
        Assert.That(attribute.Parent, Is.Null);
        Assert.DoesNotThrow(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(attribute, "\n"));
    }

    [Test]
    public void PinnedNeedsC3_BasicRefactoring_ExtractInterface_795_NewRoot_IsRejected()
    {
        // C3 should add a SafeSite_ twin of this test (the same root built with elastic trivia or none) and
        // delete this pin once the call site is migrated.
        // Needs elastic trivia (or none) instead of MemberTrivia at the call site in C3: the site deliberately
        // sets non-elastic CRLF + indentation trivia on each interface member (MemberTrivia).
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

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(ifaceCompUnit, "\r\n", elasticTrivia: false), "extract-interface new root", "root");
    }

    [Test]
    public void PinnedNeedsC3_AdvancedStructural_ExtractSuperclass_126_MixedRoot_IsRejected()
    {
        // C3 should add a SafeSite_ twin of this test (the properties detached or rebuilt before embedding)
        // and delete this pin once the call site is migrated.
        // Needs a detach/rebuild step for the embedded properties in C3. Provenance of this site is unverified
        // (proposal open question 4): the new root embeds the existing
        // class's property declarations, so the content check correctly sees user formatting here.
        var root = Parse("using System;\n\nnamespace Demo;\n\npublic class Customer\n{\n    public string Name { get; set; }\n    public int Age { get; set; }\n}\n");
        var properties = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members.OfType<PropertyDeclarationSyntax>().ToArray();
        var baseClass = ClassDeclaration("PersonBase")
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AbstractKeyword))
            .AddMembers(properties);
        var baseUnit = CompilationUnit().WithUsings(root.Usings)
            .AddMembers(FileScopedNamespaceDeclaration(ParseName("Demo")).AddMembers(baseClass));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(baseUnit, "\n"), "mixed new root", "root");
    }

    // ---- Null arguments and parameter names ----

    [Test]
    public void NormalizeNewChild_NullNode_ThrowsArgumentNullException_WithNodeParamName()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace<MethodDeclarationSyntax>(null!, "\n"));
        Assert.That(ex!.ParamName, Is.EqualTo("node"));
    }

    [Test]
    public void NormalizeNewRoot_NullRoot_ThrowsArgumentNullException_WithRootParamName()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(null!, "\n"));
        Assert.That(ex!.ParamName, Is.EqualTo("root"));
    }

    [Test]
    public void NormalizeNewRoot_RejectionsCarryRootParamName_ForCheck3()
    {
        var root = Parse("public class C { }", @"C:\repo\Existing.cs");
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(root, "\n"));
        Assert.That(ex!.ParamName, Is.EqualTo("root"));
    }

    // ---- eol validation (first check in both public methods) ----

    [Test]
    public void NormalizeNewChild_NullEol_ThrowsArgumentNullException_BeforeAnyOtherCheck()
    {
        // The node is null as well, so this only passes if eol is validated first.
        var ex = Assert.Throws<ArgumentNullException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace<MethodDeclarationSyntax>(null!, null!));
        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    [Test]
    public void NormalizeNewRoot_NullEol_ThrowsArgumentNullException_BeforeAnyOtherCheck()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(null!, null!));
        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    [TestCase("")]
    [TestCase("\r")]
    [TestCase("\n\n")]
    [TestCase("\r\n\r\n")]
    [TestCase("\n\r")]
    [TestCase(" ")]
    [TestCase("<br>")]
    public void NormalizeNewChild_InvalidEol_ThrowsArgumentException_WithEolParamName(string eol)
    {
        // Assert.Throws is exact-type, so this also proves the result is not an ArgumentNullException.
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), eol));
        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    [TestCase("")]
    [TestCase("\r")]
    [TestCase("\n\n")]
    [TestCase("\r\n\r\n")]
    [TestCase("\n\r")]
    [TestCase(" ")]
    [TestCase("<br>")]
    public void NormalizeNewRoot_InvalidEol_ThrowsArgumentException_WithEolParamName(string eol)
    {
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(NewRoot(), eol));
        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    [Test]
    public void NormalizeNewChild_InvalidEol_IsReportedBeforeCompilationUnitAndTreeChecks()
    {
        var attached = ParseClass("public class C { public void M() { } }").Members.OfType<MethodDeclarationSyntax>().First();

        var unitEx = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(CompilationUnit(), "x"));
        var attachedEx = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(attached, "x"));

        Assert.That(unitEx!.ParamName, Is.EqualTo("eol"));
        Assert.That(attachedEx!.ParamName, Is.EqualTo("eol"));
    }

    [Test]
    public void NormalizeNewRoot_InvalidEol_IsReportedBeforeTreeChecks()
    {
        var root = Parse("public class C { }", @"C:\repo\Existing.cs");
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(root, "x"));
        Assert.That(ex!.ParamName, Is.EqualTo("eol"));
    }

    // ---- Check 4: directives, disabled text, skipped tokens, conflict markers; scope of the elastic skip ----

    [Test]
    public void ContentCheck_RejectsRegionDirective_WhoseEndOfLineIsHiddenInsideTheDirective()
    {
        // DescendantTrivia(descendIntoTrivia: false) yields a directive as ONE trivia. Its line break lives in the
        // directive's end-of-directive token and is never enumerated, so the directive itself has to be counted.
        var directive = ParseLeadingTrivia("#region Generated\n");
        Assert.That(directive.Count, Is.EqualTo(1));
        Assert.That(directive[0].IsDirective, Is.True);
        var node = NewVoidMethod().WithLeadingTrivia(directive);
        Assert.That(
            node.DescendantTrivia(descendIntoTrivia: false).Count(t => t.IsKind(SyntaxKind.EndOfLineTrivia)),
            Is.Zero,
            "premise: no end-of-line trivia is visible, so only the directive can trigger the rejection");

        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));

        Assert.That(ex!.Message, Does.Contain("contains 1 piece(s)"));
        Assert.That(ex.Message, Does.Contain(ReplaceHint));
        Assert.That(ex.ParamName, Is.EqualTo("node"));
    }

    [Test]
    public void ContentCheck_RejectsParsedRegionMember()
    {
        var node = ParseMemberDeclaration("#region R\npublic void Run() { }\n#endregion\n")!;
        Assert.That(node.Parent, Is.Null);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "parsed #region member");
    }

    [Test]
    public void ContentCheck_RejectsDisabledText()
    {
        var node = NewVoidMethod().WithLeadingTrivia(DisabledText("int x;"));
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
        Assert.That(ex!.Message, Does.Contain("contains 1 piece(s)"));
    }

    [Test]
    public void ContentCheck_RejectsSkippedTokensTrivia()
    {
        var skipped = Trivia(SkippedTokensTrivia(TokenList(Token(SyntaxKind.SemicolonToken))));
        var node = NewVoidMethod().WithLeadingTrivia(skipped);
        Assert.That(node.GetLeadingTrivia()[0].IsKind(SyntaxKind.SkippedTokensTrivia), Is.True, "premise");
        var ex = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"));
        Assert.That(ex!.Message, Does.Contain("contains 1 piece(s)"));
    }

    [Test]
    public void ContentCheck_RejectsConflictMarkerTrivia()
    {
        var marker = ParseLeadingTrivia("<<<<<<< HEAD\n");
        Assert.That(marker.Any(t => t.IsKind(SyntaxKind.ConflictMarkerTrivia)), Is.True, "premise");
        var node = NewVoidMethod().WithLeadingTrivia(marker);
        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "conflict marker");
    }

    [Test]
    public void ContentCheck_ElasticAnnotationDoesNotExemptCommentsOrDirectives()
    {
        var candidates = new[]
        {
            Comment("// kept").WithAdditionalAnnotations(SyntaxAnnotation.ElasticAnnotation),
            ParseLeadingTrivia("#region R\n")[0].WithAdditionalAnnotations(SyntaxAnnotation.ElasticAnnotation),
            DisabledText("x").WithAdditionalAnnotations(SyntaxAnnotation.ElasticAnnotation),
        };

        foreach (var trivia in candidates)
        {
            var node = NewVoidMethod().WithLeadingTrivia(trivia);
            Assert.That(node.GetLeadingTrivia()[0].HasAnnotation(SyntaxAnnotation.ElasticAnnotation), Is.True, $"premise: {trivia.Kind()} is annotated elastic");
            AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), $"elastic {trivia.Kind()}");
        }
    }

    // ---- Nodes derived from a file-backed tree: check 3 only sees the tree the node now lives in ----

    private const string ExistingFilePath = @"C:\repo\Existing.cs";

    private const string ExistingMultiLineSource =
        "using System;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a, int b)\n    {\n        return a + b;\n    }\n}\n";

    private static MethodDeclarationSyntax ExistingMultiLineMethod(out CompilationUnitSyntax root)
    {
        root = Parse(ExistingMultiLineSource, ExistingFilePath);
        Assert.That(root.SyntaxTree.FilePath, Is.EqualTo(ExistingFilePath), "control: the source tree has a file path");
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
    }

    private static TypeSyntax LongType() => PredefinedType(Token(SyntaxKind.LongKeyword)).WithTrailingTrivia(Space);

    // Observed Roslyn behaviour (falsifies the hypothesis that a derived node loses the file path): ReplaceNode and
    // With* on a node of a file-backed tree return a parentless node whose SyntaxTree.FilePath is still the source
    // file's path, so check 3 already rejects them. The path is only lost when the node is re-rooted through
    // CSharpSyntaxTree.Create, reparsed from text, or rebuilt with SyntaxFactory from reused parts; the second half
    // of this helper covers that case so check 4 is proven to be an independent safety net.
    private static void AssertDerivedNodeRejected(SyntaxNode produced, Action<SyntaxNode> normalize, string paramName, string because)
    {
        Assert.That(produced.Parent, Is.Null, "derived node is parentless");
        Assert.That(produced.SyntaxTree.FilePath, Is.EqualTo(ExistingFilePath), "derived node keeps the source tree's file path");
        var ex = Assert.Throws<ArgumentException>(() => normalize(produced), because);
        Assert.That(ex!.Message, Does.Contain("has a file path"), because);
        Assert.That(ex.ParamName, Is.EqualTo(paramName), because);

        var pathless = PathlessCopy(produced);
        AssertContentCheckRejected(() => normalize(pathless), $"{because} (path lost)", paramName);
    }

    private static SyntaxNode PathlessCopy(SyntaxNode node)
    {
        var copy = CSharpSyntaxTree.Create((CSharpSyntaxNode)node).GetRoot();
        Assert.That(copy.Parent, Is.Null, "pathless copy is parentless");
        Assert.That(copy.SyntaxTree.FilePath, Is.Empty, "pathless copy has no file path");
        return copy;
    }

    private static void NormalizeChild(SyntaxNode node) => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n");

    private static void NormalizeRoot(SyntaxNode node) => RoslynFormattingHelper.NormalizeNewRootWhitespace((CompilationUnitSyntax)node, "\n");

    [Test]
    public void FileBackedMember_ProducedWithReplaceNode_IsRejected()
    {
        var method = ExistingMultiLineMethod(out _);
        var produced = method.ReplaceNode(method.ReturnType, LongType());
        AssertDerivedNodeRejected(produced, NormalizeChild, "node", "member via ReplaceNode");
    }

    [Test]
    public void FileBackedMember_ProducedWithWithoutLeadingTrivia_IsRejected()
    {
        var method = ExistingMultiLineMethod(out _);
        var produced = method.WithoutLeadingTrivia();
        AssertDerivedNodeRejected(produced, NormalizeChild, "node", "member via WithoutLeadingTrivia");
    }

    [Test]
    public void FileBackedRoot_ProducedWithReplaceNode_IsRejected()
    {
        var method = ExistingMultiLineMethod(out var root);
        var produced = root.ReplaceNode(method.ReturnType, LongType());
        AssertDerivedNodeRejected(produced, NormalizeRoot, "root", "root via ReplaceNode");
    }

    [Test]
    public void FileBackedRoot_ProducedWithWithoutLeadingTrivia_IsRejected()
    {
        ExistingMultiLineMethod(out var root);
        var produced = root.WithoutLeadingTrivia();
        AssertDerivedNodeRejected(produced, NormalizeRoot, "root", "root via WithoutLeadingTrivia");
    }

    [Test]
    public void FileBackedSingleLineMember_WithoutTrivia_IsRejectedByCheck3_ButAcceptedOncePathIsLost_DocumentedResidualRisk()
    {
        var root = Parse("public class Svc { public int Total(int a, int b) { return a + b; } }", ExistingFilePath);
        Assert.That(root.SyntaxTree.FilePath, Is.EqualTo(ExistingFilePath), "control");
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var produced = method.WithoutTrivia();

        var ex = Assert.Throws<ArgumentException>(() => NormalizeChild(produced));
        Assert.That(ex!.Message, Does.Contain("has a file path"));

        // Documented residual risk: once the file path is lost (re-rooted with CSharpSyntaxTree.Create, reparsed from
        // text, or rebuilt with SyntaxFactory from reused parts), a genuinely single-line member carries only
        // single-space trivia, so check 4 has nothing to reject and existing code is accepted. No public API can tell
        // it from a freshly built node, and normalizing it is harmless because it has no layout to lose. A multi-line
        // member is still caught by check 4 (see the FileBackedMember_ tests above).
        var pathless = PathlessCopy(produced);
        Assert.DoesNotThrow(() => NormalizeChild(pathless));
    }

    // ---- indentation and elasticTrivia are forwarded ----

    [Test]
    public void NormalizeNewChild_HonoursTabIndentation()
    {
        var tabs = RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), "\n", indentation: "\t").ToFullString();
        var spaces = RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), "\n").ToFullString();

        Assert.That(tabs, Does.Contain("\n\tGo();"));
        Assert.That(tabs, Does.Not.Contain("    "));
        Assert.That(spaces, Does.Contain("\n    Go();"), "control: the default indentation is four spaces");
    }

    [Test]
    public void NormalizeNewRoot_HonoursTabIndentation()
    {
        var text = RoslynFormattingHelper.NormalizeNewRootWhitespace(NewRoot(), "\n", indentation: "\t").ToFullString();

        Assert.That(text, Does.Contain("\n\tpublic void Run()"));
        Assert.That(text, Does.Contain("\n\t\tGo();"));
        Assert.That(text, Does.Not.Contain("    "));
    }

    [Test]
    public void NormalizeNewChild_ElasticTriviaFalse_IsForwarded()
    {
        var elastic = RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), "\n");
        var plain = RoslynFormattingHelper.NormalizeNewChildWhitespace(NewVoidMethod(), "\n", elasticTrivia: false);

        const string Expected = "public void Run()\n{\n    Go();\n}";
        Assert.That(elastic.ToFullString(), Is.EqualTo(Expected));
        Assert.That(plain.ToFullString(), Is.EqualTo(Expected));

        var elasticEols = elastic.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).ToList();
        var plainEols = plain.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).ToList();
        Assert.That(elasticEols, Is.Not.Empty);
        Assert.That(elasticEols.All(t => t.HasAnnotation(SyntaxAnnotation.ElasticAnnotation)), Is.True, "default elasticTrivia: true");
        Assert.That(plainEols, Is.Not.Empty);
        Assert.That(plainEols.Any(t => t.HasAnnotation(SyntaxAnnotation.ElasticAnnotation)), Is.False, "elasticTrivia: false");
    }

    [Test]
    public void NormalizeNewRoot_ElasticTriviaFalse_IsForwarded()
    {
        var elastic = RoslynFormattingHelper.NormalizeNewRootWhitespace(NewRoot(), "\n");
        var plain = RoslynFormattingHelper.NormalizeNewRootWhitespace(NewRoot(), "\n", elasticTrivia: false);

        Assert.That(plain.ToFullString(), Is.EqualTo(elastic.ToFullString()));
        Assert.That(elastic.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).All(t => t.HasAnnotation(SyntaxAnnotation.ElasticAnnotation)), Is.True);
        Assert.That(plain.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).Any(t => t.HasAnnotation(SyntaxAnnotation.ElasticAnnotation)), Is.False);
    }
}
