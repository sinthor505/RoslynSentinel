// Slice 1a of docs/current/plans/plan_member_local_function_targets.md: local-function candidates in
// SyntaxTargetResolver. Default ResolveCandidates calls must not surface local functions; the opt-in flag,
// the container check and the precedence helper are exercised directly on syntax parsed from source text.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

[TestFixture]
[Category("SyntaxTargetResolver")]
public class LocalFunctionCandidateTests
{
    private const string LocalOnlySource = """
        namespace TestProj;

        public class Type
        {
            public void Outer()
            {
                int Helper() => 1;
                Helper();
            }
        }
        """;

    private static (SyntaxNode Root, SourceText Text) Parse(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return (tree.GetRoot(), tree.GetText());
    }

    [Test]
    public void DefaultResolve_DoesNotReturnLocalFunction()
    {
        var (root, text) = Parse(LocalOnlySource);

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper");

        Assert.That(candidates, Is.Empty);
    }

    [Test]
    public void OptIn_ReturnsLocalFunctionWithEnclosingMemberName()
    {
        var (root, text) = Parse(LocalOnlySource);

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].Kind, Is.EqualTo(CandidateKind.LocalFunction));
        Assert.That(candidates[0].EnclosingMemberName, Is.EqualTo("Outer"));
        Assert.That(candidates[0].ContainingTypeName, Is.EqualTo("Type"));
    }

    [TestCase("Outer", true)]
    [TestCase("Type.Outer", true)]
    [TestCase("Type", true)]
    [TestCase("Other", false)]
    public void CandidateMatchesContainer_AcceptsEnclosingMemberAndType(string containerName, bool expected)
    {
        var (root, text) = Parse(LocalOnlySource);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(SyntaxTargetResolver.CandidateMatchesContainer(candidates[0], containerName), Is.EqualTo(expected));
    }

    [Test]
    public void PreferMembers_DropsLocalFunctionWhenSameNamedMethodExists()
    {
        const string source = """
            namespace TestProj;

            public class Type
            {
                public void Helper() { }

                public void Outer()
                {
                    int Helper() => 1;
                    Helper();
                }
            }
            """;
        var (root, text) = Parse(source);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);
        Assert.That(candidates, Has.Count.EqualTo(2));

        var preferred = SyntaxTargetResolver.PreferMembersOverLocalFunctions(candidates);

        Assert.That(preferred, Has.Count.EqualTo(1));
        Assert.That(preferred[0].Kind, Is.EqualTo(CandidateKind.Method));
    }

    [Test]
    public void PreferMembers_DropsLocalFunctionWhenSameNamedClassExists()
    {
        const string source = """
            namespace TestProj;

            public class Helper { }

            public class Type
            {
                public void Outer()
                {
                    int Helper() => 1;
                    Helper();
                }
            }
            """;
        var (root, text) = Parse(source);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);
        Assert.That(candidates, Has.Count.EqualTo(2));

        var preferred = SyntaxTargetResolver.PreferMembersOverLocalFunctions(candidates);

        Assert.That(preferred, Has.Count.EqualTo(1));
        Assert.That(preferred[0].Kind, Is.EqualTo(CandidateKind.Class));
    }

    [Test]
    public void PreferMembers_KeepsLocalFunctionWhenAlone()
    {
        var (root, text) = Parse(LocalOnlySource);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);

        var preferred = SyntaxTargetResolver.PreferMembersOverLocalFunctions(candidates);

        Assert.That(preferred, Has.Count.EqualTo(1));
        Assert.That(preferred[0].Kind, Is.EqualTo(CandidateKind.LocalFunction));
    }

    [Test]
    public void BuildHint_TwoSameNamedLocalFunctions_NamesEnclosingMembersAndContainerRetry()
    {
        const string source = """
            namespace TestProj;

            public class Type
            {
                public void First()
                {
                    int Helper() => 1;
                    Helper();
                }

                public void Second()
                {
                    int Helper() => 2;
                    Helper();
                }
            }
            """;
        var (root, text) = Parse(source);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);
        Assert.That(candidates, Has.Count.EqualTo(2));

        var hint = SyntaxTargetResolver.BuildHintForCandidates(candidates, new List<int>(), "ambiguous");

        Assert.That(hint, Does.Contain("local function inside Type.First"));
        Assert.That(hint, Does.Contain("local function inside Type.Second"));
        Assert.That(hint, Does.Contain("containerName: \"First\""));
        Assert.That(hint, Does.Contain("containerName: \"Second\""));
        Assert.That(hint, Does.Not.Contain("overloads"));
    }

    [Test]
    public void BuildShadowedLocalFunctionHint_LocalOnlyList_ReturnsNull()
    {
        var (root, text) = Parse(LocalOnlySource);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);

        var hint = SyntaxTargetResolver.BuildShadowedLocalFunctionHint(candidates, text);

        Assert.That(hint, Is.Null);
    }

    [Test]
    public void BuildShadowedLocalFunctionHint_MemberOnlyList_ReturnsNull()
    {
        const string source = """
            namespace TestProj;

            public class Type
            {
                public void Helper() { }
            }
            """;
        var (root, text) = Parse(source);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);
        Assert.That(candidates, Has.Count.EqualTo(1));

        var hint = SyntaxTargetResolver.BuildShadowedLocalFunctionHint(candidates, text);

        Assert.That(hint, Is.Null);
    }

    [Test]
    public void BuildShadowedLocalFunctionHint_MethodAndLocalFunctionSameName_NamesEnclosingMember()
    {
        const string source = """
            namespace TestProj;

            public class Type
            {
                public void Helper() { }

                public void Outer()
                {
                    int Helper() => 1;
                    Helper();
                }
            }
            """;
        var (root, text) = Parse(source);
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, text, "Helper", includeLocalFunctions: true);
        Assert.That(candidates, Has.Count.EqualTo(2));

        var hint = SyntaxTargetResolver.BuildShadowedLocalFunctionHint(candidates, text);

        Assert.That(hint, Is.Not.Null);
        Assert.That(hint, Does.Contain("inside Type.Outer"));
        Assert.That(hint, Does.Contain("containerName: \"Outer\""));
    }
}
