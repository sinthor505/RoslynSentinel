using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynSentinel.Tests.ModelEval.ImplementationScenarios.Graders;

/// <summary>A Roslyn compilation over the .cs files of a scenario's Core folder, so graders check compiled symbols instead of text.</summary>
internal sealed class ScenarioCompilation
{
    /// <summary>Builds the compilation over every .cs file under <paramref name="relativeFolder"/> of <paramref name="solutionDirectory"/>.</summary>
    public static ScenarioCompilation Create(string solutionDirectory, string relativeFolder)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
    }

    public CSharpCompilation Compilation => throw new NotImplementedException(); // TODO implement (Step 5)

    public SemanticModel ModelFor(string relativePath)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
    }

    public ISymbol? SymbolOf(SyntaxNode node)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
    }

    public ITypeSymbol? TypeOf(SyntaxNode node)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
    }

    /// <summary>The doc-comment diagnostics (CS1574, CS1580, CS1581, CS1584) of one file.</summary>
    public IReadOnlyList<Diagnostic> DocumentationDiagnostics(string relativePath)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
    }

    public INamedTypeSymbol? Type(string metadataName)
    {
        throw new NotImplementedException(); // TODO implement (Step 5)
    }
}
