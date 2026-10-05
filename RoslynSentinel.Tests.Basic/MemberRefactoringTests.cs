using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Tests for the new code-editing engine methods in BasicRefactoringEngine:
/// AddMemberAsync (record/struct support), AddUsingDirectiveAsync, ModifyEnumAsync,
/// InsertMemberAfterAsync, InsertMemberBeforeAsync, AddAttributeAsync, AddBaseTypeAsync.
/// </summary>
[TestFixture]
[Category("MemberRefactoringEngine")] // sentinel:auto-category
public class MemberRefactoringTests
{
    private IWorkspaceManager _workspaceManager;
    private MemberRefactoringEngine _memberRefactoringEngine;
    private BasicRefactoringEngine _basicRefactoringEngine;
    private SymbolNavigationEngine _symbolNavigationEngine;

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
        _symbolNavigationEngine = new SymbolNavigationEngine(_workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        _basicRefactoringEngine = new BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            new SentinelConfiguration());
        _memberRefactoringEngine = new MemberRefactoringEngine(
            _workspaceManager,
            _symbolNavigationEngine,
            new ValidationEngine(_workspaceManager));

        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Wrappers.cs", GenericSource)]);
        _workspaceManager.SetTestSolution(solution);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private void SetSource(string source, string fileName = "Test.cs")
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [(fileName, source)]);
        _workspaceManager.SetTestSolution(solution);
    }

    // ══════════════════════════════════════════════════════════════
    // AddMemberAsync -> record and struct support
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddMember_ToRecord_InsertsMethod()
    {
        SetSource(@"
public record Person(string Name, int Age);
", "Person.cs");

        var result = await _memberRefactoringEngine.AddMemberAsync("Person.cs", "Person", "public string Greet() => $\"Hello {Name}\";");

        Assert.That(result.UpdatedText, Does.Contain("Greet"), "Method should be added to record.");
        Assert.That(result.UpdatedText, Does.Contain("Person"), "Record declaration should still be present.");
    }

    [Test]
    public async Task AddMember_ToStruct_InsertsMethod()
    {
        SetSource(@"
public struct Point
{
    public int X;
    public int Y;
}
", "Point.cs");

        var result = await _memberRefactoringEngine.AddMemberAsync("Point.cs", "Point", "public double Length() => Math.Sqrt(X * X + Y * Y);");

        Assert.That(result.UpdatedText, Does.Contain("Length"), "Method should be added to struct.");
        Assert.That(result.UpdatedText, Does.Contain("Point"), "Struct declaration should still be present.");
    }

    [Test]
    public async Task AddMember_ToClass_StillWorks()
    {
        SetSource(@"
public class Animal
{
    public string Name { get; set; }
}
", "Animal.cs");

        var result = await _memberRefactoringEngine.AddMemberAsync("Animal.cs", "Animal", "public string Speak() => \"...\";");

        Assert.That(result.UpdatedText, Does.Contain("Speak"), "Method should be added to class.");
    }
    [Test]
    public async Task AddMember_AppendsWithBlankLineAndDoesNotReformatSiblings()
    {
        const string source = """

        public class Calc
        {
            public int First() => 1;

            public int Second() => 2;
        }

        """;
        SetSource(source, "Calc.cs");

        var result = await _memberRefactoringEngine.AddMemberAsync("Calc.cs", "Calc", "public int Third() => 3;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public int Third() => 3;"));

        Assert.That(result.UpdatedText, Does.Contain("public int First() => 1;\r\n\r\n    public int Second")
            .Or.Contain("public int First() => 1;\n\n    public int Second"),
            "Blank line between untouched siblings First and Second must survive unchanged - appending a " +
            "new member must not reformat the whole container.");

        var secondIdx = result.UpdatedText!.IndexOf("public int Second()", StringComparison.Ordinal);
        var thirdIdx = result.UpdatedText.IndexOf("public int Third()", StringComparison.Ordinal);
        Assert.That(thirdIdx, Is.GreaterThan(secondIdx));

        var betweenSecondAndThird = result.UpdatedText.Substring(secondIdx, thirdIdx - secondIdx);
        Assert.That(betweenSecondAndThird, Does.Contain("\n\n").Or.Contain("\r\n\r\n"),
            "A blank line must separate the newly appended member from the preceding sibling (Second) - " +
            "this is the exact defect from docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md.");
    }
    [Test]
    public async Task AddMember_ToEnum_RejectsInsteadOfSilentNoOp()
    {
        SetSource(@"
public enum ToolScope
{
    file, project, solution
}
", "ToolScope.cs");

        var result = await _memberRefactoringEngine.AddMemberAsync("ToolScope.cs", "ToolScope", "reproScratchMarker");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit),
            "Enum containers must be rejected loudly, not silently return the container unchanged " +
            "while reporting Modified (see docs/current/issue_member_add_silent_persistence.md).");
        Assert.That(result.UpdatedText, Is.Null.Or.Empty, "No text should be produced for a rejected edit.");
        Assert.That(result.Message, Does.Contain("ModifyEnumAsync"), "ErrorData should point at the enum-shaped tool.");
    }

    // ══════════════════════════════════════════════════════════════
    // Enum member support: AddEnumMemberAsync / RemoveEnumMemberAsync /
    // ReplaceEnumMemberAsync / IsEnumContainerAsync / TryGetEnumMemberContainerNameAsync /
    // GetContainerMembersAsync(enum) -> all delegate to the pre-existing ModifyEnumAsync.
    // ══════════════════════════════════════════════════════════════

    private const string ToolScopeEnumSource = @"
public enum ToolScope
{
    file, project, solution
}
";

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task IsEnumContainer_OnEnum_ReturnsTrue()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");
        Assert.That(await _symbolNavigationEngine.IsEnumContainerAsync("ToolScope.cs", "ToolScope"), Is.True);
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task IsEnumContainer_OnClass_ReturnsFalse()
    {
        SetSource("public class Widget { }", "Widget.cs");
        Assert.That(await _symbolNavigationEngine.IsEnumContainerAsync("Widget.cs", "Widget"), Is.False);
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task IsEnumContainer_NotFound_ReturnsFalse()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");
        Assert.That(await _symbolNavigationEngine.IsEnumContainerAsync("ToolScope.cs", "NoSuchType"), Is.False);
    }

    [Test]
    [Category("ContainerMemberInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetContainerMembers_OnEnum_ReturnsEnumMembers()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var (outcome, message, members) = await _symbolNavigationEngine.GetContainerMembersAsync("ToolScope.cs", "ToolScope");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(members.Select(m => m.Name), Is.EquivalentTo(new[] { "file", "project", "solution" }));
        Assert.That(members, Has.All.Matches<SymbolNavigationEngine.ContainerMemberInfo>(m => m?.Kind == "enumMember"));
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_PropertyWithDocCommentAttributeAndInitializer_ReturnsFullDeclaration()
    {
        var source = """
            public class Holder
            {
                /// <summary>d</summary>
                [Obsolete]
                public string Name { get; set; } = "x";
            }
            """;
        SetSource(source, "MemberSourceProp.cs");

        var (outcome, message, errorCode, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceProp.cs", "Name");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified), message);
        Assert.That(errorCode, Is.Null);
        Assert.That(info, Is.Not.Null);
        Assert.That(info!.Name, Is.EqualTo("Name"));
        Assert.That(info.Kind, Is.EqualTo("property"));
        Assert.That(info.Source, Does.StartWith("/// <summary>d</summary>"));
        Assert.That(info.Source, Does.Contain("[Obsolete]"));
        Assert.That(info.Source, Does.Contain("public string Name { get; set; } = \"x\";"));
        Assert.That(info.StartLine, Is.EqualTo(3));
        Assert.That(info.EndLine, Is.EqualTo(5));
        Assert.That(info.IsComplete, Is.True);
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_Method_ReturnsFullBody()
    {
        var source = """
            public class Holder
            {
                public int Add(int a, int b)
                {
                    var sum = a + b;
                    return sum;
                }

                public int Other;
            }
            """;
        SetSource(source, "MemberSourceMethod.cs");

        var (outcome, message, _, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceMethod.cs", "Add");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified), message);
        Assert.That(info!.Kind, Is.EqualTo("method"));
        Assert.That(info.Source, Does.Contain("var sum = a + b;"));
        Assert.That(info.Source, Does.Contain("return sum;"));
        Assert.That(info.Source.TrimEnd(), Does.EndWith("}"));
        Assert.That(info.Source, Does.Not.Contain("Other"));
        Assert.That(info.StartLine, Is.EqualTo(3));
        Assert.That(info.EndLine, Is.EqualTo(7));
        Assert.That(info.IsComplete, Is.True);
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_MissingMember_ReturnsTargetNotFoundWithNotFoundCode()
    {
        SetSource("public class Holder { public int A; }", "MemberSourceMissing.cs");

        var (outcome, message, errorCode, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceMissing.cs", "DoesNotExist");

        Assert.That(outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.That(errorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(message, Does.Contain("Member 'DoesNotExist' not found"));
        Assert.That(info, Is.Null);
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_OverloadsWithoutSnippet_ReturnsCannotEditAmbiguous()
    {
        var source = """
            public class Holder
            {
                public void Run(int x) { }
                public void Run(string s) { }
            }
            """;
        SetSource(source, "MemberSourceOverload.cs");

        var (outcome, message, errorCode, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceOverload.cs", "Run");

        Assert.That(outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(errorCode, Is.EqualTo(ToolErrorCode.Ambiguous));
        Assert.That(message, Does.Contain("2 candidates"));
        Assert.That(info, Is.Null);
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_OverloadsWithSnippet_ReturnsMatchingOverload()
    {
        var source = """
            public class Holder
            {
                public void Run(int x) { }
                public void Run(string s) { }
            }
            """;
        SetSource(source, "MemberSourceOverloadSnippet.cs");

        var (outcome, message, _, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceOverloadSnippet.cs", "Run", contextSnippet: "Run(string s)");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified), message);
        Assert.That(info!.Source, Does.Contain("string s"));
        Assert.That(info.Source, Does.Not.Contain("int x"));
        Assert.That(info.StartLine, Is.EqualTo(4));
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_TypeNameOnly_ReturnsTargetIneligible()
    {
        SetSource("public class Holder { public int A; }", "MemberSourceType.cs");

        var (outcome, message, errorCode, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceType.cs", "Holder");

        Assert.That(outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(errorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(message, Does.Contain("ReadFile or GetFileOutline"));
        Assert.That(info, Is.Null);
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_EnumMember_ReturnsMemberDeclaration()
    {
        var source = """
            public enum Color
            {
                Red = 1,
                Green = 2
            }
            """;
        SetSource(source, "MemberSourceEnum.cs");

        var (outcome, message, _, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceEnum.cs", "Green");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified), message);
        Assert.That(info!.Kind, Is.EqualTo("enumMember"));
        Assert.That(info.Source, Is.EqualTo("Green = 2"));
        Assert.That(info.StartLine, Is.EqualTo(4));
    }

    [Test]
    [Category("MemberSourceInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetMemberSource_MethodLongerThanCap_TruncatesToTwoHundredLines()
    {
        // 250-line method: signature, "{", 247 body lines, "}".
        var body = string.Join("\n", Enumerable.Repeat("        counter++;", 247));
        var source = "public class Holder\n{\n    private int counter;\n\n    public void Big()\n    {\n" + body + "\n    }\n}\n";
        SetSource(source, "MemberSourceLong.cs");

        var (outcome, message, _, info) = await _symbolNavigationEngine.GetMemberSourceAsync("MemberSourceLong.cs", "Big");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified), message);
        Assert.That(info!.IsComplete, Is.False);
        Assert.That(info.Source.Split('\n'), Has.Length.EqualTo(200));
        Assert.That(info.StartLine, Is.EqualTo(5));
        Assert.That(info.EndLine, Is.EqualTo(5 + 250 - 1));
    }

    [Test]
    [Category("ContainerMemberInfo")] // sentinel:auto-category
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task GetContainerMembers_AttributedMembers_SignatureExcludesAttributeLine()
    {
        var source = """
            public class Holder
            {
                [Obsolete]
                public string? Name { get; set; }

                [Obsolete]
                public void Run(int x) { }

                public int Plain;
            }
            """;
        SetSource(source, "Attributed.cs");

        var (outcome, message, members) = await _symbolNavigationEngine.GetContainerMembersAsync("Attributed.cs", "Holder");

        Assert.That(outcome, Is.EqualTo(EditOutcome.Modified));

        // Verify signatures exclude attribute text
        var nameProperty = members.FirstOrDefault(m => m.Name == "Name");
        Assert.That(nameProperty, Is.Not.Null);
        Assert.That(nameProperty!.Signature, Is.EqualTo("public string? Name"));
        Assert.That(nameProperty.Signature, Does.Not.StartWith("["));

        var runMethod = members.FirstOrDefault(m => m.Name == "Run");
        Assert.That(runMethod, Is.Not.Null);
        Assert.That(runMethod!.Signature, Is.EqualTo("public void Run(int x)"));
        Assert.That(runMethod.Signature, Does.Not.StartWith("["));

        var plainField = members.FirstOrDefault(m => m.Name == "Plain");
        Assert.That(plainField, Is.Not.Null);
        Assert.That(plainField!.Signature, Is.EqualTo("public int Plain"));
        Assert.That(plainField.Signature, Does.Not.StartWith("["));

        // Verify no member has attribute in signature
        Assert.That(members, Has.All.Matches<SymbolNavigationEngine.ContainerMemberInfo>(m => !m.Signature.StartsWith("[")));
    }

    [Test]
    public async Task AddEnumMember_Appends_WhenNoPositionGiven()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.AddEnumMemberAsync("ToolScope.cs", "ToolScope", "process");

        Assert.That(result.UpdatedText, Does.Contain("process"));
        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
    }

    [Test]
    public async Task AddEnumMember_InsertsAfterNamedMember()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.AddEnumMemberAsync("ToolScope.cs", "ToolScope", "process", afterMemberName: "project");

        Assert.That(result.UpdatedText, Does.Contain("process"));
        var projectIndex = result.UpdatedText!.IndexOf("project", StringComparison.Ordinal);
        var processIndex = result.UpdatedText.IndexOf("process", StringComparison.Ordinal);
        Assert.That(processIndex, Is.GreaterThan(projectIndex), "'process' should appear after 'project' in the member list.");
    }

    [Test]
    public async Task AddEnumMember_DuplicateName_ReturnsErrorWithoutText()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.AddEnumMemberAsync("ToolScope.cs", "ToolScope", "project");

        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
        Assert.That(result.Message, Does.Contain("project"));
    }

    [Test]
    public async Task RemoveEnumMember_RemovesNamedMember()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.RemoveEnumMemberAsync("ToolScope.cs", "ToolScope", "project");

        Assert.That(result.UpdatedText, Does.Not.Contain("project"));
        Assert.That(result.UpdatedText, Does.Contain("file"));
        Assert.That(result.UpdatedText, Does.Contain("solution"));
    }

    [Test]
    public async Task RemoveEnumMember_NotFound_ReturnsErrorWithoutText()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.RemoveEnumMemberAsync("ToolScope.cs", "ToolScope", "nonexistent");

        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
    }

    [Test]
    public async Task RemoveEnumMember_LastMember_RefusesWithCannotRemove()
    {
        SetSource(@"
public enum Singleton
{
    only
}
", "Singleton.cs");

        var result = await _memberRefactoringEngine.RemoveEnumMemberAsync("Singleton.cs", "Singleton", "only");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotRemove));
        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
    }

    [Test]
    public async Task ReplaceEnumMember_SubstitutesNamedMemberInPlace()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.ReplaceEnumMemberAsync("ToolScope.cs", "ToolScope", "project", "workspace=5");

        Assert.That(result.UpdatedText, Does.Contain("workspace"));
        Assert.That(result.UpdatedText, Does.Not.Contain("project"));
        Assert.That(result.UpdatedText, Does.Contain("file"));
        Assert.That(result.UpdatedText, Does.Contain("solution"));
    }

    [Test]
    public async Task ReplaceEnumMember_NotFound_ReturnsErrorWithoutText()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var result = await _memberRefactoringEngine.ReplaceEnumMemberAsync("ToolScope.cs", "ToolScope", "nonexistent", "workspace");

        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task TryGetEnumMemberContainerName_OnEnumMember_ReturnsEnumName()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var containerName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync("ToolScope.cs", "project");

        Assert.That(containerName, Is.EqualTo("ToolScope"));
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task TryGetEnumMemberContainerName_OnRegularMember_ReturnsNull()
    {
        SetSource(@"
public class Animal
{
    public string Name { get; set; }
}
", "Animal.cs");

        var containerName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync("Animal.cs", "Name");

        Assert.That(containerName, Is.Null);
    }

    [Test]
    [Category("SymbolNavigationEngine")] // sentinel:auto-category
    public async Task TryGetEnumMemberContainerName_NotFound_ReturnsNull()
    {
        SetSource(ToolScopeEnumSource, "ToolScope.cs");

        var containerName = await _symbolNavigationEngine.TryGetEnumMemberContainerNameAsync("ToolScope.cs", "nonexistent");

        Assert.That(containerName, Is.Null);
    }

    // ══════════════════════════════════════════════════════════════
    // AddTopLevelTypeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddTopLevelType_Enum_AddsToSingleNamespace()
    {
        SetSource(@"
namespace MyApp;

public class Widget { }
", "Widget.cs");

        var result = await _memberRefactoringEngine.AddTopLevelTypeAsync("Widget.cs", "public enum BuildOutcome { IsSuccess, Failure }");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public enum BuildOutcome"));
        Assert.That(result.UpdatedText, Does.Contain("public class Widget"), "Existing type should still be present.");
    }

    [Test]
    public async Task AddTopLevelType_Class_AddsToBlockScopedNamespace()
    {
        SetSource(@"
namespace MyApp
{
    public class Widget { }
}
", "Widget.cs");

        var result = await _memberRefactoringEngine.AddTopLevelTypeAsync("Widget.cs", "public class Gadget { }");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public class Gadget"));
        Assert.That(result.UpdatedText, Does.Contain("public class Widget"));
    }

    [Test]
    public async Task AddTopLevelType_NoNamespace_AddsToCompilationUnit()
    {
        SetSource(@"
public class Widget { }
", "Widget.cs");

        var result = await _memberRefactoringEngine.AddTopLevelTypeAsync("Widget.cs", "public class Gadget { }");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public class Gadget"));
        Assert.That(result.UpdatedText, Does.Contain("public class Widget"));
    }

    [Test]
    public async Task AddTopLevelType_MultipleNamespaces_RequiresNamespaceNameDisambiguation()
    {
        SetSource(@"
namespace MyApp.First
{
    public class Widget { }
}

namespace MyApp.Second
{
    public class Gizmo { }
}
", "Widget.cs");

        var result = await _memberRefactoringEngine.AddTopLevelTypeAsync("Widget.cs", "public class Gadget { }");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.That(result.Message, Does.Contain("MyApp.First"));
        Assert.That(result.Message, Does.Contain("MyApp.Second"));
    }

    [Test]
    public async Task AddTopLevelType_MultipleNamespaces_NamespaceNameDisambiguates()
    {
        SetSource(@"
namespace MyApp.First
{
    public class Widget { }
}

namespace MyApp.Second
{
    public class Gizmo { }
}
", "Widget.cs");

        var result = await _memberRefactoringEngine.AddTopLevelTypeAsync("Widget.cs", "public class Gadget { }", namespaceName: "MyApp.Second");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public class Gadget"));
    }

    [Test]
    public async Task AddTopLevelType_NotATypeDeclaration_ReturnsTargetNotFound()
    {
        SetSource(@"
namespace MyApp;

public class Widget { }
", "Widget.cs");

        var result = await _memberRefactoringEngine.AddTopLevelTypeAsync("Widget.cs", "public string Foo() => \"bar\";");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.That(result.Message, Does.Contain("did not parse as a type declaration"));
    }

    // ══════════════════════════════════════════════════════════════
    // ModifyEnumAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task ModifyEnum_AppendsValueToEnum()
    {
        SetSource(@"
public enum Color
{
    Red,
    Green
}
", "Color.cs");

        var result = await _memberRefactoringEngine.ModifyEnumAsync("Color.cs", "Color", "Red,Green,Blue");

        Assert.That(result.UpdatedText, Does.Contain("Blue"), "New enum value should be present.");
        Assert.That(result.UpdatedText, Does.Contain("Red"), "Existing values should remain.");
    }

    [Test]
    public async Task ModifyEnum_WithExplicitValue()
    {
        SetSource(@"
public enum Status
{
    Active,
    Inactive
}
", "Status.cs");

        var result = await _memberRefactoringEngine.ModifyEnumAsync("Status.cs", "Status", "Active,Inactive,Archived=99");

        Assert.That(result.UpdatedText, Does.Contain("Archived"), "New value should be present.");
        Assert.That(result.UpdatedText, Does.Contain("99"), "Explicit integer value should be present.");
    }

    [Test]
    public async Task ModifyEnum_RemovesAndReordersValues()
    {
        SetSource(@"
public enum Color
{
    Red,
    Green,
    Blue
}
", "Color.cs");

        var result = await _memberRefactoringEngine.ModifyEnumAsync("Color.cs", "Color", "Blue,Red");

        Assert.That(result.UpdatedText, Does.Contain("Blue"));
        Assert.That(result.UpdatedText, Does.Contain("Red"));
        Assert.That(result.UpdatedText, Does.Not.Contain("Green"), "Omitted value should be removed.");
        Assert.That(result.Message, Does.Contain("removed Green"));
        Assert.That(result.Message, Does.Contain("reordered"));
    }

    [Test]
    public async Task ModifyEnum_GracefulFallback_WhenEnumNotFound()
    {
        SetSource(@"
public class Foo { }
", "Foo.cs");

        var result = await _memberRefactoringEngine.ModifyEnumAsync("Foo.cs", "NonExistentEnum", "SomeValue");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.TargetNotFound));
        Assert.That(result.UpdatedText, Is.Null.Or.Empty, "No text should be produced when the target enum doesn't exist.");
    }

    // ══════════════════════════════════════════════════════════════
    // InsertMemberAfterAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task InsertMemberAfter_InsertsAfterNamedMember()
    {
        SetSource(@"
public class Service
{
    public void Start() { }
    public void Stop() { }
}
", "Service.cs");

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("Service.cs", "Service", "Start",
            "public void Pause() { }");

        // Pause should appear between Start and Stop
        var startIdx = result.UpdatedText!.IndexOf("Start");
        var pauseIdx = result.UpdatedText!.IndexOf("Pause");
        var stopIdx = result.UpdatedText!.IndexOf("Stop");
        Assert.That(pauseIdx, Is.GreaterThan(startIdx), "Pause should come after Start.");
        Assert.That(pauseIdx, Is.LessThan(stopIdx), "Pause should come before Stop.");
    }

    [Test]
    public async Task InsertMemberAfter_AppendsWhenAfterMemberNotFound()
    {
        SetSource(@"
public class Repo
{
    public void Save() { }
}
", "Repo.cs");

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("Repo.cs", "Repo", "NonExistent",
            "public void Delete() { }");

        Assert.That(result.UpdatedText, Does.Contain("Delete"), "Member should be appended when anchor not found.");
        Assert.That(result.UpdatedText, Does.Contain("Save"), "Existing member should remain.");
    }

    [Test]
    public async Task InsertMemberAfter_WorksOnLastMember()
    {
        SetSource(@"
public class Widget
{
    public void Draw() { }
}
", "Widget.cs");

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("Widget.cs", "Widget", "Draw",
            "public void Resize() { }");

        var drawIdx = result.UpdatedText!.IndexOf("Draw");
        var resizeIdx = result.UpdatedText!.IndexOf("Resize");
        Assert.That(resizeIdx, Is.GreaterThan(drawIdx), "Resize should be after Draw.");
    }

    [Test]
    public async Task InsertMemberAfter_OnEnum_RejectsInsteadOfSilentNoOp()
    {
        SetSource(@"
public enum ToolScope
{
    file, project, solution
}
", "ToolScope.cs");

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("ToolScope.cs", "ToolScope", "file", "reproScratchMarker");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit),
            "Enum containers aren't TypeDeclarationSyntax, so this falls back to AddMemberAsync, which " +
            "must reject rather than silently no-op (see docs/current/issue_member_add_silent_persistence.md).");
        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
    }
    [Test]
    public async Task InsertMemberAfter_PreservesBlankLineAndSiblingMembers()
    {
        const string source = """

        public class Calc
        {
            public int First() => 1;

            public int Second() => 2;

            public int Third() => 3;
        }

        """;
        SetSource(source, "Calc.cs");

        var result = await _memberRefactoringEngine.InsertMemberAfterAsync("Calc.cs", "Calc", "First", "public int OneAndAHalf() => 1;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public int OneAndAHalf() => 1;"), "New member should be inserted.");

        var firstIdx = result.UpdatedText!.IndexOf("public int First()", StringComparison.Ordinal);
        var newIdx = result.UpdatedText.IndexOf("public int OneAndAHalf()", StringComparison.Ordinal);
        var secondIdx = result.UpdatedText.IndexOf("public int Second()", StringComparison.Ordinal);
        var thirdIdx = result.UpdatedText.IndexOf("public int Third()", StringComparison.Ordinal);

        Assert.That(newIdx, Is.GreaterThan(firstIdx));
        Assert.That(secondIdx, Is.GreaterThan(newIdx));
        Assert.That(thirdIdx, Is.GreaterThan(secondIdx), "Third should remain after Second.");

        var betweenFirstAndNew = result.UpdatedText.Substring(firstIdx, newIdx - firstIdx);
        Assert.That(betweenFirstAndNew, Does.Contain("\n\n").Or.Contain("\r\n\r\n"),
            "A blank line must separate the newly inserted member from the preceding sibling (First).");

        var betweenNewAndSecond = result.UpdatedText.Substring(newIdx, secondIdx - newIdx);
        Assert.That(betweenNewAndSecond, Does.Contain("\n\n").Or.Contain("\r\n\r\n"),
            "A blank line must separate the newly inserted member from the following sibling (Second) - " +
            "this is the exact defect from docs/current/blockers/blocking_error_member_replace_strips_blank_line_between_adjacent_members.md.");

        Assert.That(result.UpdatedText, Does.Contain("public int Second() => 2;\r\n\r\n    public int Third")
            .Or.Contain("public int Second() => 2;\n\n    public int Third"),
            "Blank line between untouched siblings Second and Third must survive unchanged - a whole-container " +
            "reformat would collapse or alter it, matching the 2026-09-07 unrelated-method-respacing symptom.");
    }
    // ══════════════════════════════════════════════════════════════
    // InsertMemberBeforeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task InsertMemberBefore_InsertsBeforeNamedMember()
    {
        SetSource(@"
public class Controller
{
    public void Get() { }
    public void Post() { }
}
", "Controller.cs");

        var result = await _memberRefactoringEngine.InsertMemberBeforeAsync("Controller.cs", "Controller", "Post",
            "public void Put() { }");

        var getIdx = result.UpdatedText!.IndexOf("Get()");
        var putIdx = result.UpdatedText!.IndexOf("Put()");
        var postIdx = result.UpdatedText!.IndexOf("Post()");
        Assert.That(putIdx, Is.GreaterThan(getIdx), "Put should come after Get.");
        Assert.That(putIdx, Is.LessThan(postIdx), "Put should come before Post.");
    }

    [Test]
    public async Task InsertMemberBefore_AppendsWhenBeforeMemberNotFound()
    {
        SetSource(@"
public class Cache
{
    public void Set() { }
}
", "Cache.cs");

        var result = await _memberRefactoringEngine.InsertMemberBeforeAsync("Cache.cs", "Cache", "NonExistent",
            "public void Evict() { }");

        Assert.That(result.UpdatedText, Does.Contain("Evict"), "Member should be appended when anchor not found.");
    }

    [Test]
    public async Task InsertMemberBefore_WorksOnFirstMember()
    {
        SetSource(@"
public class Logger
{
    public void Log() { }
    public void Flush() { }
}
", "Logger.cs");

        var result = await _memberRefactoringEngine.InsertMemberBeforeAsync("Logger.cs", "Logger", "Log",
            "public void Init() { }");

        var initIdx = result.UpdatedText!.IndexOf("Init");
        var logIdx = result.UpdatedText!.IndexOf("Log()");
        Assert.That(initIdx, Is.LessThan(logIdx), "Init should appear before Log.");
    }

    [Test]
    public async Task InsertMemberBefore_OnEnum_RejectsInsteadOfSilentNoOp()
    {
        SetSource(@"
public enum ToolScope
{
    file, project, solution
}
", "ToolScope.cs");

        var result = await _memberRefactoringEngine.InsertMemberBeforeAsync("ToolScope.cs", "ToolScope", "solution", "reproScratchMarker");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit),
            "Enum containers aren't TypeDeclarationSyntax, so this falls back to AddMemberAsync, which " +
            "must reject rather than silently no-op (see docs/current/issue_member_add_silent_persistence.md).");
        Assert.That(result.UpdatedText, Is.Null.Or.Empty);
    }

    // ══════════════════════════════════════════════════════════════
    // AddAttributeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddAttribute_ToClass_WithBrackets()
    {
        SetSource(@"
public class MyController
{
    public void Index() { }
}
", "MyController.cs");

        var result = await _memberRefactoringEngine.AddAttributeAsync("MyController.cs", "MyController", "[Serializable]");

        Assert.That(result.UpdatedText, Does.Contain("Serializable"), "Attribute should be added to class.");
        Assert.That(result.UpdatedText, Does.Contain("MyController"), "Class should still be present.");
    }

    [Test]
    public async Task AddAttribute_ToMethod_WithoutBrackets()
    {
        SetSource(@"
public class Api
{
    public void GetItems() { }
}
", "Api.cs");

        var result = await _memberRefactoringEngine.AddAttributeAsync("Api.cs", "GetItems", "Obsolete");

        Assert.That(result.UpdatedText, Does.Contain("Obsolete"), "Attribute should be added to method.");
    }

    [Test]
    public async Task AddAttribute_ToClass_WithBrackets_StringArg()
    {
        SetSource(@"
public class Handler
{
    public void Handle() { }
}
", "Handler.cs");

        var result = await _memberRefactoringEngine.AddAttributeAsync("Handler.cs", "Handler", "[Description(\"My handler\")]");

        Assert.That(result.UpdatedText, Does.Contain("Description"), "Attribute with argument should be added.");
    }

    // ══════════════════════════════════════════════════════════════
    // AddBaseTypeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddBaseType_AddsFirstInterface()
    {
        SetSource(@"
public class Repository
{
    public void Save() { }
}
", "Repository.cs");

        var result = await _memberRefactoringEngine.AddBaseTypeAsync("Repository.cs", "Repository", "IRepository");

        Assert.That(result.UpdatedText, Does.Contain("IRepository"), "Interface should be added to base list.");
    }

    [Test]
    public async Task AddBaseType_AddsSecondInterface()
    {
        SetSource(@"
public class Service : IService
{
    public void Run() { }
}
", "Service.cs");

        var result = await _memberRefactoringEngine.AddBaseTypeAsync("Service.cs", "Service", "IDisposable");

        Assert.That(result.UpdatedText, Does.Contain("IService"), "First interface should still be present.");
        Assert.That(result.UpdatedText, Does.Contain("IDisposable"), "Second interface should be added.");
    }

    [Test]
    public async Task AddBaseType_NoDuplicate_WhenAlreadyPresent()
    {
        SetSource(@"
public class Worker : IWorker
{
    public void Work() { }
}
", "Worker.cs");

        var result = await _memberRefactoringEngine.AddBaseTypeAsync("Worker.cs", "Worker", "IWorker");

        // Only one occurrence in the base list
        var count = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, "IWorker").Count;
        Assert.That(count, Is.EqualTo(1), "IWorker should not be duplicated.");
    }

    // ══════════════════════════════════════════════════════════════
    // RemoveAttributeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task RemoveAttribute_RemovesExistingAttribute()
    {
        SetSource(@"
[Obsolete]
public class Foo
{
    [Obsolete(""use Bar"")]
    public void DoIt() { }
}
", "Foo.cs");

        var result = await _memberRefactoringEngine.RemoveAttributeAsync("Foo.cs", "DoIt", "Obsolete");

        Assert.That(result.UpdatedText, Does.Not.Contain("[Obsolete("), "Attribute should be removed from method.");
        Assert.That(result.UpdatedText, Does.Contain("[Obsolete]"), "Class attribute should remain.");
    }

    [Test]
    public async Task RemoveAttribute_NoOpWhenAbsent()
    {
        SetSource(@"
public class Bar
{
    public void Run() { }
}
", "Bar.cs");

        var result = await _memberRefactoringEngine.RemoveAttributeAsync("Bar.cs", "Run", "Obsolete");

        Assert.That(result.UpdatedText, Does.Contain("public void Run()"));
        Assert.That(result.UpdatedText, Does.Not.Contain("[Obsolete]"));
    }

    // Observed flaky 2026-08-25: failed under a full-suite/parallel run, passed in isolation and
    // on suite rerun. Not a regression -> see feedback_comment_suspected_flaky_tests memory.
    [Test]
    public async Task RemoveAttribute_MatchesSuffixVariant()
    {
        // RemoveAttributeAsync only targets members (methods/properties/fields), not whole
        // type declarations, so the suffix-matching behavior ("Obsolete" matching
        // "[ObsoleteAttribute]") must be exercised via a member, not the class itself.
        SetSource(@"
public class Baz
{
    [ObsoleteAttribute]
    public void Run() { }
}
", "Baz.cs");

        var result = await _memberRefactoringEngine.RemoveAttributeAsync("Baz.cs", "Run", "Obsolete");

        Assert.That(result.UpdatedText, Does.Not.Contain("[ObsoleteAttribute]"));
        Assert.That(result.UpdatedText, Does.Contain("public void Run()"));
    }

    [Test]
    [Description("Type-level ambiguity (ResolveTypeByNameOrSnippet) via ModifyBaseType's AddBaseType "
                 + "action: 2 same-named nested types in sibling containers (a genuinely compilable "
                 + "collision per the plan's Task D test guidance - plain top-level name collisions "
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

        var result = await _memberRefactoringEngine.AddBaseTypeAsync("C.cs", "Nested", "IFoo",
            contextSnippet: "this text does not appear anywhere in the file");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(result.Message, Does.Contain("contextSnippet not found (2 candidates):"));
        Assert.That(result.Message, Does.Contain("line 4 `public class Nested { public int A; }`"));
        Assert.That(result.Message, Does.Contain("line 8 `public class Nested { public int B; }`"));
    }

    // ══════════════════════════════════════════════════════════════
    // RemoveBaseTypeAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task RemoveBaseType_RemovesOneInterface()
    {
        SetSource(@"
public class Service : IService, IDisposable
{
    public void Run() { }
    public void Dispose() { }
}
", "Service.cs");

        var result = await _memberRefactoringEngine.RemoveBaseTypeAsync("Service.cs", "Service", "IDisposable");

        Assert.That(result.UpdatedText, Does.Contain("IService"), "IService should remain.");
        Assert.That(result.UpdatedText, Does.Not.Contain("IDisposable"), "IDisposable should be removed.");
    }

    [Test]
    public async Task RemoveBaseType_RemovesOnlyBaseType_LeavesNoBaseList()
    {
        SetSource(@"
public class Child : Parent
{
    public void Act() { }
}
", "Child.cs");

        var result = await _memberRefactoringEngine.RemoveBaseTypeAsync("Child.cs", "Child", "Parent");

        Assert.That(result.UpdatedText, Does.Not.Contain(": Parent"), "Base list should be gone.");
        Assert.That(result.UpdatedText, Does.Contain("public class Child"), "Class declaration should remain.");
    }

    [Test]
    public async Task RemoveBaseType_NoOpWhenNotPresent()
    {
        SetSource(@"
public class Worker : IWorker { }
", "Worker.cs");

        var result = await _memberRefactoringEngine.RemoveBaseTypeAsync("Worker.cs", "Worker", "IDisposable");

        Assert.That(result.UpdatedText, Does.Contain(": IWorker"), "Base list should be unchanged.");
    }

    // ══════════════════════════════════════════════════════════════
    // ChangeAccessibilityAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task ChangeAccessibility_PublicToPrivate()
    {
        SetSource(@"
public class Calc
{
    public int Add(int a, int b) => a + b;
}
", "Calc.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Calc.cs", "Add", AccessibilityLevel.@private);

        Assert.That(result.UpdatedText, Does.Contain("private int Add"), "Method should now be private.");
        Assert.That(result.UpdatedText, Does.Not.Contain("public int Add"));
    }

    [Test]
    public async Task ChangeAccessibility_PrivateToPublic()
    {
        SetSource(@"
public class Calc
{
    private int _value;
}
", "Calc.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Calc.cs", "_value", AccessibilityLevel.@public);

        Assert.That(result.UpdatedText, Does.Contain("public int _value"));
    }

    [Test]
    public async Task ChangeAccessibility_ProtectedInternalToInternal()
    {
        SetSource(@"
public class Base
{
    protected internal void Hook() { }
}
", "Base.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Base.cs", "Hook", AccessibilityLevel.@internal);

        Assert.That(result.UpdatedText, Does.Contain("internal void Hook"), "Should be internal.");
        Assert.That(result.UpdatedText, Does.Not.Contain("protected internal void Hook"));
    }

    [Test]
    public async Task ChangeAccessibility_DoesNotReformatUnrelatedMembers()
    {
        const string source = """

        public class Calc
        {
            public int Add(int a, int b) => a + b;

            public int Subtract(int a, int b) => a - b;


            public int Multiply(int a, int b) => a * b;
        }

        """;
        SetSource(source, "Calc.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Calc.cs", "Add", AccessibilityLevel.@private);

        Assert.That(result.UpdatedText, Does.Contain("private int Add"), "Method should now be private.");
        Assert.That(result.UpdatedText, Does.Contain("public int Subtract(int a, int b) => a - b;\r\n\r\n\r\n    public int Multiply")
            .Or.Contain("public int Subtract(int a, int b) => a - b;\n\n\n    public int Multiply"),
            "Blank lines between untouched members below the edit must survive unchanged - a whole-file reformat would collapse them.");
    }
    [Test]
    public async Task ChangeAccessibility_PreservesLeadingDocComment()
    {
        SetSource(@"
public class Calc
{
    /// <summary>
    /// Does a thing.
    /// </summary>
    private void DoThing() { }
}
", "Calc.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Calc.cs", "DoThing", AccessibilityLevel.@internal);

        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"), "Doc comment must survive an accessibility change.");
        Assert.That(result.UpdatedText, Does.Contain("/// Does a thing."));
        Assert.That(result.UpdatedText, Does.Contain("internal void DoThing"));
    }
    [Test]
    public async Task RemoveConstructorParameter_UnusedParam_RemovesParamAndField_PreservesOtherMembers()
    {
        SetSource(@"
public class Widget
{
    private readonly string _name;
    private readonly int _size;

    public Widget(string name, int size)
    {
        _name = name;
        _size = size;
    }

    public string Describe() => $""{_name}"";
}
", "Widget.cs");

        var result = await _memberRefactoringEngine.RemoveConstructorParameterAsync("Widget.cs", "Widget", "size");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Not.Contain("int size"));
        Assert.That(result.UpdatedText, Does.Not.Contain("_size = size"));
        Assert.That(result.UpdatedText, Does.Not.Contain("_size;"));
        Assert.That(result.UpdatedText, Does.Contain("_name = name"));
        Assert.That(result.UpdatedText, Does.Contain("public string Describe()"));
    }

    [Test]
    public async Task ReplaceMember_PreservesBlankLinesAndSiblingMembers()
    {
        SetSource(@"
public class Calc
{
    public int First() => 1;

    public int Second() => 2;

    public int Third() => 3;
}
", "Calc.cs");

        var result = await _memberRefactoringEngine.ReplaceMemberAsync("Calc.cs", "Second", "public int Second() => 22;");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("public int First() => 1;"));
        Assert.That(result.UpdatedText, Does.Contain("public int Second() => 22;"));
        Assert.That(result.UpdatedText, Does.Contain("public int Third() => 3;"));
        Assert.That(result.UpdatedText, Does.Not.Contain("public int Second() => 2;"));

        var firstIndex = result.UpdatedText!.IndexOf("public int First()", StringComparison.Ordinal);
        var secondIndex = result.UpdatedText.IndexOf("public int Second()", StringComparison.Ordinal);
        var thirdIndex = result.UpdatedText.IndexOf("public int Third()", StringComparison.Ordinal);
        var between1and2 = result.UpdatedText.Substring(firstIndex, secondIndex - firstIndex);
        var between2and3 = result.UpdatedText.Substring(secondIndex, thirdIndex - secondIndex);

        var blankLinesBetween1and2 = 0;
        foreach (var line in between1and2.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                blankLinesBetween1and2++;
            }
        }

        var blankLinesBetween2and3 = 0;
        foreach (var line in between2and3.Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                blankLinesBetween2and3++;
            }
        }

        Assert.That(blankLinesBetween1and2, Is.GreaterThanOrEqualTo(1), "Blank line between First and Second must survive.");
        Assert.That(blankLinesBetween2and3, Is.GreaterThanOrEqualTo(1), "Blank line between Second and Third must survive.");
    }
    [Test]
    public async Task RemoveMember_DoesNotReformatUnrelatedSiblingSpacingOrBlankLines()
    {
        SetSource(@"
public class Calc
{
    public string UnrelatedMethodBefore(int    x , int y)
    {
        return (x + y).ToString();
    }

    public int ToBeRemoved() => 0;

    public int UnrelatedMethodAfter() => 1;
}
", "Calc.cs");

        var result = await _memberRefactoringEngine.RemoveMemberAsync("Calc.cs", "ToBeRemoved");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Not.Contain("ToBeRemoved"));

        Assert.That(result.UpdatedText, Does.Contain("public string UnrelatedMethodBefore(int    x , int y)"),
            "RemoveMember must not reformat an unrelated sibling's interior spacing - this is the exact " +
            "'(int    x , int y)' -> '(int x, int y)' respacing symptom from the 2026-09-07 memory " +
            "(project_member_replace_drops_leading_blank_line_and_verify_gap.md, run 5).");

        var beforeIdx = result.UpdatedText!.IndexOf("UnrelatedMethodBefore", StringComparison.Ordinal);
        var afterIdx = result.UpdatedText.IndexOf("UnrelatedMethodAfter", StringComparison.Ordinal);
        Assert.That(afterIdx, Is.GreaterThan(beforeIdx));

        var between = result.UpdatedText.Substring(beforeIdx, afterIdx - beforeIdx);
        Assert.That(between, Does.Contain("\n\n").Or.Contain("\r\n\r\n"),
            "A blank line must still separate UnrelatedMethodBefore from UnrelatedMethodAfter after the " +
            "removal - the untouched sibling on the far side of the removed member must not be respaced.");
    }

    [Test]
    public async Task ReplaceMember_WithContainerName_ScopesToRequestedContainerOnly()
    {
        // Regression test for docs/current/blockers/resolved/blocking_error_member_replace_ignores_containername_scoping.md:
        // two classes in one file each declare their own same-named Setup() method. Without
        // containerName-based scoping, ReplaceMemberAsync's member lookup fell back to a bare
        // name match and always resolved to the FIRST same-named method in the file, regardless
        // of which containerName was actually requested.
        SetSource(@"
public class FirstAccuracyTests
{
    private int _memberRefactoringEngine;

    public void Setup()
    {
        _memberRefactoringEngine = 1;
    }
}

public class SecondAccuracyTests
{
    private string _memberRefactoringEngine;

    public void Setup()
    {
        _memberRefactoringEngine = ""two"";
    }
}
", "Tests.cs");

        var result = await _memberRefactoringEngine.ReplaceMemberAsync("Tests.cs", "Setup", "public void Setup() { _memberRefactoringEngine = 2; }", containerName: "SecondAccuracyTests");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        Assert.That(result.UpdatedText, Does.Contain("_memberRefactoringEngine = 1;"),
            "FirstAccuracyTests.Setup() must be left untouched - containerName scoped this replace to SecondAccuracyTests.");
        Assert.That(result.UpdatedText, Does.Contain("_memberRefactoringEngine = 2;"),
            "SecondAccuracyTests.Setup() should have been replaced with the new body.");
        Assert.That(result.UpdatedText, Does.Not.Contain("_memberRefactoringEngine = \"two\";"),
            "The old SecondAccuracyTests.Setup() body must be gone after the replace.");

        var firstIndex = result.UpdatedText!.IndexOf("class FirstAccuracyTests", StringComparison.Ordinal);
        var secondIndex = result.UpdatedText.IndexOf("class SecondAccuracyTests", StringComparison.Ordinal);
        var replacedIndex = result.UpdatedText.IndexOf("_memberRefactoringEngine = 2;", StringComparison.Ordinal);
        Assert.That(replacedIndex, Is.GreaterThan(secondIndex),
            "The replaced Setup() body must land inside SecondAccuracyTests, not FirstAccuracyTests.");
        Assert.That(secondIndex, Is.GreaterThan(firstIndex));
    }

    [Test]
    public async Task RemoveMember_WithContainerName_ScopesToRequestedContainerOnly()
    {
        // Sibling regression test: same two-classes-same-method-name scenario, but for
        // RemoveMemberAsync, which shares ReplaceMemberAsync's member-resolution code path
        // and had the identical containerName-ignored bug.
        SetSource(@"
public class FirstAccuracyTests
{
    public void Setup()
    {
    }
}

public class SecondAccuracyTests
{
    public void Setup()
    {
    }
}
", "Tests.cs");

        var result = await _memberRefactoringEngine.RemoveMemberAsync("Tests.cs", "Setup", containerName: "SecondAccuracyTests");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.Modified));
        var firstIndex = result.UpdatedText!.IndexOf("class FirstAccuracyTests", StringComparison.Ordinal);
        var secondIndex = result.UpdatedText.IndexOf("class SecondAccuracyTests", StringComparison.Ordinal);
        Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(secondIndex, Is.GreaterThanOrEqualTo(0));

        var firstClassBody = result.UpdatedText.Substring(firstIndex, secondIndex - firstIndex);
        Assert.That(firstClassBody, Does.Contain("public void Setup()"),
            "FirstAccuracyTests.Setup() must be left untouched - containerName scoped this remove to SecondAccuracyTests.");

        var secondClassBody = result.UpdatedText.Substring(secondIndex);
        Assert.That(secondClassBody, Does.Not.Contain("public void Setup()"),
            "SecondAccuracyTests.Setup() should have been removed.");
    }

    [Test]
    public async Task AddModifier_PreservesLeadingDocComment()
    {
        SetSource(@"
public class Calc
{
    /// <summary>
    /// Does a thing.
    /// </summary>
    public void DoThing() { }
}
", "Calc.cs");

        var result = await _memberRefactoringEngine.AddModifierAsync("Calc.cs", "DoThing", "virtual");

        Assert.That(result.UpdatedText, Does.Contain("/// <summary>"), "Doc comment must survive adding a modifier.");
        Assert.That(result.UpdatedText, Does.Contain("/// Does a thing."));
        Assert.That(result.UpdatedText, Does.Contain("virtual void DoThing"));
    }
    [Test]
    public async Task ChangeAccessibility_CrlfDominantFile_ProducesNoStrayLf()
    {
        SetSource("public class Calc\r\n{\r\n    private void DoThing() { }\r\n}\r\n", "Calc.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Calc.cs", "DoThing", AccessibilityLevel.@internal);

        Assert.That(result.UpdatedText, Does.Contain("internal void DoThing"));

        var text = result.UpdatedText!;
        var bareLfCount = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && (i == 0 || text[i - 1] != '\r'))
            {
                bareLfCount++;
            }
        }

        Assert.That(bareLfCount, Is.Zero, "A CRLF-dominant file must not gain any stray bare LF line endings.");
    }

    [Test]
    public async Task ChangeAccessibility_LfDominantFile_ProducesNoStrayCrlf()
    {
        SetSource("public class Calc\n{\n    private void DoThing() { }\n}\n", "Calc.cs");

        var result = await _memberRefactoringEngine.ChangeAccessibilityAsync("Calc.cs", "DoThing", AccessibilityLevel.@internal);

        Assert.That(result.UpdatedText, Does.Contain("internal void DoThing"));

        var text = result.UpdatedText!;
        Assert.That(text, Does.Not.Contain("\r\n"), "An LF-dominant file must not gain any stray CRLF line endings.");
    }

    // AddModifierAsync / RemoveModifierAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddModifier_AddsVirtualToMethod()
    {
        SetSource(@"
public class Base
{
    public void Execute() { }
}
", "Base.cs");

        var result = await _memberRefactoringEngine.AddModifierAsync("Base.cs", "Execute", "virtual");

        Assert.That(result.UpdatedText, Does.Contain("virtual"), "Method should now be virtual.");
    }

    [Test]
    public async Task AddModifier_IsIdempotent()
    {
        SetSource(@"
public class Base
{
    public virtual void Execute() { }
}
", "Base.cs");

        var result = await _memberRefactoringEngine.AddModifierAsync("Base.cs", "Execute", "virtual");
        var count = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"\bvirtual\b").Count;

        Assert.That(count, Is.EqualTo(1), "virtual should appear only once.");
    }

    [Test]
    public async Task RemoveModifier_RemovesStatic()
    {
        SetSource(@"
public class Helper
{
    public static void Go() { }
}
", "Helper.cs");

        var result = await _memberRefactoringEngine.RemoveModifierAsync("Helper.cs", "Go", "static");

        Assert.That(result.UpdatedText, Does.Not.Contain("static void Go"));
        Assert.That(result.UpdatedText, Does.Contain("public void Go"));
    }

    [Test]
    public async Task RemoveModifier_NoOpWhenAbsent()
    {
        SetSource(@"
public class Helper
{
    public void Go() { }
}
", "Helper.cs");

        var result = await _memberRefactoringEngine.RemoveModifierAsync("Helper.cs", "Go", "static");

        Assert.That(result.UpdatedText, Does.Contain("public void Go"));
        Assert.That(result.UpdatedText, Does.Not.Contain("static"));
    }

    // ══════════════════════════════════════════════════════════════
    // AddPropertyAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddProperty_ReadonlyProperty()
    {
        SetSource(@"
public class Person { }
", "Person.cs");

        var result = await _memberRefactoringEngine.AddPropertyAsync("Person.cs", "Person", "Name", "string", hasSetter: false);

        Assert.That(result.UpdatedText, Does.Contain("string Name"));
        Assert.That(result.UpdatedText, Does.Contain("get;"));
        Assert.That(result.UpdatedText, Does.Not.Contain("set;"));
    }

    [Test]
    public async Task AddProperty_ReadWriteProperty()
    {
        SetSource(@"
public class Person { }
", "Person.cs");

        var result = await _memberRefactoringEngine.AddPropertyAsync("Person.cs", "Person", "Age", "int");

        Assert.That(result.UpdatedText, Does.Contain("int Age"));
        Assert.That(result.UpdatedText, Does.Contain("get;"));
        Assert.That(result.UpdatedText, Does.Contain("set;"));
    }

    [Test]
    public async Task AddProperty_InitOnlyProperty()
    {
        SetSource(@"
public class Record { }
", "Record.cs");

        var result = await _memberRefactoringEngine.AddPropertyAsync("Record.cs", "Record", "Id", "Guid", hasSetter: true, isInit: true);

        Assert.That(result.UpdatedText, Does.Contain("Guid Id"));
        Assert.That(result.UpdatedText, Does.Contain("init;"));
        Assert.That(result.UpdatedText, Does.Not.Contain("set;"));
    }

    // ══════════════════════════════════════════════════════════════
    // AddFieldAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddField_PrivateReadonly()
    {
        SetSource(@"
public class Service { }
", "Service.cs");

        var result = await _memberRefactoringEngine.AddFieldAsync("Service.cs", "Service", "_logger", "ILogger", isReadonly: true);

        Assert.That(result.UpdatedText, Does.Contain("private"));
        Assert.That(result.UpdatedText, Does.Contain("readonly"));
        Assert.That(result.UpdatedText, Does.Contain("ILogger _logger"));
    }

    [Test]
    public async Task AddField_PublicStaticWithInitializer()
    {
        SetSource(@"
public class Config { }
", "Config.cs");

        var result = await _memberRefactoringEngine.AddFieldAsync("Config.cs", "Config", "MaxRetries", "int",
            accessibility: "public", isStatic: true, initializer: "3");

        Assert.That(result.UpdatedText, Does.Contain("public"));
        Assert.That(result.UpdatedText, Does.Contain("static"));
        Assert.That(result.UpdatedText, Does.Contain("int MaxRetries"));
        Assert.That(result.UpdatedText, Does.Contain("= 3"));
    }

    // ══════════════════════════════════════════════════════════════
    // SortMembersAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task SortMembers_FieldsBeforeMethods()
    {
        SetSource(@"
public class Repo
{
    public void Save() { }
    private string _name;
}
", "Repo.cs");

        var result = await _memberRefactoringEngine.SortMembersAsync("Repo.cs", "Repo");

        var fieldIdx = result.UpdatedText!.IndexOf("_name", StringComparison.Ordinal);
        var methodIdx = result.UpdatedText!.IndexOf("Save()", StringComparison.Ordinal);
        Assert.That(fieldIdx, Is.LessThan(methodIdx), "Fields should appear before methods.");
    }

    [Test]
    public async Task SortMembers_ConstructorBeforeProperties()
    {
        SetSource(@"
public class Dto
{
    public string Name { get; set; }
    public int Age { get; set; }
    public Dto(string name) { Name = name; }
}
", "Dto.cs");

        var result = await _memberRefactoringEngine.SortMembersAsync("Dto.cs", "Dto");

        var ctorIdx = result.UpdatedText!.IndexOf("Dto(string name)", StringComparison.Ordinal);
        var propIdx = result.UpdatedText!.IndexOf("Name", StringComparison.Ordinal);
        Assert.That(ctorIdx, Is.LessThan(propIdx), "Constructor should appear before properties.");
    }

    [Test]
    public async Task SortMembers_StaticBeforeInstance()
    {
        SetSource(@"
public class Utils
{
    public void InstanceMethod() { }
    public static void StaticMethod() { }
}
", "Utils.cs");

        var result = await _memberRefactoringEngine.SortMembersAsync("Utils.cs", "Utils");

        var staticIdx = result.UpdatedText!.IndexOf("StaticMethod()", StringComparison.Ordinal);
        var instanceIdx = result.UpdatedText!.IndexOf("InstanceMethod()", StringComparison.Ordinal);
        Assert.That(staticIdx, Is.LessThan(instanceIdx), "Static methods should come before instance methods.");
    }

    // ══════════════════════════════════════════════════════════════
    // AddConstructorParameterAsync
    // ══════════════════════════════════════════════════════════════

    [Test]
    public async Task AddConstructorParameter_AddsToExistingCtor()
    {
        SetSource(@"
public class OrderService
{
    private readonly IProductRepo _productRepo;
    public OrderService(IProductRepo productRepo)
    {
        _productRepo = productRepo;
    }
    public void Place() { }
}
", "OrderService.cs");

        var result = await _memberRefactoringEngine.AddConstructorParameterAsync("OrderService.cs", "OrderService", "logger", "ILogger");

        Assert.That(result.UpdatedText, Does.Contain("ILogger logger"), "New param should be in ctor signature.");
        Assert.That(result.UpdatedText, Does.Contain("private readonly ILogger _logger"), "Field should be added.");
        Assert.That(result.UpdatedText, Does.Contain("_logger = logger"), "Assignment should be in body.");
    }

    [Test]
    public async Task AddConstructorParameter_CreatesCtorWhenNoneExists()
    {
        SetSource(@"
public class UserService
{
    public void Create() { }
}
", "UserService.cs");

        var result = await _memberRefactoringEngine.AddConstructorParameterAsync("UserService.cs", "UserService", "repo", "IUserRepo");

        Assert.That(result.UpdatedText, Does.Contain("IUserRepo repo"), "New param should be in ctor.");
        Assert.That(result.UpdatedText, Does.Contain("private readonly IUserRepo _repo"), "Field should exist.");
        Assert.That(result.UpdatedText, Does.Contain("_repo = repo"), "Assignment should be in body.");
    }

    [Test]
    public async Task AddConstructorParameter_UsesCustomFieldName()
    {
        SetSource(@"
public class NotifyService { }
", "NotifyService.cs");

        var result = await _memberRefactoringEngine.AddConstructorParameterAsync("NotifyService.cs", "NotifyService",
            "sender", "IEmailSender", fieldName: "_emailSender");

        Assert.That(result.UpdatedText, Does.Contain("private readonly IEmailSender _emailSender"));
        Assert.That(result.UpdatedText, Does.Contain("_emailSender = sender"));
    }

    [Test]
    public async Task AddConstructorParameter_FieldNameEqualsParamName_DisambiguatesWithUnderscore()
    {
        SetSource(@"
public class OrderService
{
    public void Place() { }
}
", "OrderService.cs");

        var result = await _memberRefactoringEngine.AddConstructorParameterAsync("OrderService.cs", "OrderService",
            "stopwatch", "System.Diagnostics.Stopwatch", fieldName: "stopwatch");

        Assert.That(result.UpdatedText, Does.Contain("private readonly System.Diagnostics.Stopwatch _stopwatch"),
            "Colliding fieldName should be disambiguated to _stopwatch, not left as a bare collision.");
        Assert.That(result.UpdatedText, Does.Contain("_stopwatch = stopwatch"),
            "Assignment must target the disambiguated field, never a self-assignment like 'stopwatch = stopwatch;'.");
        var assignmentStatements = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"\bstopwatch\s*=\s*stopwatch;");
        Assert.That(assignmentStatements, Is.Empty,
            "Must not degenerate into a no-op self-assignment of the parameter to itself.");
        Assert.That(result.Message, Does.Contain("paramName='stopwatch'"));
        Assert.That(result.Message, Does.Contain("fieldName='_stopwatch'"));
    }

    [Test]
    public async Task AddConstructorParameter_FieldNameAlreadyUnderscorePrefixedAndEqualsParam_DisambiguatesWithUnderscore()
    {
        SetSource(@"
public class OrderService
{
    public void Place() { }
}
", "OrderService.cs");

        var result = await _memberRefactoringEngine.AddConstructorParameterAsync("OrderService.cs", "OrderService",
            "stopwatch", "System.Diagnostics.Stopwatch", fieldName: "_stopwatch");

        Assert.That(result.UpdatedText, Does.Contain("private readonly System.Diagnostics.Stopwatch _stopwatch"));
        Assert.That(result.UpdatedText, Does.Contain("_stopwatch = stopwatch"));
    }

    [Test]
    public async Task AddConstructorParameter_ParamNameAlreadyUnderscorePrefixed_StillDisambiguatesViaDoubleUnderscore()
    {
        SetSource(@"
public class OrderService
{
    public void Place() { }
}
", "OrderService.cs");

        var result = await _memberRefactoringEngine.AddConstructorParameterAsync("OrderService.cs", "OrderService",
            "_stopwatch", "System.Diagnostics.Stopwatch", fieldName: "_stopwatch");

        Assert.That(result.UpdatedText, Does.Contain("private readonly System.Diagnostics.Stopwatch __stopwatch"));
        Assert.That(result.UpdatedText, Does.Contain("__stopwatch = _stopwatch"));
        var assignmentStatements = System.Text.RegularExpressions.Regex.Matches(result.UpdatedText!, @"(?<!_)_stopwatch\s*=\s*_stopwatch;");
        Assert.That(assignmentStatements, Is.Empty,
            "Must not degenerate into a no-op self-assignment of the parameter to itself.");
    }

    [Test]
    public async Task AddMember_ShouldAppendToClass()
    {
        // Arrange
        SetSource("public class C { }", "C.cs");
        var member = "public int NewField;";

        // Act
        var result = await _memberRefactoringEngine.AddMemberAsync("C.cs", "C", member);

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public int NewField;"));
    }

    [Test]
    public async Task RemoveMember_ShouldDeleteByName()
    {
        // Arrange
        SetSource("public class C { public void Junk() {} public void Keep() {} }", "C.cs");

        // Act
        var result = await _memberRefactoringEngine.RemoveMemberAsync("C.cs", "Junk");

        // Assert
        Assert.That(result.UpdatedText!, Does.Not.Contain("void Junk()"));
        Assert.That(result.UpdatedText!, Contains.Substring("void Keep()"));
    }

    private const string ApplyDiscountLikeSource =
        "namespace ContosoOrders.Core;\r\n" +
        "\r\n" +
        "public class Order\r\n" +
        "{\r\n" +
        "    // Unused private method: nothing in the solution calls this. Target for SafeDeleteUnusedSymbol.\r\n" +
        "    private string BuildInternalDebugLabel()\r\n" +
        "    {\r\n" +
        "        return $\"[{_customerId}] {_lines.Count} line(s)\";\r\n" +
        "    }\r\n" +
        "\r\n" +
        "    private decimal ApplyDiscount(decimal percentage)\r\n" +
        "    {\r\n" +
        "        // NOTE: this method uses DiscountCalculator, but the using directive for\r\n" +
        "        // ContosoOrders.Core.Discounts is intentionally missing from this file (fully qualified below\r\n" +
        "        // as a workaround) to create a scenario for AddUsingDirective.\r\n" +
        "        return ContosoOrders.Core.Discounts.DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage);\r\n" +
        "    }\r\n" +
        "}\r\n";

    [Test]
    public async Task ReplaceMember_ShouldReplaceMethodByName()
    {
        // Arrange
        SetSource("public class C { public void Old() { } }", "C.cs");
        var newSource = "public void New() { Console.WriteLine(\"Hello\"); }";

        // Act
        var result = await _memberRefactoringEngine.ReplaceMemberAsync("C.cs", "Old", newSource);

        // Assert
        Assert.That(result.UpdatedText!, Contains.Substring("public void New()"));
        Assert.That(result.UpdatedText!, Does.Not.Contain("public void Old()"));
    }

    [Test]
    [Description("Regression (ContosoOrders live agent run, attempt 7): ApplyDiscount is a single, "
                 + "non-overloaded method - memberName alone already resolves it unambiguously. The "
                 + "agent nonetheless passed a defensive contextSnippet that didn't match the file "
                 + "(a formatting/indentation mismatch unrelated to which member was targeted), and "
                 + "the call failed twice with 'contextSnippet not found' even though there was "
                 + "nothing to disambiguate. A contextSnippet that doesn't match must not block "
                 + "resolution when the name alone is already unambiguous.")]
    public async Task ReplaceMember_SingleNonOverloadedMember_MismatchedContextSnippetIsIgnored()
    {
        SetSource(ApplyDiscountLikeSource, "Order.cs");
        var newSource = "public decimal ApplyDiscount(decimal percentage)\n{\n    return DiscountCalculator.ApplyPercentage(CalculateTotal(), percentage);\n}";

        var result = await _memberRefactoringEngine.ReplaceMemberAsync("Order.cs", "ApplyDiscount", newSource,
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

        var noSnippetResult = await _memberRefactoringEngine.ReplaceMemberAsync("C.cs", "Foo", "public void Foo(bool x) { }");
        Assert.That(noSnippetResult.UpdatedText, Is.Not.Null.And.Not.Empty,
            "With 2+ overloads and no contextSnippet, existing first-match behavior should still apply.");

        var mismatchedSnippetResult = await _memberRefactoringEngine.ReplaceMemberAsync("C.cs", "Foo", "public void Foo(bool x) { }",
            contextSnippet: "this text does not appear anywhere in the file");
        Assert.That(mismatchedSnippetResult.UpdatedText, Is.Null.Or.Empty,
            "A genuinely ambiguous name (2+ overloads) with a non-matching contextSnippet must still fail - " +
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
                 + "just the first one - the losing NearestSnippet/CorrectedCoordinates strategies "
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

        var result = await _memberRefactoringEngine.ReplaceMemberAsync("C.cs", "Foo", "public void Foo(double x) { }",
            contextSnippet: "this text does not appear anywhere in the file");

        Assert.That(result.Outcome, Is.EqualTo(EditOutcome.CannotEdit));
        Assert.That(result.Message, Does.Contain("contextSnippet not found (3 candidates):"));
        Assert.That(result.Message, Does.Contain("line 4 `public void Foo(int x) { }`"));
        Assert.That(result.Message, Does.Contain("line 5 `public void Foo(string x) { }`"));
        Assert.That(result.Message, Does.Contain("line 6 `public void Foo(bool x) { }`"));
    }

    [Test]
    public async Task BUG_60_RemoveMember_SucceedsWhenMemberUnused()
    {
        const string code = @"
            public class Helper
            {
                public string UnusedMethod() => ""test"";
                
                public void OtherMethod() { }
            }";
        SetSource(code);
        var doc = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        var result = await _memberRefactoringEngine.RemoveMemberAsync(filePath, "UnusedMethod");
        // Should succeed and remove the unused method
        Assert.That(result.UpdatedText, Does.Not.Contain("UnusedMethod"), "Should remove unused member without errors");
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
        var result = await _memberRefactoringEngine.AddMemberAsync(
            _workspaceManager.ResolveFromWire("Wrappers.cs"),
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
        var result = await _memberRefactoringEngine.AddMemberAsync(
            _workspaceManager.ResolveFromWire("Wrappers.cs"),
            "PlainType",
            "public int Added { get; set; }");

        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty, result.Message);
    }

    [Test]
    public async Task AddMember_UnknownContainer_ListsTheTypesTheFileActuallyDeclaresAsync()
    {
        // The old message was the literal "// Container not found." -> no names, and prefixed as if
        // it were a line of code. Listing what's available is what lets the caller correct itself
        // in one turn instead of guessing, and immediately reveals a wrong-file mistake.
        var result = await _memberRefactoringEngine.AddMemberAsync(
            _workspaceManager.ResolveFromWire("Wrappers.cs"),
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

    // ── Bug 60: RemoveMember -> Doesn't Check for Usages ───────────────────────
    [Test]
    public async Task BUG_60_RemoveMember_ChecksUsagesBeforeRemoving()
    {
        const string code = @"
public class Helper
{
    public string GetName() => ""Test"";

    public void UseHelper()
    {
        var name = GetName(); // Usage here
    }
}";
        SetSource(code, "Helper.cs");
        var result = await _memberRefactoringEngine.RemoveMemberAsync("Helper.cs", "GetName");
        // Should error or return unchanged because GetName is used
        Assert.That(result, Is.Not.Null, "Should return a result");
        if (!result!.Message!.Contains("error") && !result.Message!.Contains("Error"))
        {
            // If not an error, GetName should still be in the output
            Assert.That(result.Message, Does.Contain("GetName"), "If removal succeeds, should indicate that member is used");
        }
    }

    // ────────────────────────────────────────────────────────────────────────────
    // BUG-60: RemoveMember -> Validates Usages Before Removal
    // ────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task BUG_60_RemoveMember_ErrorsWhenMemberIsUsed()
    {
        const string code = @"
            public class Helper
            {
                public string GetName() => ""Test"";
                
                public void UseHelper()
                {
                    var name = GetName();
                }
            }";
        SetSource(code);
        var doc = (await _workspaceManager.GetSolutionAsync(ReadSource.Committed, CancellationToken.None)).Projects.First().Documents.First();
        var filePath = doc.FilePath ?? "Test.cs";
        var result = await _memberRefactoringEngine.RemoveMemberAsync(filePath, "GetName");
        // Should error because GetName is used in UseHelper
        Assert.That(result.Message, Does.Contain("ERROR") | Does.Contain("usages"), "Should error when trying to remove a used member");
    }

    // All four spellings of the same one-type-parameter type. "EngineResultWrapper<T>" is the
    // literal that failed in run 398; the backtick form is the metadata spelling a caller reading
    // a Roslyn/reflection name would supply; the bare form is what already worked.
    [TestCase("EngineResultWrapper")]
    [TestCase("EngineResultWrapper<T>")]
    [TestCase("EngineResultWrapper<TResult>")]
    [TestCase("EngineResultWrapper`1")]
    public async Task AddMember_ResolvesEverySpellingOfAGenericContainerAsync(string containerName)
    {
        var result = await _memberRefactoringEngine.AddMemberAsync(
            _workspaceManager.ResolveFromWire("Wrappers.cs"),
            containerName,
            "public int Added { get; set; }");

        Assert.That(result.UpdatedText, Is.Not.Null.And.Not.Empty,
            $"containerName '{containerName}' should resolve to EngineResultWrapper<T>. {result.Message}");
        Assert.That(result.UpdatedText, Does.Contain("public int Added"));
    }
}
