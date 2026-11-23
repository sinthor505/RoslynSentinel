using RoslynSentinel.Tests.Fakes;
using RoslynSentinel.Tools.Basic;

namespace RoslynSentinel.Tests.Tools.Advanced;

// RenameSymbol on a namespace (docCommentId "N:..."): newName replaces the LAST segment. In-memory two-project
// workspace (Lib <- App), the same namespace is declared in both projects and in several declaration shapes.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public sealed class RenameNamespaceTests
{
    private const string LibRoot = @"C:\virtual\RenameNamespace\Lib";
    private const string AppRoot = @"C:\virtual\RenameNamespace\App";

    private static string LibFoo => Path.Combine(LibRoot, "Foo.cs");
    private static string LibBar => Path.Combine(LibRoot, "Bar.cs");
    private static string LibInner => Path.Combine(LibRoot, "Inner.cs");
    private static string AppConsumer => Path.Combine(AppRoot, "Consumer.cs");
    private static string AppQualified => Path.Combine(AppRoot, "Qualified.cs");
    private static string AppExtra => Path.Combine(AppRoot, "Extra.cs");

    private static FakeWorkspaceManager CreateWorkspace()
    {
        var lib = new (string name, string content)[]
        {
            // block-scoped declaration
            (LibFoo, "namespace Acme.Basic\r\n{\r\n    public class Foo\r\n    {\r\n        public static int Value => 1;\r\n    }\r\n}\r\n"),
            // file-scoped declaration
            (LibBar, "namespace Acme.Basic;\r\n\r\npublic class Bar\r\n{\r\n}\r\n"),
            // nested child namespace: its qualified name contains the segment being renamed
            (LibInner, "namespace Acme.Basic.Inner\r\n{\r\n    public class Baz\r\n    {\r\n    }\r\n}\r\n"),
        };
        var app = new (string name, string content)[]
        {
            (AppConsumer, "using Acme.Basic;\r\nusing Acme.Basic.Inner;\r\n\r\nnamespace App;\r\n\r\npublic class Consumer\r\n{\r\n    public Foo F = new Foo();\r\n    public Bar B = new Bar();\r\n    public Baz Z = new Baz();\r\n}\r\n"),
            (AppQualified, "namespace App;\r\n\r\npublic class Qualified\r\n{\r\n    public int V => Acme.Basic.Foo.Value;\r\n    public Acme.Basic.Inner.Baz Z = new Acme.Basic.Inner.Baz();\r\n}\r\n"),
            // the same namespace declared again in the referencing project
            (AppExtra, "namespace Acme.Basic\r\n{\r\n    public class AppOnly\r\n    {\r\n        public Foo F = new Foo();\r\n    }\r\n}\r\n"),
        };
        var manager = FakeWorkspaceManager.ReadWrite();
        manager.SetTestSolution(TestSolutionBuilder.CreateTwoProjectSolution("Lib", lib, "App", app));
        return manager;
    }

    private static string ReadText(FakeWorkspaceManager manager, string path)
    {
        var solution = manager.CurrentSolution!;
        var documentId = solution.GetDocumentIdsWithFilePath(path).Single();
        return solution.GetDocument(documentId)!.GetTextAsync().GetAwaiter().GetResult().ToString();
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_Namespace_RewritesEveryDeclarationUsingAndQualifiedReferenceAcrossProjects()
    {
        using var manager = CreateWorkspace();
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(manager);

        var result = await tools.RenameSymbolTool(reason: "rename namespace segment", projectName: "Lib", docCommentId: "N:Acme.Basic", newName: "Advanced");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        foreach (var path in new[] { LibFoo, LibBar, LibInner, AppConsumer, AppQualified, AppExtra })
        {
            var text = ReadText(manager, path);
            Assert.That(text, Does.Contain("Acme.Advanced"), $"{Path.GetFileName(path)} was not rewritten:\n{text}");
            Assert.That(text, Does.Not.Contain("Basic"), $"{Path.GetFileName(path)} still mentions the old segment:\n{text}");
        }

        Assert.That(ReadText(manager, LibFoo), Does.Contain("namespace Acme.Advanced\r\n"));
        Assert.That(ReadText(manager, LibBar), Does.Contain("namespace Acme.Advanced;"));
        Assert.That(ReadText(manager, LibInner), Does.Contain("namespace Acme.Advanced.Inner"));
        Assert.That(ReadText(manager, AppConsumer), Does.Contain("using Acme.Advanced;").And.Contain("using Acme.Advanced.Inner;"));
        Assert.That(ReadText(manager, AppQualified), Does.Contain("Acme.Advanced.Foo.Value").And.Contain("Acme.Advanced.Inner.Baz"));
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_Namespace_DottedNewName_IsRejectedWithInvalidArgumentAndChangesNothing()
    {
        using var manager = CreateWorkspace();
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(manager);
        var before = ReadText(manager, LibFoo);

        var result = await tools.RenameSymbolTool(reason: "dotted new name", projectName: "Lib", docCommentId: "N:Acme.Basic", newName: "Acme.Advanced");

        Assert.That(result.IsError, Is.True);
        Assert.That(result.ErrorData!.ErrorCode, Is.EqualTo("InvalidArgument"));
        Assert.That(result.ErrorData.Message, Does.Contain("last segment"));
        Assert.That(ReadText(manager, LibFoo), Is.EqualTo(before));
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_Namespace_DryRun_ChangesNothing()
    {
        using var manager = CreateWorkspace();
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(manager);
        var before = ReadText(manager, AppConsumer);

        var result = await tools.RenameSymbolTool(reason: "dry run namespace rename", projectName: "Lib", docCommentId: "N:Acme.Basic", newName: "Advanced", dryRun: true);

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(ReadText(manager, AppConsumer), Is.EqualTo(before));
    }

    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_Namespace_ReturnsUpdatedHandleForTheRenamedNamespace()
    {
        using var manager = CreateWorkspace();
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(manager);

        var result = await tools.RenameSymbolTool(reason: "updated handle check", projectName: "Lib", docCommentId: "N:Acme.Basic", newName: "Advanced");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        Assert.That(System.Text.Json.JsonSerializer.Serialize(result.SuccessData), Does.Contain("N:Acme.Advanced\""));
    }

    // Pins what happens when the rename is invoked from the DOWNSTREAM project that re-declares the namespace:
    // the upstream declarations must be renamed too, or the solution would no longer compile (the compile gate refuses).
    [Test]
    [Category("RefactoringSignatureTools")]
    public async Task RenameSymbol_Namespace_InvokedFromDownstreamProject_RenamesUpstreamDeclarationsToo()
    {
        using var manager = CreateWorkspace();
        var tools = BatteryToolFactory.CreateRefactoringSignatureTools(manager);

        var result = await tools.RenameSymbolTool(reason: "rename from downstream project", projectName: "App", docCommentId: "N:Acme.Basic", newName: "Advanced");

        Assert.That(result.IsError, Is.False, $"error: {result.ErrorData?.Message}");
        foreach (var path in new[] { LibFoo, LibBar, LibInner, AppConsumer, AppQualified, AppExtra })
        {
            Assert.That(ReadText(manager, path), Does.Not.Contain("Basic"), $"{Path.GetFileName(path)} still mentions the old segment");
        }
    }
}
