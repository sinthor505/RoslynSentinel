#pragma warning disable CS8618
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace RoslynSentinel.Tests.Advanced;

[TestFixture]
public class DeepFunctionalVerificationTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private SyntaxUpgradeEngine _syntaxUpgradeEngine;
    private CodeStyleEngine _codeStyleEngine;
    private CodeHealingEngine _codeHealingEngine;
    private ProjectStructureEngine _projectStructureEngine;
    private RefactoringEngine _refactoringEngine;
    private DependencyEngine _dependencyEngine;
    private ModernizationEngine _modernizationEngine;
    private StructuralRefinementEngine _structuralRefinementEngine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(new NullLogger<PersistentWorkspaceManager>());
        var config = new SentinelConfiguration();
        _syntaxUpgradeEngine = new SyntaxUpgradeEngine(_workspaceManager, config);
        _codeStyleEngine = new CodeStyleEngine(_workspaceManager, config);
        _codeHealingEngine = new CodeHealingEngine(_workspaceManager, config);
        _projectStructureEngine = new ProjectStructureEngine(_workspaceManager, config);
        _refactoringEngine = new RefactoringEngine(new NullLogger<RefactoringEngine>(), _workspaceManager, config);
        _dependencyEngine = new DependencyEngine(_workspaceManager);
        _modernizationEngine = new ModernizationEngine(_workspaceManager, config);
        _structuralRefinementEngine = new StructuralRefinementEngine(_workspaceManager, config);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs", string projectName = "TestProj")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject(projectName, new[] { (fileName, source) });
        _workspaceManager.SetTestSolution(solution);
    }

    [Test]
    public async Task ModernizeExceptions_ShouldReplaceAndGenerateClasses()
    {
        // Arrange
        SetSource(@"
namespace App;
public class Service {
    public void Run() {
        throw new Exception(""Database connection failed"");
    }
}", "Service.cs");

        var targets = new List<CodeHealingEngine.ExceptionTarget> {
            new CodeHealingEngine.ExceptionTarget("Service.cs", 5, "DatabaseException")
        };

        // Act
        var changes = await _codeHealingEngine.ModernizeExceptionsAsync(targets);

        // Assert
        Assert.That(changes.Count, Is.EqualTo(2), "Should produce 2 changes: the update and the new file.");
        Assert.That(changes["Service.cs"], Contains.Substring("throw new DatabaseException"));

        var newFile = changes.Keys.First(k => k.Contains("DatabaseException.cs"));
        Assert.That(changes[newFile], Contains.Substring("public class DatabaseException : Exception"));
        Assert.That(changes[newFile], Contains.Substring("namespace App;"));
    }

    [Test]
    public async Task SafeDelete_ShouldBlockOnReflectionRisk()
    {
        // Arrange
        SetSource("public class Target { public void DeadMethod() {} }", "Target.cs", "Proj");

        var projectId = _workspaceManager.CurrentSolution?.ProjectIds[0] ?? throw new InvalidOperationException("No project found.");
        var callerDocId = DocumentId.CreateNewId(projectId);
        var solution = _workspaceManager.CurrentSolution.AddDocument(callerDocId, "Caller.cs", "public class Caller { void M() { var name = \"DeadMethod\"; } }");
        _workspaceManager.SetTestSolution(solution);

        // Act & Assert
        var result = await _structuralRefinementEngine.SafeDeleteSymbolAsync("Target.cs", "DeadMethod", "public void DeadMethod()", null, null);

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(result.Message, Does.Contain("Potential reflection risk"));
        Assert.That(result.Message, Does.Contain("Caller.cs"));
    }

    [Test]
    public async Task RecordToClass_ShouldPreserveImmutability()
    {
        // Arrange
        SetSource("public record MyRecord(string Name, int Age);");

        // Act
        var result = await _modernizationEngine.RecordToClassAsync("Test.cs", "MyRecord");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public class MyRecord"));
        Assert.That(result.UpdatedText!, Contains.Substring("public string Name { get; init; }"));
        Assert.That(result.UpdatedText!, Contains.Substring("public int Age { get; init; }"));
    }

    [Test]
    public async Task UseNameofExpression_ShouldReplaceExactMatches()
    {
        // Arrange
        SetSource(@"
public class MyType {
    public void M() {
        var s = ""MyType"";
    }
}");

        // Act
        var result = await _syntaxUpgradeEngine.UseNameofExpressionAsync("Test.cs", "\"MyType\"");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("var s = nameof(MyType);"));
    }

    [Test]
    public async Task FindStructuralSmells_ShouldIdentifyThreadSafetyIssues()
    {
        // Arrange
        SetSource(@"
using System.Threading;
public class C {
    private SemaphoreSlim _s = new(1);
    public void M() {
        _s.Wait();
        // Missing finally release
    }
    public void L() {
        lock(this) { }
    }
}");

        // Act
        var smells = await _projectStructureEngine.FindStructuralSmellsAsync(ProjectStructureEngine.StructuralSmellType.ThreadSafety);

        // Assert
        Assert.That(smells.Any(s => s.Contains("SemaphoreSlim")), Is.True);
        Assert.That(smells.Any(s => s.Contains("lock object 'this'")), Is.True);
    }

    [Test]
    public async Task GetProjectDependencies_ShouldExtractFromCsproj()
    {
        // Arrange
        var csproj = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <ItemGroup>
    <PackageReference Include=""Newtonsoft.Json"" Version=""13.0.1"" />
  </ItemGroup>
</Project>";

        var tempCsproj = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MyProj.csproj");
        await File.WriteAllTextAsync(tempCsproj, csproj);

        var projectId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, "MyProj", "MyProj", LanguageNames.CSharp, filePath: tempCsproj));

        _workspaceManager.SetTestSolution(solution);

        // Act
        var report = await _dependencyEngine.GetProjectDependenciesAsync("MyProj", CancellationToken.None);

        // Assert
        Assert.That(report.PackageReferences, Contains.Item("Newtonsoft.Json"));

        // Cleanup
        File.Delete(tempCsproj);
    }

    [Test]
    public async Task ReplaceMember_ShouldReplaceMethodByName()
    {
        // Arrange
        SetSource("public class C { public void Old() { } }", "C.cs");
        var newSource = "public void New() { Console.WriteLine(\"Hello\"); }";

        // Act
        var result = await _refactoringEngine.ReplaceMemberAsync("C.cs", "Old", newSource);

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public void New()"));
        Assert.That(result.UpdatedText!, Does.Not.Contain("public void Old()"));
    }

    private const string ApplyDiscountLikeSource = @"
