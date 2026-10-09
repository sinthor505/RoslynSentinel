using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;
using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Battery.Basic;

// ParameterEdit (slice 4a-2) merges MethodSignature and ConstructorParameter behind an 'operation' enum and delegates to
// the same Impl methods, so each operation/action must behave exactly like its original tool on the same input. Every case
// runs the original on one InMemoryWorkspace and ParameterEdit on a second, identical one, then compares outcome and file text.
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Category("ParameterEditTools")] // sentinel:auto-category
[Category("RefactoringSignatureTools")] // sentinel:auto-category
public class ParameterEditToolTests
{
    private const string FixtureRelativePath = "ContosoOrders.Core/ParameterEditFixture.cs";

    private const string FixtureSource = """
    namespace ContosoOrders.Core;

    public class ParameterTarget
    {
        private readonly int _a;

        public ParameterTarget(int a) { _a = a; }

        public void Compute(int first, string second) { }
    }
    """;

    private const string CallerRelativePath = "ContosoOrders.Core/ParameterEditCaller.cs";

    private const string MethodCallerSource = """
    namespace ContosoOrders.Core;

    public class ParameterEditCaller
    {
        public void Call() => new ParameterTarget(1).Compute(1, "x");
    }
    """;

    private sealed record Tools(RefactoringSignatureTools Signature, ParameterEditTools ParameterEdit);

    private static Tools BuildTools(IWorkspaceManager workspaceManager)
    {
        var config = new SentinelConfiguration();
        var diffEngine = new DiffEngine();
        var basicEngine = new BasicRefactoringEngine(workspaceManager, NullLogger<BasicRefactoringEngine>.Instance, config);
        var navigation = new SymbolNavigationEngine(workspaceManager, NullLogger<SymbolNavigationEngine>.Instance);
        var validation = new ValidationEngine(workspaceManager, diffEngine, NullLogger<ValidationEngine>.Instance);
        var memberEngine = new MemberRefactoringEngine(workspaceManager, navigation, validation, config);
        var signatureImpl = new RefactoringSignatureImpl(basicEngine, memberEngine, workspaceManager, validation, navigation, NullLogger<RefactoringSignatureImpl>.Instance);
        return new Tools(new RefactoringSignatureTools(signatureImpl), new ParameterEditTools(signatureImpl));
    }

    // ---- operation 'method' (MethodSignature) ----

