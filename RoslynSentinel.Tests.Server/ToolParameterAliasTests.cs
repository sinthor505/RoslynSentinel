using System.IO.Pipelines;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using RoslynSentinel.Server.Advanced;

namespace RoslynSentinel.Tests.Server;

/// <summary>
/// Coverage for <see cref="ToolArgumentValidator.ApplyParameterAliases"/> and
/// <see cref="ToolArgumentValidator.ParameterAliases"/>: parameter names a model repeatedly supplies
/// instead of the declared one (mined from transcripts with scripts/Get-UnknownParameterReport.ps1)
/// are renamed to the declared parameter, the tool runs on the real value, and the response carries
/// a short note naming the real parameter.
/// <para>
/// Runs against a real in-process <see cref="McpClient"/>/<c>McpServer</c> pair (same pattern as
/// <see cref="ToolArgumentCaseNormalizationTests"/>) so the rename is proven to reach the SDK's own
/// case-sensitive argument binder.
/// </para>
/// </summary>
[TestFixture]
public class ToolParameterAliasTests
{
    private IHost _host = null!;
    private McpClient _client = null!;
    private TestSolutionFixture _fixture = null!;
    private string _seededFilePath = null!;

    private static readonly HashSet<string> ActiveModes = new(StringComparer.OrdinalIgnoreCase) { "Workspace" };

    private const string TargetMethodName = "Target";

    // A comment token seeded in two different folders so a path-restricted text Search can be told apart from an unrestricted one.
    private const string SharedToken = "SharedAliasToken";
    private const string OtherFolderName = "Other";

    private static string TextOf(CallToolResult result) =>
        string.Join(" ", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [SetUp]
    public async Task SetUp()
    {
        _fixture = new TestSolutionFixture();

        var source = "namespace ContosoOrders.Core;" + Environment.NewLine +
                     "public class ParameterAliasProbe" + Environment.NewLine +
                     "{" + Environment.NewLine +
                     $"    public void {TargetMethodName}() {{ }}" + Environment.NewLine +
                     $"    public void Caller() {{ {TargetMethodName}(); }}" + Environment.NewLine +
                     $"    // {SharedToken}" + Environment.NewLine +
                     "}" + Environment.NewLine;

        var relativeFilePath = Path.Combine("ContosoOrders.Core", "ParameterAliasProbe.cs");
        using (var seedWorkspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance) { SolutionPath = _fixture.SolutionPath })
        {
            await _fixture.AddFileToSolution(seedWorkspaceManager, relativeFilePath, source, reloadSolution: false);

            var otherSource = "namespace ContosoOrders.Core;" + Environment.NewLine +
                              "public class ParameterAliasOther" + Environment.NewLine +
                              "{" + Environment.NewLine +
                              $"    // {SharedToken}" + Environment.NewLine +
                              "}" + Environment.NewLine;
            await _fixture.AddFileToSolution(
                seedWorkspaceManager,
                Path.Combine("ContosoOrders.Core", OtherFolderName, "ParameterAliasOther.cs"),
                otherSource,
                reloadSolution: false);
        }

        _seededFilePath = Path.Combine(_fixture.SolutionDirectory, relativeFilePath);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var services = new ServiceCollection();
        services.AddRoslynSentinelEnginesBasic();

        var mcpBuilder = services.AddMcpServer();
        mcpBuilder.WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        mcpBuilder.AddRoslynSentinelToolsBasic(services, ActiveModes);

        var hostBuilder = Host.CreateApplicationBuilder();
        foreach (var descriptor in services)
        {
            hostBuilder.Services.Add(descriptor);
        }

        _host = hostBuilder.Build();
        _ = _host.RunAsync();

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream(),
            loggerFactory: NullLoggerFactory.Instance);

        _client = await McpClient.CreateAsync(clientTransport, cancellationToken: TestContext.CurrentContext.CancellationToken);

        var loadResult = await _client.CallToolAsync(
            "LoadSolution",
            new Dictionary<string, object?> { ["reason"] = "test message", ["solutionPath"] = _fixture.SolutionPath }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(loadResult.IsError, Is.Not.True, "Fixture solution failed to load - cannot exercise aliases without a loaded solution.");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _fixture.Dispose();
    }

