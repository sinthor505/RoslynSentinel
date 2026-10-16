using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// Q1c and Q1d tests for CrefQualifier.QualifyDocumentAsync (Q1b helper). Fixture pattern copied from CrefSpikeTests:
/// one in-memory document, crefs bound through the semantic model. Edits are applied with SourceText.WithChanges
/// and the exact resulting text is asserted. Behaviour is pinned as observed; surprising results carry a Pinned comment.
/// Qualified crefs are re-bound in a fresh workspace to confirm they still resolve to the same symbol.
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
    public async Task BareCrefInNestedContainingType_QualifiesWithMinimalInnerNameAsync()
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

    private const string PartDeclPath = "PartDecl.cs";

    private const string PartDocPath = "PartDoc.cs";

    private const string InheritedSource = """
namespace Sample;

public class Base
{
    public int Count { get; set; }
}

public class Derived : Base
{
    /// <summary>Reads <see cref="Count"/>.</summary>
    public void Other() { }
}
""";

    private const string NestedMemberSource = """
namespace Sample;

public class A
{
    public int Count { get; set; }

    public class N
    {
        /// <summary>Reads <see cref="Count"/>.</summary>
        public void Other() { }
    }
}
""";

    private const string NestedMemberExpected = """
namespace Sample;

public class A
{
    public int Count { get; set; }

    public class N
    {
        /// <summary>Reads <see cref="A.Count"/>.</summary>
        public void Other() { }
    }
}
""";

    private const string PartialDeclSource = """
namespace Sample;

public partial class A
{
    public int Count { get; set; }
}
""";

    private const string PartialDocSource = """
namespace Sample;

public partial class A
{
    /// <summary>Reads <see cref="Count"/>.</summary>
    public void Other() { }
}
""";

    private const string PartialDocExpected = """
namespace Sample;

public partial class A
{
    /// <summary>Reads <see cref="A.Count"/>.</summary>
    public void Other() { }
}
""";

    private const string ClassDocSource = """
namespace Sample;

/// <summary>Uses <see cref="Count"/> and <seealso cref="Foo(int)"/>.</summary>
public class A
{
    public int Count { get; set; }

    public int Foo(int x) => x;

    /// <summary>Holds a count.</summary>
    /// <seealso cref="Count"/>
    public void Other() { }

    /// <inheritdoc cref="Foo(int)"/>
    public void Third() { }
}
""";

    private const string ClassDocExpected = """
namespace Sample;

/// <summary>Uses <see cref="A.Count"/> and <seealso cref="A.Foo(int)"/>.</summary>
public class A
{
    public int Count { get; set; }

    public int Foo(int x) => x;

    /// <summary>Holds a count.</summary>
    /// <seealso cref="A.Count"/>
    public void Other() { }

    /// <inheritdoc cref="A.Foo(int)"/>
    public void Third() { }
}
""";

    [Test]
    public async Task BareCrefToInheritedMember_IsUneditedAndSkippedUnresolvedAsync()
    {
        // Observed (Q1a spike): a bare cref to an inherited member does not bind; counted unresolved, unedited.
        var (result, text) = await QualifyAsync(InheritedSource, null);

        Assert.That(result.Qualified, Is.EqualTo(0));
        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(1));
        Assert.That(result.Edits, Is.Empty);
        Assert.That(text, Is.EqualTo(InheritedSource));
    }

    [Test]
    public async Task BareCrefInNestedType_ToOuterMember_QualifiesWithOuterNameAsync()
    {
        // Pinned: Count's containing type A is in scope inside N, so the prefix is "A".
        var (result, text) = await QualifyAsync(NestedMemberSource, null);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(NestedMemberExpected));
    }

    [Test]
    public async Task PartialClassMemberDeclaredInOtherFile_QualifiesAsync()
    {
        using var workspace = InMemoryWorkspace.Create((PartDeclPath, PartialDeclSource), (PartDocPath, PartialDocSource));

        var declResult = await CrefQualifier.QualifyDocumentAsync(GetDocument(workspace, PartDeclPath), null, CancellationToken.None);
        Assert.That(declResult.Qualified, Is.EqualTo(0));
        Assert.That(declResult.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(declResult.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(declResult.SkippedUnresolved, Is.EqualTo(0));

        var docDoc = GetDocument(workspace, PartDocPath);
        var docResult = await CrefQualifier.QualifyDocumentAsync(docDoc, null, CancellationToken.None);
        Assert.That(docResult.Qualified, Is.EqualTo(1));
        Assert.That(docResult.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(docResult.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(docResult.SkippedUnresolved, Is.EqualTo(0));
        var text = (await docDoc.GetTextAsync()).WithChanges(docResult.Edits).ToString();
        Assert.That(text, Is.EqualTo(PartialDocExpected));
    }

    [Test]
    public async Task CrlfAndLfInput_DifferOnlyByInsertedPrefixesAsync()
    {
        var crlfSource = BareNameSource.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var (lfResult, lfText) = await QualifyAsync(BareNameSource, null);
        var (crlfResult, crlfText) = await QualifyAsync(crlfSource, null);

        Assert.That(lfResult.Qualified, Is.EqualTo(1));
        Assert.That(crlfResult.Qualified, Is.EqualTo(1));
        Assert.That(crlfResult.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(lfText, Is.EqualTo(BareNameExpected));
        Assert.That(crlfText, Is.EqualTo(crlfSource.Replace("<see cref=\"Count\"/>", "<see cref=\"A.Count\"/>")));
        Assert.That(CountOccurrences(crlfText, "\r\n"), Is.EqualTo(CountOccurrences(crlfSource, "\r\n")));
        Assert.That(CountOccurrences(crlfText, "\n") - CountOccurrences(crlfText, "\r\n"), Is.EqualTo(0));
        Assert.That(CountOccurrences(lfText, "\r"), Is.EqualTo(0));
    }

    [Test]
    public async Task EditedDocumentReloaded_HasNoFurtherEditsAsync()
    {
        var (first, editedText) = await QualifyAsync(BareNameSource, null);
        Assert.That(first.Qualified, Is.EqualTo(1));

        using var workspace = InMemoryWorkspace.Create((FileName, editedText));
        var second = await CrefQualifier.QualifyDocumentAsync(GetDocument(workspace), null, CancellationToken.None);

        Assert.That(second.Qualified, Is.EqualTo(0));
        Assert.That(second.SkippedAlreadyQualified, Is.EqualTo(first.Qualified));
        Assert.That(second.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(second.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(second.Edits, Is.Empty);
    }

    [Test]
    public async Task SeeAlsoInheritDocAndClassDocCrefs_AllQualifyAsync()
    {
        var (result, text) = await QualifyAsync(ClassDocSource, null);

        Assert.That(result.Qualified, Is.EqualTo(4));
        Assert.That(result.SkippedAlreadyQualified, Is.EqualTo(0));
        Assert.That(result.SkippedTypeOrPredefined, Is.EqualTo(0));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(ClassDocExpected));
        await AssertCrefSymbolsPreservedAsync(ClassDocSource, ClassDocExpected, 4);
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

    private const string CtorSource = """
namespace Sample;

public class A
{
    public A() { }

    /// <summary>Creates via <see cref="A()"/>.</summary>
    public void Other() { }
}
""";

    private const string CtorExpected = """
namespace Sample;

public class A
{
    public A() { }

    /// <summary>Creates via <see cref="A.A()"/>.</summary>
    public void Other() { }
}
""";

    private const string ParamsSource = """
namespace Sample;

public class A
{
    public int Foo(int x, string y) => x;

    public T Bar<T>(T v) => v;

    /// <summary>Uses <see cref="Foo(int, string)"/> and <see cref="Bar{T}(T)"/>.</summary>
    public void Other() { }
}
""";

    private const string ParamsExpected = """
namespace Sample;

public class A
{
    public int Foo(int x, string y) => x;

    public T Bar<T>(T v) => v;

    /// <summary>Uses <see cref="A.Foo(int, string)"/> and <see cref="A.Bar{T}(T)"/>.</summary>
    public void Other() { }
}
""";

    private const string OtherNamespaceSource = """
namespace Other
{
    public class A
    {
        public static int Count { get; } = 0;
    }
}

namespace Sample
{
    using static Other.A;

    public class B
    {
        /// <summary>Reads <see cref="Count"/>.</summary>
        public void Method() { }
    }
}
""";

    private const string OtherNamespaceExpected = """
namespace Other
{
    public class A
    {
        public static int Count { get; } = 0;
    }
}

namespace Sample
{
    using static Other.A;

    public class B
    {
        /// <summary>Reads <see cref="Other.A.Count"/>.</summary>
        public void Method() { }
    }
}
""";

    private const string MultiEditSource = """
namespace Sample;

public class A
{
    public int Count { get; set; }

    public int Foo(int x) => x;

    /// <summary>Uses <see cref="Count"/>, <see cref="Foo(int)"/> and <see cref="Count"/>.</summary>
    public void Other() { }
}
""";

    private const string MultiEditExpected = """
namespace Sample;

public class A
{
    public int Count { get; set; }

    public int Foo(int x) => x;

    /// <summary>Uses <see cref="A.Count"/>, <see cref="A.Foo(int)"/> and <see cref="A.Count"/>.</summary>
    public void Other() { }
}
""";

    [Test]
    public async Task ConstructorCref_BecomesTypeQualifiedConstructorAsync()
    {
        // Pinned: the constructor's containing type is A, so the prefix is "A" and the cref becomes A.A().
        var (result, text) = await QualifyAsync(CtorSource, null);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(CtorExpected));
        await AssertCrefSymbolsPreservedAsync(CtorSource, CtorExpected, 1);
    }

    [Test]
    public async Task MultipleParametersAndGenericMethodCrefs_ParameterListPreservedAsync()
    {
        var (result, text) = await QualifyAsync(ParamsSource, null);

        Assert.That(result.Qualified, Is.EqualTo(2));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(ParamsExpected));
        await AssertCrefSymbolsPreservedAsync(ParamsSource, ParamsExpected, 2);
    }

    // Pinned: a static member of Other.A reached through using static, with no using for namespace Other, gets the
    // namespace-qualified prefix Other.A because A is not in scope by its short name.
    [Test]
    public async Task MemberOfTypeInOtherNamespace_ReachedViaUsingStatic_UsesNamespaceQualifiedPrefixAsync()
    {
        var (result, text) = await QualifyAsync(OtherNamespaceSource, null);

        Assert.That(result.Qualified, Is.EqualTo(1));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(text, Is.EqualTo(OtherNamespaceExpected));
        await AssertCrefSymbolsPreservedAsync(OtherNamespaceSource, OtherNamespaceExpected, 1);
    }

    [Test]
    public async Task SeveralCrefsInOneDocument_EditsAreSortedAndNonOverlappingAsync()
    {
        var (result, text) = await QualifyAsync(MultiEditSource, null);

        Assert.That(result.Qualified, Is.EqualTo(3));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        Assert.That(result.Edits, Has.Count.EqualTo(3));
        Assert.That(result.Edits.Select(e => e.Span.Start), Is.Ordered.Ascending);
        for (var i = 1; i < result.Edits.Count; i++)
        {
            Assert.That(result.Edits[i - 1].Span.End, Is.LessThanOrEqualTo(result.Edits[i].Span.Start));
        }
        Assert.That(text, Is.EqualTo(MultiEditExpected));
    }

    [Test]
    public async Task GenericContainingTypeCrefs_ReparseAndBindToSameSymbolsAsync()
    {
        await AssertCrefSymbolsPreservedAsync(GenericSource, GenericExpected, 2);
    }

    [Test]
    public async Task ParameterListCref_ReparsesAndBindsToSameSymbolAsync()
    {
        await AssertCrefSymbolsPreservedAsync(ParameterListSource, ParameterListExpected, 1);
    }

    // Re-parses the edited text in a fresh workspace and checks that every outermost cref binds to the same
    // documentation comment id, in the same order, as the bare cref it was qualified from.
    private static async Task AssertCrefSymbolsPreservedAsync(string source, string expectedText, int expectedQualified)
    {
        using var originalWorkspace = InMemoryWorkspace.Create((FileName, source));
        var originalDoc = GetDocument(originalWorkspace);
        var originalRoot = await originalDoc.GetSyntaxRootAsync();
        var originalModel = await originalDoc.GetSemanticModelAsync();
        var originalIds = originalRoot!.DescendantNodes(descendIntoTrivia: true)
            .OfType<CrefSyntax>()
            .Where(c => c.Parent is XmlCrefAttributeSyntax)
            .Select(c => originalModel!.GetSymbolInfo(c).Symbol?.OriginalDefinition.GetDocumentationCommentId())
            .ToList();

        var result = await CrefQualifier.QualifyDocumentAsync(originalDoc, null, CancellationToken.None);
        Assert.That(result.Qualified, Is.EqualTo(expectedQualified));
        Assert.That(result.SkippedUnresolved, Is.EqualTo(0));
        var newText = (await originalDoc.GetTextAsync()).WithChanges(result.Edits).ToString();
        Assert.That(newText, Is.EqualTo(expectedText));

        using var editedWorkspace = InMemoryWorkspace.Create((FileName, newText));
        var editedDoc = GetDocument(editedWorkspace);
        var editedRoot = await editedDoc.GetSyntaxRootAsync();
        var editedModel = await editedDoc.GetSemanticModelAsync();
        var editedIds = editedRoot!.DescendantNodes(descendIntoTrivia: true)
            .OfType<QualifiedCrefSyntax>()
            .Where(c => c.Parent is XmlCrefAttributeSyntax)
            .Select(c => editedModel!.GetSymbolInfo(c).Symbol?.OriginalDefinition.GetDocumentationCommentId())
            .ToList();

        Assert.That(editedIds, Has.Count.EqualTo(originalIds.Count));
        Assert.That(editedIds, Is.EqualTo(originalIds));
        Assert.That(editedIds, Has.All.Not.Null);
    }

    private static int CountOccurrences(string text, string token) => text.Split(token).Length - 1;

    private static Document GetDocument(InMemoryWorkspace workspace) => GetDocument(workspace, FileName);

    private static Document GetDocument(InMemoryWorkspace workspace, string fileName)
    {
        var solution = workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var documentId = solution.GetDocumentIdsWithFilePath(workspace.PathOf(fileName)).Single();
        return solution.GetDocument(documentId)!;
    }
}
