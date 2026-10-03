// containerName resolution for generic types (defect A5 of run 20260910-013550-398).
//
// Turn 19 passed containerName: "EngineResultWrapper<T>" -> the type's own declared spelling -> and
// was rejected; turn 20 passed the bare "EngineResultWrapper" and succeeded. The cause was a single
// comparison in ResolveTypeByNameOrSnippet: Roslyn's Identifier.Text is already arity-stripped, so
// it could never equal a caller's raw generic string. Third recorded instance of this class of miss
// (cf. docs/current/project_qwen36_35b_smoketest_and_member_containername_gap.md).
//
// Driven through BasicRefactoringEngine rather than the RefactoringTools surface: the fix is one
// engine-level chokepoint shared by 13 call sites across Member, ModifyEnum and ModifyBaseType, and
// the engine needs three constructor arguments where the tool class needs sixteen.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class GenericContainerNameTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private BasicRefactoringEngine _refactoringEngine = null!;

    private const string GenericSource = """
        namespace TestProj;

        public class EngineResultWrapper<T>
        {
            public T? Value { get; set; }
        }

        public class PairWrapper<TKey, TValue>
        {
            public TKey? Key { get; set; }
        }

        public class PlainType
        {
            public int Count { get; set; }
        }
        """;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _refactoringEngine = new BasicRefactoringEngine(
            _workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, new SentinelConfiguration());

        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrappers.cs", GenericSource)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    public void NormalizeTypeName_LeavesANonTrailingAngleBracketAlone()
    {
        // Guard on the deliberate narrowness of the normalizer: only a *trailing* argument list is
        // arity. Truncating at any '<' would make a caller who pasted a whole declaration line
        // silently resolve to a type they didn't name, which is worse than reporting the miss.
        Assert.Multiple(() =>
        {
            Assert.That(SyntaxTargetResolver.NormalizeTypeName("Foo<T>"), Is.EqualTo("Foo"));
            Assert.That(SyntaxTargetResolver.NormalizeTypeName("Foo<TKey, TValue>"), Is.EqualTo("Foo"));
            Assert.That(SyntaxTargetResolver.NormalizeTypeName("Foo`1"), Is.EqualTo("Foo"));
            Assert.That(SyntaxTargetResolver.NormalizeTypeName("  Foo<T>  "), Is.EqualTo("Foo"));
            Assert.That(SyntaxTargetResolver.NormalizeTypeName("Foo"), Is.EqualTo("Foo"));
            Assert.That(SyntaxTargetResolver.NormalizeTypeName("public class Foo<T> : IBar"),
                Is.EqualTo("public class Foo<T> : IBar"),
                "no trailing '>', so nothing is stripped");
        });
    }
}

[TestFixture]
public class SyntaxTargetResolverTests
{
    private const string TestSource = """
        namespace TestProj;

        public interface IService { }

        public class ServiceImpl : IService { }

        public enum Status { Active, Inactive }

        public class Container
        {
            public Container() { }
            public void Method() { }
            public int Property { get; set; }
        }

        public struct ValueType { }
        """;

    [Test]
    public void ResolveCandidates_ZeroMatches_ReturnsEmptyList()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;

