using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>A Roslyn compilation over the .cs files of a scenario's Core folder, so graders check compiled symbols instead of text.</summary>
internal sealed class ScenarioCompilation
{
    private static readonly string[] ImplicitUsings =
    {
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Threading", "System.Threading.Tasks", "System.Net.Http",
    };

    private static readonly HashSet<string> DocumentationDiagnosticIds = new() { "CS1574", "CS1580", "CS1581", "CS1584" };

    // Syntax trees of the scenario folder by solution-relative path with '/' separators (the generated global-usings tree is not in here).
    private readonly Dictionary<string, SyntaxTree> _trees;

    private ScenarioCompilation(CSharpCompilation compilation, Dictionary<string, SyntaxTree> trees)
    {
        Compilation = compilation;
        _trees = trees;
    }

    /// <summary>Builds the compilation over every .cs file under <paramref name="relativeFolder"/> of <paramref name="solutionDirectory"/>.</summary>
    public static ScenarioCompilation Create(string solutionDirectory, string relativeFolder)
    {
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithDocumentationMode(DocumentationMode.Diagnose);

        var folder = Path.Combine(solutionDirectory, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(solutionDirectory, file).Replace('\\', '/');
            trees[relative] = CSharpSyntaxTree.ParseText(File.ReadAllText(file), parseOptions, path: file);
        }

        var globalUsings = CSharpSyntaxTree.ParseText(
            string.Join(Environment.NewLine, ImplicitUsings.Select(name => $"global using global::{name};")),
            parseOptions,
            path: "GlobalUsings.g.cs");

        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        var compilation = CSharpCompilation.Create(
            "ScenarioCompilation_" + Guid.NewGuid().ToString("N"),
            trees.Values.Append(globalUsings),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        return new ScenarioCompilation(compilation, trees);
    }

    public CSharpCompilation Compilation { get; }

    /// <summary>The semantic model of one scenario file; nodes for <see cref="SymbolOf"/> and <see cref="TypeOf"/> come from its syntax tree.</summary>
    public SemanticModel ModelFor(string relativePath) => Compilation.GetSemanticModel(TreeOf(relativePath));

    /// <summary>The symbol a node refers to (a constructor for an object creation), else the symbol it declares; null when neither.</summary>
    public ISymbol? SymbolOf(SyntaxNode node)
    {
        var model = Compilation.GetSemanticModel(node.SyntaxTree);
        return model.GetSymbolInfo(node).Symbol ?? model.GetDeclaredSymbol(node);
    }

    public ITypeSymbol? TypeOf(SyntaxNode node) =>
        Compilation.GetSemanticModel(node.SyntaxTree).GetTypeInfo(node).Type;

    /// <summary>The doc-comment diagnostics (CS1574, CS1580, CS1581, CS1584) of one file.</summary>
    public IReadOnlyList<Diagnostic> DocumentationDiagnostics(string relativePath)
    {
        var tree = TreeOf(relativePath);
        return Compilation.GetDiagnostics()
            .Where(diagnostic => DocumentationDiagnosticIds.Contains(diagnostic.Id) && diagnostic.Location.SourceTree == tree)
            .ToList();
    }

    /// <summary>The type with this metadata name (for example <c>ContosoOrders.Core.Evals.X.Planner</c>), or null when it is not declared or referenced.</summary>
    public INamedTypeSymbol? Type(string metadataName) => Compilation.GetTypeByMetadataName(metadataName);

    private SyntaxTree TreeOf(string relativePath)
    {
        var key = relativePath.Replace('\\', '/');
        return _trees.TryGetValue(key, out var tree)
            ? tree
            : throw new ArgumentException($"'{relativePath}' is not a file of this compilation (files: {string.Join(", ", _trees.Keys)}).", nameof(relativePath));
    }
}
