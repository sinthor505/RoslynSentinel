using RoslynSentinel.Engines.Advanced;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tests.Support;
using RoslynSentinel.Tools.Advanced;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// In-memory (FakeWorkspaceManager) tests; split out of BatteryTwentyFourTests so they cannot leak files into the bin dir.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class WrapRangeAndGenerationTests
{
    private static readonly string SimpleSource = Sources.Order;

    // The original fixture was CRLF (a verbatim literal inside a CRLF file); the EOL guard compares against the document's endings.
    private static readonly string CrlfSimpleSource = SimpleSource.ReplaceLineEndings("\r\n");

    private const string MappingSource = @"
namespace TestProj;

public class Order
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
}

public class OrderDto
{
    public int OrderId { get; set; }
    public string CustomerName { get; set; }
}
";

    private const string AnonymousSource = @"
namespace TestProj;

public class Holder
{
    public void Run()
    {
        var item = new { Id = 1, Name = ""x"" };
    }
}
";

    // --- WrapInTryCatch ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInTryCatch_ValidStatementRange_WrapsStatements()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Order.cs"), 11, 12, "tryCatch");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Order.cs");
        Assert.That(text, Does.Contain("try"));
        Assert.That(text, Does.Contain("catch"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapRange_RangeStartingOnConstructorHeader_ReturnsErrorNotNullReference()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Order.cs"), 8, 10, "tryCatch");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData!.Message, Does.Contain("Choose a range"));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("unexpectedly"));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("NullReference"));
    }

    // --- WrapInRegion ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInRegion_ValidMemberRange_WrapsInRegion()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Order.cs"), 3, 6, "region", "Properties");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("#region Properties"));
    }

    // --- WrapInUsing ---
    // TODO WrapRangeTool using wraps non-disposable statements in invalid code that the compile gate refuses; assert success once a disposable statement range is fixtured
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInUsing_NonDisposableStatementRange_ReturnsCompilerErrorRefusal()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Order.cs"), 11, 12, "using", "resource");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("introduces new compiler errors"));
    }

    // --- GenerateMapping ---
    [Test]
    [Category("GenerationTools")] // sentinel:auto-category
    public async Task GenerateMapping_OrderToOrderDto_ReturnsCompilerErrorRefusal()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", MappingSource.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateGenerationTools(workspace.Manager);
        var result = await tools.GenerateMapping(workspace.PathOf("Order.cs"), "Order", "OrderDto");
        // TODO GenerateMapping(Order, OrderDto) on matching types is refused because the generated code introduces compiler errors; decide what the mapping should emit
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("introduces new compiler errors"));
    }

    // --- ConvertAnonymousToNamedTool ---
    // TODO ConvertAnonymousToNamedTool reports success but leaves the anonymous type in place for this inline input; find the input shape that converts
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ConvertAnonymousToNamed_InlineAnonymousType_ReportsSuccessWithoutChange()
    {
        using var workspace = InMemoryWorkspace.Create(("Holder.cs", AnonymousSource.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ConvertAnonymousToNamedTool(reason: "test message", workspace.PathOf("Holder.cs"), "OrderData");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Holder.cs"), Does.Contain("new {"));
    }

    // --- InvertAssignmentsTool ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task InvertAssignments_ValidLineRange_InvertsAssignments()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.InvertAssignmentsTool(reason: "test message", workspace.PathOf("Order.cs"), 8, 12);
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Does.Contain("orderId = OrderId"));
    }

    // --- SyncInterfaceToImplementation ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task SyncInterfaceToImplementation_ClassWithInterface_KeepsClassDeclaration()
    {
        const string src = @"namespace TestProj;
public interface IWorker { void Work(); }
public class Worker : IWorker { public void Work() {} public void Extra() {} }";
        using var workspace = InMemoryWorkspace.Create(("Worker.cs", src.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs"), "IWorker", SyncInterfaceAction.sync, "Worker");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Worker.cs"), Does.Contain("class Worker : IWorker"));
    }

    // Sync writes the new member into the interface's own file and leaves the class file untouched.
    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterfaceToImplementation_InterfaceInOtherFile_UpdatesInterfaceFileOnly()
    {
        const string workerSrc = @"namespace TestProj;
public class Worker : IWorker { public void Work() {} public void Extra() {} }";
        const string ifaceSrc = @"namespace TestProj;
public interface IWorker { void Work(); }";
        using var workspace = InMemoryWorkspace.Create(
            ("Worker.cs", workerSrc.ReplaceLineEndings("\r\n")),
            ("IWorker.cs", ifaceSrc.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs"), "IWorker", SyncInterfaceAction.sync, "Worker");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("IWorker.cs"), Does.Contain("void Extra()"));
        Assert.That(workspace.ReadText("Worker.cs"), Is.EqualTo(workerSrc.ReplaceLineEndings("\r\n")));
        Assert.That(workspace.ReadText("IWorker.cs"), Does.Not.Contain("// Updated file:"));
        Assert.That(workspace.ReadText("Worker.cs"), Does.Not.Contain("// Updated file:"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterface_sync_MissingInterface_NamesTheInterface()
    {
        const string src = @"namespace TestProj;
public class Worker { public void Work() {} }";
        using var workspace = InMemoryWorkspace.Create(("Worker.cs", src.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs"), "IMissing", SyncInterfaceAction.sync, "Worker");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Interface 'IMissing'"));
        Assert.That(result.ErrorData!.Message, Does.Not.Contain("Class 'Worker'"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterface_sync_MissingClass_NamesTheClass()
    {
        const string src = @"namespace TestProj;
public interface IWorker { void Work(); }";
        using var workspace = InMemoryWorkspace.Create(("Worker.cs", src.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs"), "IWorker", SyncInterfaceAction.sync, "Ghost");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("Class 'Ghost'"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterface_sync_AlreadyInSync_IsNotAnError()
    {
        const string src = @"namespace TestProj;
public interface IWorker { void Work(); }
public class Worker : IWorker { public void Work() {} }";
        using var workspace = InMemoryWorkspace.Create(("Worker.cs", src.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs"), "IWorker", SyncInterfaceAction.sync, "Worker");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Worker.cs"), Is.EqualTo(src.ReplaceLineEndings("\r\n")));
    }

    [Test]
    [Category("AdvancedRefactoringTools")]
    public async Task SyncInterface_implement_AlreadyImplemented_IsNotAnError()
    {
        const string src = @"namespace TestProj;
public interface IWorker { void Work(); }
public class Worker : IWorker { public void Work() {} }";
        using var workspace = InMemoryWorkspace.Create(("Worker.cs", src.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.SyncInterfaceTool(reason: "test message", workspace.PathOf("Worker.cs"), "IWorker", SyncInterfaceAction.implement, "Worker");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
    }
}
