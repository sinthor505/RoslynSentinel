using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class MoveTypeAndExtractTests
{
    private const string SimpleSource = @"
namespace TestProj;

public class Order
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }

    public Order(int orderId, string customerName)
    {
        OrderId = orderId;
        CustomerName = customerName;
    }

    public string GetLabel()
    {
        return string.Format(""{0}: {1}"", OrderId, CustomerName);
    }

    public string GetStatus()
    {
        if (OrderId == 1) return ""Active"";
        if (OrderId == 2) return ""Pending"";
        return ""Unknown"";
    }
}

public interface IService
{
    string GetLabel();
}

public enum Status { Active = 1, Pending = 2 }
";

    private const string RefactorSource = @"
using System;
using System.Collections.Generic;

namespace RefactorNs;

public abstract class Animal
{
    public string Name;
    public abstract string Sound();
    public virtual void Move() { Console.WriteLine(""moving""); }
}

public class Dog : Animal
{
    public string Breed;
    public Dog(string name, string breed) { Name = name; Breed = breed; }
    public override string Sound() => ""woof"";
    public override void Move() => base.Move();
    public string GetInfo() { return string.Format(""{0} ({1})"", Name, Breed); }
    public void Process(int a, int b, int c, int d, int e) { }
    public int Calculate(int x)
    {
        if(x > 0) return 1;
        if(x < 0) return -1;
        return 0;
    }
}

public class Target {}
";

    // The original fixture was CRLF (verbatim literals inside a CRLF file); the EOL guard compares against the document's endings.
    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");
    private static readonly string CrlfRefactorSource = RefactorSource.ReplaceLineEndings("\r\n");

    private static AdvancedRefactoringTools BuildTools(IWorkspaceManager workspaceManager) => BatteryToolFactory.CreateAdvancedRefactoringTools(workspaceManager);

    // Concatenates every document's text so a test can assert a type exists somewhere in the solution.
    private static string AllText(InMemoryWorkspace workspace)
    {
        var solution = workspace.Manager.CurrentSolution!;
        return string.Join("\n", solution.Projects
            .SelectMany(p => p.Documents)
            .Select(d => d.GetTextAsync().GetAwaiter().GetResult().ToString()));
    }

    // --- ExtractSuperclass ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractSuperclass_DogWithAnimalBase_IsRefusedWithoutMemberNames()
    {
        using var workspace = InMemoryWorkspace.Create(("Dog.cs", CrlfRefactorSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Dog.cs"), "Dog", ExtractAsType.superclass, "AnimalBase");
        // TODO ExtractMembers superclass with no memberNames throws IndexOutOfRangeException instead of a structured argument error
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("failed unexpectedly"));
    }

    // --- ExtractInterface ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractInterface_OrderClass_CreatesIOrderInterface()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", ExtractAsType.@interface, "IOrder");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(AllText(workspace), Does.Contain("interface IOrder"));
    }

    // --- MoveTypeToFile ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveTypeToFile_StatusEnum_MovesEnumToOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveTypeTool(reason: "test message", workspace.PathOf("Order.cs"), "Status", "ownFile");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
        Assert.That(workspace.ReadText("Order.cs"), Does.Not.Contain("enum Status"));
    }

    // --- MoveAllTypesToFiles (file scope) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFiles_FileScope_EachTypeGetsOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.file, workspace.PathOf("Order.cs"));
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
        Assert.That(workspace.ReadText("IService.cs"), Does.Contain("interface IService"));
        Assert.That(workspace.ReadText("Order.cs"), Does.Not.Contain("enum Status"));
    }

    // --- MoveAllTypesToFiles (project scope) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFilesInProject_ProjectScope_MovesStatusToOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.project, "InMemoryProject");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
    }

    // --- MoveAllTypesToFiles (solution scope) ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveAllTypesToFilesInSolution_SolutionScope_MovesStatusToOwnFile()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveAllTypesToFilesTool(reason: "test message", ToolScope.solution);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Status.cs"), Does.Contain("enum Status"));
    }

    // --- ExtractMembersToPartial ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ExtractMembersToPartial_GetLabel_IsRefusedWithCompilerErrors()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.ExtractMembersTool(reason: "test message", workspace.PathOf("Order.cs"), "Order", ExtractAsType.partialClass, memberNames: ["GetLabel"]);
        // TODO ExtractMembers partialClass on a non-partial class yields compiler errors (ErrorData.Message omits the diagnostics; cause not confirmed)
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.Message, Does.Contain("new compiler errors"));
    }

    // --- MoveTypeToOuterScope ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task MoveTypeToOuterScope_NestedInner_MovesInnerOutOfOuter()
    {
        using var workspace = InMemoryWorkspace.Create(("Outer.cs", "namespace TestProj; public class Outer { public class Inner {} }".ReplaceLineEndings("\r\n")));
        var tools = BuildTools(workspace.Manager);
        var result = await tools.MoveTypeTool(reason: "test message", workspace.PathOf("Outer.cs"), "Inner", "outerScope");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(AllText(workspace), Does.Contain("class Inner"));
        Assert.That(AllText(workspace), Does.Not.Match(@"class Outer\s*\{\s*public class Inner"));
    }
}
