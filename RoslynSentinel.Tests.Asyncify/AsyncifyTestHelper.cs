using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynSentinel.Tests.Asyncify;

public static class AsyncifyTestHelper
{
    public static Solution CreateSolutionWithProject(string projectName, (string name, string content)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var references = new List<MetadataReference>();
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        string[] candidateNames =
        [
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
        ];

        foreach (var name in candidateNames)
        {
            var path = Path.Combine(coreDir, name);
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        var objectAssembly = typeof(object).Assembly.Location;
        if (!references.Any(r => r.Display != null && r.Display.Equals(objectAssembly, StringComparison.OrdinalIgnoreCase)))
            references.Add(MetadataReference.CreateFromFile(objectAssembly));

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
}
