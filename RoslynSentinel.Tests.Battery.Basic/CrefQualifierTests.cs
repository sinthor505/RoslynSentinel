using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Q1c tests for CrefQualifier.QualifyDocumentAsync (Q1b helper). Fixture pattern copied from CrefSpikeTests:
/// one in-memory document, crefs bound through the semantic model. Edits are applied with SourceText.WithChanges
/// and the exact resulting text is asserted. Behaviour is pinned as observed; a suspected helper bug is marked
/// [Ignore("BUG: ...")] rather than pinned as correct.
/// </summary>
[TestFixture]
[Category("CrefQualifier")]
public class CrefQualifierTests
{
    private const string FileName = "Qualify.cs";

    private const string BareNameSource = """
namespace Sample;

public class A
{
    /// <summary>Count of items.</summary>
    public int Count { get; set; }

    /// <summary>Reads <see cref="Count"/>.</summary>
    public void Other() { }
}
""";

    private const string BareNameExpected = """
namespace Sample;

public class A
{
    /// <summary>Count of items.</summary>
    public int Count { get; set; }

    /// <summary>Reads <see cref="A.Count"/>.</summary>
    public void Other() { }
}
""";

    private const string ParameterListSource = """
namespace Sample;

public class A
{
    public int Foo(int x) => x;

    /// <summary>Calls <see cref="Foo(int)"/>.</summary>
    public void Other() { }
}
""";

    private const string ParameterListExpected = """
namespace Sample;

public class A
{
    public int Foo(int x) => x;

    /// <summary>Calls <see cref="A.Foo(int)"/>.</summary>
    public void Other() { }
}
""";

    private const string GenericSource = """
namespace Sample;

public class Box<T>
{
    public T Get(T x) => x;

    /// <summary>Uses <see cref="Get(T)"/> and <see cref="Get"/>.</summary>
    public void Other() { }
}
""";

    private const string GenericExpected = """
namespace Sample;

public class Box<T>
{
    public T Get(T x) => x;

    /// <summary>Uses <see cref="Box{T}.Get(T)"/> and <see cref="Box{T}.Get"/>.</summary>
    public void Other() { }
}
""";

    private const string NestedSource = """
namespace Sample;

public class Outer
{
    public class Inner
    {
        public int Value { get; set; }

        /// <summary>Reads <see cref="Value"/>.</summary>
        public void Other() { }
    }
}
""";

    private const string NestedExpected = """
namespace Sample;

public class Outer
{
    public class Inner
    {
        public int Value { get; set; }

        /// <summary>Reads <see cref="Inner.Value"/>.</summary>
        public void Other() { }
    }
}
""";

    private const string OperatorSource = """
namespace Sample;

public class A
{
    public static A operator +(A a, A b) => a;

    /// <summary>Adds via <see cref="operator +(A,A)"/>.</summary>
    public void Other() { }
}
""";

    private const string OperatorExpected = """
namespace Sample;

public class A
{
    public static A operator +(A a, A b) => a;

    /// <summary>Adds via <see cref="A.operator +(A,A)"/>.</summary>
    public void Other() { }
}
""";

    private const string IndexerSource = """
namespace Sample;

public class A
{
    public int this[int i] => i;

    /// <summary>Reads <see cref="this[int]"/>.</summary>
    public void Other() { }
}
""";

    private const string IndexerExpected = """
namespace Sample;

public class A
{
    public int this[int i] => i;

    /// <summary>Reads <see cref="A.this[int]"/>.</summary>
    public void Other() { }
}
""";

    private const string TypeParameterSource = """
namespace Sample;

public class Box<T>
{
    /// <summary>Holds a <see cref="T"/>.</summary>
    public void Other() { }
}
""";

    private const string NoEditSource = """
using System.Collections.Generic;

namespace Sample;

public class A
{
    public int Count { get; set; }

    /// <summary>Uses <see cref="A.Count"/>.</summary>
    public void Other() { }
}

public class Holder<T>
{
    /// <summary>Uses <see cref="List{T}"/> and <see cref="int"/>.</summary>
    public void Other() { }
}
""";

    private const string OverloadGroupSource = """
namespace Sample;

public class B
{
    public int Count() => 0;

    public int Count(int x) => x;

    /// <summary>Uses <see cref="Count"/>.</summary>
    public void Other() { }
}
""";

    private const string OverloadGroupExpected = """
namespace Sample;

public class B
{
    public int Count() => 0;

    public int Count(int x) => x;

    /// <summary>Uses <see cref="B.Count"/>.</summary>
    public void Other() { }
}
""";

    private const string FilterSource = """
namespace Sample;

public class A
{
    public int Count { get; set; }

    public int Foo(int x) => x;

    /// <summary>Uses <see cref="Count"/>, <see cref="Foo(int)"/> and <see cref="A.Count"/>.</summary>
    public void Other() { }
}
""";

    private const string FilterExpected = """
namespace Sample;

public class A
{
    public int Count { get; set; }

    public int Foo(int x) => x;

    /// <summary>Uses <see cref="Count"/>, <see cref="A.Foo(int)"/> and <see cref="A.Count"/>.</summary>
    public void Other() { }
}
""";

