// containerName resolution for generic types (defect A5 of run 20260910-013550-398).
//
// Turn 19 passed containerName: "EngineResultWrapper<T>" -> the type's own declared spelling -> and
// was rejected; turn 20 passed the bare "EngineResultWrapper" and succeeded. The cause was a single
// comparison in ResolveTypeByNameOrSnippet: Roslyn's Identifier.Text is already arity-stripped, so
// it could never equal a caller's raw generic string. Third recorded instance of this class of miss
// (cf. docs/current/project_qwen36_35b_smoketest_and_member_containername_gap.md).
//
// Driven through BasicRefactoringEngine rather than the RefactoringStructuralTools surface: the fix is one
// engine-level chokepoint shared by 13 call sites across MemberTool, ModifyEnumTool and ModifyBaseTypeTool, and
// the engine needs three constructor arguments where the tool class needs sixteen.

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
[Category("SyntaxTargetResolver")] // sentinel:auto-category
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
[Category("SyntaxTargetResolver")] // sentinel:auto-category
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
    [Category("CandidateKind")] // sentinel:auto-category
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
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
    [Category("CandidateKind")] // sentinel:auto-category
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
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
    [Category("CandidateKind")] // sentinel:auto-category
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
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
    [Category("CandidateKind")] // sentinel:auto-category
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
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
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
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
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
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

    [Test]
    [Category("SyntaxNodeCandidate")] // sentinel:auto-category
    public void ResolveBySnippetWithWarning_AmbiguousSnippet_CarriesCandidatesOnlyWhenFilePathGiven()
    {
        var source = """
            public class Holder
            {
                public void Run(int x) { }
                public void Run(string s) { }
            }
            """;
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result!;
        var candidates = SyntaxTargetResolver.ResolveCandidates(root, sourceText, "Run");

        var withPath = Assert.Throws<ToolAmbiguousMatchException>(() =>
            SyntaxTargetResolver.ResolveBySnippetWithWarning(candidates, sourceText, "public void Run(", null, null,
                (c, m, mode) => SyntaxTargetResolver.BuildHintForCandidates(c, m, mode), filePath: "Source.cs"));
        Assert.That(withPath!.Candidates, Has.Count.EqualTo(2));
        Assert.That(withPath.Candidates!.All(c => c.Counted && c.FilePath == "Source.cs" && c.Line > 0
            && !string.IsNullOrEmpty(c.ContextSnippet)), Is.True);

        var withoutPath = Assert.Throws<ToolAmbiguousMatchException>(() =>
            SyntaxTargetResolver.ResolveBySnippetWithWarning(candidates, sourceText, "public void Run(", null, null,
                (c, m, mode) => SyntaxTargetResolver.BuildHintForCandidates(c, m, mode)));
        Assert.That(withoutPath!.Candidates, Is.Null.Or.Empty);
    }

    [Test]
    public void ContainingTypeMatches_GenericAndCaseSpellings_AllMatch()
    {
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Box", "Box"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Box", "box"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Box", "Box<T>"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Box", "Box`1"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Box<T>", "Box"), Is.True);
    }

    [Test]
    public void ContainingTypeMatches_NullActual_IsFalse()
    {
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches(null, "Box"), Is.False);
    }

    [Test]
    public void FilterByContainingTypeStrict_SingleCandidateInOtherType_ReturnsEmpty()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var root = compilation.Projects.First().Documents.First().GetSyntaxRootAsync().Result!;
        var candidates = new List<SyntaxNodeCandidate>
        {
            new SyntaxNodeCandidate(root, CandidateKind.Method, "Run", "Other", 1, 1, "public void Run()"),
        };

        var strict = SyntaxTargetResolver.FilterByContainingTypeStrict(candidates, "Box");
        var lenient = SyntaxTargetResolver.FilterByContainingType(candidates, "Box");

        // The strict filter drops the candidate; the lenient filter keeps it (a single candidate is never narrowed).
        Assert.That(strict, Is.Empty);
        Assert.That(lenient, Has.Count.EqualTo(1));
        Assert.That(lenient[0].ContainingTypeName, Is.EqualTo("Other"));
    }

    [Test]
    public void FilterByContainingTypeStrict_NullContainer_ReturnsInput()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var root = compilation.Projects.First().Documents.First().GetSyntaxRootAsync().Result!;
        var candidates = new List<SyntaxNodeCandidate>
        {
            new SyntaxNodeCandidate(root, CandidateKind.Method, "Run", "Other", 1, 1, "public void Run()"),
        };

        Assert.That(SyntaxTargetResolver.FilterByContainingTypeStrict(candidates, null), Is.SameAs(candidates));
        Assert.That(SyntaxTargetResolver.FilterByContainingTypeStrict(candidates, "  "), Is.SameAs(candidates));
    }

    [Test]
    public void ContainingTypeMatches_DottedContainer_MatchesByLastSegment()
    {
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Inner", "Outer.Inner"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Type", "Ns.Type"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Inner", "Outer<T>.Inner<U>"), Is.True);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Inner", "Outer.Inner<U>"), Is.True);
    }

    [Test]
    public void ContainingTypeMatches_DottedContainerWithADifferentLastSegment_IsFalse()
    {
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Inner", "Outer.Other"), Is.False);
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Inner", "Inner."), Is.False);
    }

    [Test]
    public void ContainingTypeMatches_DottedContainer_DoesNotVerifyTheQualifier()
    {
        // Documents the accepted looseness: the candidate only carries a bare identifier, so the
        // qualifier "Wrong" cannot be checked and the match falls back to the last segment alone.
        Assert.That(SyntaxTargetResolver.ContainingTypeMatches("Inner", "Wrong.Inner"), Is.True);
    }

    [Test]
    public void FilterByContainingTypeStrict_DottedContainerWithNoMatchingLastSegment_ReturnsEmpty()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var root = compilation.Projects.First().Documents.First().GetSyntaxRootAsync().Result!;
        var candidates = new List<SyntaxNodeCandidate>
        {
            new SyntaxNodeCandidate(root, CandidateKind.Method, "Run", "Inner", 1, 1, "public void Run()"),
            new SyntaxNodeCandidate(root, CandidateKind.Method, "Run", "Inner", 2, 2, "public void Run(int x)"),
        };

        var strict = SyntaxTargetResolver.FilterByContainingTypeStrict(candidates, "Outer.Other");

        Assert.That(strict, Is.Empty);
    }

    [Test]
    public void BuildContainerMissDetail_ContainerAbsent_ListsTypesAndWhereTheMemberLives()
    {
        var source = "public class A\n{\n    public void Run()\n    {\n    }\n}\n\npublic class B\n{\n    public int X;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "Missing", "Sample.cs",
            "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false);

        Assert.That(message, Does.Contain("No type named 'Missing' was found in this file"));
        Assert.That(message, Does.Contain("'Run' is declared in: 1. A."));
        Assert.That(candidates, Has.Count.EqualTo(1));
    }

    [Test]
    public void BuildContainerMissDetail_ContainerPresentWithoutMember_NamesTheTypesThatDeclareIt_WithRetryPerTypeAndReadFileRange()
    {
        var source = "public class A\n{\n    public void Run()\n    {\n    }\n}\n\npublic class B\n{\n    public int X;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "B", "Sample.cs",
            "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false);

        Assert.That(message, Does.Contain("Type 'B' declares no member named 'Run'"));
        Assert.That(message, Does.Contain("'Run' is declared in: 1. A."));
        Assert.That(message, Does.Contain("Retry ReadFile(filePath: 'Sample.cs', memberName: 'Run', containerName: 'A')."));
        Assert.That(message, Does.Contain("To read exact lines: ReadFile(filePath: 'Sample.cs', startLine: 3, endLine: 5)."));
        Assert.That(candidates, Has.Count.EqualTo(1));
    }

    [Test]
    public void BuildContainerMissDetail_MemberDeclaredNowhere_PointsAtGetFileOutline()
    {
        var source = "public class A\n{\n    public void Run()\n    {\n    }\n}\n\npublic class B\n{\n    public int X;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Nope", "A", "Sample.cs",
            "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false);

        Assert.That(message, Does.Contain("No type in this file declares 'Nope'"));
        Assert.That(message, Does.Contain("GetFileOutline(filePath: 'Sample.cs')"));
        Assert.That(candidates, Is.Empty);
        Assert.That(message, Does.Not.Contain("To read exact lines"));
    }

    [Test]
    public void BuildContainerMissDetail_MethodsOnly_IgnoresAPropertyOfTheSameName()
    {
        var source = "public class A\n{\n    public int Value { get; set; }\n}\n\npublic class B\n{\n    public int Value() => 1;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Value", "C", "Sample.cs",
            "GetMethodSource(filePath: '{file}', methodName: '{member}', containerName: '{container}')", methodsOnly: true);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(message, Does.Contain("'Value' is declared in: 1. B."));
        Assert.That(message, Does.Not.Contain("A.public int Value"));
    }

    [Test]
    public void BuildContainerMissDetail_OverloadsInOneType_OffersNoContainerRetryForThatType()
    {
        var source = "public class A\n{\n    public void Run()\n    {\n    }\n    public void Run(int x)\n    {\n    }\n}\n\npublic class B\n{\n    public int X;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "B", "Sample.cs",
            "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false);

        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(message, Does.Contain("lines 3-5"));
        Assert.That(message, Does.Contain("lines 6-8"));
        Assert.That(message, Does.Not.Contain("containerName: 'A'"));
    }

    [Test]
    public void BuildContainerMissDetail_CandidateLinesMatchTheMessage()
    {
        var source = "public class A\n{\n    public void Run()\n    {\n    }\n    public void Run(int x)\n    {\n    }\n}\n\npublic class B\n{\n    public int X;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "B", "Sample.cs",
            "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false);

        Assert.That(candidates, Has.Count.EqualTo(2));
        Assert.That(candidates[0].Line, Is.EqualTo(3));
        Assert.That(candidates[1].Line, Is.EqualTo(6));
        foreach (var candidate in candidates)
        {
            Assert.That(message, Does.Contain($"lines {candidate.Line}-"));
        }

        Assert.That(message, Does.Contain("lines 3-5"));
        Assert.That(message, Does.Contain("lines 6-8"));
        Assert.That(message, Does.Contain("To read exact lines: ReadFile(filePath: 'Sample.cs', startLine: 3, endLine: 5)."));
    }

    [Test]
    public void BuildContainerMissDetail_RetryTemplate_IsExpandedWithFileMemberAndContainer()
    {
        var source = "public class A\n{\n    public void Run()\n    {\n    }\n}\n\npublic class B\n{\n    public int X;\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, _) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "B", "Sample.cs",
            "GetMethodSource(filePath: '{file}', methodName: '{member}', containerName: '{container}')", methodsOnly: false);

        Assert.That(message, Does.Contain("Retry GetMethodSource(filePath: 'Sample.cs', methodName: 'Run', containerName: 'A')."));
        Assert.That(message, Does.Not.Contain("{file}"));
        Assert.That(message, Does.Not.Contain("{member}"));
        Assert.That(message, Does.Not.Contain("{container}"));
    }

    [Test]
    public void BuildContainerMissDetail_TopLevelDeclaration_SaysOmitTheContainerAndDropsTheContainerArgument()
    {
        var source = "public class Run\n{\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;

        var (message, candidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "Missing", "Sample.cs",
            "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')", methodsOnly: false, includeTypeKinds: true);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].ContainingType, Is.Empty);
        Assert.That(message, Does.Contain("the declaration is top level, so omit the container"));
        Assert.That(message, Does.Contain("Retry ReadFile(filePath: 'Sample.cs', memberName: 'Run'); the declaration is top level"));
        Assert.That(message, Does.Not.Contain("Retry ReadFile(filePath: 'Sample.cs', memberName: 'Run', containerName:"));
    }

    [Test]
    public void BuildContainerMissDetail_IncludeTypeKinds_ListsATypeNamedLikeTheMember()
    {
        var source = "public class Holder\n{\n}\n\npublic class Run\n{\n}\n";
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Sample.cs", source)]);
        var document = compilation.Projects.First().Documents.First();
        var root = document.GetSyntaxRootAsync().Result!;
        var sourceText = document.GetTextAsync().Result;
        const string template = "ReadFile(filePath: '{file}', memberName: '{member}', containerName: '{container}')";

        var (withoutTypes, withoutCandidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "Holder", "Sample.cs", template, methodsOnly: false, includeTypeKinds: false);

        Assert.That(withoutCandidates, Is.Empty);
        Assert.That(withoutTypes, Does.Contain("Type 'Holder' declares no member named 'Run'"));
        Assert.That(withoutTypes, Does.Contain("No type in this file declares 'Run'"));

        var (withTypes, withCandidates) = SyntaxTargetResolver.BuildContainerMissDetail(
            root, sourceText, "Run", "Holder", "Sample.cs", template, methodsOnly: false, includeTypeKinds: true);

        Assert.That(withCandidates, Has.Count.EqualTo(1));
        Assert.That(withTypes, Does.Contain("Type 'Holder' declares no declaration named 'Run'"));
        Assert.That(withTypes, Does.Contain("(top level)"));
    }

    [Test]
    public void FilterByContainingTypeStrict_TwoCandidatesInOtherTypes_ReturnsEmpty_WhereLenientFallsBackToAll()
    {
        var compilation = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", TestSource)]);
        var root = compilation.Projects.First().Documents.First().GetSyntaxRootAsync().Result!;
        var candidates = new List<SyntaxNodeCandidate>
        {
            new SyntaxNodeCandidate(root, CandidateKind.Method, "Run", "Other1", 1, 1, "public void Run()"),
            new SyntaxNodeCandidate(root, CandidateKind.Method, "Run", "Other2", 2, 2, "public void Run(int x)"),
        };

        var strict = SyntaxTargetResolver.FilterByContainingTypeStrict(candidates, "Box");
        var lenient = SyntaxTargetResolver.FilterByContainingType(candidates, "Box");

        // Lenient keeps both because no candidate matches "Box" (narrowed.Count is 0, so the input comes back).
        Assert.That(strict, Is.Empty);
        Assert.That(lenient, Has.Count.EqualTo(2));
    }
}