namespace ContosoOrders.Core;
public class Order
{
    public decimal ApplyDiscount(decimal percentage)
    {
        // NOTE: this method uses DiscountCalculator, but the using directive for
        // ContosoOrders.Core.Discounts is intentionally missing from this file (fully qualified below
        // as a workaround) to create a scenario for AddUsingDirective.
        return ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage);
    }
}";

    [Test]
    [Description("Regression (ContosoOrders live agent run, attempt 7): ApplyDiscount is a single, "
                 + "non-overloaded method — memberName alone already resolves it unambiguously. The "
                 + "agent nonetheless passed a defensive contextSnippet that didn't match the file "
                 + "(a formatting/indentation mismatch unrelated to which member was targeted), and "
                 + "the call failed twice with 'contextSnippet not found' even though there was "
                 + "nothing to disambiguate. A contextSnippet that doesn't match must not block "
                 + "resolution when the name alone is already unambiguous.")]
    public async Task ReplaceMember_SingleNonOverloadedMember_MismatchedContextSnippetIsIgnored()
    {
        SetSource(ApplyDiscountLikeSource, "Order.cs");
        var newSource = "public decimal ApplyDiscount(decimal percentage)\n{\n    return DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage);\n}";

        var result = await _refactoringEngine.ReplaceMemberAsync("Order.cs", "ApplyDiscount", newSource,
            contextSnippet: "this text does not appear anywhere in the file");

        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty, result.Message);
        Assert.That(result.UpdatedText, Does.Contain("DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage)"));
        Assert.That(result.UpdatedText, Does.Not.Contain("ContosoOrders.Core.Discounts.DiscountCalculator"));
    }

    [Test]
    public async Task ReplaceMember_OverloadedMembers_StillRequireContextSnippetToDisambiguate()
    {
        SetSource(@"
public class C
{
    public void Foo(int x) { }
    public void Foo(string x) { }
}", "C.cs");

        var noSnippetResult = await _refactoringEngine.ReplaceMemberAsync("C.cs", "Foo", "public void Foo(bool x) { }");
        Assert.That(noSnippetResult.UpdatedText, Is.Not.Null.And.Not.Empty,
            "With 2+ overloads and no contextSnippet, existing first-match behavior should still apply.");

        var mismatchedSnippetResult = await _refactoringEngine.ReplaceMemberAsync("C.cs", "Foo", "public void Foo(bool x) { }",
            contextSnippet: "this text does not appear anywhere in the file");
        Assert.That(mismatchedSnippetResult.UpdatedText, Is.Null.Or.Empty,
            "A genuinely ambiguous name (2+ overloads) with a non-matching contextSnippet must still fail — " +
            "the single-candidate bypass must not apply when there IS real ambiguity to resolve.");
        Assert.That(mismatchedSnippetResult.Outcome, Is.EqualTo(EditOutcome.CannotEdit));

        // Task I (docs/plan-tool-disambiguation-remediation-v1.md) picked NearMissList as the
        // winning hint strategy: it's the only one of the 3 evaluated that lists every real
        // candidate (up to 3) instead of just the nearest one, so assert its specific shape here
        // now that there's a real answer, per the plan's Risks-section instruction to tighten
        // loosely-asserting tests once a strategy is chosen.
        Assert.That(mismatchedSnippetResult.Message, Does.Contain("contextSnippet not found (2 candidates):"));
        Assert.That(mismatchedSnippetResult.Message, Does.Contain("line 4 `public void Foo(int x) { }`"));
        Assert.That(mismatchedSnippetResult.Message, Does.Contain("line 5 `public void Foo(string x) { }`"));
        Assert.That(mismatchedSnippetResult.Message, Does.Contain("Provide a more specific contextSnippet or use lineBefore/lineAfter."));
    }

    [Test]
    [Description("NearMissList must surface every real candidate a snippet actually matched, not "
                 + "just the first one — the losing NearestSnippet/CorrectedCoordinates strategies "
                 + "only ever showed candidate #1 here, which would mislead an agent into thinking "
                 + "there was one unrelated nearby match instead of 2 genuine ones to choose between.")]
    public async Task ReplaceMember_ThreeOverloads_AmbiguousSnippetListsUpToThreeCandidates()
    {
        SetSource(@"
public class C
{
    public void Foo(int x) { }
    public void Foo(string x) { }
    public void Foo(bool x) { }
}", "C.cs");

        var result = await _refactoringEngine.ReplaceMemberAsync("C.cs", "Foo", "public void Foo(double x) { }",
            contextSnippet: "this text does not appear anywhere in the file");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(result.Message, Does.Contain("contextSnippet not found (3 candidates):"));
        Assert.That(result.Message, Does.Contain("line 4 `public void Foo(int x) { }`"));
        Assert.That(result.Message, Does.Contain("line 5 `public void Foo(string x) { }`"));
        Assert.That(result.Message, Does.Contain("line 6 `public void Foo(bool x) { }`"));
    }

    [Test]
    [Description("Type-level ambiguity (ResolveTypeByNameOrSnippet) via ModifyBaseType's AddBaseType "
                 + "action: 2 same-named nested types in sibling containers (a genuinely compilable "
                 + "collision per the plan's Task D test guidance — plain top-level name collisions "
                 + "don't compile). Confirms the NearMissList hint also covers the type-level helper, "
                 + "not just the member-level one.")]
    public async Task AddBaseType_TwoNestedTypesSameName_AmbiguousSnippetListsBothCandidates()
    {
        SetSource(@"
public class Outer1
{
    public class Nested { public int A; }
}
public class Outer2
{
    public class Nested { public int B; }
}", "C.cs");

        var result = await _refactoringEngine.AddBaseTypeAsync("C.cs", "Nested", "IFoo",
            contextSnippet: "this text does not appear anywhere in the file");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(result.Message, Does.Contain("contextSnippet not found (2 candidates):"));
        Assert.That(result.Message, Does.Contain("line 4 `public class Nested { public int A; }`"));
        Assert.That(result.Message, Does.Contain("line 8 `public class Nested { public int B; }`"));
    }

    [Test]
    public async Task AddMember_ShouldAppendToClass()
    {
        // Arrange
        SetSource("public class C { }", "C.cs");
        var member = "public int NewField;";

        // Act
        var result = await _refactoringEngine.AddMemberAsync("C.cs", "C", member);

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public int NewField;"));
    }

    [Test]
    public async Task RemoveMember_ShouldDeleteByName()
    {
        // Arrange
        SetSource("public class C { public void Junk() {} public void Keep() {} }", "C.cs");

        // Act
        var result = await _refactoringEngine.RemoveMemberAsync("C.cs", "Junk");

        // Assert
        Assert.That(result.UpdatedText!, Does.Not.Contain("void Junk()"));
        Assert.That(result.UpdatedText!, Contains.Substring("void Keep()"));
    }

    [Test]
    public async Task FixDangerousLock_ShouldInjectLockObject()
    {
        // Arrange
        SetSource("public class C { public void M() { lock(this) { } } }", "C.cs");

        // Act
        var result = await _codeStyleEngine.FixDangerousLockAsync("C.cs");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("private readonly Lock _lockObj = new Lock()"));
        Assert.That(result.UpdatedText!, Contains.Substring("lock (_lockObj)"));
        Assert.That(result.UpdatedText!, Contains.Substring("using System.Threading;"));
    }

    [Test]
    public async Task ConvertPropertyToMethods_ShouldCreateGetSet()
    {
        // Arrange
        SetSource("public class C { public string Name { get; set; } }", "C.cs");

        // Act
        var result = await _codeStyleEngine.ConvertPropertyToMethodsAsync("C.cs", "Name");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public string GetName()"));
        Assert.That(result.UpdatedText!, Contains.Substring("public void SetName(string value)"));
        Assert.That(result.UpdatedText!, Contains.Substring("private string _name;"));
    }

    [Test]
    public async Task CleanupImplicitSpans_ShouldRemoveAsSpan()
    {
        // Arrange
        SetSource(@"
using System;
public class C {
    public void M(byte[] data) {
        ReadOnlySpan<byte> span = data.AsSpan();
    }
}", "C.cs");

        // Act
        var result = await _syntaxUpgradeEngine.CleanupImplicitSpansAsync("C.cs");

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("ReadOnlySpan<byte> span = data;"));
        Assert.That(result.UpdatedText!, Does.Not.Contain(".AsSpan()"));
    }
}
