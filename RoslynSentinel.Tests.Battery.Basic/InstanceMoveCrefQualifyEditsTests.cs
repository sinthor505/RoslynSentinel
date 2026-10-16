using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Slice Q3-3t: InstanceMoveCrefQualifyEdits (CollectMovedDocQualifyEditsAsync and HasBareCrefInMovedDocs). Every
/// document-based test asserts DocumentationMode.Parse, applies the edits with WithChanges and asserts the exact text,
/// and carries a control so the zero result is not vacuous.
/// </summary>
[TestFixture]
[Category("InstanceMoveCrefQualifyEdits")]
public class InstanceMoveCrefQualifyEditsTests
{
    private sealed record Loaded(
        Document Document,
        INamedTypeSymbol SourceType,
        IReadOnlyList<MemberDeclarationSyntax> Members,
        IReadOnlyList<ISymbol> Symbols);

    [Test]
    public async Task BareCrefInMovedDoc_NamingRemainingMember_IsQualifiedInsideMovedSpanAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
                "    public int Count(string input) => input.Length;",
                "",
                "    public int Other() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", s => s.Name == "Count");

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(result.Edits.Count, Is.EqualTo(1));
        Assert.That(loaded.Members.Single().FullSpan.Contains(result.Edits.Single().Span.Start), Is.True,
            "The qualify edit must lie inside the moved member's FullSpan");
        Assert.That(await ApplyAsync(loaded.Document, result.Edits), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"A.Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    public int Other() => 0;",
            "}")));
    }

    [Test]
    public async Task BareCrefInNonMovedSibling_ProducesNoEditButDocumentWideQualifierWouldAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>Counts.</summary>",
                "    public int Count(string input) => input.Length;",
                "",
                "    /// <summary>See <see cref=\"Other\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "",
                "    public int Other() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", s => s.Name == "Count");

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(loaded.Members, Is.Not.Empty, "Count must be the moved member for the span filter to be the thing under test");
        // Control: the document-wide qualifier does edit the sibling's bare cref, so the empty result is the span filter.
        var control = await CrefQualifier.QualifyDocumentAsync(
            loaded.Document, s => s.ContainingType?.Name == "A", CancellationToken.None);
        Assert.That(control.Edits.Count, Is.EqualTo(1), "Control: the sibling's bare cref is qualifiable document-wide");

        Assert.That(result.Edits, Is.Empty, "The sibling sits outside every moved member's FullSpan");
    }

    [Test]
    public async Task BareCrefInMovedDoc_NamingMovedSymbol_IsNotQualifiedAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>See <see cref=\"Helper\"/> and <see cref=\"Other\"/>.</summary>",
                "    public int Count(string input) => input.Length;",
                "",
                "    public int Helper() => 1;",
                "",
                "    public int Other() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", s => s.Name is "Count" or "Helper");

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(result.Edits.Count, Is.EqualTo(1), "Only the remaining member's cref is qualified; Helper is moved");
        Assert.That(await ApplyAsync(loaded.Document, result.Edits), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>See <see cref=\"Helper\"/> and <see cref=\"A.Other\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    public int Helper() => 1;",
            "",
            "    public int Other() => 0;",
            "}")));
    }

    [Test]
    public async Task MixedOverloadGroup_QualifiesOverloadGroupWhenAnyCandidateStays()
    {
        // Count(string) moves, Count(int) stays. The bare cref is ambiguous. The filter passes when any candidate passes
        // (Count(int) does), so the cref is qualified. The prefix comes from the first candidate; both share type A.
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
                "    public int Count(string input) => input.Length;",
                "",
                "    public int Count(int input) => input;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A",
            s => s is IMethodSymbol { Name: "Count" } m && m.Parameters.Length == 1
                && m.Parameters[0].Type.SpecialType == SpecialType.System_String);

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(result.Edits.Count, Is.EqualTo(1));
        Assert.That(result.DroppedByRebind, Is.Zero, "The ambiguous cref still rebinds to the same candidate after the edit");
        Assert.That(await ApplyAsync(loaded.Document, result.Edits), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"A.Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    public int Count(int input) => input;",
            "}")));
    }

    [Test]
    public async Task GenericSourceType_BareCrefInMovedDoc_QualifiesWithBraceArityAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A<T>",
                "{",
                "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
                "    public int Count(T input) => 0;",
                "",
                "    public int Other() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", s => s.Name == "Count");

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(result.Edits.Count, Is.EqualTo(1));
        // Cref syntax uses braces for type arguments, so the prefix is A{T}. rather than A<T>.
        Assert.That(await ApplyAsync(loaded.Document, result.Edits), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class A<T>",
            "{",
            "    /// <summary>Counts, see <see cref=\"A{T}.Other\"/>.</summary>",
            "    public int Count(T input) => 0;",
            "",
            "    public int Other() => 0;",
            "}")));
    }

    [Test]
    public async Task EmptyMembersToMove_ReturnsNoEditsAndZeroAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>See <see cref=\"Other\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "",
                "    public int Other() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", _ => false);

        // Control: the document-wide qualifier would edit this cref, so the empty result comes from the empty inputs.
        var control = await CrefQualifier.QualifyDocumentAsync(
            loaded.Document, s => s.ContainingType?.Name == "A", CancellationToken.None);
        Assert.That(control.Edits.Count, Is.EqualTo(1));

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, Array.Empty<ISymbol>(), Array.Empty<MemberDeclarationSyntax>(),
            CancellationToken.None);

        Assert.That(result.Edits, Is.Empty);
        Assert.That(result.DroppedByRebind, Is.Zero);
    }

    [Test]
    public async Task GenericSourceType_ConstructedSourceType_StillQualifiesAsync()
    {
        // A constructed A<int> is a different symbol from the definition; the OriginalDefinition compare must still match.
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A<T>",
                "{",
                "    /// <summary>Counts, see <see cref=\"Other\"/>.</summary>",
                "    public int Count(T input) => 0;",
                "",
                "    public int Other() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", s => s.Name == "Count");
        var compilation = await loaded.Document.Project.GetCompilationAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("No compilation.");
        var constructed = loaded.SourceType.Construct(compilation.GetSpecialType(SpecialType.System_Int32));

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, constructed, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(result.Edits.Count, Is.EqualTo(1), "A constructed source type must still qualify the cref");
        Assert.That(await ApplyAsync(loaded.Document, result.Edits), Is.EqualTo(Lines(
            "namespace Example;",
            "",
            "public class A<T>",
            "{",
            "    /// <summary>Counts, see <see cref=\"A{T}.Other\"/>.</summary>",
            "    public int Count(T input) => 0;",
            "",
            "    public int Other() => 0;",
            "}")));
    }

    [Test]
    public async Task MixedOverloadGroup_AllCandidatesMoved_IsLeftAloneAsync()
    {
        // Count(string) and Count(int) both move, so the filter rejects every candidate and the cref is not edited.
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
                "    public int Count(string input) => input.Length;",
                "",
                "    public int Count(int input) => input;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var loaded = await LoadAsync(workspace, "Source.cs", "A", s => s.Name == "Count");
        Assert.That(loaded.Symbols.Count, Is.EqualTo(2), "Both overloads are moved");

        // Control: the document-wide qualifier would edit the bare cref, so the empty result is the all-moved filter.
        var control = await CrefQualifier.QualifyDocumentAsync(
            loaded.Document, s => s.ContainingType?.Name == "A", CancellationToken.None);
        Assert.That(control.Edits.Count, Is.EqualTo(1), "Control: the bare cref is qualifiable document-wide");

        var result = await InstanceMoveCrefQualifyEdits.CollectMovedDocQualifyEditsAsync(
            loaded.Document, loaded.SourceType, loaded.Symbols, loaded.Members, CancellationToken.None);

        Assert.That(result.Edits, Is.Empty, "A group where every candidate moved is left alone");
    }

    [TestCase("BareName", "    /// <summary>See <see cref=\"Count\"/>.</summary>\n    public int Process() => 0;", true)]
    [TestCase("BareMethodWithParameters", "    /// <summary>See <see cref=\"Count(int)\"/>.</summary>\n    public int Process() => 0;", true)]
    [TestCase("BareIndexer", "    /// <summary>See <see cref=\"this[int]\"/>.</summary>\n    public int this[int i] => i;", true)]
    [TestCase("BareOperator", "    /// <summary>See <see cref=\"operator +(A, A)\"/>.</summary>\n    public static A operator +(A a, A b) => a;", true)]
    [TestCase("QualifiedCref", "    /// <summary>See <see cref=\"A.Count\"/>.</summary>\n    public int Process() => 0;", false)]
    [TestCase("NoCrefs", "    /// <summary>Plain text.</summary>\n    public int Process() => 0;", false)]
    public void HasBareCrefInMovedDocs_DetectsOnlyOutermostBareMemberCrefs(string name, string member, bool expected)
    {
        var source = "namespace Example;\n\npublic class A\n{\n" + member + "\n}\n";
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(documentationMode: DocumentationMode.Parse));
        Assert.That(((CSharpParseOptions)tree.Options).DocumentationMode, Is.EqualTo(DocumentationMode.Parse),
            "The probe tree must be parsed with DocumentationMode.Parse");
        var members = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Single().Members.ToList();

        Assert.That(InstanceMoveCrefQualifyEdits.HasBareCrefInMovedDocs(members), Is.EqualTo(expected), name);
    }

    private static async Task<Loaded> LoadAsync(InMemoryWorkspace workspace, string relativePath, string typeName, Func<ISymbol, bool> isMoved)
    {
        var document = CurrentSolution(workspace).GetDocument(DocumentIdOf(workspace, relativePath))
            ?? throw new InvalidOperationException("Document not found.");
        var root = await document.GetSyntaxRootAsync() ?? throw new InvalidOperationException("No syntax root.");
        var model = await document.GetSemanticModelAsync() ?? throw new InvalidOperationException("No semantic model.");
        Assert.That(((CSharpParseOptions)root.SyntaxTree.Options).DocumentationMode, Is.EqualTo(DocumentationMode.Parse),
            "The test document must be parsed with DocumentationMode.Parse or its crefs are not parsed");

        var typeDeclaration = root.DescendantNodes().OfType<TypeDeclarationSyntax>().Single(t => t.Identifier.Text == typeName);
        var sourceType = (INamedTypeSymbol)(model.GetDeclaredSymbol(typeDeclaration) ?? throw new InvalidOperationException("No type symbol."));

        var members = new List<MemberDeclarationSyntax>();
        var symbols = new List<ISymbol>();
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member is TypeDeclarationSyntax)
            {
                continue;
            }

            var symbol = model.GetDeclaredSymbol(member);
            if (symbol != null && isMoved(symbol))
            {
                members.Add(member);
                symbols.Add(symbol);
            }
        }

        return new Loaded(document, sourceType, members, symbols);
    }

    private static async Task<string> ApplyAsync(Document document, IReadOnlyList<TextChange> edits)
    {
        var text = await document.GetTextAsync();
        return text.WithChanges(edits).ToString();
    }

    private static Solution CurrentSolution(InMemoryWorkspace workspace) =>
        workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");

    private static DocumentId DocumentIdOf(InMemoryWorkspace workspace, string relativePath) =>
        CurrentSolution(workspace).GetDocumentIdsWithFilePath(workspace.PathOf(relativePath)).Single();

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";
}
