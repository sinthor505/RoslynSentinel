using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynSentinel.Common;

namespace RoslynSentinel.Tests.Basic;

// Step 11 of docs/current/plans/plan_multi_file_movefile_and_namespace_change_tool.md: ValidateChangesAgainstBaselineAsync builds
// the candidate from candidateBase but takes the introduced-error delta against errorBaseline. Scenarios use detached in-memory
// solutions (nothing is applied to a workspace), in the same style as MoveBindCheckTests.
[TestFixture]
public sealed class ValidationEngineBaselineSplitTests
{
    private static readonly FilePathWrapper PathA = "C:\\x\\A.cs";
    private static readonly FilePathWrapper PathB = "C:\\x\\B.cs";
    private static readonly FilePathWrapper PathX = "C:\\x\\X.cs";
    private static readonly FilePathWrapper PathY = "C:\\x\\Y.cs";
    private static readonly FilePathWrapper PathZ = "C:\\x\\Z.cs";

    private static Solution AddProject(Solution solution, ProjectId projectId, string name)
    {
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        return solution.AddProject(projectInfo);
    }

    private static Solution AddSource(Solution solution, ProjectId projectId, string fileName, FilePathWrapper filePath, string text)
    {
        return solution.AddDocument(DocumentId.CreateNewId(projectId), fileName, SourceText.From(text), filePath: filePath);
    }

