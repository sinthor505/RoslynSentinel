using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using RoslynSentinel.Common;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Advanced;
public record DuplicateMethodGroup(string Hash, List<MethodLocation> Locations);
public record MethodLocation(FilePathWrapper filePath, string TypeName, string MethodName);
public class AnalysisEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;
    public AnalysisEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }
}