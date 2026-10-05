using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tools.Basic;
using RoslynSentinel.Tests;

#pragma warning disable CS8618
namespace RoslynSentinel.Tests.Tools.Basic;

/// <summary>
/// Tests for SemanticFindReplaceTools: rename a bool property/field and invert its polarity at every site.
/// </summary>
[TestFixture]
public class SemanticFindReplaceToolTests
{
    private IWorkspaceManager _workspaceManager;
    private SemanticReplaceEngine _engine;
    private ValidationEngine _validationEngine;
    private SemanticFindReplaceTools _tool;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(
            NullLogger<IWorkspaceManager>.Instance);
        _engine = new SemanticReplaceEngine(_workspaceManager);
        _validationEngine = new ValidationEngine(_workspaceManager);
        _tool = new SemanticFindReplaceTools(
            _engine,
            _validationEngine,
            _workspaceManager,
            NullLogger<SemanticFindReplaceTools>.Instance);
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    [Test]
    [Description("Preview mode returns sites without writing")]
    public async Task SemanticFindReplace_PreviewMode_ReturnsSitesWithoutWriting()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

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
            var x = !_c.IsSuccess;
            _c.IsSuccess = false;
        }
    }
}";
        File.WriteAllText(targetFile, originalContent, System.Text.Encoding.UTF8);
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var docCommentId = $"P:C.IsSuccess";

        // Call the tool in preview mode
        var result = await _tool.SemanticFindReplace(
            new ToolCallReason("testing preview mode"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "HasError",
            SemanticReplaceMode.preview);

        // Assert preview mode returns success with data
        Assert.That(!result.IsError, Is.True, result.ErrorData?.Message);
        Assert.That(result.SuccessData, Is.Not.Null);
        
        // Verify file is unchanged on disk
        var diskContent = File.ReadAllText(targetFile);
        Assert.That(diskContent, Is.EqualTo(originalContent), "File should be unchanged after preview");
        Assert.That(diskContent.Contains("IsSuccess"), "File should still contain original symbol name");
    }

    [Test]
    [Description("Apply mode rewrites file with renamed symbol and inverted logic")]
    public async Task SemanticFindReplace_ApplyMode_RewritesFileCorrectly()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

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
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var docCommentId = $"P:C.IsSuccess";

        // Call the tool in apply mode
        var result = await _tool.SemanticFindReplace(
            new ToolCallReason("testing apply mode"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "HasError",
            SemanticReplaceMode.apply);

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
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
        var source = @"public class C
{
    public bool IsSuccess { get; set; }
}";
        File.WriteAllText(targetFile, source, System.Text.Encoding.UTF8);
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var docCommentId = "P:C.UnknownMember";

        // Call with unknown symbol
        var result = await _tool.SemanticFindReplace(
            new ToolCallReason("testing unknown symbol"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "HasError",
            SemanticReplaceMode.preview);

        // Assert NotFound error
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
    }

    [Test]
    [Description("Unsupported site returns TargetIneligible error with location info")]
    public async Task SemanticFindReplace_UnsupportedSite_ReturnsTargetIneligibleWithLocation()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

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
        _c.IsSuccess |= true;
    }
}";
        File.WriteAllText(targetFile, originalContent, System.Text.Encoding.UTF8);
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var docCommentId = $"P:C.IsSuccess";

        // Call with unsupported site in preview mode
        var result = await _tool.SemanticFindReplace(
            new ToolCallReason("testing unsupported site"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "HasError",
            SemanticReplaceMode.preview);

        // Assert failure with proper error code
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData, Is.Not.Null);
        Assert.That(result.ErrorData.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        
        // Verify error message contains the file name and line number (line 11 is the |= line)
        var message = result.ErrorData.Message;
        var fileName = Path.GetFileName(targetFile);
        Assert.That(message, Does.Contain(fileName), $"Error should mention the file name ({fileName})");
        Assert.That(message, Does.Contain(":11"), "Error should contain the line number (line 11 is the |= line)");
        
        // Verify file is unchanged on disk
        var diskContent = File.ReadAllText(targetFile);
        Assert.That(diskContent, Is.EqualTo(originalContent), "File should be unchanged after error");
    }

    [Test]
    [Description("Alias retarget: newName = the sibling of a computed inverse alias previews, then applies, leaving the alias declaration untouched")]
    public async Task SemanticFindReplace_AliasRetarget_PreviewThenApplyRewritesUsagesAndKeepsAliasDeclaration()
    {
        using var fixture = new TestSolutionFixture();
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var targetFile = Directory.EnumerateFiles(fixture.SolutionDirectory, "*.cs", SearchOption.AllDirectories).First();
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
        File.WriteAllText(targetFile, originalContent, System.Text.Encoding.UTF8);
        await _workspaceManager.LoadSolutionAsync(fixture.SolutionPath);

        var docCommentId = "P:C.IsSuccess";

        // Renaming in place stays refused, and the message teaches the alias-retarget form.
        var refused = await _tool.SemanticFindReplace(
            new ToolCallReason("testing alias in-place refusal"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "HasSucceeded",
            SemanticReplaceMode.preview);
        Assert.That(!refused.IsError, Is.False);
        Assert.That(refused.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.TargetIneligible));
        Assert.That(refused.ErrorData.Message, Does.Contain("alias retarget"));
        Assert.That(refused.ErrorData.Message, Does.Contain("'IsError'"));

        var preview = await _tool.SemanticFindReplace(
            new ToolCallReason("testing alias retarget preview"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "IsError",
            SemanticReplaceMode.preview);
        Assert.That(!preview.IsError, Is.True, preview.ErrorData?.Message);
        Assert.That(File.ReadAllText(targetFile), Is.EqualTo(originalContent), "File should be unchanged after preview");

        var apply = await _tool.SemanticFindReplace(
            new ToolCallReason("testing alias retarget apply"),
            SemanticReplaceOperation.invertBoolean,
            docCommentId,
            "IsError",
            SemanticReplaceMode.apply);
        Assert.That(!apply.IsError, Is.True, apply.ErrorData?.Message);

        var expected = originalContent
            .Replace("if (_c.IsSuccess)", "if (!_c.IsError)")
            .Replace("var x = !_c.IsSuccess;", "var x = _c.IsError;")
            .Replace("_c.IsSuccess = false;", "_c.IsError = true;");
        Assert.That(File.ReadAllText(targetFile), Is.EqualTo(expected));
    }
}
