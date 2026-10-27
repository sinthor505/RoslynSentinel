using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Engines.Basic;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Basic;

/// <summary>
/// Engine-level tests for the generated interface file of <see cref="BasicRefactoringEngine.ExtractInterfaceAsync"/>
/// (slice C3-G3a of docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md): the
/// interface file root is built from scratch and formatted by RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync
/// (slice C8): the copied using directives stay byte-exact (header, trailing comments, own line endings) and the new
/// interface structure takes the resolved line ending (IWorkspaceMutator.ResolveEol), ending with a line break.
/// Sources use explicit "\n" / "\r\n" so the tests do not depend on this file's own line endings.
/// </summary>
[TestFixture]
[Category("BasicRefactoringEngine")]
public class ExtractInterfaceNewRootTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private BasicRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new BasicRefactoringEngine(
            _workspaceManager,
            NullLogger<BasicRefactoringEngine>.Instance,
            new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private async Task<string> ExtractInterfaceFileAsync(string source, string className, string interfaceName)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ExtractInterfaceAsync("Source.cs", className, interfaceName);
        Assert.That(result, Has.Count.EqualTo(2), "the original file and the interface file are returned");
        var content = result.Single(kv => kv.Key != "Source.cs").Value;
        TestContext.Out.WriteLine(content.Replace("\r", "\\r").Replace("\n", "\\n\n"));
        return content;
    }

    [Test]
    public async Task ExtractInterface_MissingClass_ThrowsNotFound()
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Source.cs", "namespace N;\npublic class Order { }\n")]);
        _workspaceManager.SetTestSolution(solution);

        var ex = await Assert.ThrowsAsync<ToolNotFoundException>(async () =>
            await _engine.ExtractInterfaceAsync("Source.cs", "Missing", "IMissing"));
        Assert.That(ex!.MissingTargetName, Is.EqualTo("Missing"));
    }

    [Test]
    public async Task ExtractInterface_LfDocument_FileScopedNamespace_ProducesExactInterfaceFile()
    {
        const string source = "using System;\nusing System.Collections.Generic;\n\nnamespace MyApp.Services;\n\npublic class OrderService\n{\n    public List<string> GetOrders() => new();\n    public int Count { get; set; }\n    public void ProcessOrder(string id) { }\n}\n";

        var content = await ExtractInterfaceFileAsync(source, "OrderService", "IOrderService");

        Assert.That(content, Is.EqualTo(
            "using System;\nusing System.Collections.Generic;\n\nnamespace MyApp.Services;\n\npublic interface IOrderService\n{\n    int Count { get; set; }\n\n    List<string> GetOrders();\n    void ProcessOrder(string id);\n}\n"));
    }

    [Test]
    public async Task ExtractInterface_LfDocument_BlockNamespaceAndOddSignatureSpacing_ProducesExactInterfaceFile()
    {
        // The namespace name carries a trailing end-of-line and the signature has irregular spacing: the signature parts
        // are stripped and re-laid out by the formatter, which keeps the constraint clause on the declaration line.
        const string source = "using System;\n\nnamespace Acme.Core\n{\n    public class Repo\n    {\n        public T  Echo<T>(  T   value ,\n            int   n )   where T : class => value;\n        public Dictionary<string,   int> Map { get; set; }\n    }\n}\n";

        var content = await ExtractInterfaceFileAsync(source, "Repo", "IRepo");

        Assert.That(content, Is.EqualTo(
            "using System;\n\nnamespace Acme.Core\n{\n    public interface IRepo\n    {\n        Dictionary<string, int> Map { get; set; }\n\n        T Echo<T>(T value, int n) where T : class;\n    }\n}\n"));
    }

    [Test]
    public async Task ExtractInterface_LfDocument_HeaderCommentAndDirectiveBeforeUsings_AreKept()
    {
        const string source = "// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace MyApp;\n\npublic class Svc\n{\n    public string? Name { get; set; }\n}\n";

        var content = await ExtractInterfaceFileAsync(source, "Svc", "ISvc");

        Assert.That(content, Is.EqualTo(
            "// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace MyApp;\n\npublic interface ISvc\n{\n    string? Name { get; set; }\n}\n"));
    }

    [Test]
    public async Task ExtractInterface_CrlfDocument_ProducesInterfaceFileWithCrlfOnly()
    {
        const string expectedLf = "using System;\nusing System.Collections.Generic;\n\nnamespace MyApp.Services;\n\npublic interface IOrderService\n{\n    int Count { get; set; }\n\n    List<string> GetOrders();\n    void ProcessOrder(string id);\n}\n";
        const string sourceLf = "using System;\nusing System.Collections.Generic;\n\nnamespace MyApp.Services;\n\npublic class OrderService\n{\n    public List<string> GetOrders() => new();\n    public int Count { get; set; }\n    public void ProcessOrder(string id) { }\n}\n";

        var content = await ExtractInterfaceFileAsync(sourceLf.Replace("\n", "\r\n"), "OrderService", "IOrderService");

        Assert.That(content, Is.EqualTo(expectedLf.Replace("\n", "\r\n")));
        Assert.That(content.Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "no lone line feed may remain in a CRLF document's interface file");
    }

    [Test]
    public async Task ExtractInterface_CrOnlyDocumentWithHeader_DoesNotThrowAndKeepsHeader()
    {
        // A CR-only document: the copied usings keep their CR terminators byte-exact and the new interface structure takes the
        // resolved line ending (CRLF here); the chokepoint converts the whole file on write.
        const string source = "// Copyright (c) Acme\r#nullable enable\rusing System;\r\rnamespace MyApp;\r\rpublic class Svc\r{\r    public string? Name { get; set; }\r}\r";

        _workspaceManager.EolModeOverride = EolMode.Crlf;
        var content = await ExtractInterfaceFileAsync(source, "Svc", "ISvc");

        Assert.That(content, Is.EqualTo(
            "// Copyright (c) Acme\r#nullable enable\rusing System;\r\rnamespace MyApp;\r\rpublic interface ISvc\r\n{\r    string? Name { get; set; }\r\n}\r\n"));
    }

    [Test]
    public async Task ExtractInterface_CrlfDocumentWithHeaderAndSignatureComments_KeepsHeaderAndDropsSignatureComments()
    {
        // Documented behaviour: comments inside a copied signature (parameter list, constraint list) are dropped
        // because the signature parts are stripped of all trivia; the file header on the usings is kept.
        const string source = "// Copyright (c) Acme\r\nusing System;\r\n\r\nnamespace MyApp;\r\n\r\npublic class Svc\r\n{\r\n    public int Add(int a /* left */, int b) => a + b; // sum\r\n}\r\n";

        var content = await ExtractInterfaceFileAsync(source, "Svc", "ISvc");

        Assert.That(content, Is.EqualTo(
            "// Copyright (c) Acme\r\nusing System;\r\n\r\nnamespace MyApp;\r\n\r\npublic interface ISvc\r\n{\r\n    int Add(int a, int b);\r\n}\r\n"));
    }

    [Test]
    public async Task ExtractInterface_KeepsMemberDocComments()
    {
        // Source member documentation comments are carried onto the interface members; a comment inside a copied
        // signature is still dropped.
        const string source = "using System;\n\nnamespace MyApp;\n\npublic class Svc\n{\n    /// <summary>Adds numbers.</summary>\n    public int Add(int a /* c */, int b) => a + b;\n\n    /// <summary>The value.</summary>\n    public int Value { get; set; }\n}\n";

        var content = await ExtractInterfaceFileAsync(source, "Svc", "ISvc");

        Assert.That(content, Does.Match(@"/// <summary>Adds numbers\.</summary>\r?\n    int Add\(int a, int b\);"), "method doc comment sits directly above its signature");
        Assert.That(content, Does.Match(@"/// <summary>The value\.</summary>\r?\n    int Value \{ get; set; \}"), "property doc comment sits directly above its signature");
        Assert.That(content, Does.Not.Contain("/* c */"), "comment inside the copied signature is dropped");
    }

    [Test]
    public async Task ExtractInterface_MixedDocument_UsingsStayByteExact_NewStructureTakesDominantEol()
    {
        // The CRLF header line, the using's trailing comment and its inner spacing are kept; the new lines are LF.
        const string source = "// Copyright (c) Acme\r\nusing System; // for Console\nusing  System.Linq;\n\nnamespace MyApp;\n\npublic class Svc\n{\n    public int Total(int a) => a;\r\n    public string Name { get; set; }\n}\n";

        _workspaceManager.EolModeOverride = EolMode.Lf;
        var content = await ExtractInterfaceFileAsync(source, "Svc", "ISvc");

        Assert.That(content, Is.EqualTo(
            "// Copyright (c) Acme\r\nusing System; // for Console\nusing  System.Linq;\n\nnamespace MyApp;\n\npublic interface ISvc\n{\n    string Name { get; set; }\n\n    int Total(int a);\n}\n"));
    }
}
