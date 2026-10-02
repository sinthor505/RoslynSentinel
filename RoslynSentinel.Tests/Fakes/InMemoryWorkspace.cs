namespace RoslynSentinel.Tests.Fakes;

// An in-memory workspace for tests about an engine's or tool's code-correctness: no temp directory, no MSBuild load,
// no file watcher, no disk write. Documents live in an AdhocWorkspace solution under a virtual root that is never
// created on disk, and FakeWorkspaceManager.ApplyProposedChangesAsync applies edits back to that solution, so a tool's
// full validate-then-apply path runs and the result is read with ReadText. Tests about disk behavior (BOM, line
// endings, file watcher, drift, undo from disk) must not use this - see DiskWriteRoundTrip for write fidelity and
// TestSolutionFixture for a real on-disk solution.
public sealed class InMemoryWorkspace : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RoslynSentinelInMemory", Guid.NewGuid().ToString("n"));

    private InMemoryWorkspace()
    {
    }

    public FakeWorkspaceManager Manager { get; } = new();

    // The real fixture projects are SDK projects with ImplicitUsings enabled, so test sources written against them use
    // [Serializable], Task, etc. without a using. Without this file the compile gate would reject those sources.
    private const string ImplicitUsingsFileName = "GlobalUsings.InMemory.cs";

    private const string ImplicitUsingsSource = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.IO;\nglobal using System.Linq;\nglobal using System.Net.Http;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n";

    public static InMemoryWorkspace Create(params (string RelativePath, string Content)[] files)
    {
        var workspace = new InMemoryWorkspace();
        var documents = files
            .Append((RelativePath: ImplicitUsingsFileName, Content: ImplicitUsingsSource))
            .Select(f => (name: f.RelativePath, content: f.Content, filePath: workspace.PathOf(f.RelativePath)))
            .ToArray();
        workspace.Manager.SetTestSolution(TestSolutionBuilder.CreateSolutionWithProject("InMemoryProject", Path.Combine(workspace._root, "InMemoryProject.csproj"), documents));
        return workspace;
    }

    // The absolute (virtual) path a tool call must use for a file, since this workspace has no solution root to resolve
    // a relative path against. GetFullPath normalizes '/' to '\' because document lookup compares paths with an
    // ordinal-ignore-case comparer that does not treat the two separators as equal.
    public string PathOf(string relativePath) => Path.GetFullPath(Path.Combine(_root, relativePath));

    public string ReadText(string relativePath)
    {
        var solution = Manager.CurrentSolution ?? throw new InvalidOperationException("No solution.");
        var documentId = solution.GetDocumentIdsWithFilePath(PathOf(relativePath)).FirstOrDefault()
            ?? throw new FileNotFoundException($"'{relativePath}' is not a document in this in-memory workspace.");
        return solution.GetDocument(documentId)!.GetTextAsync().GetAwaiter().GetResult().ToString();
    }

    public void Dispose() => Manager.Dispose();
}
