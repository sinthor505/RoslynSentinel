// containerName resolution for generic types (defect A5 of run 20260910-013550-398).
//
// Turn 19 passed containerName: "EngineResultWrapper<T>" — the type's own declared spelling — and
// was rejected; turn 20 passed the bare "EngineResultWrapper" and succeeded. The cause was a single
// comparison in ResolveTypeByNameOrSnippet: Roslyn's Identifier.Text is already arity-stripped, so
// it could never equal a caller's raw generic string. Third recorded instance of this class of miss
// (cf. docs/current/project_qwen36_35b_smoketest_and_member_containername_gap.md).
//
// Driven through RefactoringEngine rather than the SentinelRefactoringTools surface: the fix is one
// engine-level chokepoint shared by 13 call sites across Member, ModifyEnum and ModifyBaseType, and
// the engine needs three constructor arguments where the tool class needs sixteen.

using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class GenericContainerNameTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private RefactoringEngine _refactoringEngine = null!;

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
        _refactoringEngine = new RefactoringEngine(
            NullLogger<RefactoringEngine>.Instance, _workspaceManager, new SentinelConfiguration());

        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrappers.cs", GenericSource)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // All four spellings of the same one-type-parameter type. "EngineResultWrapper<T>" is the
    // literal that failed in run 398; the backtick form is the metadata spelling a caller reading
    // a Roslyn/reflection name would supply; the bare form is what already worked.
    [TestCase("EngineResultWrapper")]
    [TestCase("EngineResultWrapper<T>")]
    [TestCase("EngineResultWrapper<TResult>")]
    [TestCase("EngineResultWrapper`1")]
    public async Task AddMember_ResolvesEverySpellingOfAGenericContainerAsync(string containerName)
    {
        var result = await _refactoringEngine.AddMemberAsync(
            FilePathWrapper.FromWire("Wrappers.cs", _workspaceManager.GetSolutionRoot()),
            containerName,
            "public int Added { get; set; }");

        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty,
            $"containerName '{containerName}' should resolve to EngineResultWrapper<T>. {result.Message}");
        Assert.That(result.UpdatedText, Does.Contain("public int Added"));
    }

    [TestCase("PairWrapper")]
    [TestCase("PairWrapper<TKey, TValue>")]
    [TestCase("PairWrapper<TKey,TValue>")]
    [TestCase("PairWrapper`2")]
    public async Task AddMember_ResolvesAMultiParameterGenericContainerAsync(string containerName)
    {
        // Separate case from the arity-1 type: a naive "strip everything from the first '<'" fix
        // handles Foo<T> but a comma-splitting one does not, and the whitespace variant is the
        // spelling a caller copying from a declaration line actually produces.
        var result = await _refactoringEngine.AddMemberAsync(
            FilePathWrapper.FromWire("Wrappers.cs", _workspaceManager.GetSolutionRoot()),
            containerName,
            "public int Added { get; set; }");

        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty,
            $"containerName '{containerName}' should resolve to PairWrapper<TKey, TValue>. {result.Message}");
    }

    [Test]
    public async Task AddMember_NonGenericContainerStillResolvesAsync()
    {
        // Normalization must not disturb the ordinary case, which is the overwhelming majority of
        // calls through this chokepoint.
        var result = await _refactoringEngine.AddMemberAsync(
            FilePathWrapper.FromWire("Wrappers.cs", _workspaceManager.GetSolutionRoot()),
            "PlainType",
            "public int Added { get; set; }");

        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty, result.Message);
    }

    [Test]
    public async Task AddMember_UnknownContainer_ListsTheTypesTheFileActuallyDeclaresAsync()
    {
        // The old message was the literal "// Container not found." — no names, and prefixed as if
        // it were a line of code. Listing what's available is what lets the caller correct itself
        // in one turn instead of guessing, and immediately reveals a wrong-file mistake.
        var result = await _refactoringEngine.AddMemberAsync(
            FilePathWrapper.FromWire("Wrappers.cs", _workspaceManager.GetSolutionRoot()),
            "NoSuchType",
            "public int Added { get; set; }");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.Multiple(() =>
        {
            Assert.That(result.Message, Does.Contain("NoSuchType"), "must name what was asked for");
            Assert.That(result.Message, Does.Contain("EngineResultWrapper"));
            Assert.That(result.Message, Does.Contain("PairWrapper"));
            Assert.That(result.Message, Does.Contain("PlainType"));
            Assert.That(result.Message, Does.Not.StartWith("//"),
                "this is an error message, not a line of code");
        });
    }

    [Test]
    public void NormalizeTypeName_LeavesANonTrailingAngleBracketAlone()
    {
        // Guard on the deliberate narrowness of the normalizer: only a *trailing* argument list is
        // arity. Truncating at any '<' would make a caller who pasted a whole declaration line
        // silently resolve to a type they didn't name, which is worse than reporting the miss.
        Assert.Multiple(() =>
        {
            Assert.That(RefactoringEngine.NormalizeTypeName("Foo<T>"), Is.EqualTo("Foo"));
            Assert.That(RefactoringEngine.NormalizeTypeName("Foo<TKey, TValue>"), Is.EqualTo("Foo"));
            Assert.That(RefactoringEngine.NormalizeTypeName("Foo`1"), Is.EqualTo("Foo"));
            Assert.That(RefactoringEngine.NormalizeTypeName("  Foo<T>  "), Is.EqualTo("Foo"));
            Assert.That(RefactoringEngine.NormalizeTypeName("Foo"), Is.EqualTo("Foo"));
            Assert.That(RefactoringEngine.NormalizeTypeName("public class Foo<T> : IBar"),
                Is.EqualTo("public class Foo<T> : IBar"),
                "no trailing '>', so nothing is stripped");
        });
    }
}
