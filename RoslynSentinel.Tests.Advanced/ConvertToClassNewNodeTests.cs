using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Byte-exact tests for the NEW files produced by <see cref="StructuralRefactoringEngine.ConvertTupleToClassAsync"/>
/// and <see cref="StructuralRefactoringEngine.ConvertAnonymousToNamedAsync"/> (slice C3-G3b of
/// docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md). Since slice C7b both new files go
/// through RoslynFormattingHelper.FormatNewRootKeepingTriviaAsync: the synthesized structure is formatted in the
/// document's dominant line ending and the file ends with one; code embedded from the source (using directives with
/// their header trivia, tuple element types) is kept byte-exact, except the whitespace in front of a new token. Sources
/// use explicit "\n" / "\r\n" / "\r" so the tests do not depend on this file's own line endings.
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")]
public class ConvertToClassNewNodeTests
{
    private PersistentWorkspaceManager _workspaceManager;
    private StructuralRefactoringEngine _engine;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<IWorkspaceManager>.Instance);
        _engine = new StructuralRefactoringEngine(_workspaceManager);
    }

    [TearDown]
    public void TearDown() => _workspaceManager.Dispose();

    private async Task<string> TupleClassFileAsync(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ConvertTupleToClassAsync("Test.cs", "GetData", "DataResult");
        Assert.That(result, Has.Count.EqualTo(2), "the updated original file and the new class file are returned");
        return result.Single(kv => kv.Key.Contains("DataResult.cs")).Value;
    }

    private async Task<(string Original, string ClassFile)> AnonymousFilesAsync(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ConvertAnonymousToNamedAsync("Test.cs", "ItemDto");
        Assert.That(result, Has.Count.EqualTo(2), "the updated original file and the new class file are returned");
        return (result.Single(kv => kv.Key.Contains("Test.cs")).Value, result.Single(kv => kv.Key.Contains("ItemDto.cs")).Value);
    }

    private async Task<string> AnonymousClassFileAsync(string source) => (await AnonymousFilesAsync(source)).ClassFile;

    // ---- ConvertAnonymousToNamedTool ----

    private const string AnonymousLfSource = "public class Factory\n{\n    public void Build()\n    {\n        var item = new { Name = \"widget\", Quantity = 10 };\n    }\n}\n";
    private const string AnonymousLfExpected = "public class ItemDto\n{\n    public string Name { get; set; }\n    public int Quantity { get; set; }\n}\n";
    private const string AnonymousLfOriginalExpected = "public class Factory\n{\n    public void Build()\n    {\n        var item = new ItemDto { Name = \"widget\", Quantity = 10 };\n    }\n}\n";

    [Test]
    public async Task ConvertAnonymousToNamed_LfDocument_ProducesExactClassFile()
    {
        var content = await AnonymousClassFileAsync(AnonymousLfSource);

        Assert.That(content, Is.EqualTo(AnonymousLfExpected));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_LfDocument_RewritesTheOriginalExpression()
    {
        var (original, _) = await AnonymousFilesAsync(AnonymousLfSource);

        Assert.That(original, Is.EqualTo(AnonymousLfOriginalExpected));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_UnnamedInitializer_DerivesThePropertyNameFromTheExpression()
    {
        var (original, content) = await AnonymousFilesAsync("public class Factory\n{\n    public void Build(int x)\n    {\n        var item = new { x };\n    }\n}\n");

        Assert.That(content, Is.EqualTo("public class ItemDto\n{\n    public int X { get; set; }\n}\n"));
        Assert.That(original, Is.EqualTo("public class Factory\n{\n    public void Build(int x)\n    {\n        var item = new ItemDto { X = x };\n    }\n}\n"));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_MemberAccessAndUnnamableTypes_UseMemberNamesAndObjectFallback()
    {
        // this.count -> Count (int); the nested anonymous value cannot be named in a new file, so it stays object.
        var (original, content) = await AnonymousFilesAsync("using System.Collections.Generic;\n\nnamespace Demo;\n\npublic class Factory\n{\n    private int count;\n    private List<string> names = new();\n\n    public void Build()\n    {\n        var item = new { this.count, this.names, Inner = new { A = 1 } };\n    }\n}\n");

        Assert.That(content, Is.EqualTo("using System.Collections.Generic;\n\nnamespace Demo;\n\npublic class ItemDto\n{\n    public int Count { get; set; }\n    public List<string> Names { get; set; }\n    public object Inner { get; set; }\n}\n"));
        Assert.That(original, Does.Contain("new ItemDto { Count = this.count, Names = this.names, Inner = new { A = 1 } }"));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_MultiLineInitializerWithComments_KeepsLayoutAndComments()
    {
        var (original, _) = await AnonymousFilesAsync("public class Factory\n{\n    public void Build()\n    {\n        var item = new\n        {\n            // the name\n            Name = \"widget\", // trailing\n            Quantity = 10\n        };\n    }\n}\n");

        Assert.That(original, Is.EqualTo("public class Factory\n{\n    public void Build()\n    {\n        var item = new ItemDto\n        {\n            // the name\n            Name = \"widget\", // trailing\n            Quantity = 10\n        };\n    }\n}\n"));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_CrlfDocument_ProducesClassFileWithCrlfOnly()
    {
        var (original, content) = await AnonymousFilesAsync(AnonymousLfSource.Replace("\n", "\r\n"));

        Assert.That(content, Is.EqualTo(AnonymousLfExpected.Replace("\n", "\r\n")));
        Assert.That(content.Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "no lone line feed may remain in a CRLF document's class file");
        Assert.That(original, Is.EqualTo(AnonymousLfOriginalExpected.Replace("\n", "\r\n")));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_CrOnlyDocument_DoesNotThrowAndUsesResolvedEol()
    {
        _workspaceManager.EolModeOverride = EolMode.Crlf;
        // A CR-only source: the new file's changed lines take the resolved line ending (CRLF here).
        var content = await AnonymousClassFileAsync(AnonymousLfSource.Replace("\n", "\r"));

        Assert.That(content.Replace("\r\n", "\r"), Is.EqualTo(AnonymousLfExpected.Replace("\n", "\r")), "content unchanged apart from the terminators of the changed lines");
        Assert.That(content, Does.Contain("\r\n"), "changed lines take the resolved line ending");
    }

    // ---- ConvertTupleToClass ----

    private const string TupleNoNamespaceSource = "public class DataService\n{\n    public (int Id, string Name) GetData() { return (1, \"test\"); }\n}\n";
    private const string TupleNoNamespaceExpected = "public class DataResult\n{\n    public int Id { get; set; }\n    public string Name { get; set; }\n}\n";

    private const string TupleHeaderExpected = "// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\n\npublic class DataResult\n{\n    public int Id { get; set; }\n    public string? Name { get; set; }\n}\n";

    private const string TupleFileScopedSource = "using System;\nusing System.Collections.Generic;\n\nnamespace Demo;\n\npublic class DataService\n{\n    public (int Id, List<string> Names) GetData() => default;\n}\n";
    private const string TupleFileScopedExpected = "using System;\nusing System.Collections.Generic;\n\nnamespace Demo;\n\npublic class DataResult\n{\n    public int Id { get; set; }\n    public List<string> Names { get; set; }\n}\n";

    [Test]
    public async Task ConvertTupleToClass_LfDocument_NoUsingsNoNamespace_ProducesExactClassFile()
    {
        var content = await TupleClassFileAsync(TupleNoNamespaceSource);

        Assert.That(content, Is.EqualTo(TupleNoNamespaceExpected));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_FileScopedNamespaceAndUsings_ProducesExactClassFile()
    {
        var content = await TupleClassFileAsync(TupleFileScopedSource);

        Assert.That(content, Is.EqualTo(TupleFileScopedExpected));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_BlockNamespace_ProducesExactClassFile()
    {
        // The namespace name carries a trailing end-of-line in the source: existing formatting that must not
        // reach the new-root guard.
        var content = await TupleClassFileAsync("using System;\n\nnamespace Acme.Core\n{\n    public class Svc\n    {\n        public (int Id, string Name) GetData() => default;\n    }\n}\n");

        Assert.That(content, Is.EqualTo("using System;\n\nnamespace Acme.Core\n{\n    public class DataResult\n    {\n        public int Id { get; set; }\n        public string Name { get; set; }\n    }\n}\n"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_HeaderCommentAndDirectiveBeforeUsings_AreKept()
    {
        var content = await TupleClassFileAsync("// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public (int Id, string? Name) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo(TupleHeaderExpected));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_IrregularTypeSpacing_IsKeptInsideTheType()
    {
        // The element type is embedded byte-exact; only the whitespace in front of the new property name is formatted.
        var content = await TupleClassFileAsync("using System.Collections.Generic;\n\npublic class Svc\n{\n    public (Dictionary<string,   int>   Map,   int    N) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("using System.Collections.Generic;\n\npublic class DataResult\n{\n    public Dictionary<string,   int> Map { get; set; }\n    public int N { get; set; }\n}\n"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_TrailingCommentOnUsing_Characterization()
    {
        var content = await TupleClassFileAsync("using System; // for Console\nusing System.Linq;\n\npublic class Svc\n{\n    public (int Id, string Name) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("using System; // for Console\nusing System.Linq;\n\npublic class DataResult\n{\n    public int Id { get; set; }\n    public string Name { get; set; }\n}\n"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_CommentBetweenTypeAndName_Characterization()
    {
        var content = await TupleClassFileAsync("public class Svc\n{\n    public (int /* id */ Id, string Name) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("public class DataResult\n{\n    public int /* id */ Id { get; set; }\n    public string Name { get; set; }\n}\n"));
    }

    [Test]
    public async Task ConvertTupleToClass_CrlfDocument_ClassFileIsCrlfOnly()
    {
        var content = await TupleClassFileAsync(TupleFileScopedSource.Replace("\n", "\r\n"));

        Assert.That(content, Is.EqualTo(TupleFileScopedExpected.Replace("\n", "\r\n")));
    }

    [Test]
    public async Task ConvertTupleToClass_CrlfDocumentWithHeader_KeepsHeaderAndIsCrlfOnly()
    {
        var content = await TupleClassFileAsync("// Copyright (c) Acme\r\n#nullable enable\r\nusing System;\r\n\r\nnamespace Demo;\r\n\r\npublic class Svc\r\n{\r\n    public (int Id, string? Name) GetData() => default;\r\n}\r\n");

        Assert.That(content, Is.EqualTo(TupleHeaderExpected.Replace("\n", "\r\n")));
    }

    [Test]
    public async Task ConvertTupleToClass_CrOnlyDocumentWithHeader_DoesNotThrowAndKeepsHeader()
    {
        _workspaceManager.EolModeOverride = EolMode.Crlf;
        var content = await TupleClassFileAsync("// Copyright (c) Acme\r#nullable enable\rusing System;\r\rnamespace Demo;\r\rpublic class Svc\r{\r    public (int Id, string? Name) GetData() => default;\r}\r");

        Assert.That(content.Replace("\r\n", "\r"), Is.EqualTo(TupleHeaderExpected.Replace("\n", "\r")), "content unchanged apart from the terminators of the changed lines");
        Assert.That(content, Does.Contain("\r\n"), "changed lines take the resolved line ending");
    }
}
