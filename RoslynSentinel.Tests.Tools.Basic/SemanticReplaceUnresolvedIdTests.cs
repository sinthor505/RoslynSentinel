using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// SemanticFindReplace with a docCommentId that resolves to no symbol (plan_not_found_error_orientation Step 5e): invertBoolean
/// (SemanticReplaceEngine.ResolveBoolMemberAsync), replaceConstruction (ConstructionReplacer, symbol and factory) and removeNullForgiving
/// (NullForgivingRemover). A near name gives the candidate with its exact docCommentId and the tool's retry call; no near name gives the
/// no-similar-name fact and one orientation call. The code stays NotFound and the file is left as it was.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("SemanticFindReplaceTools")] // sentinel:auto-category
public class SemanticReplaceUnresolvedIdTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/SemanticReplaceMissFixture.cs";

    private const string Source = """
        public class Widget
        {
            public Widget(int a) { }
            public string Name { get; set; } = "";
            public string? Nick { get; set; }
            public bool IsReady { get; set; }
        }

        public static class WidgetFactory
        {
            public static Widget Create(int a) => new Widget(a);
        }
        """;

    private const string ValidFactory = "M:WidgetFactory.Create(System.Int32)";

    private static async Task<(string Message, string FileText)> RunAsync(SemanticReplaceOperation operation, string symbol, string? newName = null, string? factory = null)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, Source));
        var tools = new SemanticFindReplaceTools(
            new SemanticReplaceEngine(workspace.Manager),
            Battery.Basic.TestEngines.ValidationEngine(workspace.Manager),
            workspace.Manager,
            NullLogger<SemanticFindReplaceTools>.Instance);

        var result = await tools.SemanticFindReplaceTool(new ToolCallReason("testing an unresolved id"), operation, symbol, SemanticReplaceMode.apply, newName: newName, factory: factory);

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        return (result.ErrorData!.Message, workspace.ReadText(FixtureRelativePath));
    }

    [Test]
    public async Task InvertBoolean_NearId_OffersTheCandidateIdAndTheRetryCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.invertBoolean, "P:Widget.IsRead", newName: "IsPending");

        Assert.That(message, Does.Contain("docCommentId 'P:Widget.IsRead' resolves to no symbol"));
        Assert.That(message, Does.Contain("docCommentId 'P:Widget.IsReady'"), "the candidate states its exact id");
        Assert.That(message, Does.Contain("SemanticFindReplace"), "the retry is the tool's own call");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task InvertBoolean_NoSimilarId_StatesTheFactAndOffersTheOrientationCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.invertBoolean, "P:Widget.Qzxvbnm", newName: "IsPending");

        Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
        Assert.That(message, Does.Contain("GetFileOutline"), "the container Widget resolves to one file, so that file's outline is the orientation call");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task InvertBoolean_IdWithoutKindPrefix_StatesTheShapeFact()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.invertBoolean, "Widget.IsReady", newName: "IsPending");

        Assert.That(message, Does.Contain("is not a documentation-comment ID"));
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task ReplaceConstruction_NearSymbolId_OffersTheCandidateIdAndTheRetryCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.replaceConstruction, "T:Widgt", factory: ValidFactory);

        Assert.That(message, Does.StartWith("SemanticFindReplace replaceConstruction: symbol: docCommentId 'T:Widgt' resolves to no symbol"));
        Assert.That(message, Does.Contain("docCommentId 'T:Widget'"), "the candidate states its exact id");
        Assert.That(message, Does.Contain("SemanticFindReplace"), "the retry is the tool's own call");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task ReplaceConstruction_NoSimilarSymbolId_StatesTheFactAndOffersTheOrientationCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.replaceConstruction, "T:Qzxvbnm", factory: ValidFactory);

        Assert.That(message, Does.Contain("symbol: docCommentId 'T:Qzxvbnm' resolves to no symbol"));
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
        Assert.That(message, Does.Contain("ListAll"), "a bare type id has no container file, so the listing is the orientation call");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task ReplaceConstruction_NearFactoryId_OffersTheCandidateIdAndTheRetryCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.replaceConstruction, "T:Widget", factory: "M:WidgetFactory.Creat(System.Int32)");

        Assert.That(message, Does.Contain("factory: docCommentId 'M:WidgetFactory.Creat(System.Int32)' resolves to no symbol"));
        Assert.That(message, Does.Contain($"docCommentId '{ValidFactory}'"), "the candidate states its exact id");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task ReplaceConstruction_NoSimilarFactoryId_StatesTheFactAndOffersTheOrientationCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.replaceConstruction, "T:Widget", factory: "M:WidgetFactory.Qzxvbnm(System.Int32)");

        Assert.That(message, Does.Contain("factory: docCommentId 'M:WidgetFactory.Qzxvbnm(System.Int32)' resolves to no symbol"));
        Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
        Assert.That(message, Does.Contain("GetFileOutline"), "the container WidgetFactory resolves to one file");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task RemoveNullForgiving_NearId_OffersTheCandidateIdAndTheRetryCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.removeNullForgiving, "P:Widget.Nam");

        Assert.That(message, Does.Contain("symbol: docCommentId 'P:Widget.Nam' resolves to no symbol"));
        Assert.That(message, Does.Contain("docCommentId 'P:Widget.Name'"), "the candidate states its exact id");
        Assert.That(message, Does.Contain("SemanticFindReplace"), "the retry is the tool's own call");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }

    [Test]
    public async Task RemoveNullForgiving_NoSimilarId_StatesTheFactAndOffersTheOrientationCall()
    {
        var (message, fileText) = await RunAsync(SemanticReplaceOperation.removeNullForgiving, "P:Widget.Qzxvbnm");

        Assert.That(message, Does.Contain("No symbol in the solution has a similar name"));
        Assert.That(message, Does.Contain("GetFileOutline"), "the container Widget resolves to one file, so that file's outline is the orientation call");
        Assert.That(fileText, Is.EqualTo(Source), "nothing was written");
    }
}