    [Test]
    public async Task BareNameCref_InOtherMemberDoc_QualifiesWithContainingTypeAsync()
    {
        var (result, text) = await QualifyAsync(BareNameSource, null);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(BareNameExpected));
    }

    [Test]
    public async Task BareCrefWithParameterList_PreservesParametersAsync()
    {
        var (result, text) = await QualifyAsync(ParameterListSource, null);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(ParameterListExpected));
    }

    [Test]
    public async Task BareCrefInGenericContainingType_UsesBracesInPrefixAsync()
    {
        // Pinned: ToMinimalDisplayString(Box<T>) is "Box<T>", and the helper maps '<' and '>' to '{' and '}'.
        var (result, text) = await QualifyAsync(GenericSource, null);

        Assert.That(result.Qualified, Is.EqualTo(2));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(GenericExpected));
    }

    // Pinned, surprising but correct: ToMinimalDisplayString at the cref position yields "Inner" (Inner is in scope
    // inside Outer), so the prefix is not the full Outer.Inner path. The result still binds in its own scope.
    [Test]
    public async Task BareCrefInNestedContainingType_QualifiesWithOuterAndInnerAsync()
    {
        var (result, text) = await QualifyAsync(NestedSource, null);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(NestedExpected));
    }

    [Test]
    public async Task OperatorCref_GetsPrefixAndStillBindsToSameSymbolAsync()
    {
        await AssertQualifiedCrefKeepsSymbolAsync<OperatorMemberCrefSyntax>(OperatorSource, OperatorExpected);
    }

    [Test]
    public async Task IndexerCref_GetsPrefixAndStillBindsToSameSymbolAsync()
    {
        await AssertQualifiedCrefKeepsSymbolAsync<IndexerMemberCrefSyntax>(IndexerSource, IndexerExpected);
    }

    [Test]
    public async Task TypeParameterCref_IsNotEditedAsync()
    {
        var (result, text) = await QualifyAsync(TypeParameterSource, null);

        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(1));
        Assert.That(result.Qualified, Is.EqualTo(0));
        Assert.That(result.Edits, Is.Empty);
        Assert.That(text, Is.EqualTo(TypeParameterSource));
    }

    [Test]
    public async Task AlreadyQualifiedAndTypeCrefs_AreNotEditedAsync()
    {
        var (result, text) = await QualifyAsync(NoEditSource, null);

        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(1));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(2));
        Assert.That(result.Qualified, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(result.Edits, Is.Empty);
        Assert.That(text, Is.EqualTo(NoEditSource));
    }

    [Test]
    public async Task OverloadGroupBareCref_FilterAcceptsOneCandidate_QualifiesAsync()
    {
        // Any rule: the overload group qualifies when at least one candidate passes the filter.
        var (result, text) = await QualifyAsync(OverloadGroupSource, s => s is IMethodSymbol m && m.Parameters.Length == 1);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(OverloadGroupExpected));
    }

    [Test]
    public async Task OverloadGroupBareCref_FilterRejectsAllCandidates_NoEditAsync()
    {
        var (result, text) = await QualifyAsync(OverloadGroupSource, _ => false);

        Assert.That(result.Qualified, Is.EqualTo(0));
        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(result.Edits, Is.Empty);
        Assert.That(text, Is.EqualTo(OverloadGroupSource));
    }

    [Test]
    public async Task SymbolFilterRejectingMember_SkipsOnlyThatMemberAsync()
    {
        var (result, text) = await QualifyAsync(FilterSource, s => s.Name != "Count");

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(1));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(FilterExpected));
    }

    private static async Task<(CrefQualifierResult Result, string Text)> QualifyAsync(
        string source,
        Func<ISymbol, bool>? symbolFilter)
    {
        using var workspace = InMemoryWorkspace.Create((FileName, source));
        var doc = GetDocument(workspace);
        var result = await CrefQualifier.QualifyDocumentAsync(doc, symbolFilter, CancellationToken.None);
        var original = await doc.GetTextAsync();
        return (result, original.WithChanges(result.Edits).ToString());
    }

    // Qualifies the single bare cref of type TBare, then re-parses the edited text in a fresh workspace and checks
    // that the outermost qualified cref binds to a symbol with the same documentation comment id.
    private static async Task AssertQualifiedCrefKeepsSymbolAsync<TBare>(string source, string expectedText)
        where TBare : CrefSyntax
    {
        using var originalWorkspace = InMemoryWorkspace.Create((FileName, source));
        var originalDoc = GetDocument(originalWorkspace);
        var originalRoot = await originalDoc.GetSyntaxRootAsync();
        var originalModel = await originalDoc.GetSemanticModelAsync();
        var bareCref = originalRoot!.DescendantNodes(descendIntoTrivia: true).OfType<TBare>().Single();
        var originalSymbol = originalModel!.GetSymbolInfo(bareCref).Symbol;
        Assert.That(originalSymbol, Is.Not.Null, "bare cref did not bind before qualification");

        var result = await CrefQualifier.QualifyDocumentAsync(originalDoc, null, CancellationToken.None);
        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        var newText = (await originalDoc.GetTextAsync()).WithChanges(result.Edits).ToString();
        Assert.That(newText, Is.EqualTo(expectedText));

        using var editedWorkspace = InMemoryWorkspace.Create((FileName, newText));
        var editedDoc = GetDocument(editedWorkspace);
        var editedRoot = await editedDoc.GetSyntaxRootAsync();
        var editedModel = await editedDoc.GetSemanticModelAsync();
        var qualifiedCref = editedRoot!.DescendantNodes(descendIntoTrivia: true)
            .OfType<QualifiedCrefSyntax>()
            .Where(c => c.Parent is XmlCrefAttributeSyntax)
            .Single();
        var editedSymbol = editedModel!.GetSymbolInfo(qualifiedCref).Symbol;
        Assert.That(editedSymbol, Is.Not.Null, "qualified cref did not bind after the edit");
        Assert.That(
            editedSymbol!.GetDocumentationCommentId(),
            Is.EqualTo(originalSymbol!.GetDocumentationCommentId()));
    }

    private static Document GetDocument(InMemoryWorkspace workspace)
    {
        var solution = workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var documentId = solution.GetDocumentIdsWithFilePath(workspace.PathOf(FileName)).Single();
        return solution.GetDocument(documentId)!;
    }
}
