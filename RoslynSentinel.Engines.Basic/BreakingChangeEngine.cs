using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;
public record PublicApiMember(string Kind, string ContainingType, string Signature, string? FilePath, int Line);
public record BreakingChange(string ChangeKind, string Description, string AffectedMember, string? FilePath, int Line);
/// <summary>
/// Captures public API surfaces and detects breaking changes between snapshots.
/// Workflow: (1) call Get
/// to capture a baseline JSON string,
/// (2) make code changes, (3) call DetectBreakingChanges with the baseline to see what broke.
/// </summary>
/// 
public class BreakingChangeEngine
{
    private readonly IWorkspaceManager _workspaceManager;
    public BreakingChangeEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }
}