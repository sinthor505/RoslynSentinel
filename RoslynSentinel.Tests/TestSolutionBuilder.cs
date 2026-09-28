using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RoslynSentinel.Tests;

public static class TestSolutionBuilder
{
    public static Solution CreateSolutionWithProject(string projectName, (string name, string content)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var references = new List<MetadataReference>();

        // Use a more robust way to get all required base assemblies for .NET 10 tests
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        string[] candidateNames = {
            "System.Runtime.dll",
            "mscorlib.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Console.dll",
            "System.Private.CoreLib.dll",
            "netstandard.dll",
            // Required for semantic model type resolution in accuracy tests
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.Net.Http.dll",
            "System.Collections.Concurrent.dll",
            "System.Text.RegularExpressions.dll",
        };

        foreach (var name in candidateNames)
        {
            var path = Path.Combine(coreDir, name);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Force-add the assembly containing System.Object if it was missed
        var objectAssembly = typeof(object).Assembly.Location;
        if (!references.Any(r => r.Display != null && r.Display.Equals(objectAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(MetadataReference.CreateFromFile(objectAssembly));
        }

        // Create a mock project FilePathWrapper for test purposes
        var projectPath = Path.Combine(Path.GetTempPath(), "TestProj", $"{projectName}.csproj");

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName, LanguageNames.CSharp)
            .WithMetadataReferences(references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(projectPath)
            .WithDefaultNamespace(projectName);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var doc in documents)
        {
            var documentId = DocumentId.CreateNewId(projectId, doc.name);
            solution = solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.name);
        }

        return solution;
    }

    // Overload for tests that need real on-disk file paths.
    // projectCsprojPath sets the project's FilePathWrapper (controls FindContainingProject lookups).
    // Each document's filePath must be the absolute path that will be written to disk.
    public static Solution CreateSolutionWithProject(
        string projectName,
        string projectCsprojPath,
        (string name, string content, string filePath)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var references = new List<MetadataReference>();
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        string[] candidateNames = {
            "System.Runtime.dll",
            "mscorlib.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Console.dll",
            "System.Private.CoreLib.dll",
            "netstandard.dll",
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.Net.Http.dll",
            "System.Collections.Concurrent.dll",
            "System.Text.RegularExpressions.dll",
        };

        foreach (var name in candidateNames)
        {
            var path = Path.Combine(coreDir, name);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var objectAssembly = typeof(object).Assembly.Location;
        if (!references.Any(r => r.Display != null && r.Display.Equals(objectAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(MetadataReference.CreateFromFile(objectAssembly));
        }

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName, LanguageNames.CSharp)
            .WithMetadataReferences(references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(projectCsprojPath)
            .WithDefaultNamespace(projectName);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var doc in documents)
        {
            var documentId = DocumentId.CreateNewId(projectId, doc.name);
            solution = solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.filePath);
        }

        return solution;
    }
    // Added by AddMember (expected - used for diagnostics)
    public static Solution CreateEmptySolution()
    {
        return new AdhocWorkspace().CurrentSolution;
    }

    // Builds two distinct AdhocWorkspace projects, each with its own Compilation, where
    // upstreamProjectName is referenced by downstreamProjectName via a real ProjectReference.
    // This is the minimum fixture that can reproduce a cross-compilation ITypeSymbol identity
    // bug: a type declared in the upstream project and a field of that type declared in the
    // downstream project are resolved through two different SemanticModel/Compilation object
    // graphs, so SymbolEqualityComparer.Default never sees them as "the same" type unless the
    // consuming code re-resolves across compilations (see
    // docs/current/blockers/resolved/blocking_error_movemember_candidate_lookup_misses_sibling_field.md).
    public static Solution CreateTwoProjectSolution(
        string upstreamProjectName,
        (string name, string content)[] upstreamDocuments,
        string downstreamProjectName,
        (string name, string content)[] downstreamDocuments)
    {
        var workspace = new AdhocWorkspace();
        var upstreamProjectId = ProjectId.CreateNewId();
        var downstreamProjectId = ProjectId.CreateNewId();

        var references = new List<MetadataReference>();
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        string[] candidateNames = {
            "System.Runtime.dll",
            "mscorlib.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Console.dll",
            "System.Private.CoreLib.dll",
            "netstandard.dll",
            "System.Threading.dll",
            "System.Threading.Tasks.dll",
            "System.Net.Http.dll",
            "System.Collections.Concurrent.dll",
            "System.Text.RegularExpressions.dll",
        };

        foreach (var name in candidateNames)
        {
            var path = Path.Combine(coreDir, name);
            if (File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var objectAssembly = typeof(object).Assembly.Location;
        if (!references.Any(r => r.Display != null && r.Display.Equals(objectAssembly, StringComparison.OrdinalIgnoreCase)))
        {
            references.Add(MetadataReference.CreateFromFile(objectAssembly));
        }

        var upstreamProjectPath = Path.Combine(Path.GetTempPath(), "TestProj", $"{upstreamProjectName}.csproj");
        var upstreamProjectInfo = ProjectInfo.Create(upstreamProjectId, VersionStamp.Default, upstreamProjectName, upstreamProjectName, LanguageNames.CSharp)
            .WithMetadataReferences(references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(upstreamProjectPath)
            .WithDefaultNamespace(upstreamProjectName);

        var downstreamProjectPath = Path.Combine(Path.GetTempPath(), "TestProj", $"{downstreamProjectName}.csproj");
        var downstreamProjectInfo = ProjectInfo.Create(downstreamProjectId, VersionStamp.Default, downstreamProjectName, downstreamProjectName, LanguageNames.CSharp)
            .WithMetadataReferences(references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithFilePath(downstreamProjectPath)
            .WithDefaultNamespace(downstreamProjectName)
            .WithProjectReferences([new ProjectReference(upstreamProjectId)]);

        var solution = workspace.CurrentSolution
            .AddProject(upstreamProjectInfo)
            .AddProject(downstreamProjectInfo);

        foreach (var doc in upstreamDocuments)
        {
            var documentId = DocumentId.CreateNewId(upstreamProjectId, doc.name);
            solution = solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.name);
        }

        foreach (var doc in downstreamDocuments)
        {
            var documentId = DocumentId.CreateNewId(downstreamProjectId, doc.name);
            solution = solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.name);
        }

        return solution;
    }
}
