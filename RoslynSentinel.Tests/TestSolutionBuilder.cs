using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RoslynSentinel.Tests;

public static class TestSolutionBuilder
{
    public static Solution CreateSolutionWithProject(string projectName, (string name, string content)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        
        var references = new List<MetadataReference> {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location)
        };

        // Add System.Runtime if possible
        var runtimePath = Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll");
        if (File.Exists(runtimePath)) references.Add(MetadataReference.CreateFromFile(runtimePath));

        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Default, projectName, projectName, LanguageNames.CSharp)
            .WithMetadataReferences(references);

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        foreach (var doc in documents)
        {
            var documentId = DocumentId.CreateNewId(projectId, doc.name);
            solution = solution.AddDocument(documentId, doc.name, SourceText.From(doc.content), filePath: doc.name);
        }

        return solution;
    }
}
