using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using NUnit.Framework;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Q1a spike for docs/current/plans/plan_cref_qualifier_helper_and_tool.md, Step 1. Settles two unverified claims
/// before the CrefQualifier helper is written:
/// (a) cref nodes are reachable in a loaded document via DescendantNodes(descendIntoTrivia: true) and bind through
///     SemanticModel.GetSymbolInfo (fixture: InMemoryWorkspace, AdhocWorkspace-backed);
/// (b) SymbolFinder.FindReferencesAsync returns locations for overload-specific and bare crefs.
/// Findings are recorded in comments next to each assertion.
/// </summary>
[TestFixture]
[Category("CrefSpike")]
public class CrefSpikeTests
{
    private const string SpikeFileName = "Spike.cs";

    // Class A carries the forms the plan asks about. Class A cannot hold a property Count and a method Count(int)
    // at once (CS0102), so the parameterized overload form is covered by Foo(int)/Foo() here and by class B below.
    private const string SpikeSource = @"using System;

namespace Spike;

public class A
{
    /// <summary>Count. See <see cref=""A.Count""/> and <see cref=""Count""/>.</summary>
    public int Count { get; set; }

    /// <summary>Overload one. See <see cref=""A.Foo(int)""/> and <see cref=""Foo(int)""/>.</summary>
    public int Foo(int x) => x;

    /// <summary>Overload two. See <see cref=""Foo()""/>.</summary>
    public int Foo() => 0;

    /// <summary>Nested. See <see cref=""Foo(int)""/>.</summary>
    public class Nested
    {
        /// <summary>Value. See <see cref=""Foo(int)""/>.</summary>
        public int Value { get; set; }
    }
}

/// <summary>Derived. See <see cref=""Foo(int)""/>.</summary>
public class Derived : A
{
    /// <summary>Bar. See <see cref=""Foo(int)""/> and <see cref=""A.Foo(int)""/>.</summary>
    public int Bar() => Foo(1);
}
";

    // Class B: a method group with two overloads, to bind a parameterized cref to the 1-parameter overload.
    private const string OverloadSource = @"namespace Spike;

public class B
{
    /// <summary>Overloads. See <see cref=""B.Count(int)""/>, <see cref=""Count(int)""/> and <see cref=""Count""/>.</summary>
    public int Count() => 0;

    /// <summary>One argument.</summary>
    public int Count(int x) => x;
}
";

    // Minimal inherited-cref case with no overloads, nested types or hiding: a control (Base's own member doc), the
    // derived class doc, and the derived member doc.
    private const string MinimalInheritedSource = @"namespace Spike;

public class Base
{
    /// <summary>Control. See <see cref=""M(int)""/>.</summary>
    public void M(int x) { }
}

/// <summary>Class doc. See <see cref=""M(int)""/>.</summary>
public class D : Base
{
    /// <summary>Member doc. See <see cref=""M(int)""/>.</summary>
    void X() { }
}
";

    // Same shape as the spike's inherited case, with the base's overloads and nested type moved into Base.
    private const string ShapedInheritedSource = @"namespace Spike;

public class Base
{
    public int Foo(int x) => x;

    public int Foo() => 0;

    public class Nested { }
}

/// <summary>Derived. See <see cref=""Foo(int)""/>.</summary>
public class Derived : Base
{
    /// <summary>Bar. See <see cref=""Foo(int)""/>.</summary>
    public int Bar() => Foo(1);
}
";

