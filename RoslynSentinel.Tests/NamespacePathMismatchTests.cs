// Tests for AnalysisEngine.FindNamespacePathMismatchesAsync
// All tests run in-memory via AdhocWorkspace (no MSBuild/project-file loading).

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RoslynSentinel.Server;

namespace RoslynSentinel.Tests;

[TestFixture]
public class NamespacePathMismatchTests
{
    private PersistentWorkspaceManager _workspaceManager = null!;
    private AnalysisEngine _engine = null!;

    [SetUp]
    public void Setup()
    {
        _workspaceManager = new PersistentWorkspaceManager(NullLogger<PersistentWorkspaceManager>.Instance);
        _engine = new AnalysisEngine(_workspaceManager, new SentinelConfiguration());
    }

    [TearDown]
    public void TearDown() => _workspaceManager?.Dispose();

    // ─────────────────────────────────────────────────────────────────────────────
    // Helper — builds a solution where document file paths are ABSOLUTE, so the
    // DeriveExpectedNamespace path-relative logic works correctly.
    // projectName is used as both the project name and the root namespace fallback.
    // documents: (relPath, content) — relPath is relative to the project root.
    // ─────────────────────────────────────────────────────────────────────────────
    private static Solution CreateSolutionWithAbsolutePaths(
        string projectName,
        (string relPath, string content)[] documents)
    {
        // Match the path convention used by TestSolutionBuilder so that projectRoot
        // = Path.GetTempPath()\<projectName> and project.FilePath exists under it.
        var projectRoot = Path.Combine(Path.GetTempPath(), projectName);
        var projectPath = Path.Combine(projectRoot, $"{projectName}.csproj");

        var workspace  = new AdhocWorkspace();
        var projectId  = ProjectId.CreateNewId();

        // Minimal metadata references (same set as TestSolutionBuilder).
        var coreDir    = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>();
        foreach (var name in new[] { "System.Runtime.dll", "System.Private.CoreLib.dll", "mscorlib.dll" })
        {
            var path = Path.Combine(coreDir, name);
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }
        var objectLoc = typeof(object).Assembly.Location;
        if (!references.Any(r => string.Equals(r.Display, objectLoc, StringComparison.OrdinalIgnoreCase)))
            references.Add(MetadataReference.CreateFromFile(objectLoc));

        var projectInfo = ProjectInfo.Create(
                projectId,
                VersionStamp.Default,
                projectName,
                projectName,
                LanguageNames.CSharp)
            .WithMetadataReferences(references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(projectPath)
            .WithDefaultNamespace(projectName);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var (relPath, content) in documents)
        {
            var absolutePath = Path.Combine(projectRoot, relPath);
            var docId        = DocumentId.CreateNewId(projectId, relPath);
            solution = solution.AddDocument(
                docId,
                Path.GetFileName(relPath),
                SourceText.From(content),
                filePath: absolutePath);
        }

        return solution;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 1 — Clean solution: all namespaces match folder paths → IsClean = true
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task CleanSolution_AllNamespacesMatchPaths_IsClean()
    {
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            (@"Greeter.cs",            "namespace TestProj { public class Greeter {} }"),
            (@"Services\MyService.cs", "namespace TestProj.Services { public class MyService {} }"),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.IsClean,       Is.True,  "Expected IsClean=true for a fully consistent solution");
        Assert.That(report.MismatchCount, Is.Zero,  "Expected no mismatches");
        Assert.That(report.Errors,        Is.Empty, "Expected no errors");
        Assert.That(report.Warnings,      Is.Empty, "Expected no warnings");
        Assert.That(report.TotalFiles,    Is.EqualTo(2));
        Assert.That(report.Summary,       Does.Contain("Clean"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 2 — Single NamespaceFolderMismatch warning (no duplicate type found)
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task FolderMismatch_NoConflictingType_ProducesWarning()
    {
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            // File is in Services\ but declares the wrong namespace.
            (@"Services\MyService.cs", "namespace TestProj.WrongFolder { public class MyService {} }"),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.IsClean,  Is.False);
        Assert.That(report.Warnings, Has.Count.EqualTo(1));
        Assert.That(report.Errors,   Is.Empty);

        var w = report.Warnings[0];
        Assert.That(w.Reason,            Is.EqualTo("NamespaceFolderMismatch"));
        Assert.That(w.Severity,          Is.EqualTo("Warning"));
        Assert.That(w.DeclaredNamespace, Is.EqualTo("TestProj.WrongFolder"));
        Assert.That(w.ExpectedNamespace, Is.EqualTo("TestProj.Services"));
        Assert.That(w.ConflictingFiles,  Is.Empty);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 3 — Error: DuplicateTypeAtMismatchedPath
    // FileA is in Services\ but declares TestProj.Orders and type Foo.
    // FileB is elsewhere and declares TestProj.Services with the same type Foo.
    // → FileA should be an Error with ConflictingFiles pointing to FileB.
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task DuplicateTypeAtMismatchedPath_ProducesError()
    {
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            // FileA: physical location says "Services" but declares wrong namespace.
            (@"Services\Foo.cs",    "namespace TestProj.Orders { public class Foo {} }"),
            // FileB: another file that correctly declares TestProj.Services.Foo.
            (@"SomeDir\Shadow.cs",  "namespace TestProj.Services { public class Foo {} }"),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.Errors, Is.Not.Empty, "Expected at least one Error finding");

        var error = report.Errors.Find(e => e.Reason == "DuplicateTypeAtMismatchedPath");
        Assert.That(error, Is.Not.Null, "Expected a DuplicateTypeAtMismatchedPath error");
        Assert.That(error!.Severity,          Is.EqualTo("Error"));
        Assert.That(error.DeclaredNamespace,  Is.EqualTo("TestProj.Orders"));
        Assert.That(error.ExpectedNamespace,  Is.EqualTo("TestProj.Services"));
        Assert.That(error.ConflictingFiles,   Is.Not.Empty, "ConflictingFiles must name the shadow file");
        Assert.That(error.ConflictingFiles,   Has.Some.EndsWith("Shadow.cs"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 4 — Generated files skipped (*.g.cs, *.generated.cs, *.Designer.cs)
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task GeneratedFiles_AreSkipped()
    {
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            (@"Foo.g.cs",          "namespace Wrong.Namespace { public class Foo {} }"),
            (@"Bar.generated.cs",  "namespace Wrong.Namespace { public class Bar {} }"),
            (@"Baz.Designer.cs",   "namespace Wrong.Namespace { public class Baz {} }"),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.IsClean,    Is.True, "Generated files should be skipped entirely");
        Assert.That(report.TotalFiles, Is.Zero, "TotalFiles should not count generated files");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 5 — projectName scope filter: mismatch in ProjectB is not reported when
    //          we scope to ProjectA
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task ProjectScope_FiltersToNamedProject()
    {
        // Build two separate projects and manually compose a solution.
        var projectRoot = Path.Combine(Path.GetTempPath(), "ProjectA");
        var workspace   = new AdhocWorkspace();

        Solution BuildProjectInSolution(Solution sln, string projName, string relFile, string content)
        {
            var root = Path.Combine(Path.GetTempPath(), projName);
            var pid  = ProjectId.CreateNewId();
            var info = ProjectInfo.Create(pid, VersionStamp.Default, projName, projName, LanguageNames.CSharp)
                .WithFilePath(Path.Combine(root, $"{projName}.csproj"))
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            sln = sln.AddProject(info);
            var did = DocumentId.CreateNewId(pid, relFile);
            return sln.AddDocument(did, Path.GetFileName(relFile), SourceText.From(content),
                filePath: Path.Combine(root, relFile));
        }

        var solution = workspace.CurrentSolution;
        solution = BuildProjectInSolution(solution, "ProjectA", @"Clean.cs",
            "namespace ProjectA { public class Clean {} }");
        solution = BuildProjectInSolution(solution, "ProjectB", @"Services\Dirty.cs",
            "namespace ProjectB.Wrong { public class Dirty {} }");

        // Scoped to ProjectA → ProjectB's mismatch should not appear.
        var reportA = await _engine.FindNamespacePathMismatchesAsync(solution, "ProjectA");
        Assert.That(reportA.IsClean, Is.True, "ProjectA is clean; ProjectB mismatch should not appear");

        // No scope → ProjectB mismatch should appear.
        var reportAll = await _engine.FindNamespacePathMismatchesAsync(solution, null);
        Assert.That(reportAll.Warnings, Has.Some.With.Property("ProjectName").EqualTo("ProjectB"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 6 — Multiple namespaces in one file → MultipleNamespacesInFile warning
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task MultipleNamespacesInFile_ProducesWarning()
    {
        var source = """
            namespace TestProj.A { public class Foo {} }
            namespace TestProj.B { public class Bar {} }
            """;
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [(@"Mixed.cs", source)]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.Warnings, Has.Some.With.Property("Reason").EqualTo("MultipleNamespacesInFile"),
            "A file with two namespace blocks should produce a MultipleNamespacesInFile warning");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 7 — Global namespace: no namespace declaration in a project that has a
    //          root namespace (the project name) → GlobalNamespace warning
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task GlobalNamespace_ProducesWarning()
    {
        // File has no namespace declaration; project name "TestProj" implies root NS.
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            (@"GlobalClass.cs", "public class GlobalClass {}"),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.Warnings, Has.Some.With.Property("Reason").EqualTo("GlobalNamespace"),
            "A file with no namespace in a named project should produce a GlobalNamespace warning");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 8 — File-scoped namespace (C# 10+ syntax) is parsed correctly
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task FileScopedNamespace_CleanFile_IsClean()
    {
        // File-scoped namespace that matches the folder path → no warning.
        var source = """
            namespace TestProj.Services;
            public class OrderService {}
            """;
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            (@"Services\OrderService.cs", source),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.IsClean, Is.True,
            "A file-scoped namespace matching the folder path should produce no findings");
    }

    [Test]
    public async Task FileScopedNamespace_Mismatch_ProducesWarning()
    {
        var source = """
            namespace TestProj.WrongPlace;
            public class OrderService {}
            """;
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            (@"Services\OrderService.cs", source),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.IsClean, Is.False,
            "A mismatched file-scoped namespace should still be detected");
        Assert.That(report.Warnings, Has.Some.With.Property("DeclaredNamespace").EqualTo("TestProj.WrongPlace"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 9 — Root namespace fallback: no <RootNamespace> in csproj
    //          (AdhocWorkspace has no real .csproj) → project name used as fallback
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task RootNamespaceFallback_UsesProjectName()
    {
        // Project name is "MyApp". There is no real .csproj on disk, so GetRootNamespace
        // must fall back to project name "MyApp".
        var solution = CreateSolutionWithAbsolutePaths("MyApp", [
            (@"Core\Processor.cs", "namespace MyApp.Core { public class Processor {} }"),
        ]);

        var report = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report.IsClean, Is.True,
            "When root namespace = project name, 'MyApp.Core' should match path 'Core\\'");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Test 10 — Idempotency: calling twice returns identical results
    // ─────────────────────────────────────────────────────────────────────────────
    [Test]
    public async Task Idempotency_TwoCallsReturnSameResults()
    {
        var solution = CreateSolutionWithAbsolutePaths("TestProj", [
            (@"Greeter.cs",            "namespace TestProj { public class Greeter {} }"),
            (@"Services\MyService.cs", "namespace TestProj.WrongFolder { public class MyService {} }"),
        ]);

        var report1 = await _engine.FindNamespacePathMismatchesAsync(solution, null);
        var report2 = await _engine.FindNamespacePathMismatchesAsync(solution, null);

        Assert.That(report1.IsClean,       Is.EqualTo(report2.IsClean));
        Assert.That(report1.TotalFiles,    Is.EqualTo(report2.TotalFiles));
        Assert.That(report1.MismatchCount, Is.EqualTo(report2.MismatchCount));
        Assert.That(report1.Warnings.Count, Is.EqualTo(report2.Warnings.Count));
        Assert.That(report1.Errors.Count,   Is.EqualTo(report2.Errors.Count));
    }
}
