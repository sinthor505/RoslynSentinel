using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;

namespace RoslynSentinel.Tests.Battery.Basic;

/// <summary>
/// MemberRefactoringEngine.ReplaceMemberWithAdditionsAsync (D2): a newMemberSource with several declarations replaces the target
/// with the ONE declaration that has the target's name and member kind and adds the rest after it in source order, in one write.
/// Zero or two-plus same-name same-kind declarations, a malformed declaration, a stray '}' and a body fragment are rejected and
/// nothing is written. ReplaceMemberAsync itself still rejects multi-declaration source (MemberSingleDeclarationTests).
/// Engine-level, so it runs on an InMemoryWorkspace; assertions are on the engine's UpdatedText and on the workspace text.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("RefactoringStructuralTools")] // sentinel:auto-category
public class MemberReplaceMultiDeclarationTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/MemberReplaceMultiDeclarationFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class ReplaceAddTarget
    {
        public int Alpha() => 1;

        public int Beta() => 2;
    }
    """;

    private const string OverloadFixtureSource = """
    namespace ContosoOrders.Core;

    public class ReplaceAddTarget
    {
        public int Compute(int x) => x;

        public int Compute(string s) => s.Length;

        public int Beta() => 2;
    }
    """;

    private static MemberRefactoringEngine BuildEngine(InMemoryWorkspace workspace)
    {
        var symbolNav = TestEngines.SymbolNavigationEngine(workspace.Manager, NullLogger<SymbolNavigationEngine>.Instance);
        return TestEngines.MemberRefactoringEngine(workspace.Manager, symbolNav, TestEngines.ValidationEngine(workspace.Manager));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();

    private static int IndexOfOrFail(string text, string fragment)
    {
        var index = text.IndexOf(fragment, StringComparison.Ordinal);
        Assert.That(index, Is.GreaterThanOrEqualTo(0), $"'{fragment}' is missing from:\n{text}");
        return index;
    }

    /// <summary>The compile errors the write's compile gate would see for <paramref name="updatedText"/> in the fixture document.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> CompileErrorsAsync(InMemoryWorkspace workspace, string relativePath, string updatedText)
    {
        var solution = workspace.Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var documentId = solution.GetDocumentIdsWithFilePath(workspace.PathOf(relativePath)).First();
        var trial = solution.WithDocumentText(documentId, SourceText.From(updatedText));
        var compilation = await trial.GetProject(documentId.ProjectId)!.GetCompilationAsync();
        var tree = await trial.GetDocument(documentId)!.GetSyntaxTreeAsync();
        return compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree == tree).ToList();
    }

    [Test]
    public async Task Replace_ExactlyOneNameMatch_ReplacesTargetAndAddsTheRestAfterItInOrderAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);

        var result = await engine.ReplaceMemberWithAdditionsAsync(workspace.PathOf(FixtureRelativePath), "Alpha",
            "public int Alpha() => 10;\n\npublic int Gamma() => 3;\n\nprivate string _delta = \"d\";");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
        const string expected = """
            namespace ContosoOrders.Core;

            public class ReplaceAddTarget
            {
                public int Alpha() => 10;

                public int Gamma() => 3;

                private string _delta = "d";

                public int Beta() => 2;
            }
            """;
        Assert.Multiple(() =>
        {
            Assert.That(Normalize(result.UpdatedText), Is.EqualTo(Normalize(expected)));
            Assert.That(result.Message, Does.Contain("Replaced with Method 'Alpha'"));
            Assert.That(result.Message, Does.Contain("Method 'Gamma'").And.Contain("Field '_delta'"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(FixtureSource), "The engine proposes text; it must not write it.");
    }

    [Test]
    public async Task Replace_AdditionDeclaredBeforeTheMatchingDeclaration_StillGoesAfterTheReplacementAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);

        var result = await engine.ReplaceMemberWithAdditionsAsync(workspace.PathOf(FixtureRelativePath), "Alpha",
            "public int Gamma() => 3;\n\npublic int Alpha() => 10;");

        var text = Normalize(result.UpdatedText);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            Assert.That(IndexOfOrFail(text, "public int Alpha() => 10;"), Is.LessThan(IndexOfOrFail(text, "public int Gamma() => 3;")));
            Assert.That(IndexOfOrFail(text, "public int Gamma() => 3;"), Is.LessThan(IndexOfOrFail(text, "public int Beta() => 2;")));
            Assert.That(text, Does.Not.Contain("public int Alpha() => 1;"));
        });
    }

    [Test]
    public async Task Replace_ZeroDeclarationsMatchTheTarget_IsRejectedWithTheMultiDeclarationMessageAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var before = workspace.ReadText(FixtureRelativePath);

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public int Gamma() => 3;\n\npublic int Delta() => 4;"));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("2 member declarations"));
            Assert.That(error.Message, Does.Contain("Method 'Gamma'").And.Contain("Method 'Delta'"));
            Assert.That(error.Message, Does.Contain("replace takes exactly one member"));
            Assert.That(error.Message, Does.Contain("named 'Alpha'").And.Contain("has 0"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected replace must not write anything.");
    }

    [Test]
    public async Task Replace_OnlyAnotherKindHasTheTargetsName_IsRejectedAsZeroMatchesAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public int Alpha => 10;\n\npublic int Gamma() => 3;"));

        Assert.That(error!.Message, Does.Contain("replace takes exactly one member").And.Contain("has 0"));
    }

    [Test]
    public async Task Replace_TwoSameNameSameKindDeclarations_IsRejectedWithTheMultiDeclarationMessageAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var before = workspace.ReadText(FixtureRelativePath);

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public int Alpha() => 10;\n\npublic int Alpha(int x) => x;"));

        Assert.Multiple(() =>
        {
            Assert.That(error!.Message, Does.Contain("2 member declarations"));
            Assert.That(error.Message, Does.Contain("replace takes exactly one member"));
            Assert.That(error.Message, Does.Contain("named 'Alpha'").And.Contain("has 2"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected replace must not write anything.");
    }

    [Test]
    public async Task Replace_ContextSnippetStillSelectsTheOverloadBeingReplacedAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, OverloadFixtureSource));
        var engine = BuildEngine(workspace);

        var result = await engine.ReplaceMemberWithAdditionsAsync(workspace.PathOf(FixtureRelativePath), "Compute",
            "public int Compute(string s) => 42;\n\npublic int Helper() => 7;",
            contextSnippet: "public int Compute(string s) => s.Length;");

        var text = Normalize(result.UpdatedText);
        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified), result.Message);
            Assert.That(text, Does.Contain("public int Compute(int x) => x;"), "The other overload must be untouched.");
            Assert.That(text, Does.Not.Contain("s.Length"));
            Assert.That(IndexOfOrFail(text, "public int Compute(int x) => x;"), Is.LessThan(IndexOfOrFail(text, "public int Compute(string s) => 42;")));
            Assert.That(IndexOfOrFail(text, "public int Compute(string s) => 42;"), Is.LessThan(IndexOfOrFail(text, "public int Helper() => 7;")));
            Assert.That(IndexOfOrFail(text, "public int Helper() => 7;"), Is.LessThan(IndexOfOrFail(text, "public int Beta() => 2;")));
        });
    }

    [Test]
    public async Task Replace_CallerBeforeHelperInOneCall_PassesTheCompileGateWhereOneDeclarationPerCallFailsAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var path = workspace.PathOf(FixtureRelativePath);

        // One declaration per call: the caller lands first and calls a helper that does not exist yet (CS0103).
        var callerOnly = await engine.ReplaceMemberAsync(path, "Alpha", "public int Alpha() => Helper() + 1;");
        var callerOnlyErrors = await CompileErrorsAsync(workspace, FixtureRelativePath, callerOnly.UpdatedText);
        Assert.That(callerOnlyErrors.Select(d => d.Id), Does.Contain("CS0103"), "The one-per-call form is expected to fail the compile gate.");

        var together = await engine.ReplaceMemberWithAdditionsAsync(path, "Alpha", "public int Alpha() => Helper() + 1;\n\nprivate int Helper() => 5;");
        var togetherErrors = await CompileErrorsAsync(workspace, FixtureRelativePath, together.UpdatedText);

        Assert.Multiple(() =>
        {
            Assert.That(together.Outcome, Is.EqualTo(EditOutcome.Modified), together.Message);
            Assert.That(togetherErrors.Select(d => $"{d.Id}: {d.GetMessage()}"), Is.Empty);
        });
    }

    [TestCase("public int Alpha() => 10;\n\npublic int Gamma( => 3;", TestName = "Replace_ParseErrorInSecondDeclaration_WritesNothing_UnclosedParameterList")]
    [TestCase("public int Alpha() => 10;\n\npublic int Gamma() { return ; + }", TestName = "Replace_ParseErrorInSecondDeclaration_WritesNothing_BadBody")]
    [TestCase("public int Alpha() => 10;\n\npublic int Gamma() => 3;\n\nprivate int Delta() { return ; + }", TestName = "Replace_ParseErrorInThirdDeclaration_WritesNothing_BadBody")]
    public async Task Replace_ParseErrorInALaterDeclaration_IsRejectedAndWritesNothingAsync(string newSource)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var before = workspace.ReadText(FixtureRelativePath);

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", newSource));

        Assert.That(error!.Message, Is.Not.Empty);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected replace must not write anything.");
    }

    [Test]
    public async Task Replace_StrayClosingBrace_IsStillRejectedNamingTheBraceAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var before = workspace.ReadText(FixtureRelativePath);

        var single = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public string Alpha() { return \"}\"; }\n}"));
        var several = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public int Alpha() => 10;\n\npublic int Gamma() => 3;\n}"));

        Assert.Multiple(() =>
        {
            Assert.That(single!.Message, Does.Contain("newMemberSource has an extra or unbalanced '}'"));
            Assert.That(several!.Message, Does.Contain("newMemberSource has an extra or unbalanced '}'"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected replace must not write anything.");
    }

    [Test]
    public async Task Replace_BodyFragmentOrSingleMalformedDeclaration_IsStillRejectedAsNotAValidMemberAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var before = workspace.ReadText(FixtureRelativePath);

        var fragment = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "return 5;"));
        var malformed = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberWithAdditionsAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public int Alpha() { return ; + }"));

        Assert.Multiple(() =>
        {
            Assert.That(fragment!.Message, Does.Contain("not a valid member declaration"));
            Assert.That(malformed!.Message, Does.Contain("not a valid member declaration"));
        });
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(before), "A rejected replace must not write anything.");
    }

    [Test]
    public async Task Replace_SingleDeclaration_TakesTheReplaceMemberPathUnchangedAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);
        var path = workspace.PathOf(FixtureRelativePath);

        var viaAdditions = await engine.ReplaceMemberWithAdditionsAsync(path, "Alpha", "public int Alpha() => 10;");
        var viaReplace = await engine.ReplaceMemberAsync(path, "Alpha", "public int Alpha() => 10;");

        Assert.Multiple(() =>
        {
            Assert.That(viaAdditions.Outcome, Is.EqualTo(EditOutcome.Modified), viaAdditions.Message);
            Assert.That(viaAdditions.UpdatedText, Is.EqualTo(viaReplace.UpdatedText));
            Assert.That(viaAdditions.Message, Is.EqualTo(viaReplace.Message));
        });
    }

    [Test]
    public async Task ReplaceMemberAsync_StillRejectsMultiDeclarationSourceAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var engine = BuildEngine(workspace);

        var error = await Assert.ThrowsAsync<ToolInvalidArgumentException>(async () => await engine.ReplaceMemberAsync(
            workspace.PathOf(FixtureRelativePath), "Alpha", "public int Alpha() => 10;\n\npublic int Gamma() => 3;"));

        Assert.That(error!.Message, Does.Contain("replace takes exactly one member"));
    }

    [Test]
    public void SplitMemberDeclarations_ReturnsEveryDeclarationInSourceOrder()
    {
        var declarations = MemberRefactoringEngine.SplitMemberDeclarations("public int A() => 1;\n\nprivate string _b = \"b\";\n\npublic int C { get; set; }");

        Assert.That(declarations.Select(d => d.Kind()), Is.EqualTo(new[]
        {
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.MethodDeclaration,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.FieldDeclaration,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.PropertyDeclaration,
        }));
    }

    [Test]
    public void SplitMemberDeclarations_RejectsAMalformedDeclarationAndAStrayBrace()
    {
        Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.SplitMemberDeclarations("public int A() => 1;\n\npublic int B( => 2;"));
        Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.SplitMemberDeclarations("public int A() => 1;\n}"));
        Assert.Throws<ToolInvalidArgumentException>(() => MemberRefactoringEngine.SplitMemberDeclarations("return 5;"));
    }
}