    [Test]
    public async Task ParameterEdit_MethodAdd_WithDefault_MatchesMethodSignatureAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.MethodSignature(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, methodName: "Compute",
                paramName: "third", paramType: "int", defaultValue: "3", cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.method, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add,
                methodName: "Compute", paramName: "third", paramType: "int", defaultValue: "3", cancellationToken: default));

        Assert.That(text, Does.Contain("int third = 3"));
    }

    [Test]
    public async Task ParameterEdit_MethodAdd_WithNullDefault_MatchesMethodSignatureAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.MethodSignature(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, methodName: "Compute",
                paramName: "third", paramType: "string", nullDefault: true, cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.method, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add,
                methodName: "Compute", paramName: "third", paramType: "string", nullDefault: true, cancellationToken: default));

        Assert.That(text, Does.Contain("string third = null"));
    }

    [Test]
    public async Task ParameterEdit_MethodRemove_MatchesMethodSignatureAsync()
    {
        // The call site lives in a second file so this parity case does not depend on same-file handling; same-file and multi-site call sites are covered by RemoveMethodParameterSinglePassTests.
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.MethodSignature(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.remove, methodName: "Compute",
                paramName: "second", cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.method, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.remove,
                methodName: "Compute", paramName: "second", cancellationToken: default),
            (CallerRelativePath, MethodCallerSource));

        Assert.That(text, Does.Not.Contain("string second"));
    }

    [Test]
    public async Task ParameterEdit_MethodView_MatchesMethodSignatureAsync()
    {
        await AssertParityAsync(
            (t, ws) => t.Signature.MethodSignature(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.view, methodName: "Compute", cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.method, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.view,
                methodName: "Compute", cancellationToken: default));
    }

    [Test]
    public async Task ParameterEdit_MethodAdd_DryRun_MatchesAndDoesNotWriteAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.MethodSignature(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, methodName: "Compute",
                paramName: "third", paramType: "int", defaultValue: "3", dryRun: true, cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.method, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add,
                methodName: "Compute", paramName: "third", paramType: "int", defaultValue: "3", dryRun: true, cancellationToken: default));

        Assert.That(text, Does.Not.Contain("third"));
    }

    // ---- operation 'constructor' (ConstructorParameter) ----

    [Test]
    public async Task ParameterEdit_ConstructorAdd_MatchesConstructorParameterAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.ConstructorParameter(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, className: "ParameterTarget",
                paramName: "name", paramType: "string", fieldName: "_label", cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.constructor, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add,
                className: "ParameterTarget", paramName: "name", paramType: "string", fieldName: "_label", cancellationToken: default));

        Assert.That(text, Does.Contain("string name").And.Contain("_label"));
    }

    [Test]
    public async Task ParameterEdit_ConstructorAdd_WithCallSiteFixups_MatchesConstructorParameterAsync()
    {
        const string callerRelativePath = "ContosoOrders.Core/ParameterEditCaller.cs";
        const string callerSource = """
        namespace ContosoOrders.Core;

        public class ParameterEditCaller
        {
            public ParameterTarget Make() => new ParameterTarget(1);
        }
        """;

        using var original = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource), (callerRelativePath, callerSource));
        using var merged = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource), (callerRelativePath, callerSource));

        var expected = await BuildTools(original.Manager).Signature.ConstructorParameter(
            reason: "parity original", filePath: original.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, className: "ParameterTarget",
            paramName: "name", paramType: "string", callSiteFixups: new Dictionary<string, string> { ["*"] = "\"n\"" }, cancellationToken: default);
        var actual = await BuildTools(merged.Manager).ParameterEdit.ParameterEdit(
            reason: "parity merged", operation: ParameterEditOperation.constructor, filePath: merged.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add, className: "ParameterTarget",
            paramName: "name", paramType: "string", callSiteFixups: new Dictionary<string, string> { ["*"] = "\"n\"" }, cancellationToken: default);

        AssertSameOutcome(original, merged, expected, actual);
        Assert.That(original.ReadText(callerRelativePath), Is.EqualTo(merged.ReadText(callerRelativePath)));
        Assert.That(merged.ReadText(callerRelativePath), Does.Contain("new ParameterTarget(1, \"n\")"));
    }

    [Test]
    public async Task ParameterEdit_ConstructorRemove_MatchesConstructorParameterAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.ConstructorParameter(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.remove, className: "ParameterTarget",
                paramName: "a", cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.constructor, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.remove,
                className: "ParameterTarget", paramName: "a", cancellationToken: default));

        Assert.That(text, Does.Not.Contain("int a)"));
    }

    [Test]
    public async Task ParameterEdit_ConstructorView_MatchesConstructorParameterAsync()
    {
        await AssertParityAsync(
            (t, ws) => t.Signature.ConstructorParameter(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.view, className: "ParameterTarget", cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.constructor, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.view,
                className: "ParameterTarget", cancellationToken: default));
    }

    [Test]
    public async Task ParameterEdit_ConstructorAdd_DryRun_MatchesAndDoesNotWriteAsync()
    {
        var text = await AssertParityAsync(
            (t, ws) => t.Signature.ConstructorParameter(reason: "parity original", filePath: ws.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, className: "ParameterTarget",
                paramName: "name", paramType: "string", dryRun: true, cancellationToken: default),
            (t, ws) => t.ParameterEdit.ParameterEdit(reason: "parity merged", operation: ParameterEditOperation.constructor, filePath: ws.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add,
                className: "ParameterTarget", paramName: "name", paramType: "string", dryRun: true, cancellationToken: default));

        Assert.That(text, Does.Not.Contain("string name"));
    }

    // ---- validation: wrong param subsets fail by name, before delegation ----

    [Test]
    public async Task ParameterEdit_Method_WithConstructorOnlyParams_RejectsAsInvalidArgumentNamingThemAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).ParameterEdit.ParameterEdit(
            reason: "mixed params", operation: ParameterEditOperation.method, filePath: workspace.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add, methodName: "Compute",
            className: "ParameterTarget", fieldName: "_x", paramName: "third", paramType: "int", cancellationToken: default);

        AssertInvalidArgument(result, "does not take 'className', 'fieldName'");
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Not.Contain("third"));
    }

    [Test]
    public async Task ParameterEdit_Constructor_WithMethodName_RejectsAsInvalidArgumentAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).ParameterEdit.ParameterEdit(
            reason: "mixed params", operation: ParameterEditOperation.constructor, filePath: workspace.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add, className: "ParameterTarget",
            methodName: "Compute", paramName: "name", paramType: "string", cancellationToken: default);

        AssertInvalidArgument(result, "does not take 'methodName'");
        Assert.That(workspace.ReadText(FixtureRelativePath), Does.Not.Contain("string name"));
    }

    [Test]
    public async Task ParameterEdit_Method_WithoutMethodName_RejectsNamingTheMissingParamAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).ParameterEdit.ParameterEdit(
            reason: "missing name", operation: ParameterEditOperation.method, filePath: workspace.PathOf(FixtureRelativePath), action: AddRemoveViewAction.view, cancellationToken: default);

        AssertInvalidArgument(result, "operation 'method' requires 'methodName'");
    }

    [Test]
    public async Task ParameterEdit_Constructor_WithoutClassName_RejectsNamingTheMissingParamAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).ParameterEdit.ParameterEdit(
            reason: "missing name", operation: ParameterEditOperation.constructor, filePath: workspace.PathOf(FixtureRelativePath), action: AddRemoveViewAction.view, cancellationToken: default);

        AssertInvalidArgument(result, "operation 'constructor' requires 'className'");
    }

    [TestCase(AddRemoveViewAction.add, "'paramName' and 'paramType'")]
    [TestCase(AddRemoveViewAction.remove, "'paramName'")]
    public async Task ParameterEdit_MissingParamNameOrType_RejectsNamingThemAsync(AddRemoveViewAction action, string expected)
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));

        var result = await BuildTools(workspace.Manager).ParameterEdit.ParameterEdit(
            reason: "missing params", operation: ParameterEditOperation.method, filePath: workspace.PathOf(FixtureRelativePath), action: action, methodName: "Compute", cancellationToken: default);

        AssertInvalidArgument(result, $"action '{action}' requires {expected}");
    }

    [Test]
    public async Task ParameterEdit_AddWithDefaultValueAndNullDefault_ReturnsTheSameErrorAsMethodSignatureAsync()
    {
        using var workspace = InMemoryWorkspace.Create((FixtureRelativePath, FixtureSource));
        var tools = BuildTools(workspace.Manager);

        var expected = await tools.Signature.MethodSignature(reason: "exclusive defaults", filePath: workspace.PathOf(FixtureRelativePath), operation: AddRemoveViewAction.add, methodName: "Compute",
            paramName: "third", paramType: "string", defaultValue: "\"x\"", nullDefault: true, cancellationToken: default);
        var actual = await tools.ParameterEdit.ParameterEdit(reason: "exclusive defaults", operation: ParameterEditOperation.method, filePath: workspace.PathOf(FixtureRelativePath), action: AddRemoveViewAction.add,
            methodName: "Compute", paramName: "third", paramType: "string", defaultValue: "\"x\"", nullDefault: true, cancellationToken: default);

        Assert.That(!expected.IsError, Is.False);
        Assert.That(!actual.IsError, Is.False);
        Assert.That(actual.ErrorData!.ErrorCode, Is.EqualTo(expected.ErrorData!.ErrorCode));
        Assert.That(actual.ErrorData.Message, Is.EqualTo(expected.ErrorData.Message));
    }

    private static void AssertInvalidArgument(SentinelCallToolResult<object> result, string expectedMessagePart)
    {
        Assert.That(!result.IsError, Is.False);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(result.ErrorData.Message, Does.Contain(expectedMessagePart));
    }

    // Runs the original tool on one InMemoryWorkspace and ParameterEdit on a second identical one, asserts the same outcome
    // and file text, and returns the merged workspace's file text for operation-specific assertions.
    private static async Task<string> AssertParityAsync(
        Func<Tools, InMemoryWorkspace, Task<SentinelCallToolResult<object>>> runOriginal,
        Func<Tools, InMemoryWorkspace, Task<SentinelCallToolResult<object>>> runMerged,
        params (string RelativePath, string Content)[] extraFiles)
    {
        var files = new[] { (FixtureRelativePath, FixtureSource) }.Concat(extraFiles).ToArray();
        using var original = InMemoryWorkspace.Create(files);
        using var merged = InMemoryWorkspace.Create(files);

        var expected = await runOriginal(BuildTools(original.Manager), original);
        var actual = await runMerged(BuildTools(merged.Manager), merged);

        AssertSameOutcome(original, merged, expected, actual);
        foreach (var extra in extraFiles)
        {
            Assert.That(merged.ReadText(extra.RelativePath), Is.EqualTo(original.ReadText(extra.RelativePath)), $"{extra.RelativePath} should be edited identically");
        }

        return merged.ReadText(FixtureRelativePath);
    }

    // The two workspaces differ only in their (GUID) virtual root and in the random per-call ChangeId, so success data is
    // compared as JSON with both masked.
    private static void AssertSameOutcome(InMemoryWorkspace original, InMemoryWorkspace merged, SentinelCallToolResult<object> expected, SentinelCallToolResult<object> actual)
    {
        Assert.That(!expected.IsError, Is.True, expected.ErrorData?.Message);
        Assert.That(!actual.IsError, Is.True, actual.ErrorData?.Message);
        Assert.Multiple(() =>
        {
            Assert.That(Normalize(actual.SuccessData, merged), Is.EqualTo(Normalize(expected.SuccessData, original)));
            Assert.That(merged.ReadText(FixtureRelativePath), Is.EqualTo(original.ReadText(FixtureRelativePath)));
        });
    }

    private static string Normalize(object? data, InMemoryWorkspace workspace)
    {
        string json = data is null ? "null" : JsonSerializer.Serialize(data, data.GetType());
        string root = Path.GetDirectoryName(Path.GetDirectoryName(workspace.PathOf(FixtureRelativePath)))!;
        // The id also appears inside the human-readable message (e.g. "UndoLastApply(changeId: ...)"), so mask every occurrence.
        var changeId = System.Text.RegularExpressions.Regex.Match(json, "\"ChangeId\":\"([^\"]+)\"");
        if (changeId.Success)
        {
            json = json.Replace(changeId.Groups[1].Value, "<ID>", StringComparison.Ordinal);
        }

        return json
            .Replace(JsonEncodedText.Encode(root).ToString(), "<ROOT>", StringComparison.OrdinalIgnoreCase)
            .Replace(root, "<ROOT>", StringComparison.OrdinalIgnoreCase);
    }
}
