using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// Direct tests for EnclosingMemberResolver.GetEnclosingMemberName, moved verbatim out of
// WorkspaceReadNavigationImpl. Pins the existing behaviour, quirks included (constructors return
// the type name, indexers "this[]", operators "operator +", local functions and lambdas resolve
// to the outer member).
[TestFixture]
public class EnclosingMemberResolverTests
{
    private const string Source = """
        using System;

        public class Sample
        {
            private int _field1, _field2;

            public Sample()
            {
                var ctorMarker = 1;
            }

            public int Prop { get; set; }

            public int this[int i]
            {
                get { return i; }
            }

            public static Sample operator +(Sample a, Sample b)
            {
                return a; // operatorMarker
            }

            public void Run()
            {
                int Local() { return 2; } // localMarker
                Func<int> f = () => 3; // lambdaMarker
                Console.WriteLine("methodMarker");
            }

            public event EventHandler? Changed;
        }
        """;

    private static string? Resolve(string marker, bool atStartOfMarker = true)
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var root = tree.GetRoot();
        var index = Source.IndexOf(marker, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"marker '{marker}' not found in test source");
        return EnclosingMemberResolver.GetEnclosingMemberName(root, atStartOfMarker ? index : index + marker.Length);
    }

    [Test]
    public void MethodBody_ReturnsMethodName()
    {
        Assert.That(Resolve("methodMarker"), Is.EqualTo("Run"));
    }

    [Test]
    public void Constructor_ReturnsTypeName()
    {
        Assert.That(Resolve("ctorMarker"), Is.EqualTo("Sample"));
    }

    [Test]
    public void Operator_ReturnsOperatorWithToken()
    {
        Assert.That(Resolve("operatorMarker"), Is.EqualTo("operator +"));
    }

    [Test]
    public void Property_ReturnsPropertyName()
    {
        Assert.That(Resolve("Prop"), Is.EqualTo("Prop"));
    }

    [Test]
    public void Indexer_ReturnsThisBrackets()
    {
        Assert.That(Resolve("return i;"), Is.EqualTo("this[]"));
    }

    [Test]
    public void Field_ReturnsAllVariableNames()
    {
        Assert.That(Resolve("_field1"), Is.EqualTo("_field1, _field2"));
    }

    [Test]
    public void EventField_ReturnsEventName()
    {
        Assert.That(Resolve("Changed"), Is.EqualTo("Changed"));
    }

    [Test]
    public void LocalFunctionAndLambda_ResolveToOuterMember()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Resolve("return 2;"), Is.EqualTo("Run"));
            Assert.That(Resolve("=> 3"), Is.EqualTo("Run"));
        });
    }

    [Test]
    public void UsingDirective_ReturnsNull()
    {
        Assert.That(Resolve("using System;"), Is.Null);
    }

    [Test]
    public void TypeHeader_ReturnsNull()
    {
        Assert.That(Resolve("class Sample"), Is.Null);
    }

    [Test]
    public void PositionOutsideRoot_ReturnsNull()
    {
        var root = CSharpSyntaxTree.ParseText(Source).GetRoot();
        Assert.Multiple(() =>
        {
            Assert.That(EnclosingMemberResolver.GetEnclosingMemberName(root, -1), Is.Null);
            Assert.That(EnclosingMemberResolver.GetEnclosingMemberName(root, root.FullSpan.End + 1), Is.Null);
        });
    }
}
