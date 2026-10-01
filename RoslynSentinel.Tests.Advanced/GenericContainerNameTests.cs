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
            Assert.That(SymbolNavigationEngine.NormalizeTypeName("Foo<T>"), Is.EqualTo("Foo"));
            Assert.That(SymbolNavigationEngine.NormalizeTypeName("Foo<TKey, TValue>"), Is.EqualTo("Foo"));
            Assert.That(SymbolNavigationEngine.NormalizeTypeName("Foo`1"), Is.EqualTo("Foo"));
            Assert.That(SymbolNavigationEngine.NormalizeTypeName("  Foo<T>  "), Is.EqualTo("Foo"));
            Assert.That(SymbolNavigationEngine.NormalizeTypeName("Foo"), Is.EqualTo("Foo"));
            Assert.That(SymbolNavigationEngine.NormalizeTypeName("public class Foo<T> : IBar"),
                Is.EqualTo("public class Foo<T> : IBar"),
                "no trailing '>', so nothing is stripped");
        });
    }
}
