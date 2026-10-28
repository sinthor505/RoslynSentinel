using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tests;
using RoslynSentinel.Tests.Fakes;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for SemanticFindReplaceTools: rename a bool property/field and invert its polarity at every site.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("SemanticFindReplaceTools")] // sentinel:auto-category
public class SemanticFindReplaceToolTests
{

    [Test]
    [Description("Preview mode returns sites without writing")]
    public async Task SemanticFindReplace_PreviewMode_ReturnsSitesWithoutWriting()
    {
        var originalContent = @"public class C
{
    public bool IsSuccess { get; set; }
}

public class Usage
{
    private C _c = new();
    private void Test()
    {
        if (_c.IsSuccess)
        {
            var x = !_c.IsSuccess;
            _c.IsSuccess = false;
        }
    }
}";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, originalContent));
        var tools = BuildTools(workspace.Manager);

        var docCommentId = $"P:C.IsSuccess";

        // Call the tool in preview mode
        var result = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing preview mode"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "HasError",
            mode: SemanticReplaceMode.preview);

        // Assert preview mode returns success with data
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        // TODO pin the site count once the SuccessData sites type is confirmed; for now pin preview mode and a non-empty sites list
        Assert.That(System.Text.Json.JsonSerializer.Serialize(result.SuccessData), Does.Contain("\"mode\":\"preview\"").And.Contain("\"sites\":[{\"FilePath\":"));

