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
    // wrapper=using needs the range to start with a local declaration of an IDisposable; the declaration becomes the using header.
    private const string DisposableRangeSource = @"using System.IO;

namespace TestProj;

public class Writer
{
    public int Run()
    {
        var s = new MemoryStream();
        s.WriteByte(1);
        return (int)s.Length;
    }

    public int Count()
    {
        var n = 5;
        return n + 1;
    }
}
";

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInUsing_RangeNotStartingWithDeclaration_RefusedAsTargetIneligible()
    {
        using var workspace = InMemoryWorkspace.Create(("Order.cs", CrlfSimpleSource));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Order.cs"), 11, 12, "using", "resource");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData!.Message, Does.Contain("IDisposable"));
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(CrlfSimpleSource));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInUsing_RangeStartingWithDisposableDeclaration_UsesTheDeclarationAsTheHeader()
    {
        using var workspace = InMemoryWorkspace.Create(("Writer.cs", DisposableRangeSource.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Writer.cs"), 9, 11, "using", "s");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var text = workspace.ReadText("Writer.cs");
        Assert.That(text, Does.Contain("using (var s = new MemoryStream())"));
        Assert.That(text, Does.Contain("s.WriteByte(1);"));
        Assert.That(text, Does.Contain("return (int)s.Length;"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInUsing_RangeStartingWithNonDisposableDeclaration_RefusedAsTargetIneligible()
    {
        using var workspace = InMemoryWorkspace.Create(("Writer.cs", DisposableRangeSource.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Writer.cs"), 16, 17, "using", "n");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(result.ErrorData!.Message, Does.Contain("IDisposable"));
        Assert.That(result.ErrorData!.Message, Does.Contain("'n'"));
    }

    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task WrapInUsing_NameDiffersFromDeclaredVariable_RefusedNamingBoth()
    {
        using var workspace = InMemoryWorkspace.Create(("Writer.cs", DisposableRangeSource.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.WrapRangeTool(reason: "test message", workspace.PathOf("Writer.cs"), 9, 11, "using", "stream");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.Message, Does.Contain("'stream'"));
        Assert.That(result.ErrorData!.Message, Does.Contain("'s'"));
    }

    // --- GenerateMapping ---
    [Test]
    [Category("GenerationTools")] // sentinel:auto-category
    public async Task GenerateMapping_OrderToOrderDto_WritesMapperClassToNewFileAndLeavesSourceUnchanged()
    {
        var crlfMappingSource = MappingSource.ReplaceLineEndings("\r\n");
        using var workspace = InMemoryWorkspace.Create(("Order.cs", crlfMappingSource));
        var tools = BatteryToolFactory.CreateGenerationTools(workspace.Manager);
        var result = await tools.GenerateMapping(workspace.PathOf("Order.cs"), "Order", "OrderDto");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(workspace.ReadText("Order.cs"), Is.EqualTo(crlfMappingSource), "the source file must never be rewritten");
        var mapper = workspace.ReadText("OrderToOrderDtoMapper.cs");
        Assert.That(mapper, Does.Contain("namespace TestProj;"));
        Assert.That(mapper, Does.Contain("static class OrderToOrderDtoMapper"));
        Assert.That(mapper, Does.Contain("MapOrderToOrderDto"));
        Assert.That(mapper, Does.Contain("dest.OrderId = source.OrderId;"));
        Assert.That(mapper, Does.Contain("dest.CustomerName = source.CustomerName;"));
    }

    [Test]
    [Category("GenerationTools")] // sentinel:auto-category
    public async Task GenerateMapping_MapperFileAlreadyExists_RefusedWithoutOverwriting()
    {
        const string existingMapper = "namespace TestProj;\r\n\r\npublic static class Existing\r\n{\r\n}\r\n";
        using var workspace = InMemoryWorkspace.Create(("Order.cs", MappingSource.ReplaceLineEndings("\r\n")), ("OrderToOrderDtoMapper.cs", existingMapper));
        var tools = BatteryToolFactory.CreateGenerationTools(workspace.Manager);
        var result = await tools.GenerateMapping(workspace.PathOf("Order.cs"), "Order", "OrderDto");
        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData!.Message, Does.Contain("already exists"));
        Assert.That(workspace.ReadText("OrderToOrderDtoMapper.cs"), Is.EqualTo(existingMapper));
    }

    // --- ConvertAnonymousToNamedTool ---
    [Test]
    [Category("AdvancedRefactoringTools")] // sentinel:auto-category
    public async Task ConvertAnonymousToNamed_InlineAnonymousType_ReplacesItWithTheNamedClass()
    {
        using var workspace = InMemoryWorkspace.Create(("Holder.cs", AnonymousSource.ReplaceLineEndings("\r\n")));
        var tools = BatteryToolFactory.CreateAdvancedRefactoringTools(workspace.Manager);
        var result = await tools.ConvertAnonymousToNamedTool(reason: "test message", workspace.PathOf("Holder.cs"), "OrderData");
        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        var holder = workspace.ReadText("Holder.cs");
        Assert.That(holder, Does.Contain("new OrderData { Id = 1, Name = \"x\" }"));
        Assert.That(holder, Does.Not.Contain("new {"));
        var orderData = workspace.ReadText("OrderData.cs");
        Assert.That(orderData, Does.Contain("namespace TestProj;"));
        Assert.That(orderData, Does.Contain("public class OrderData"));
        Assert.That(orderData, Does.Contain("public int Id { get; set; }"));
        Assert.That(orderData, Does.Contain("public string Name { get; set; }"));
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
