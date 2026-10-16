using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Slice Q3-2: InstanceMoveCrefEdits.CollectRetargetEditsAsync. Every test applies the returned edits to the source
/// text and asserts the exact resulting text. ResolveMovedAsync asserts DocumentationMode.Parse on the parsed tree,
/// so crefs are real CrefSyntax nodes and the tests cannot pass vacuously.
/// </summary>
[TestFixture]
[Category("InstanceMoveCrefEdits")]
public class InstanceMoveCrefEditsTests
{
    [Test]
    public async Task QualifiedCrefInNonMovedSibling_BecomesTargetQualifiedAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    public int Count(string input) => input.Length;",
                "",
                "    /// <summary>See <see cref=\"A.Count\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var moved = await ResolveMovedAsync(solution, source, s => s.Name == "Count");

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.EqualTo(1));
        Assert.That(result.SkippedCandidates, Is.Zero);
        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { source }));
        await AssertTextAfterEditsAsync(solution, result, source, Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>See <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}"));
    }

    [Test]
    public async Task BareCrefInNonMovedSibling_GainsTargetQualifierAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    public int Count(string input) => input.Length;",
                "",
                "    /// <summary>See <see cref=\"Count\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var moved = await ResolveMovedAsync(solution, source, s => s.Name == "Count");

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.EqualTo(1));
        Assert.That(result.SkippedCandidates, Is.Zero);
        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { source }));
        await AssertTextAfterEditsAsync(solution, result, source, Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>See <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}"));
    }

    [Test]
    public async Task BareCrefInsideMovedMemberOwnDoc_ProducesNoEditAsync()
    {
        // The Process sibling keeps the bare-cref path live: its edit proves the bare cref was found, so the empty
        // result for the moved member's own doc cannot be a vacuous no-match.
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
                "    /// <summary>See <see cref=\"Count\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var moved = await ResolveMovedAsync(solution, source, s => s.Name == "Count");

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.EqualTo(1), "Only the sibling's bare cref is edited; the moved member's own bare cref is skipped");
        Assert.That(result.SkippedCandidates, Is.Zero);
        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { source }),
            "The only entry is the sibling's edit; the moved member's own bare cref produced no entry");
        await AssertTextAfterEditsAsync(solution, result, source, Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>See <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}"));
    }

    [Test]
    public async Task QualifiedCrefInsideMovedMemberOwnDoc_IsStillRetargetedAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>See <see cref=\"A.Count\"/>.</summary>",
                "    public int Count(string input) => input.Length;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var moved = await ResolveMovedAsync(solution, source, s => s.Name == "Count");

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.EqualTo(1));
        Assert.That(result.SkippedCandidates, Is.Zero);
        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { source }));
        await AssertTextAfterEditsAsync(solution, result, source, Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>See <see cref=\"B.Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "}"));
    }

    [Test]
    public async Task QualifiedCrefInSecondDocument_LandsInThatDocumentsEntryAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    public int Count(string input) => input.Length;",
                "}")),
            ("Caller.cs", Lines(
                "namespace Example;",
                "",
                "public class Caller",
                "{",
                "    /// <summary>See <see cref=\"A.Count\"/>.</summary>",
                "    public int Run() => 0;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var caller = DocumentIdOf(workspace, "Caller.cs");
        var moved = await ResolveMovedAsync(solution, source, s => s.Name == "Count");

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.EqualTo(1));
        Assert.That(result.SkippedCandidates, Is.Zero);
        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { caller }),
            "The only edit belongs to Caller.cs; the source has no crefs to the moved member");
        await AssertTextAfterEditsAsync(solution, result, caller, Lines(
            "namespace Example;",
            "",
            "public class Caller",
            "{",
            "    /// <summary>See <see cref=\"B.Count\"/>.</summary>",
            "    public int Run() => 0;",
            "}"));
    }

    [Test]
    public async Task OverloadCandidateCref_IsNotEditedAndIsCountedAsSkippedAsync()
    {
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    public int Count(string input) => input.Length;",
                "    public int Count(int input) => input;",
                "",
                "    /// <summary>See <see cref=\"Count\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        // Only the string overload moves; the bare cref in Process is ambiguous between the two overloads.
        var moved = await ResolveMovedAsync(solution, source, IsStringCount);

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.Zero, "An ambiguous cref must not be edited");
        Assert.That(result.SkippedCandidates, Is.EqualTo(1), "The ambiguous cref is counted as a skipped candidate");
        Assert.That(result.EditsByDocument, Is.Empty);
        await AssertTextAfterEditsAsync(solution, result, source, Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    public int Count(string input) => input.Length;",
            "    public int Count(int input) => input;",
            "",
            "    /// <summary>See <see cref=\"Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}"));
    }

    [Test]
    public async Task MultiVariableFieldCrefs_SiblingEditedOnceEachAndOwnDocSkippedAsync()
    {
        // No dedupe is exercised: each cref targets a distinct symbol and span. The test proves the field's own bare
        // crefs (inside its FullSpan) are skipped while the sibling's qualified crefs to each variable are edited.
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    /// <summary>Holds <see cref=\"a\"/> and <see cref=\"b\"/>.</summary>",
                "    public int a, b;",
                "",
                "    /// <summary>See <see cref=\"A.a\"/> and <see cref=\"A.b\"/>.</summary>",
                "    public int Process(string x) => x.Length;",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var moved = await ResolveMovedAsync(solution, source, s => s.Name is "a" or "b");

        Assert.That(moved.Members.Count, Is.EqualTo(1), "Both variables share one FieldDeclarationSyntax");
        Assert.That(moved.Symbols.Count, Is.EqualTo(2), "Both variable symbols are passed");

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        Assert.That(result.Retargeted, Is.EqualTo(2), "Two sibling crefs are edited; the field's own bare crefs are skipped");
        Assert.That(result.SkippedCandidates, Is.Zero);
        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { source }));
        Assert.That(result.EditsByDocument[source].Count, Is.EqualTo(2));
        await AssertTextAfterEditsAsync(solution, result, source, Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Holds <see cref=\"a\"/> and <see cref=\"b\"/>.</summary>",
            "    public int a, b;",
            "",
            "    /// <summary>See <see cref=\"B.a\"/> and <see cref=\"B.b\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}"));
    }

    [Test]
    public async Task LinkedCopyOfSourceFile_BareCrefInsideMovedMemberSkippedInBothCopiesAsync()
    {
        // Two projects each hold Source.cs with the same FilePath and identical text (the linked-copy shape). The
        // sibling's bare cref is the control: it must be edited in BOTH copies. The moved member's own bare cref must
        // yield no zero-length edit in either copy. The production check is FilePath-based, so a document.Id-based
        // check would emit a zero-length insertion for the linked copy and fail the exact-text and keys assertions.
        var original = Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>See <see cref=\"Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        var solution = TestSolutionBuilder.CreateTwoProjectSolution(
            "Upstream", new (string name, string content)[] { ("Source.cs", original) },
            "Linked", new (string name, string content)[] { ("Source.cs", original) });
        var upstream = solution.Projects.Single(p => p.Name == "Upstream").Documents.Single().Id;
        var linked = solution.Projects.Single(p => p.Name == "Linked").Documents.Single().Id;

        var upstreamMoved = await ResolveMovedAsync(solution, upstream, s => s.Name == "Count");
        var linkedMoved = await ResolveMovedAsync(solution, linked, s => s.Name == "Count");
        var symbols = upstreamMoved.Symbols.Concat(linkedMoved.Symbols).ToList();

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, upstream, upstreamMoved.Members, symbols, "B", CancellationToken.None);

        Assert.That(result.EditsByDocument.Keys, Is.EquivalentTo(new[] { upstream, linked }),
            "The sibling's bare cref is edited in both copies");
        // The two zero-length edits that remain are the sibling's bare-cref insertion, one per copy, at the same offset.
        // None may fall inside the moved member's span.
        var movedSpan = upstreamMoved.Members.Single().FullSpan;
        Assert.That(result.EditsByDocument.Values.SelectMany(e => e).Where(e => e.Span.Length == 0 && movedSpan.Contains(e.Span.Start)), Is.Empty,
            "No zero-length edit may come from the moved member's own doc in either copy");
        Assert.That(result.Retargeted, Is.EqualTo(2));
        Assert.That(result.SkippedCandidates, Is.Zero);

        var expected = Lines(
            "namespace Example;",
            "",
            "public class A",
            "{",
            "    /// <summary>Counts, see <see cref=\"Count\"/>.</summary>",
            "    public int Count(string input) => input.Length;",
            "",
            "    /// <summary>See <see cref=\"B.Count\"/>.</summary>",
            "    public int Process(string x) => x.Length;",
            "}");
        await AssertTextAfterEditsAsync(solution, result, upstream, expected);
        await AssertTextAfterEditsAsync(solution, result, linked, expected);
    }

    [Test]
    public async Task AmbiguousCallOutsideCref_IsNotCountedAsSkippedCandidateAsync()
    {
        // The call a.Count(null) binds ambiguously between Count(string) and Count(int?). Only crefs feed
        // SkippedCandidates, so the zero counter below is meaningful only if SymbolFinder reports the call as a candidate.
        var files = new (string RelativePath, string Content)[]
        {
            ("Source.cs", Lines(
                "namespace Example;",
                "",
                "public class A",
                "{",
                "    public int Count(string input) => input.Length;",
                "    public int Count(int? input) => input ?? 0;",
                "}")),
            ("Caller.cs", Lines(
                "namespace Example;",
                "",
                "public class Caller",
                "{",
                "    public int Run(A a) => a.Count(null);",
                "}")),
        };
        using var workspace = InMemoryWorkspace.Create(files);
        var solution = CurrentSolution(workspace);
        var source = DocumentIdOf(workspace, "Source.cs");
        var moved = await ResolveMovedAsync(solution, source, IsStringCount);

        var result = await InstanceMoveCrefEdits.CollectRetargetEditsAsync(
            solution, source, moved.Members, moved.Symbols, "B", CancellationToken.None);

        // Positive control: SymbolFinder must report the ambiguous call in Caller.cs as a candidate location. Only then
        // does SkippedCandidates staying at zero prove that the isCref gating is what keeps it out.
        var references = await SymbolFinder.FindReferencesAsync(moved.Symbols.Single(), solution, CancellationToken.None);
        var callerId = DocumentIdOf(workspace, "Caller.cs");
        Assert.That(references.SelectMany(r => r.Locations).Any(l => l.Document.Id == callerId && l.IsCandidateLocation), Is.True,
            "SymbolFinder must report the ambiguous call in Caller.cs as a candidate location");

        Assert.That(result.SkippedCandidates, Is.Zero, "A candidate outside a cref is not counted");
        Assert.That(result.Retargeted, Is.Zero);
        Assert.That(result.EditsByDocument, Is.Empty);
    }

    private static bool IsStringCount(ISymbol symbol) =>
        symbol is IMethodSymbol { Name: "Count" } method
        && method.Parameters.Length == 1
        && method.Parameters[0].Type.SpecialType == SpecialType.System_String;

    private static Solution CurrentSolution(InMemoryWorkspace workspace) =>
        workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");

    private static DocumentId DocumentIdOf(InMemoryWorkspace workspace, string relativePath) =>
        CurrentSolution(workspace).GetDocumentIdsWithFilePath(workspace.PathOf(relativePath)).Single();

    /// <summary>
    /// Resolves the moved members and their symbols from the document's syntax tree and semantic model. Fields are
    /// matched per variable so a multi-variable declaration is one member with several symbols.
    /// </summary>
    private static async Task<(IReadOnlyList<MemberDeclarationSyntax> Members, IReadOnlyList<ISymbol> Symbols)> ResolveMovedAsync(
        Solution solution, DocumentId documentId, Func<ISymbol, bool> isMoved)
    {
        var document = solution.GetDocument(documentId) ?? throw new InvalidOperationException("Document not found.");
        var root = await document.GetSyntaxRootAsync() ?? throw new InvalidOperationException("No syntax root.");
        var model = await document.GetSemanticModelAsync() ?? throw new InvalidOperationException("No semantic model.");
        Assert.That(((CSharpParseOptions)root.SyntaxTree.Options).DocumentationMode, Is.EqualTo(DocumentationMode.Parse),
            "The test document must be parsed with DocumentationMode.Parse or its crefs are not parsed");

        var members = new List<MemberDeclarationSyntax>();
        var symbols = new List<ISymbol>();
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member is FieldDeclarationSyntax field)
            {
                var fieldSymbols = field.Declaration.Variables
                    .Select(v => model.GetDeclaredSymbol(v))
                    .OfType<ISymbol>()
                    .Where(isMoved)
                    .ToList();
                if (fieldSymbols.Count > 0)
                {
                    members.Add(member);
                    symbols.AddRange(fieldSymbols);
                }

                continue;
            }

            var symbol = model.GetDeclaredSymbol(member);
            if (symbol != null && isMoved(symbol))
            {
                members.Add(member);
                symbols.Add(symbol);
            }
        }

        Assert.That(members, Is.Not.Empty, "No member matched the moved-symbol predicate");
        return (members, symbols);
    }

    /// <summary>
    /// Applies the edits recorded for one document to its original text and asserts the exact result.
    /// </summary>
    private static async Task AssertTextAfterEditsAsync(
        Solution solution, InstanceMoveCrefEditsResult result, DocumentId documentId, string expected)
    {
        // A document with no entry has no edits, so its text must equal the original.
        var edits = result.EditsByDocument.TryGetValue(documentId, out var recorded) ? recorded : Array.Empty<TextChange>();
        var text = await solution.GetDocument(documentId)!.GetTextAsync();
        Assert.That(text.WithChanges(edits).ToString(), Is.EqualTo(expected));
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";
}