        var result = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "NonExistent");

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void PreferConstructorOverType_PicksConstructor()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "Container");
        var preferred = SyntaxTargetResolver.PreferConstructorOverType(candidates);

        Assert.That(preferred, Has.Count.EqualTo(1));
        Assert.That(preferred[0].Kind, Is.EqualTo(CandidateKind.Constructor));
    }

    [Test]
    public void ResolveCandidates_EnumMemberFound()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "Active");

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].Kind, Is.EqualTo(CandidateKind.EnumMember));
    }

    [Test]
    public void ResolveCandidates_ClassFound()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "Container");

        Assert.That(candidates, Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(candidates.Any(c => c.Kind == CandidateKind.Class), Is.True);
    }

    [Test]
    public void ResolveCandidates_InterfaceAndImplementerShareName_ReturnsBothAndPreferNonInterfaceMemberPicksImplementer()
    {
        const string source = """
            namespace TestProj;

            public interface IGreeter
            {
                void Greet();
            }

            public class Greeter : IGreeter
            {
                public void Greet() { }
            }
            """;
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;

        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "Greet");

        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(candidates.Select(c => c.Kind), Is.All.EqualTo(CandidateKind.Method));
        Assert.That(candidates.Select(c => c.ContainingTypeName), Is.EquivalentTo(new[] { "IGreeter", "Greeter" }));

        var preferred = SyntaxTargetResolver.PreferNonInterfaceMember(candidates);

        Assert.That(preferred, Has.Count.EqualTo(1));
        Assert.That(preferred[0].ContainingTypeName, Is.EqualTo("Greeter"));
        Assert.That(preferred[0].Node.Parent, Is.InstanceOf<ClassDeclarationSyntax>());
    }

    [Test]
    public void BuildHintForCandidates_MemberLookup_NotFound_ReturnsExpectedMessage()
    {
        var message = SyntaxTargetResolver.BuildHintForCandidates([], [], "not found");
        Assert.That(message, Is.EqualTo("contextSnippet not found: no candidates found."));
    }

    [Test]
    public void BuildHintForCandidates_MemberLookup_Ambiguous_ReturnsExpectedMessage()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, document.GetTextAsync().Result!, "Method");

        var message = SyntaxTargetResolver.BuildHintForCandidates(candidates, [0], "ambiguous");
        Assert.That(message, Does.Contain("contextSnippet ambiguous"));
        Assert.That(message, Does.Contain("candidates)"));
        Assert.That(message, Does.Contain("Provide a more specific contextSnippet"));
    }

    [Test]
    public void BuildHintForCandidates_MemberLookup_SnippetNoMatch_WithCandidatesPresent()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, document.GetTextAsync().Result!, "Method");

        // Empty matches array with candidates present indicates snippet did not match any candidate
        var message = SyntaxTargetResolver.BuildHintForCandidates(candidates, [], "snippet no match");
        Assert.That(message, Does.Contain("contextSnippet snippet no match"));
        Assert.That(message, Does.Contain("candidates)"));
        Assert.That(message, Does.Contain("Provide a more specific contextSnippet"));
    }

    [Test]
    public void BuildHintForCandidates_TypeLookup_NotFound_ReturnsExpectedMessage()
    {
        var message = SyntaxTargetResolver.BuildHintForCandidates([], [], "not found");
        Assert.That(message, Is.EqualTo("contextSnippet not found: no candidates found."));
    }

    [Test]
    public void BuildHintForCandidates_TypeLookup_Ambiguous_ReturnsExpectedMessage()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var candidates = new[] { "IService", "ServiceImpl", "Status", "Container", "ValueType" }
            .SelectMany(n => SyntaxTargetResolver.ResolveCandidates(root, document.GetTextAsync().Result!, n))
            .Where(c => c.Node is BaseTypeDeclarationSyntax)
            .ToList();

        var message = SyntaxTargetResolver.BuildHintForCandidates(candidates, [0, 1], "ambiguous");
        Assert.That(message, Does.Contain("contextSnippet ambiguous"));
        Assert.That(message, Does.Contain("candidates)"));
        Assert.That(message, Does.Contain("Provide a more specific contextSnippet"));
    }

    [Test]
    public void BuildHintForCandidates_TypeLookup_SnippetNoMatch_WithCandidatesPresent()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var candidates = new[] { "IService", "ServiceImpl", "Status", "Container", "ValueType" }
            .SelectMany(n => SyntaxTargetResolver.ResolveCandidates(root, document.GetTextAsync().Result!, n))
            .Where(c => c.Node is BaseTypeDeclarationSyntax)
            .ToList();

        // Empty matches array with candidates present indicates snippet did not match any candidate
        var message = SyntaxTargetResolver.BuildHintForCandidates(candidates, [], "snippet no match");
        Assert.That(message, Does.Contain("contextSnippet snippet no match"));
        Assert.That(message, Does.Contain("candidates)"));
        Assert.That(message, Does.Contain("Provide a more specific contextSnippet"));
    }

    [Test]
    public void BuildContainerNotFoundMessage_NoTypesInFile_ReturnsExpectedMessage()
    {
        var emptySource = "namespace Empty;";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", emptySource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;

        var message = SyntaxTargetResolver.BuildContainerNotFoundMessage(root, "Foo");
        Assert.That(message, Does.Contain("No type named 'Foo' was found"));
        Assert.That(message, Does.Contain("this file declares no types at all"));
    }

    [Test]
    public void BuildContainerNotFoundMessage_TypesInFile_ListsAvailableTypes()
    {
        var message = SyntaxTargetResolver.BuildContainerNotFoundMessage(
            TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]).Projects.First().Documents.First().GetSyntaxRootAsync().Result!,
            "NonExistent");
        Assert.That(message, Does.Contain("No type named 'NonExistent' was found in this file"));
        Assert.That(message, Does.Contain("Types declared here:"));
        Assert.That(message, Does.Contain("Container"));
    }

    [Test]
    public void BuildHintForCandidates_NotFound_ReturnsExpectedMessage()
    {
        var message = SyntaxTargetResolver.BuildHintForCandidates([], [], "not found");
        Assert.That(message, Is.EqualTo("contextSnippet not found: no candidates found."));
    }

    [Test]
    public void BuildHintForCandidates_NotFound_NoCandidates_ReturnsExpectedMessage()
    {
        var message = SyntaxTargetResolver.BuildHintForCandidates([], [], "not found");
        Assert.That(message, Is.EqualTo("contextSnippet not found: no candidates found."));
    }

    [Test]
    public void BuildHintForCandidates_Ambiguous_UsesPrecomputedStartLine()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "Container");

        var message = SyntaxTargetResolver.BuildHintForCandidates(candidates, [0], "ambiguous");
        Assert.That(message, Does.Contain("line"));
        Assert.That(message, Does.Contain("contextSnippet ambiguous"));
    }

    [Test]
    public void BuildHintForCandidates_LongDeclarationLines_ProduceExactWordingWithPreviewsCutTo50Characters()
    {
        // Pins the agent-visible wording byte for byte. SyntaxNodeCandidate.Preview allows 80
        // characters but hints have always shown at most 50, so the formatter must cut again.
        var overLimit = "public int Compute(int firstOperand, int secondOperand, int thirdOperand) { return 0; }";
        var midLength = "public int Compute(int a, int b, int c, int d) { return 0; }";
        var short1 = "public int Compute(int a) { return a; }";
        var source = string.Join(
            "\n",
            "namespace TestProj;",
            "",
            "public class Calc",
            "{",
            "    " + overLimit,
            "    " + midLength,
            "    " + short1,
            "}");
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, document.GetTextAsync().Result!, "Compute");
        Assert.That(overLimit.Length, Is.GreaterThan(80));
        Assert.That(midLength.Length, Is.InRange(51, 80));

        var message = SyntaxTargetResolver.BuildHintForCandidates(candidates, [], "ambiguous");

        var expected = "contextSnippet ambiguous (3 candidates): "
            + "line 5 `" + overLimit.Substring(0, 47) + "...`, "
            + "line 6 `" + midLength.Substring(0, 47) + "...`, "
            + "line 7 `" + short1 + "`. "
            + "Provide a more specific contextSnippet or use lineBefore/lineAfter.";
        Assert.That(message, Is.EqualTo(expected));
    }
}