    private static async Task<List<string>> ErrorIdsAsync(Solution solution, ProjectId projectId)
    {
        var compilation = await solution.GetProject(projectId)!.GetCompilationAsync();
        return compilation!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id)
            .ToList();
    }

    private static string Describe(DiagnosticInfo d) =>
        $"{d.Id}|{d.Severity}|{d.Message}|{(string)d.FilePath}|{d.StartLine}|{d.StartColumn}";

    [Test]
    public async Task Validate_EdgeMakesDuplicateTypeVisible_RefusesCs0433Async()
    {
        using var workspace = new AdhocWorkspace();
        var projectX = ProjectId.CreateNewId("X");
        var projectY = ProjectId.CreateNewId("Y");
        var projectZ = ProjectId.CreateNewId("Z");

        // Y and Z both declare N.T. X already references Z, so its use of T binds cleanly in the baseline. The candidate edge
        // X -> Y makes the name exist in two referenced assemblies, which is CS0433 inside X. X has no changed document; it is
        // compiled only because its ProjectReferences differ between the two solutions.
        var errorBaseline = AddProject(workspace.CurrentSolution, projectZ, "Z");
        errorBaseline = AddProject(errorBaseline, projectY, "Y");
        errorBaseline = AddProject(errorBaseline, projectX, "X");
        errorBaseline = errorBaseline.AddProjectReference(projectX, new ProjectReference(projectZ));
        errorBaseline = AddSource(errorBaseline, projectZ, "Z.cs", PathZ, "namespace N { public class T { } }\n");
        errorBaseline = AddSource(errorBaseline, projectY, "Y.cs", PathY, "namespace N { public class T { } }\n");
        errorBaseline = AddSource(errorBaseline, projectX, "X.cs", PathX, "namespace N { public class X { public object Use(T t) => t; } }\n");
        var candidateBase = errorBaseline.AddProjectReference(projectX, new ProjectReference(projectY));

        Assert.That(await ErrorIdsAsync(errorBaseline, projectX), Is.Empty, "precondition: X is clean in the baseline");

        var result = await ValidationEngine.ValidateChangesAgainstBaselineAsync(errorBaseline, candidateBase, new Dictionary<FilePathWrapper, string>());

        var diagnostics = result.Report.Diagnostics.Select(Describe).ToList();
        Assert.That(result.Report.IsError, Is.True, string.Join("; ", diagnostics));
        var cs0433InX = result.Report.Diagnostics.Where(d => d.Id == "CS0433" && (string)d.FilePath == (string)PathX).ToList();
        Assert.That(cs0433InX, Is.Not.Empty, "CS0433 reported in X.cs (proves X was compiled); got: " + string.Join("; ", diagnostics));
    }

    [Test]
    public async Task Validate_BaselineEqualsCandidateBase_MatchesExistingCoreAsync()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("P");
        var solution = AddProject(workspace.CurrentSolution, projectId, "P");
        solution = AddSource(solution, projectId, "A.cs", PathA, "namespace N { public class A { public int Stay() => 1; } }\n");
        solution = AddSource(solution, projectId, "Caller.cs", PathX, "namespace N { public class C { public int M(A a) => a.Stay(); } }\n");
        var changes = new Dictionary<FilePathWrapper, string>
        {
            [PathA] = "namespace N { public class A { public int Stay() => missing; } }\n",
        };

        var legacy = await ValidationEngine.ValidateChangesWithCandidateAsync(solution, changes);
        var split = await ValidationEngine.ValidateChangesAgainstBaselineAsync(solution, solution, changes);

        Assert.That(legacy.Report.IsError, Is.True, "fixture precondition: the change introduces an error");
        Assert.That(split.Report.IsError, Is.EqualTo(legacy.Report.IsError));
        Assert.That(split.Report.Diagnostics.Select(Describe).ToList(), Is.EqualTo(legacy.Report.Diagnostics.Select(Describe).ToList()));
    }

    [Test]
    public async Task Validate_AddedReferenceFixesMissingNamespace_NoNewErrorsAgainstCommittedAsync()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = ProjectId.CreateNewId("A");
        var projectB = ProjectId.CreateNewId("B");
        var errorBaseline = AddProject(workspace.CurrentSolution, projectB, "B");
        errorBaseline = AddProject(errorBaseline, projectA, "A");
        errorBaseline = AddSource(errorBaseline, projectB, "B.cs", PathB, "namespace N { public class Helper { } }\n");
        errorBaseline = AddSource(errorBaseline, projectA, "A.cs", PathA, "namespace N { public class A { public object Make() => new Helper(); } }\n");
        var candidateBase = errorBaseline.AddProjectReference(projectA, new ProjectReference(projectB));

        // The committed solution has the missing-reference error (CS0246) in A; the candidate adds A -> B, which removes it.
        Assert.That(await ErrorIdsAsync(errorBaseline, projectA), Does.Contain("CS0246"), "precondition: A misses B in the baseline");

        var result = await ValidationEngine.ValidateChangesAgainstBaselineAsync(errorBaseline, candidateBase, new Dictionary<FilePathWrapper, string>());

        Assert.That(result.Report.IsError, Is.False, string.Join("; ", result.Report.Diagnostics.Select(Describe)));
        Assert.That(result.Report.Diagnostics, Is.Empty);
    }

    [Test]
    public async Task Validate_DependentsOfAnEdgeProject_AreCompiledViaCandidateBaseGraphAsync()
    {
        using var workspace = new AdhocWorkspace();
        var projectA = ProjectId.CreateNewId("A");
        var projectB = ProjectId.CreateNewId("B");
        var projectC = ProjectId.CreateNewId("C");
        var projectD = ProjectId.CreateNewId("D");

        // A -> B -> C in both solutions. The edge B -> D is the only difference. The baseline already has CS0246 in A
        // (B's public Make() returns D.U, which A cannot see). The edge turns that into CS0012, a new diagnostic key.
        // A has no changed document and no reference change, so it is reached only through the dependents expansion
        // over candidateBase's graph (A depends on B).
        var errorBaseline = AddProject(workspace.CurrentSolution, projectC, "C");
        errorBaseline = AddProject(errorBaseline, projectD, "D");
        errorBaseline = AddProject(errorBaseline, projectB, "B");
        errorBaseline = AddProject(errorBaseline, projectA, "A");
        errorBaseline = errorBaseline.AddProjectReference(projectB, new ProjectReference(projectC));
        errorBaseline = errorBaseline.AddProjectReference(projectA, new ProjectReference(projectB));
        errorBaseline = AddSource(errorBaseline, projectC, "C.cs", PathX, "namespace N { public class C0 { } }\n");
        errorBaseline = AddSource(errorBaseline, projectD, "D.cs", PathY, "namespace N { public class U { public int V => 1; } }\n");
        errorBaseline = AddSource(errorBaseline, projectB, "B.cs", PathB,
            "namespace N { public class B { public C0 Peek() => new C0(); public U Make() => throw new System.InvalidOperationException(); } }\n");
        errorBaseline = AddSource(errorBaseline, projectA, "A.cs", PathA, "namespace N { public class A { public int Use(B b) => b.Make().V; } }\n");
        var candidateBase = errorBaseline.AddProjectReference(projectB, new ProjectReference(projectD));

        Assert.That(await ErrorIdsAsync(errorBaseline, projectA), Does.Contain("CS0246").And.Not.Contain("CS0012"), "precondition: A has only the pre-existing CS0246 in the baseline");

        var result = await ValidationEngine.ValidateChangesAgainstBaselineAsync(errorBaseline, candidateBase, new Dictionary<FilePathWrapper, string>());

        var diagnostics = result.Report.Diagnostics.Select(Describe).ToList();
        Assert.That(result.Report.IsError, Is.True, string.Join("; ", diagnostics));
        var inA = result.Report.Diagnostics.Where(d => d.Id == "CS0012" && (string)d.FilePath == (string)PathA).ToList();
        Assert.That(inA, Is.Not.Empty, "CS0012 reported in A.cs (proves A was compiled through B's dependents); got: " + string.Join("; ", diagnostics));
    }
}