        // Verify file is unchanged
        var content = workspace.ReadText(FixtureRelativePath);
        Assert.That(content, Is.EqualTo(originalContent), "File should be unchanged after preview");
        Assert.That(content.Contains("IsSuccess"), "File should still contain original symbol name");
    }

    [Test]
    [Description("Apply mode rewrites file with renamed symbol and inverted logic")]
    public async Task SemanticFindReplace_ApplyMode_RewritesFileCorrectly()
    {
        // Kept on disk on purpose: the single end-to-end apply smoke for this tool (real write through
        // PersistentWorkspaceManager, then read back from disk). Other apply tests use InMemoryWorkspace.
        using var fixture = new TestSolutionFixture();
        using var workspaceManager = Battery.Basic.TestEngines.PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        var tools = BuildTools(workspaceManager);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var originalContent = @"public class C
{
    public bool IsSuccess { get; set; }
}

public class Usage
{
    private C _c = new();
    private void Test()
    {
        if (_c.IsSuccess)
        {
            _c.IsSuccess = false;
        }
    }
}";
        File.WriteAllText(targetFile, originalContent, System.Text.Encoding.UTF8);
        await workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var docCommentId = $"P:C.IsSuccess";

        // Call the tool in apply mode
        var result = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing apply mode"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "HasError",
            mode: SemanticReplaceMode.apply);

        // Assert apply mode succeeds
        if (result.IsError)
        {
            Assert.Fail($"Apply mode failed with error: {result.ErrorData?.Message ?? "unknown error"}");
        }

        Assert.That(!result.IsError, Is.True);
        Assert.That(result.SuccessData, Is.Not.Null);

        // Verify file content changed on disk
        var diskContent = File.ReadAllText(targetFile);
        var expected = originalContent
            .Replace("public bool IsSuccess", "public bool HasError")
            .Replace("if (_c.IsSuccess)", "if (!_c.HasError)")
            .Replace("_c.IsSuccess = false;", "_c.HasError = true;");
        Assert.That(diskContent, Is.EqualTo(expected));
    }

    [Test]
    [Description("Unknown symbol returns NotFound error")]
    public async Task SemanticFindReplace_UnknownSymbol_ReturnsNotFoundError()
    {
        var source = @"public class C
{
    public bool IsSuccess { get; set; }
}";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, source));
        var tools = BuildTools(workspace.Manager);

        var docCommentId = "P:C.UnknownMember";

        // Call with unknown symbol
        var result = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing unknown symbol"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "HasError",
            mode: SemanticReplaceMode.preview);

        // Assert NotFound error
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    [Description("Unsupported site returns TargetIneligible error with location info")]
    public async Task SemanticFindReplace_UnsupportedSite_ReturnsTargetIneligibleWithLocation()
    {
        var originalContent = @"public class C
{
    public bool IsSuccess { get; set; }
}

public class Usage
{
    private C _c = new();
    private void Test()
    {
        _c.IsSuccess |= true;
    }
}";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, originalContent));
        var tools = BuildTools(workspace.Manager);
        var targetFile = workspace.PathOf(FixtureRelativePath);

        var docCommentId = $"P:C.IsSuccess";

        // Call with unsupported site in preview mode
        var result = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing unsupported site"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "HasError",
            mode: SemanticReplaceMode.preview);

        // Assert failure with proper error code
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));

        // Verify error message contains the file name and line number (line 11 is the |= line)
        var message = result.ErrorData.Message;
        var fileName = Path.GetFileName(targetFile);
        Assert.That(message, Does.Contain(fileName), $"Error should mention the file name ({fileName})");
        Assert.That(message, Does.Contain(":11"), "Error should contain the line number (line 11 is the |= line)");

        // Verify file is unchanged
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(originalContent), "File should be unchanged after error");
    }

    [Test]
    [Description("Alias retarget: newName = the sibling of a computed inverse alias previews, then applies, leaving the alias declaration untouched")]
    public async Task SemanticFindReplace_AliasRetarget_PreviewThenApplyRewritesUsagesAndKeepsAliasDeclaration()
    {
        var originalContent = @"public class C
{
    private bool _isError;
    public bool IsError { get => _isError; set => _isError = value; }
    public bool IsSuccess { get => !_isError; set => _isError = !value; }
}

public class Usage
{
    private C _c = new();
    private void Test()
    {
        if (_c.IsSuccess)
        {
            var x = !_c.IsSuccess;
            _c.IsSuccess = false;
        }
    }
}";
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, originalContent));
        var tools = BuildTools(workspace.Manager);

        var docCommentId = "P:C.IsSuccess";

        // Renaming in place stays refused, and the message teaches the alias-retarget form.
        var refused = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing alias in-place refusal"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "HasSucceeded",
            mode: SemanticReplaceMode.preview);
        Assert.That(!refused.IsError, Is.False);
        Assert.That(refused.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(refused.ErrorData.Message, Does.Contain("alias retarget"));
        Assert.That(refused.ErrorData.Message, Does.Contain("'IsError'"));

        var preview = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing alias retarget preview"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "IsError",
            mode: SemanticReplaceMode.preview);
        Assert.That(!preview.IsError, Is.True, preview.ErrorData?.Message);
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(originalContent), "File should be unchanged after preview");

        var apply = await tools.SemanticFindReplaceTool(
            new ToolCallReason("testing alias retarget apply"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            newName: "IsError",
            mode: SemanticReplaceMode.apply);
        Assert.That(!apply.IsError, Is.True, apply.ErrorData?.Message);

        var expected = originalContent
            .Replace("if (_c.IsSuccess)", "if (!_c.IsError)")
            .Replace("var x = !_c.IsSuccess;", "var x = _c.IsError;")
            .Replace("_c.IsSuccess = false;", "_c.IsError = true;");
        Assert.That(workspace.ReadText(FixtureRelativePath), Is.EqualTo(expected));
    }

    private const string FixtureRelativePath = "ContosoOrders.Core/SemanticFindReplaceFixture.cs";

    private static SemanticFindReplaceTools BuildTools(IWorkspaceManager workspaceManager) =>
    new(new SemanticReplaceEngine(workspaceManager),
        Battery.Basic.TestEngines.ValidationEngine(workspaceManager),
        workspaceManager,
        NullLogger<SemanticFindReplaceTools>.Instance);
}
