using Microsoft.Extensions.Logging.Abstractions;

using RoslynSentinel.Common;
using RoslynSentinel.Engines.Advanced;

#pragma warning disable CS8618

namespace RoslynSentinel.Tests.Advanced;

/// <summary>
/// Byte-exact tests for the NEW files produced by <see cref="StructuralRefactoringEngine.ConvertTupleToClassAsync"/>
/// and <see cref="StructuralRefactoringEngine.ConvertAnonymousToNamedAsync"/> (slice C3-G3b of
/// docs/current/proposals/proposal_enforce_normalize_whole_subtree_whitespace_rule.md). The generated class and its
/// file root are built from scratch and normalized with the document's own line ending. Sources use explicit
/// "\n" / "\r\n" / "\r" so the tests do not depend on this file's own line endings. The LF cases were first run
/// against the unmodified engine (characterization); the CRLF cases pin the behaviour change (the new file now takes
/// the document's CRLF instead of the old hard-coded "\n").
/// </summary>
[TestFixture]
[Category("StructuralRefactoringEngine")]
public class ConvertToClassNewNodeTests
{
    private IWorkspaceManager _workspaceManager;
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

    private async Task<string> AnonymousClassFileAsync(string source)
    {
        var solution = TestSolutionBuilder.CreateSolutionWithProject("TestProj", [("Test.cs", source)]);
        _workspaceManager.SetTestSolution(solution);
        var result = await _engine.ConvertAnonymousToNamedAsync("Test.cs", "ItemDto");
        Assert.That(result, Has.Count.EqualTo(1), "only the new class file is returned");
        return result.Single(kv => kv.Key.Contains("ItemDto.cs")).Value;
    }

    // ---- ConvertAnonymousToNamed ----

    private const string AnonymousLfSource = "public class Factory\n{\n    public void Build()\n    {\n        var item = new { Name = \"widget\", Quantity = 10 };\n    }\n}\n";
    private const string AnonymousLfExpected = "public class ItemDto\n{\n    public object Name { get; set; }\n    public object Quantity { get; set; }\n}";

