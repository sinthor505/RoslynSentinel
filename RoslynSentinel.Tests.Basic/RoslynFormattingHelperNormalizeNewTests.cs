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
/// sites are accepted. The "PinnedC6_" tests pin the raw-NormalizeWhitespace sites that stay rejected until
/// slice C6 (annotate-and-format or a comment-keeping new-root variant): their new roots embed existing code
/// with comments. "PinnedNeedsC3_" pins the ElasticWhitespace(text) case, which cannot be told apart from user
/// indentation. They document behaviour and must be revisited when the owning slice lands.
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

    private static void AssertContentCheckRejected(Action act, string because, string paramName = "node", int? expectedPieces = null)
    {
        var ex = Assert.Throws<ArgumentException>(() => act(), because);
        Assert.That(ex!.Message, Does.Match(@"contains \d+ piece\(s\) of non-elastic"), because);
        if (expectedPieces is not null)
        {
            Assert.That(ex.Message, Does.Contain($"contains {expectedPieces} piece(s) of non-elastic"), because + " (offending piece count)");
        }

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
    public void PinnedNeedsDesign_MemberRefactoring_ExtractMethod_4569_EmbeddedUserStatementWithNestedComment_IsRejected()
    {
        // Site 4569 is deliberately NOT migrated in slice C3-G2: the extracted body embeds the user's own
        // statements, which can carry comments and multi-line formatting nested inside child blocks (the engine
        // already strips the top-level leading/trailing trivia of each statement, so only nested trivia remains).
        // This pin and the multi-line one below pin the GUARD's rejection of such nested trivia; they do not
        // describe engine behaviour. The engine keeps the raw NormalizeWholeSubtreeWhitespace call until a helper
        // exists that formats a new shell around existing statements; flip these pins when that helper lands.
        var statement = ParseStatement("if (a > b)\n{\n    total = a; // pick the larger\n}\n");
        var node = MethodDeclaration(ParseTypeName("int"), "ExtractedSum")
            .WithBody(Block(statement.WithoutLeadingTrivia(), ReturnStatement(IdentifierName("total"))));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "embedded user statement with a nested comment");
    }

    [Test]
    public void PinnedNeedsDesign_MemberRefactoring_ExtractMethod_4569_EmbeddedMultiLineStatement_IsRejected()
    {
        var statement = ParseStatement("if (a > b)\n        {\n            total = a;\n        }\n");
        var node = MethodDeclaration(ParseTypeName("int"), "ExtractedSum")
            .WithBody(Block(statement.WithoutLeadingTrivia(), ReturnStatement(IdentifierName("total"))));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewChildWhitespace(node, "\n"), "embedded multi-line user statement");
    }

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
        // the engine detaches it and strips the user's inner formatting before normalizing. The document is LF but
        // the caller passes CRLF, so the trailing line break proves the PASSED eol is used (BuildAddEdit does not
        // detect one); the attribute text itself is single-line, so only the appended break can show it.
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"use X\",\r\n      true)");
        Assert.That(list, Is.Not.Null);
        Assert.That(list!.Parent, Is.Not.Null);

        var text = Microsoft.CodeAnalysis.Text.SourceText.From("public class C\n{\n}\n");
        var target = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();

        var edit = AttributeTextEditBuilder.BuildAddEdit(0, target, list, text, "\r\n");

        Assert.That(edit.NewText, Is.EqualTo("[Obsolete(\"use X\", true)]\r\n"));
    }

    [Test]
    public void SafeSite_AttributeTextEditBuilder_94_ParsedAttachedAttribute_BuildReplaceEdit_IsAcceptedAndNormalizesSpacing()
    {
        // The replacement is a single-line attribute, so the eol argument cannot show up in the output; this pins
        // only that an attached parsed attribute is accepted, its spacing normalized and the span preserved.
        var text = Microsoft.CodeAnalysis.Text.SourceText.From("[Obsolete]\r\npublic class C\r\n{\r\n}\r\n");
        var oldAttribute = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<AttributeSyntax>().First();
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"y\",   true)")!.Attributes.First();
        Assert.That(newAttribute.Parent, Is.Not.Null);

        var edit = AttributeTextEditBuilder.BuildReplaceEdit(0, oldAttribute, newAttribute, "\n");

        Assert.That(edit.NewText, Is.EqualTo("Obsolete(\"y\", true)"));
        Assert.That(edit.Start, Is.EqualTo(oldAttribute.SpanStart));
        Assert.That(edit.End, Is.EqualTo(oldAttribute.Span.End));
    }

    [Test]
    public void AttributeTextEditBuilder_BuildAddEdit_InnerBlockComment_IsRejectedWithStructuredError()
    {
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"x\" /* why */)");
        var (text, target) = ParseClassTarget("public class C\n{\n}\n");

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list!, text, "\n"), "inner block comment");
    }

    [Test]
    public void AttributeTextEditBuilder_BuildAddEdit_InnerLineComment_IsRejectedWithStructuredError()
    {
        var list = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"x\", // why\n true)");
        var (text, target) = ParseClassTarget("public class C\n{\n}\n");

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list!, text, "\n"), "inner // comment");
    }

    [Test]
    public void AttributeTextEditBuilder_BuildAddEdit_LeadingBoundaryComment_IsRejectedLikeInnerComment()
    {
        // A comment before the first token used to be dropped silently by WithoutTrivia while an inner one threw;
        // both must now behave the same way.
        var list = AttributeTextEditBuilder.ParseAttributeList("[/* lead */ Obsolete]");
        var (text, target) = ParseClassTarget("public class C\n{\n}\n");

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list!, text, "\n"), "leading boundary comment");
    }

    [Test]
    public void AttributeTextEditBuilder_BuildAddEdit_TrailingBoundaryComment_IsRejectedLikeInnerComment()
    {
        var list = AttributeTextEditBuilder.ParseAttributeList("[Obsolete] // trailing");
        var (text, target) = ParseClassTarget("public class C\n{\n}\n");

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildAddEdit(0, target, list!, text, "\n"), "trailing boundary comment");
    }

    [Test]
    public void AttributeTextEditBuilder_BuildReplaceEdit_InnerComment_IsRejectedWithStructuredError()
    {
        var oldAttribute = ParseClassWithAttribute().DescendantNodes().OfType<AttributeSyntax>().First();
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"y\" /* why */)")!.Attributes.First();

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildReplaceEdit(0, oldAttribute, newAttribute, "\n"), "inner comment on an attached attribute");
    }

    [Test]
    public void AttributeTextEditBuilder_BuildReplaceEdit_CommentOnList_IsRejectedWithStructuredError()
    {
        // The comment sits outside the AttributeSyntax node (on the list), so only the list-level check sees it.
        var oldAttribute = ParseClassWithAttribute().DescendantNodes().OfType<AttributeSyntax>().First();
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("[Obsolete] /* why */")!.Attributes.First();

        AssertCommentsRejected(() => AttributeTextEditBuilder.BuildReplaceEdit(0, oldAttribute, newAttribute, "\n"), "comment on the list");
    }

    [Test]
    public void AttributeTextEditBuilder_BuildReplaceEdit_DetachedCommentFreeAttribute_KeepsEditIndexAndSpan()
    {
        var oldAttribute = ParseClassWithAttribute().DescendantNodes().OfType<AttributeSyntax>().First();
        var newAttribute = AttributeTextEditBuilder.ParseAttributeList("Obsolete(\"z\")")!.Attributes.First().WithoutTrivia();

        var edit = AttributeTextEditBuilder.BuildReplaceEdit(3, oldAttribute, newAttribute, "\r\n");

        Assert.That(edit.NewText, Is.EqualTo("Obsolete(\"z\")"));
        Assert.That(edit.EditIndex, Is.EqualTo(3));
        Assert.That(edit.Start, Is.EqualTo(oldAttribute.SpanStart));
    }

    [Test]
    public void StripFormattingTriviaForNormalize_KeepsBoundaryComments_StripAllTriviaForNormalize_DropsThem()
    {
        var list = AttributeTextEditBuilder.ParseAttributeList("[/* lead */ Obsolete] // trailing")!;

        var kept = RoslynFormattingHelper.StripFormattingTriviaForNormalize(list);
        var dropped = RoslynFormattingHelper.StripAllTriviaForNormalize(list);

        Assert.That(kept.ToFullString(), Does.Contain("/* lead */").And.Contain("// trailing"));
        Assert.That(dropped.ToFullString(), Does.Not.Contain("/*").And.Not.Contain("//"));
        Assert.That(kept.Parent, Is.Null);
        Assert.That(dropped.Parent, Is.Null);
    }

    private const string CommentsRejectedMessage = "attribute text must not contain comments; remove the comment and retry";

    private static (Microsoft.CodeAnalysis.Text.SourceText Text, ClassDeclarationSyntax Target) ParseClassTarget(string source)
    {
        var text = Microsoft.CodeAnalysis.Text.SourceText.From(source);
        var target = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        return (text, target);
    }

    private static void AssertCommentsRejected(Action act, string because)
    {
        var ex = Assert.Throws<ToolInvalidArgumentException>(() => act(), because);
        Assert.That(ex!.Message, Is.EqualTo(CommentsRejectedMessage), because);
        Assert.That(ex.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument), because);
        Assert.That(ex.Message, Does.Not.Contain("NormalizeWhitespace"), because);
        Assert.That(ex.Message, Does.Not.Contain(ReplaceHint), because);
    }

    private static CompilationUnitSyntax ParseClassWithAttribute()
        => Parse("[Obsolete]\npublic class C\n{\n}\n");

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

    private static CompilationUnitSyntax BuildExtractInterfaceRoot(string source)
    {
        // Mirrors BasicRefactoringEngine.ExtractInterfaceAsync: signature parts and usings are stripped of all trivia
        // (the usings keep their leading header), the interface members carry no hand-set trivia.
        var root = Parse(source);
        var m = root.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var ifaceMethod = (MemberDeclarationSyntax)MethodDeclaration(RoslynFormattingHelper.StripAllTriviaForNormalize(m.ReturnType), m.Identifier.WithoutTrivia())
            .WithParameterList(RoslynFormattingHelper.StripAllTriviaForNormalize(m.ParameterList))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        var iface = InterfaceDeclaration("ISvc").AddModifiers(Token(SyntaxKind.PublicKeyword)).AddMembers(ifaceMethod);
        var usings = List(root.Usings.Select(RoslynFormattingHelper.StripUsingKeepingLeadingTrivia));
        var ns = FileScopedNamespaceDeclaration(RoslynFormattingHelper.StripAllTriviaForNormalize(root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().First().Name)).AddMembers(iface);
        return CompilationUnit().WithUsings(usings).AddMembers(ns);
    }

    private const string ExtractInterfaceSource = "using System;\nusing System.Linq;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a, int b) => a + b;\n}\n";

    [Test]
    public void SafeSite_BasicRefactoring_ExtractInterface_795_NewRoot_IsAccepted()
    {
        var ifaceCompUnit = BuildExtractInterfaceRoot(ExtractInterfaceSource);

        var result = RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(ifaceCompUnit, "\r\n", elasticTrivia: false);

        Assert.That(result.ToFullString(), Is.EqualTo("using System;\r\nusing System.Linq;\r\n\r\nnamespace Demo;\r\npublic interface ISvc\r\n{\r\n    int Total(int a, int b);\r\n}"));
    }

    [Test]
    public void NormalizeNewRoot_ExtractInterfaceShapeWithUsingHeader_IsRejectedByPlainRootMethod()
    {
        // The plain root method keeps rejecting a copied using header; only the KeepingUsingHeaders variant exempts it.
        var ifaceCompUnit = BuildExtractInterfaceRoot("// Copyright\nusing System;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a) => a;\n}\n");

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(ifaceCompUnit, "\n", elasticTrivia: false), "using header is existing trivia", "root");
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_KeepsHeaderCommentAndDirective()
    {
        var ifaceCompUnit = BuildExtractInterfaceRoot("// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a) => a;\n}\n");

        var result = RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(ifaceCompUnit, "\n", elasticTrivia: false).ToFullString();

        Assert.That(result, Does.StartWith("// Copyright (c) Acme\n#nullable enable\nusing System;\n"));
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_UsesTheGivenEolForTheHeaderAndTheBody()
    {
        var ifaceCompUnit = BuildExtractInterfaceRoot("// Copyright (c) Acme\r\nusing System;\r\n\r\nnamespace Demo;\r\n\r\npublic class Svc\r\n{\r\n    public int Total(int a) => a;\r\n}\r\n");

        var result = RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(ifaceCompUnit, "\r\n", elasticTrivia: false).ToFullString();

        Assert.That(result.Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
        Assert.That(result, Does.StartWith("// Copyright (c) Acme\r\nusing System;\r\n"));
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_StillRejectsCommentOnAMember()
    {
        var iface = InterfaceDeclaration("ISvc").AddMembers(
            MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Run")
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithLeadingTrivia(Comment("// existing")));
        // Usings are built the way the engine builds them (inner and trailing trivia stripped, header kept), so the
        // member comment is the ONLY offending piece.
        var usings = List(Parse("// Copyright\nusing System;\n").Usings.Select(RoslynFormattingHelper.StripUsingKeepingLeadingTrivia));
        var root = CompilationUnit().WithUsings(usings).AddMembers(iface);

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(root, "\n"), "member comment is not a using header", "root", expectedPieces: 1);
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_StillRejectsFormattingInsideAUsing()
    {
        // The trailing end-of-line is removed (as the engine does) so the inner multi-space whitespace between the
        // keyword and the name is the ONLY offending piece.
        var usings = List(Parse("using   System;\n").Usings.Select(u => u.WithTrailingTrivia(SyntaxTriviaList.Empty)));
        var root = CompilationUnit().WithUsings(usings);

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(root, "\n"), "inner using formatting must be stripped by the caller", "root", expectedPieces: 1);
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_StripUsingKeepingLeadingTrivia_KeepsOnlyTheHeader()
    {
        var u = Parse("// Copyright\n#nullable enable\nusing   System; // trailing\n").Usings.Single();

        var stripped = RoslynFormattingHelper.StripUsingKeepingLeadingTrivia(u);

        Assert.That(stripped.Parent, Is.Null);
        Assert.That(stripped.GetLeadingTrivia().ToFullString(), Is.EqualTo(u.GetLeadingTrivia().ToFullString()));
        Assert.That(stripped.DescendantTokens().Last().TrailingTrivia, Is.Empty);
        Assert.That(stripped.UsingKeyword.TrailingTrivia, Is.Empty);
        Assert.Throws<ArgumentNullException>(() => RoslynFormattingHelper.StripUsingKeepingLeadingTrivia(null!));
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_KnownLimitation_DirectiveClosedAfterTheLastUsingIsUnbalanced()
    {
        // Pins a documented limitation: the #endif is leading trivia of the namespace token, which the engine rebuilds,
        // so the carried #if has no #endif (CS1027). Delete this pin if the variant ever carries that trivia.
        var ifaceCompUnit = BuildExtractInterfaceRoot("#if true\nusing System;\n#endif\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a) => a;\n}\n");

        var result = RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(ifaceCompUnit, "\n", elasticTrivia: false).ToFullString();

        Assert.That(result, Does.Contain("#if true"));
        Assert.That(result, Does.Not.Contain("#endif"));
        Assert.That(Parse(result).GetDiagnostics().Select(d => d.Id), Does.Contain("CS1027"));
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_KnownLimitation_HeaderOnAFileWithNoUsingsIsNotCarried()
    {
        var ifaceCompUnit = BuildExtractInterfaceRoot("// Copyright (c) Acme\nnamespace Demo;\n\npublic class Svc\n{\n    public int Total(int a) => a;\n}\n");

        var result = RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(ifaceCompUnit, "\n", elasticTrivia: false).ToFullString();

        Assert.That(result, Does.Not.Contain("Copyright"));
    }

    [Test]
    public void NormalizeNewRootKeepingUsingHeaders_RejectsBadEolAndNullRoot()
    {
        var eolEx = Assert.Throws<ArgumentException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(CompilationUnit(), "\r"));
        Assert.That(eolEx!.ParamName, Is.EqualTo("eol"));
        var nullEx = Assert.Throws<ArgumentNullException>(() => RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(null!, "\n"));
        Assert.That(nullEx!.ParamName, Is.EqualTo("root"));
    }

    [Test]
    public void PinnedC6_AdvancedStructural_ExtractSuperclass_126_MixedRoot_IsRejected()
    {
        // Slice C3-G3a deliberately left this call site on the raw NormalizeWholeSubtreeWhitespace (C6 owns it):
        // the new root embeds the existing class's WHOLE property declarations (XML docs, attributes, comments),
        // so the guard correctly rejects them, and stripping them would silently lose user comments. Migrating
        // needs a comment-preserving variant of NormalizeNewRootWhitespace or an annotate-and-format rewrite.
        // Delete this pin and add a SafeSite_ twin when it is migrated.
        var root = Parse("using System;\n\nnamespace Demo;\n\npublic class Customer\n{\n    public string Name { get; set; }\n    public int Age { get; set; }\n}\n");
        var properties = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members.OfType<PropertyDeclarationSyntax>().ToArray();
        var baseClass = ClassDeclaration("PersonBase")
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.AbstractKeyword))
            .AddMembers(properties);
        var baseUnit = CompilationUnit().WithUsings(root.Usings)
            .AddMembers(FileScopedNamespaceDeclaration(ParseName("Demo")).AddMembers(baseClass));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(baseUnit, "\n"), "mixed new root", "root");
    }

    [Test]
    public void PinnedC6_AdvancedStructural_ExtractMembersToPartial_1108_MixedRoot_IsRejected()
    {
        // Slice C3-G3b deliberately left this call site on the raw NormalizeWholeSubtreeWhitespace (C6 owns it), like :126:
        // the new root embeds the existing class's WHOLE members (comments, bodies, indentation) and the file's using
        // directives, so the guard correctly rejects them, and stripping them would silently lose user comments. The site
        // also patches the output with a ";namespace" string replace that C6 should drop. Migrating needs a
        // comment-preserving variant of NormalizeNewRootWhitespace or an annotate-and-format rewrite.
        // Delete this pin and add a SafeSite_ twin when it is migrated.
        var root = Parse("using System;\n\nnamespace Demo;\n\npublic partial class Customer\n{\n    // the name\n    public string Name { get; set; }\n    public int Total(int a) => a;\n}\n");
        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
        var membersToMove = classNode.Members.Where(m => m is MethodDeclarationSyntax || m is PropertyDeclarationSyntax).ToList();
        var newClassNode = ClassDeclaration("Customer")
            .WithModifiers(TokenList(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.PartialKeyword)))
            .WithMembers(List(membersToMove));
        var namespaceDecl = NamespaceDeclaration(ParseName("Demo")).WithMembers(SingletonList<MemberDeclarationSyntax>(newClassNode));
        var newCompilationUnit = CompilationUnit()
            .WithUsings(List(root.ChildNodes().OfType<UsingDirectiveSyntax>()))
            .WithMembers(SingletonList<MemberDeclarationSyntax>(namespaceDecl));

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespace(newCompilationUnit, "\n"), "mixed new root", "root");
    }

    // The class file root of StructuralRefactoringEngine.ConvertTupleToClassAsync (AdvancedStructuralEngine :1625): new class,
    // but it embeds the existing file's using directives, namespace name and tuple element types.
    private static CompilationUnitSyntax BuildConvertTupleClassRoot(string source)
    {
        var root = Parse(source);
        var tuple = root.DescendantNodes().OfType<TupleTypeSyntax>().First();
        var properties = tuple.Elements.Select(e => (MemberDeclarationSyntax)PropertyDeclaration(e.Type, e.Identifier.Text)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)),
                AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken)))).ToArray();
        var newClass = ClassDeclaration("DataResult").AddModifiers(Token(SyntaxKind.PublicKeyword)).AddMembers(properties);
        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var classRoot = CompilationUnit().WithUsings(root.Usings);
        return ns is null
            ? classRoot.AddMembers(newClass)
            : classRoot.AddMembers(FileScopedNamespaceDeclaration(ns.Name).AddMembers(newClass));
    }

    [Test]
    public void PinnedC6_AdvancedStructural_ConvertTupleToClass_1625_CommentInsideTupleElementType_IsRejected()
    {
        // Slice C3-G3b left this site on the raw helper (C6 owns it). NormalizeWhitespace keeps the comment between a tuple
        // element's type and its name ("public int /* id */ Id"), so the old output kept it; the guard rejects every comment
        // and stripping the embedded type would lose it silently.
        // Delete this pin and add a SafeSite_ twin when it is migrated.
        var classRoot = BuildConvertTupleClassRoot("public class Svc\n{\n    public (int /* id */ Id, string Name) GetData() => default;\n}\n");

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(classRoot, "\n"), "tuple element type comment", "root");
    }

    [Test]
    public void PinnedC6_AdvancedStructural_ConvertTupleToClass_1625_TrailingCommentOnUsing_IsRejected()
    {
        // Only the LEADING trivia of a using (the file header) is exempt from the guard. A trailing comment on a using
        // ("using System; // for Console") was kept by the old output and is rejected here.
        // Delete this pin and add a SafeSite_ twin when it is migrated.
        var classRoot = BuildConvertTupleClassRoot("using System; // for Console\n\npublic class Svc\n{\n    public (int Id, string Name) GetData() => default;\n}\n");

        AssertContentCheckRejected(() => RoslynFormattingHelper.NormalizeNewRootWhitespaceKeepingUsingHeaders(classRoot, "\n"), "using trailing comment", "root");
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