    [Test]
    public async Task Alias_IsRenamed_ToolRunsOnTheRealValue_AndNoteNamesTheDeclaredParameter()
    {
        // "symbol" is not a FindReferencesTool parameter; "symbolName" is. Without the alias this is the
        // unknown-parameter rejection the transcripts show.
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbol"] = TargetMethodName,
                ["kind"] = "callers",
                ["filePath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"The alias 'symbol' must be treated as 'symbolName'. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain("Caller"),
            $"FindReferences must have run with the real symbolName value under the renamed key. Got: {text}");
        Assert.That(text, Does.Contain("'symbol' is not a parameter of FindReferences"),
            $"The response must carry a note about the alias. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'symbolName'"), $"The note must name the declared parameter. Got: {text}");
    }

    [Test]
    public async Task DeclaredParameterAlreadyPresent_AliasIsNotRenamed_AndIsRejectedAsUnknown()
    {
        // Renaming "symbol" onto the already-supplied "symbolName" would silently drop one of two
        // values the caller passed - the alias must be left for Validate to reject.
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["symbol"] = "SomethingElse",
                ["kind"] = "callers",
                ["filePath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An alias alongside its declared target must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("symbol"), $"The rejection must name the unrecognised alias. Got: {text}");
        Assert.That(text, Does.Not.Contain("treated as"), "No rename may have happened.");
        Assert.That(text, Does.Contain("'symbol' means 'symbolName', which this call already passes; remove 'symbol'."),
            $"The rejection must say the alias target is already passed. Got: {text}");
    }

    [Test]
    public async Task UnlistedUnknownParameter_IsStillRejected()
    {
        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["kind"] = "callers",
                ["notAnAlias"] = "x",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        Assert.That(result.IsError, Is.True);
        Assert.That(TextOf(result), Does.Contain("notAnAlias"));
    }

    [TestCase("log", "target", "ref")]
    [TestCase("show", "commitHash", "ref")]
    [TestCase("log", "files", "paths")]
    [TestCase("reset", "branchName", "ref")]
    public async Task GitAlias_IsRenamed_AndNoteNamesTheDeclaredParameter(string operation, string alias, string canonical)
    {
        // Slice 6a folded Git's target/commitHash into ref and files into paths. The repo state does not
        // matter here: the rename and its note happen before the tool runs, so even a git failure carries them.
        // TODO repo state DOES matter for operation 'show': 'show HEAD' returns the HEAD patch, so a HEAD commit whose diff contains the text "Unknown parameter" fails the Not.Contain assert below; use a fixture repo or a ref with a known diff.
        var result = await _client.CallToolAsync(
            "Git",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["operation"] = operation,
                [alias] = "HEAD",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(text, Does.Not.Contain("Unknown parameter"), $"The alias '{alias}' must be treated as '{canonical}'. Got: {text}");
        Assert.That(text, Does.Contain($"'{alias}' is not a parameter of Git"), $"The response must carry a note about the alias. Got: {text}");
        Assert.That(text, Does.Contain($"treated as '{canonical}'"), $"The note must name the declared parameter. Got: {text}");
    }

    [TestCase("target", "ref")]
    [TestCase("commitHash", "ref")]
    [TestCase("files", "paths")]
    [TestCase("branchName", "ref")]
    public async Task GitAlias_AlongsideItsDeclaredParameter_IsRejectedAsUnknown(string alias, string canonical)
    {
        var result = await _client.CallToolAsync(
            "Git",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["operation"] = "log",
                [canonical] = "HEAD",
                [alias] = "HEAD~1",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An alias alongside '{canonical}' must be rejected. Got: {text}");
        Assert.That(text, Does.Contain(alias), $"The rejection must name the unrecognised alias. Got: {text}");
        Assert.That(text, Does.Not.Contain("treated as"), "No rename may have happened.");
    }

    [TestCase("tag", "target", "ref")]
    [TestCase("tag", "commitHash", "ref")]
    [TestCase("stash", "files", "paths")]
    [TestCase("tag", "maxCount", "count")]
    public async Task GitRemoteAlias_IsRenamed_AndNoteNamesTheDeclaredParameter(string operation, string alias, string canonical)
    {
        // Slice 6b moved tag/stash to GitRemote, which keeps the same aliases Git has so a caller's habit survives the move.
        var result = await _client.CallToolAsync(
            "GitRemote",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["operation"] = operation,
                [alias] = alias == "maxCount" ? 5 : "HEAD",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(text, Does.Not.Contain("Unknown parameter"), $"The alias '{alias}' must be treated as '{canonical}'. Got: {text}");
        Assert.That(text, Does.Contain($"'{alias}' is not a parameter of GitRemote"), $"The response must carry a note about the alias. Got: {text}");
        Assert.That(text, Does.Contain($"treated as '{canonical}'"), $"The note must name the declared parameter. Got: {text}");
    }

    [Test]
    public void TypedMemberClassNameAlias_IsInTheAliasTable_AsMemberHasIt()
    {
        // Slice 7 moved addTypedMember out of Member into TypedMember, which keeps Member's className habit. The
        // fixture hosts only the Workspace mode (no structural refactoring tools), so the table is asserted directly;
        // EveryAliasEntry_ForAnActiveTool_... below guards entries of tools that are active here.
        Assert.That(ToolArgumentValidator.ParameterAliases["TypedMember"]["className"], Is.EqualTo("containerName"));
        Assert.That(ToolArgumentValidator.ParameterAliases["Member"]["className"], Is.EqualTo("containerName"));
    }

    [Test]
    public void ReplaceSnippetSnippetAliases_IsInTheAliasTable_MappedToContentParams()
    {
        // ReplaceSnippet declares oldContent/newContent as top-level params; the snippet-named habit maps onto them.
        Assert.That(ToolArgumentValidator.ParameterAliases["ReplaceSnippet"]["oldSnippet"], Is.EqualTo("oldContent"));
        Assert.That(ToolArgumentValidator.ParameterAliases["ReplaceSnippet"]["newSnippet"], Is.EqualTo("newContent"));
    }

    [Test]
    public void ReplaceSnippetStringAliases_AreInTheAliasTable()
    {
        // Callers also reach ReplaceSnippet's oldContent/newContent as oldString/newString.
        Assert.That(ToolArgumentValidator.ParameterAliases["ReplaceSnippet"]["oldString"], Is.EqualTo("oldContent"));
        Assert.That(ToolArgumentValidator.ParameterAliases["ReplaceSnippet"]["newString"], Is.EqualTo("newContent"));
    }

    [Test]
    public async Task GitPathAlias_IsRenamedToPaths_AndNoteNamesPaths()
    {
        // path -> paths and limit -> count, end to end. The renames and their notes happen before the tool runs,
        // so the repo state does not matter for these assertions.
        var result = await _client.CallToolAsync(
            "Git",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["operation"] = "log",
                ["path"] = _seededFilePath,
                ["limit"] = "2",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(text, Does.Not.Contain("Unknown parameter"), $"'path' and 'limit' must be treated as 'paths' and 'count'. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'paths'"), $"The note must name 'paths'. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'count'"), $"The note must name 'count'. Got: {text}");
    }

    [Test]
    [Category("ToolArgumentValidator")] // sentinel:auto-category
    public async Task EveryAliasEntry_ForAnActiveTool_PointsAtADeclaredParameter_AndIsNotItselfDeclared()
    {
        // Guards the table against drift: an alias that became a real parameter (declared meaning
        // wins, so the entry is dead) or whose target was renamed/removed (stale) fails here.
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var declaredByTool = tools.ToDictionary(
            t => t.Name,
            t => t.JsonSchema.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        var checkedEntries = 0;
        foreach (var (toolName, aliases) in ToolArgumentValidator.ParameterAliases)
        {
            if (!declaredByTool.TryGetValue(toolName, out var declared))
                continue; // tool not registered under the modes this fixture activates

            foreach (var (alias, canonical) in aliases)
            {
                Assert.That(declared, Does.Contain(canonical),
                    $"{toolName}: alias '{alias}' targets '{canonical}', which is not a declared parameter.");
                Assert.That(declared, Does.Not.Contain(alias),
                    $"{toolName}: alias '{alias}' is itself a declared parameter, so the entry is dead or wrong.");
                checkedEntries++;
            }
        }

        Assert.That(checkedEntries, Is.GreaterThan(0), "No alias entry matched an active tool - the check was vacuous.");
    }

    [Test]
    public async Task FilePathSnakeCase_OnReadFile_IsRenamedWithAStandardNote()
    {
        // file_path is the built-in Read/Edit habit; the global table maps it to filePath on any tool that declares filePath.
        var result = await _client.CallToolAsync(
            "ReadFile",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["file_path"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"'file_path' must be treated as 'filePath'. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain("'file_path' is not a parameter of ReadFile"), $"The note must name the alias. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'filePath'"), $"The note must name the declared parameter. Got: {text}");
    }

    [Test]
    public async Task FilePathSnakeCase_OnAToolWithoutFilePath_IsStillRejected()
    {
        // Git does not declare filePath, so the global row must not invent one: the key stays unknown.
        var result = await _client.CallToolAsync(
            "Git",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["operation"] = "log",
                ["file_path"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"'file_path' on a tool without filePath must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("file_path"));
        Assert.That(text, Does.Not.Contain("treated as"), "No rename may have happened.");
    }

    [Test]
    public async Task FilePathSnakeCase_WithFilePathAlsoSupplied_IsLeftForValidate()
    {
        // Same rule as the per-tool aliases: renaming onto a supplied filePath would silently drop one of two values.
        var result = await _client.CallToolAsync(
            "ReadFile",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["filePath"] = _seededFilePath,
                ["file_path"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"'file_path' alongside filePath must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("file_path"));
        Assert.That(text, Does.Not.Contain("treated as"), "No rename may have happened.");
        // Global-table variant of the same rejection wording.
        Assert.That(text, Does.Contain("'file_path' means 'filePath', which this call already passes; remove 'file_path'."),
            $"The rejection must say the alias target is already passed. Got: {text}");
    }

    [Test]
    public async Task RejectionDoesNotSuggestAnAlreadySuppliedName()
    {
        // 'reason2' is one edit from 'reason', but the call already passes 'reason': telling the caller that
        // reason2 means reason would invite a duplicate, not a fix.
        var result = await _client.CallToolAsync(
            "Member",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["reason2"] = "second reason",
                ["operation"] = "view",
                ["filePath"] = _seededFilePath,
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An unknown parameter must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("reason2"));
        Assert.That(text, Does.Not.Contain("Did you mean 'reason'"), $"An already supplied name must not be suggested. Got: {text}");
    }

    [Test]
    public async Task RejectionSuggestsByTokenOverlap()
    {
        // Too far from 'filter' for edit distance, but it shares the token 'filter' with exactly one declared name.
        var result = await _client.CallToolAsync(
            "RunTest",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["testFilterExpressionX"] = "FullyQualifiedName~Foo",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An unknown parameter must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("Did you mean 'filter' instead of 'testFilterExpressionX'?"), $"Got: {text}");
    }

    [Test]
    public async Task Search_GrepShapedCall_RunsAndNotesEveryTranslation()
    {
        // The grep habit: pattern (alias of query), output_mode and type (ignorable), head_limit (maxResults).
        var result = await _client.CallToolAsync(
            "Search",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["mode"] = "text",
                ["pattern"] = "Caller",
                ["output_mode"] = "content",
                ["head_limit"] = "5",
                ["type"] = "cs",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"A grep-shaped Search must run. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain("Caller"), $"Search must have run with the pattern as its query. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'query'"), $"The pattern note must name query. Got: {text}");
        Assert.That(text, Does.Contain("maxResults"), $"The head_limit note must name maxResults. Got: {text}");
        Assert.That(text, Does.Contain("'output_mode' is not a parameter of Search"), $"The output_mode ignore note is missing. Got: {text}");
        Assert.That(text, Does.Contain("'type' is not a parameter of Search"), $"The type ignore note is missing. Got: {text}");
    }

    [Test]
    public async Task ReadFile_ReadShapedCall_ReturnsTheRequestedLines()
    {
        // The built-in Read habit: file_path, offset (first line), limit (line count).
        var result = await _client.CallToolAsync(
            "ReadFile",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["file_path"] = _seededFilePath,
                ["offset"] = "1",
                ["limit"] = "2",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"A Read-shaped ReadFile must run. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain("public class ParameterAliasProbe"), $"Lines 1-2 must be returned. Got: {text}");
        Assert.That(text, Does.Not.Contain("public void Target"), $"Line 4 lies outside lines 1-2. Got: {text}");
        Assert.That(text, Does.Contain("treated as 'filePath'"), $"The file_path note is missing. Got: {text}");
        Assert.That(text, Does.Contain("startLine: 1, endLine: 2"), $"The offset/limit note must give the translated range. Got: {text}");
    }

    [Test]
    public async Task Search_TextPath_AbsoluteDirectory_RestrictsToThatDirectoryAndNotesTheGlob()
    {
        // The same token sits in ContosoOrders.Core/ and ContosoOrders.Core/Other/; an absolute path to the second
        // folder must become a fileGlob so only that folder's hit comes back (not a widened whole-solution search).
        var result = await _client.CallToolAsync(
            "Search",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["mode"] = "text",
                ["query"] = SharedToken,
                ["path"] = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", OtherFolderName),
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"A text Search with an absolute directory path must run. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain("ParameterAliasOther.cs"), $"The seeded hit inside the folder must come back. Got: {text}");
        Assert.That(text, Does.Not.Contain("ParameterAliasProbe.cs"), $"The hit outside the folder must be excluded. Got: {text}");
        Assert.That(text, Does.Contain("fileGlob: \"ContosoOrders.Core/" + OtherFolderName + "/**\""), $"A note must carry the exact glob. Got: {text}");
    }

    [Test]
    public async Task GetMethodSource_ContainerName_RunsWithoutARejection()
    {
        // containerName is a declared GetMethodSource parameter (Step 4); an end-to-end call proves it is in the
        // emitted schema the validator reads, not just in the C# signature.
        var result = await _client.CallToolAsync(
            "GetMethodSource",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["filePath"] = _seededFilePath,
                ["methodName"] = TargetMethodName,
                ["containerName"] = "ParameterAliasProbe",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.Not.True, $"containerName must be accepted. Got: {text}");
        Assert.That(text, Does.Not.Contain("Unknown parameter"));
        Assert.That(text, Does.Contain(TargetMethodName), $"The method source must be returned. Got: {text}");
    }

    [Test]
    public async Task RejectionWithAppliedRenames_SaysTheCallDidNotRun()
    {
        // 'pattern' is renamed to 'query' but 'bogusParam' is unknown, so the call is rejected: the trailing notes
        // describe a correction that never ran, and the rejection must say so.
        var result = await _client.CallToolAsync(
            "Search",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["mode"] = "text",
                ["pattern"] = "Caller",
                ["bogusParam"] = "x",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An unknown parameter must still reject the call. Got: {text}");
        Assert.That(text, Does.Contain("bogusParam"));
        Assert.That(text, Does.Contain("treated as 'query'"), $"The rename note must still be appended. Got: {text}");
        Assert.That(text, Does.Contain("did not run"), $"The rejection must say the call did not run. Got: {text}");
    }

    [Test]
    public async Task CreateFileContent_Rejection_RedirectsToACallThatExists()
    {
        // The redirect must name only a call that exists in the tool list this server actually exposes.
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        var writeFileRegistered = tools.Any(t => t.Name == "WriteFile");

        var result = await _client.CallToolAsync(
            "CreateFile",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["filePath"] = Path.Combine(_fixture.SolutionDirectory, "ContosoOrders.Core", "Made.cs"),
                ["content"] = "public class Made { }",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"'content' is not a CreateFile parameter. Got: {text}");
        Assert.That(text, Does.Contain("CreateFile only creates a seeded file and takes no content."), $"Got: {text}");
        if (writeFileRegistered)
        {
            Assert.That(text, Does.Contain("WriteFile(reason:"), $"Got: {text}");
            Assert.That(text, Does.Contain("operation: CreateFile"), $"Got: {text}");
        }
        else
        {
            Assert.That(text, Does.Not.Contain("WriteFile("), $"WriteFile is not registered, so it must not be named. Got: {text}");
            Assert.That(text, Does.Contain("Member(reason:"), $"Got: {text}");
            Assert.That(text, Does.Contain("operation: addMember"), $"Got: {text}");
        }
    }

    [Test]
    public void UnknownParameterHint_CreateFileContent_NamesWriteFileOnlyWhenItIsRegistered()
    {
        var arguments = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["filePath"] = System.Text.Json.JsonSerializer.SerializeToElement("Foo.cs"),
            ["typeName"] = System.Text.Json.JsonSerializer.SerializeToElement("Foo"),
        };

        var withWriteFile = ToolArgumentValidator.GetUnknownParameterHint("CreateFile", "content", arguments, name => name == "WriteFile");
        Assert.That(withWriteFile, Does.Contain("WriteFile(reason:"));
        Assert.That(withWriteFile, Does.Contain("operation: CreateFile"));
        Assert.That(withWriteFile, Does.Contain("filePath: \"Foo.cs\""));
        Assert.That(withWriteFile, Does.Not.Contain("Member("));

        var withoutWriteFile = ToolArgumentValidator.GetUnknownParameterHint("CreateFile", "content", arguments, _ => false);
        Assert.That(withoutWriteFile, Does.Contain("Member(reason:"));
        Assert.That(withoutWriteFile, Does.Contain("operation: addMember"));
        Assert.That(withoutWriteFile, Does.Contain("containerName: \"Foo\""));
        Assert.That(withoutWriteFile, Does.Not.Contain("WriteFile("));

        // Case-insensitive key, and a literal placeholder when the value was not supplied.
        var placeholder = ToolArgumentValidator.GetUnknownParameterHint("createfile", "CONTENT", new Dictionary<string, System.Text.Json.JsonElement>(), _ => true);
        Assert.That(placeholder, Does.Contain("filePath: "));
        Assert.That(placeholder, Does.Contain("<filePath>"));
    }

    [Test]
    public void UnknownParameterHint_BuildFullBuild_OnlyForFalse()
    {
        static Dictionary<string, System.Text.Json.JsonElement> With(bool value) =>
            new() { ["fullBuild"] = System.Text.Json.JsonSerializer.SerializeToElement(value) };

        Assert.That(ToolArgumentValidator.GetUnknownParameterHint("Build", "fullBuild", With(false), _ => false), Does.Contain("fullBuildThrowaway"));
        Assert.That(ToolArgumentValidator.GetUnknownParameterHint("Build", "fullBuild", With(true), _ => false), Is.Null);
    }

    [Test]
    public async Task SearchTextPathUnmappable_Rejection_ShowsTheFileGlobHint()
    {
        // '..' makes the path unmappable, so the rewrite leaves 'path' and Validate rejects it with the hint.
        var result = await _client.CallToolAsync(
            "Search",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["mode"] = "text",
                ["query"] = "Caller",
                ["path"] = "..\\x",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True, $"An unmappable path must be rejected. Got: {text}");
        Assert.That(text, Does.Contain("Unknown parameter 'path'"), $"Got: {text}");
        Assert.That(text, Does.Contain("could not be turned into a fileGlob"), $"The hint must follow the rejection. Got: {text}");
        Assert.That(text, Does.Contain("restrict files with fileGlob"), $"Got: {text}");
        Assert.That(text.IndexOf("could not be turned into a fileGlob", StringComparison.Ordinal),
            Is.LessThan(text.IndexOf("This tool accepts only", StringComparison.Ordinal)),
            "The hint must come before the accepted-parameters list.");
    }

    [Test]
    public async Task RejectionWithoutAHint_IsUnchanged()
    {
        Assert.That(
            ToolArgumentValidator.GetUnknownParameterHint("FindReferences", "notAnAlias", new Dictionary<string, System.Text.Json.JsonElement>(), _ => true),
            Is.Null,
            "A (tool, parameter) pair without a table entry has no hint.");

        var result = await _client.CallToolAsync(
            "FindReferences",
            new Dictionary<string, object?>
            {
                ["reason"] = "test message",
                ["symbolName"] = TargetMethodName,
                ["kind"] = "callers",
                ["notAnAlias"] = "x",
            }!,
            cancellationToken: TestContext.CurrentContext.CancellationToken);

        var text = TextOf(result);
        Assert.That(result.IsError, Is.True);
        Assert.That(text, Does.Contain("notAnAlias"));
        Assert.That(text, Does.Not.Contain("WriteFile").And.Not.Contain("fileGlob").And.Not.Contain("For a line range"),
            $"No hint text may appear for a pair without an entry. Got: {text}");
    }

    [Test]
    [Category("ToolArgumentValidator")] // sentinel:auto-category
    public async Task GlobalAliasKey_IsNeverDeclaredByAnyActiveTool()
    {
        // A global key that became a real parameter on any tool is dead there; a global target that no tool
        // declares would invent a parameter. Both fail here.
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.CurrentContext.CancellationToken);
        Assert.That(tools, Is.Not.Empty, "No tools listed - the check would be vacuous.");

        var declaredByTool = tools.ToDictionary(
            t => t.Name,
            t => t.JsonSchema.TryGetProperty("properties", out var props)
                ? props.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var (alias, canonical) in ToolArgumentValidator.GlobalParameterAliases)
        {
            Assert.That(declaredByTool.Values.Any(d => d.Contains(canonical)), Is.True,
                $"Global alias '{alias}' targets '{canonical}', which no active tool declares.");

            foreach (var (toolName, declared) in declaredByTool)
            {
                Assert.That(declared, Does.Not.Contain(alias),
                    $"{toolName} declares '{alias}', a global alias key, so the alias is dead or wrong.");
            }
        }
    }
}