    [Test]
    public async Task ConvertAnonymousToNamed_LfDocument_ProducesExactClassFile()
    {
        var content = await AnonymousClassFileAsync(AnonymousLfSource);

        Assert.That(content, Is.EqualTo(AnonymousLfExpected));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_UnnamedInitializer_UsesPropName()
    {
        var content = await AnonymousClassFileAsync("public class Factory\n{\n    public void Build(int x)\n    {\n        var item = new { x };\n    }\n}\n");

        Assert.That(content, Is.EqualTo("public class ItemDto\n{\n    public object Prop { get; set; }\n}"));
    }

    [Test]
    public async Task ConvertAnonymousToNamed_CrlfDocument_ProducesClassFileWithCrlfOnly()
    {
        var content = await AnonymousClassFileAsync(AnonymousLfSource.Replace("\n", "\r\n"));

        Assert.That(content, Is.EqualTo(AnonymousLfExpected.Replace("\n", "\r\n")));
        Assert.That(content.Replace("\r\n", string.Empty), Does.Not.Contain("\n"), "no lone line feed may remain in a CRLF document's class file");
    }

    [Test]
    public async Task ConvertAnonymousToNamed_CrOnlyDocument_DoesNotThrowAndUsesLf()
    {
        // A lone CR is not a line ending the new-node helpers accept, so the document's normalizable EOL falls
        // back to "\n"; the write path later converts the changed lines to the file's own dominant ending.
        var content = await AnonymousClassFileAsync(AnonymousLfSource.Replace("\n", "\r"));

        Assert.That(content, Is.EqualTo(AnonymousLfExpected));
    }

    // ---- ConvertTupleToClass ----

    private const string TupleNoNamespaceSource = "public class DataService\n{\n    public (int Id, string Name) GetData() { return (1, \"test\"); }\n}\n";
    private const string TupleNoNamespaceExpected = "public class DataResult\n{\n    public int Id { get; set; }\n    public string Name { get; set; }\n}";

    private const string TupleFileScopedSource = "using System;\nusing System.Collections.Generic;\n\nnamespace Demo;\n\npublic class DataService\n{\n    public (int Id, List<string> Names) GetData() => default;\n}\n";
    private const string TupleFileScopedExpected = "using System;\nusing System.Collections.Generic;\n\nnamespace Demo;\npublic class DataResult\n{\n    public int Id { get; set; }\n    public List<string> Names { get; set; }\n}";

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

        Assert.That(content, Is.EqualTo("using System;\n\nnamespace Acme.Core\n{\n    public class DataResult\n    {\n        public int Id { get; set; }\n        public string Name { get; set; }\n    }\n}"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_HeaderCommentAndDirectiveBeforeUsings_AreKept()
    {
        var content = await TupleClassFileAsync("// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\n\npublic class Svc\n{\n    public (int Id, string? Name) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\npublic class DataResult\n{\n    public int Id { get; set; }\n    public string? Name { get; set; }\n}"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_IrregularTypeSpacing_IsNormalized()
    {
        var content = await TupleClassFileAsync("using System.Collections.Generic;\n\npublic class Svc\n{\n    public (Dictionary<string,   int>   Map,   int    N) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("using System.Collections.Generic;\n\npublic class DataResult\n{\n    public Dictionary<string, int> Map { get; set; }\n    public int N { get; set; }\n}"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_TrailingCommentOnUsing_Characterization()
    {
        var content = await TupleClassFileAsync("using System; // for Console\nusing System.Linq;\n\npublic class Svc\n{\n    public (int Id, string Name) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("using System; // for Console\nusing System.Linq;\n\npublic class DataResult\n{\n    public int Id { get; set; }\n    public string Name { get; set; }\n}"));
    }

    [Test]
    public async Task ConvertTupleToClass_LfDocument_CommentBetweenTypeAndName_Characterization()
    {
        var content = await TupleClassFileAsync("public class Svc\n{\n    public (int /* id */ Id, string Name) GetData() => default;\n}\n");

        Assert.That(content, Is.EqualTo("public class DataResult\n{\n    public int /* id */ Id { get; set; }\n    public string Name { get; set; }\n}"));
    }

    [Test]
    public async Task PinnedUntilC6_ConvertTupleToClass_CrlfDocument_ClassFileIsStillLfOnly()
    {
        // The class file root is not migrated (see the comment in ConvertTupleToClassAsync): it still goes through the raw
        // helper with its "\n" default, so a CRLF document gets an LF-only new file. This pin documents that gap; flip it to
        // expect CRLF (TupleFileScopedExpected.Replace("\n", "\r\n")) when C6 migrates the site.
        var content = await TupleClassFileAsync(TupleFileScopedSource.Replace("\n", "\r\n"));

        Assert.That(content, Is.EqualTo(TupleFileScopedExpected));
    }

    [Test]
    public async Task PinnedUntilC6_ConvertTupleToClass_CrlfDocumentWithHeader_KeepsHeaderButIsLfOnly()
    {
        var content = await TupleClassFileAsync("// Copyright (c) Acme\r\n#nullable enable\r\nusing System;\r\n\r\nnamespace Demo;\r\n\r\npublic class Svc\r\n{\r\n    public (int Id, string? Name) GetData() => default;\r\n}\r\n");

        Assert.That(content, Is.EqualTo("// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\npublic class DataResult\n{\n    public int Id { get; set; }\n    public string? Name { get; set; }\n}"));
    }

    [Test]
    public async Task ConvertTupleToClass_CrOnlyDocumentWithHeader_DoesNotThrowAndKeepsHeader()
    {
        var content = await TupleClassFileAsync("// Copyright (c) Acme\r#nullable enable\rusing System;\r\rnamespace Demo;\r\rpublic class Svc\r{\r    public (int Id, string? Name) GetData() => default;\r}\r");

        Assert.That(content, Is.EqualTo("// Copyright (c) Acme\n#nullable enable\nusing System;\n\nnamespace Demo;\npublic class DataResult\n{\n    public int Id { get; set; }\n    public string? Name { get; set; }\n}"));
    }
}
