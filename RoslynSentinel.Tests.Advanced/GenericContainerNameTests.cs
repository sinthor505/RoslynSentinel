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
}
