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
            "netstandard.dll"
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

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName, LanguageNames.CSharp)
            .WithMetadataReferences(references)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var doc in documents)
        {
            var documentId = DocumentId.CreateNewId(projectId, doc.name);
            solution = solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.name);
        }

        return solution;
    }
}