    [Test]
    public async Task CrefNodes_AreReachableAndBindToMemberSymbols_InLoadedDocumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SpikeFileName, SpikeSource));
        var doc = GetSpikeDocument(workspace);
        var root = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        Assert.That(root, Is.Not.Null);
        Assert.That(model, Is.Not.Null);

        // Finding: the fixture's tree DocumentationMode is exactly Parse (not Diagnose); crefs are structured in Parse mode.
        var mode = ((CSharpParseOptions)root!.SyntaxTree.Options).DocumentationMode;
        TestContext.Out.WriteLine($"DocumentationMode={mode}");
        Assert.That(mode, Is.EqualTo(DocumentationMode.Parse));

        // Finding: DescendantNodes(descendIntoTrivia: true) reaches crefs, but a qualified cref (A.Count) also yields its
        // inner NameMemberCref (Count). The helper must walk outermost crefs only (Parent is not QualifiedCrefSyntax).
        var crefs = root.DescendantNodes(descendIntoTrivia: true).OfType<CrefSyntax>()
            .Where(c => c.Parent is not QualifiedCrefSyntax).ToList();
        Assert.That(crefs, Is.Not.Empty, "No CrefSyntax reachable via DescendantNodes(descendIntoTrivia: true).");
        Assert.That(crefs.Count, Is.EqualTo(10), "Outermost crefs in SpikeSource");

        var results = new List<(string Text, ISymbol? Symbol, string? Owner)>();
        foreach (var cref in crefs)
        {
            var owner = cref.Ancestors(ascendOutOfTrivia: true).OfType<MemberDeclarationSyntax>().FirstOrDefault();
            var ownerName = owner is BaseTypeDeclarationSyntax type ? type.Identifier.Text : owner?.Kind().ToString();
            var symbol = model!.GetSymbolInfo(cref).Symbol;
            results.Add((cref.ToString(), symbol, ownerName));
            TestContext.Out.WriteLine(
                $"cref '{cref}' at {cref.SpanStart} owner={ownerName} -> {(symbol is null ? "<unbound>" : Describe(symbol))}");
        }

        // Finding: every cref binds EXCEPT the two bare Foo(int) crefs whose owner is Derived (class doc) and Derived.Bar
        // (member doc). Root cause (observed, see InheritedBareCref_MinimalCase_BindsToBaseMethodAsync): a bare name in a
        // doc comment of a derived type does not resolve to a member inherited from its base, in this fixture. The
        // qualified A.Foo(int) in Derived.Bar's doc does bind. Overloads and nested types are not the cause (see the
        // shaped test). Pinned as observed.
        var unbound = results.Where(r => r.Symbol is null).ToList();
        Assert.That(unbound.Select(r => (r.Text, r.Owner)).ToList(), Is.EquivalentTo(new[]
        {
            ("Foo(int)", "Derived"),
            ("Foo(int)", "MethodDeclaration"),
        }));

        var countProperty = FindCrefs(root, "A.Count").Single();
        Assert.That(Describe(model!.GetSymbolInfo(countProperty).Symbol!), Is.EqualTo("A.Count"));

        var bareCount = FindCrefs(root, "Count").Single();
        Assert.That(Describe(model.GetSymbolInfo(bareCount).Symbol!), Is.EqualTo("A.Count"));

        var fooInt = FindCrefs(root, "A.Foo(int)").First();
        Assert.That(Describe(model.GetSymbolInfo(fooInt).Symbol!), Is.EqualTo("A.Foo(int)"));
    }

    [Test]
    public async Task InheritedBareCref_MinimalCase_BindsToBaseMethodAsync()
    {
        // Observed (pinned): the control bare M(int) in Base binds. The bare M(int) in D's class doc and in D's member
        // doc do NOT bind, although D : Base and the fixture has no overloads, nested types, hiding or accessibility
        // issues. So the failure is the bare inherited lookup itself, not the spike's overload or nesting shape.
        // Source-level Roslyn cause not traced; the helper must treat bare inherited crefs as unbound in this fixture.
        var results = await BindCrefsAsync(MinimalInheritedSource);
        foreach (var result in results)
        {
            TestContext.Out.WriteLine($"minimal cref '{result.Text}' owner={result.Owner} bound={result.Bound}");
        }

        Assert.That(results.Select(r => (r.Text, r.Owner, r.Bound)).ToList(), Is.EqualTo(new[]
        {
            ("M(int)", "MethodDeclaration", true),
            ("M(int)", "D", false),
            ("M(int)", "MethodDeclaration", false),
        }));
    }

    [Test]
    public async Task InheritedBareCref_ShapedLikeSpike_BindsToBaseMethodAsync()
    {
        // Observed (pinned): same as the minimal case. Moving the overloads and nested type into Base does not make
        // the bare Foo(int) in Derived's class doc or member doc bind.
        var results = await BindCrefsAsync(ShapedInheritedSource);
        foreach (var result in results)
        {
            TestContext.Out.WriteLine($"shaped cref '{result.Text}' owner={result.Owner} bound={result.Bound}");
        }

        Assert.That(results.Select(r => (r.Text, r.Owner, r.Bound)).ToList(), Is.EqualTo(new[]
        {
            ("Foo(int)", "Derived", false),
            ("Foo(int)", "MethodDeclaration", false),
        }));
    }

    [Test]
    public async Task ParameterizedCref_BindsToOneArgumentOverload_InLoadedDocumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SpikeFileName, OverloadSource));
        var doc = GetSpikeDocument(workspace);
        var root = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        Assert.That(root, Is.Not.Null);
        Assert.That(model, Is.Not.Null);

        var qualified = FindCrefs(root!, "B.Count(int)").Single();
        var qualifiedInfo = model!.GetSymbolInfo(qualified);
        Assert.That(qualifiedInfo.Symbol, Is.Not.Null, "B.Count(int) did not bind");
        Assert.That(Describe(qualifiedInfo.Symbol!), Is.EqualTo("B.Count(int)"));

        var bare = FindCrefs(root, "Count(int)").Single();
        var bareInfo = model.GetSymbolInfo(bare);
        Assert.That(bareInfo.Symbol, Is.Not.Null, "bare Count(int) did not bind");
        Assert.That(((IMethodSymbol)bareInfo.Symbol!).Parameters.Length, Is.EqualTo(1));

        // Pinned observation: bare Count with no parameter list over the overload group Count() / Count(int) does not
        // bind to one symbol. GetSymbolInfo returns Symbol=null, CandidateReason=Ambiguous, and both candidates.
        var bareGroup = FindCrefs(root, "Count").Single();
        var bareGroupInfo = model.GetSymbolInfo(bareGroup);
        TestContext.Out.WriteLine(
            $"bare 'Count' over overload group: Symbol={(bareGroupInfo.Symbol is null ? "<null>" : Describe(bareGroupInfo.Symbol))}, " +
            $"CandidateReason={bareGroupInfo.CandidateReason}, Candidates={bareGroupInfo.CandidateSymbols.Length}");
        Assert.That(bareGroupInfo.Symbol, Is.Null);
        Assert.That(bareGroupInfo.CandidateSymbols.Length, Is.EqualTo(2));
        Assert.That(bareGroupInfo.CandidateReason, Is.EqualTo(CandidateReason.Ambiguous));
    }

    [Test]
    public async Task FindReferences_ReturnsDocumentationCrefLocations_ForQualifiedBareAndNestedFormsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((SpikeFileName, SpikeSource));
        var doc = GetSpikeDocument(workspace);
        var root = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        Assert.That(root, Is.Not.Null);
        Assert.That(model, Is.Not.Null);

        var target = root!.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Foo" && m.ParameterList.Parameters.Count == 1);
        var symbol = (IMethodSymbol)model!.GetDeclaredSymbol(target)!;
        Assert.That(Describe(symbol), Is.EqualTo("A.Foo(int)"));

        var referenced = await SymbolFinder.FindReferencesAsync(symbol, doc.Project.Solution);
        var locations = referenced.SelectMany(r => r.Locations).ToList();

        var labels = new List<string>();
        foreach (var location in locations)
        {
            var locationRoot = await location.Document.GetSyntaxRootAsync();
            // Finding: FindNode needs findInsideTrivia to reach a reference inside a doc comment. The reported span is the
            // name token only (for A.Foo(int) it is "Foo", not "A."), so the label walks up to the outermost cref.
            var node = locationRoot!.FindNode(location.Location.SourceSpan, findInsideTrivia: true, getInnermostNodeForTie: true);
            var cref = node.AncestorsAndSelf().OfType<CrefSyntax>().LastOrDefault();
            var label = cref is null ? "code" : $"doc:{cref}";
            labels.Add(label);
            TestContext.Out.WriteLine(
                $"{label} span={location.Location.SourceSpan} node={node.Kind()} candidate={location.IsCandidateLocation}");
        }

        // Observed: 6 locations. Qualified A.Foo(int) in A's doc and Derived.Bar's doc, bare Foo(int) in A's doc, Nested's
        // doc, Value's doc, plus the code invocation Foo(1). NOT returned: the bare Foo(int) in Derived's class doc and in
        // Derived.Bar's doc, because those crefs do not bind (see the first test and InheritedBareCref_MinimalCase).
        Assert.That(labels, Is.EquivalentTo(new[]
        {
            "code",
            "doc:A.Foo(int)", "doc:A.Foo(int)",
            "doc:Foo(int)", "doc:Foo(int)", "doc:Foo(int)",
        }));
    }

    private static async Task<List<(string Text, string? Owner, bool Bound)>> BindCrefsAsync(string source)
    {
        using var workspace = InMemoryWorkspace.Create((SpikeFileName, source));
        var doc = GetSpikeDocument(workspace);
        var root = await doc.GetSyntaxRootAsync();
        var model = await doc.GetSemanticModelAsync();
        var results = new List<(string Text, string? Owner, bool Bound)>();
        foreach (var cref in root!.DescendantNodes(descendIntoTrivia: true).OfType<CrefSyntax>()
                     .Where(c => c.Parent is not QualifiedCrefSyntax))
        {
            var owner = cref.Ancestors(ascendOutOfTrivia: true).OfType<MemberDeclarationSyntax>().FirstOrDefault();
            var ownerName = owner is BaseTypeDeclarationSyntax type ? type.Identifier.Text : owner?.Kind().ToString();
            results.Add((cref.ToString(), ownerName, model!.GetSymbolInfo(cref).Symbol is not null));
        }

        return results;
    }

    private static Document GetSpikeDocument(InMemoryWorkspace workspace)
    {
        var solution = workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var documentId = solution.GetDocumentIdsWithFilePath(workspace.PathOf(SpikeFileName)).Single();
        return solution.GetDocument(documentId)!;
    }

    private static List<CrefSyntax> FindCrefs(SyntaxNode root, string text) =>
        root.DescendantNodes(descendIntoTrivia: true).OfType<CrefSyntax>()
            .Where(c => c.ToString() == text && c.Parent is not QualifiedCrefSyntax).ToList();

    private static string Describe(ISymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
}
