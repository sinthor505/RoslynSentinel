using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public record PublicApiMember(
    string Kind,
    string ContainingType,
    string Signature,
    string? FilePath,
    int Line
);

public record BreakingChange(
    string ChangeKind,
    string Description,
    string AffectedMember,
    string? FilePath,
    int Line
);

/// <summary>
/// Captures public API surfaces and detects breaking changes between snapshots.
/// Workflow: (1) call GetPublicApiSurface to capture a baseline JSON string,
/// (2) make code changes, (3) call DetectBreakingChanges with the baseline to see what broke.
/// </summary>
public class BreakingChangeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public BreakingChangeEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>
    /// Extracts all public API members (methods, properties, constructors) from a project or file.
    /// Save the returned list as a JSON baseline to compare against later.
    /// </summary>
    public async Task<List<PublicApiMember>> GetPublicApiSurfaceAsync(
        string? projectName = null,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var results = new List<PublicApiMember>();

        IEnumerable<Document?> documents;
        if (!string.IsNullOrEmpty(filePath))
        {
            documents = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument);
        }
        else if (!string.IsNullOrEmpty(projectName))
        {
            var project = solution.Projects.FirstOrDefault(p =>
                string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            documents = project?.Documents.Cast<Document?>() ?? Enumerable.Empty<Document?>();
        }
        else
        {
            documents = solution.Projects.SelectMany(p => p.Documents).Cast<Document?>();
        }

        foreach (var doc in documents)
        {
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync(ct);
            if (root == null)
            {
                continue;
            }

            var docPath = doc.FilePath ?? doc.Name;

            foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (!IsPublicOrProtected(typeDecl.Modifiers))
                {
                    continue;
                }

                var typeName = typeDecl.Identifier.Text;
                var typeLine = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

                results.Add(new PublicApiMember("Type", typeName, BuildTypeSignature(typeDecl), docPath, typeLine));

                // TypeDeclarationSyntax covers class/struct/interface/record — all have Members.
                // EnumDeclarationSyntax is a BaseTypeDeclarationSyntax but has no named callable members.
                if (typeDecl is not TypeDeclarationSyntax typeWithMembers)
                {
                    continue;
                }

                foreach (var member in typeWithMembers.Members)
                {
                    if (!IsPublicOrProtected(GetMemberModifiers(member)))
                    {
                        continue;
                    }

                    var (kind, sig) = GetMemberSignature(member, typeName);
                    if (sig == null)
                    {
                        continue;
                    }

                    var memberLine = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    results.Add(new PublicApiMember(kind, typeName, sig, docPath, memberLine));
                }
            }
        }

        return results.OrderBy(m => m.ContainingType).ThenBy(m => m.Signature).ToList();
    }

    /// <summary>
    /// Compares the current API surface against a previously captured baseline.
    /// Provide the baseline as a list of PublicApiMember records.
    /// Returns a list of detected breaking changes.
    /// </summary>
    public async Task<List<BreakingChange>> DetectBreakingChangesAsync(
        List<PublicApiMember> baseline,
        string? projectName = null,
        string? filePath = null,
        CancellationToken ct = default)
    {
        var current = await GetPublicApiSurfaceAsync(projectName, filePath, ct);
        var changes = new List<BreakingChange>();

        // Index current by signature for fast lookup
        var currentBySignature = current.ToDictionary(m => $"{m.ContainingType}|{m.Signature}", m => m);
        var currentTypes = current.Where(m => m.Kind == "Type")
            .Select(m => m.ContainingType)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var baselineMember in baseline)
        {
            var key = $"{baselineMember.ContainingType}|{baselineMember.Signature}";

            if (currentBySignature.ContainsKey(key))
            {
                continue; // Unchanged — good
            }

            // Member was removed or renamed. Check if the type itself still exists.
            if (baselineMember.Kind == "Type")
            {
                if (!currentTypes.Contains(baselineMember.ContainingType))
                {
                    changes.Add(new BreakingChange(
                        "TypeRemoved",
                        $"Public type '{baselineMember.ContainingType}' was removed. All consumers will fail to compile.",
                        baselineMember.Signature,
                        baselineMember.FilePath,
                        baselineMember.Line));
                }
                else
                {
                    changes.Add(new BreakingChange(
                        "TypeSignatureChanged",
                        $"Type '{baselineMember.ContainingType}' signature changed from '{baselineMember.Signature}'.",
                        baselineMember.Signature,
                        baselineMember.FilePath,
                        baselineMember.Line));
                }
            }
            else
            {
                if (!currentTypes.Contains(baselineMember.ContainingType))
                {
                    // Type was removed — type-level change already reported
                    continue;
                }

                changes.Add(new BreakingChange(
                    "MemberRemovedOrRenamed",
                    $"{baselineMember.Kind} '{baselineMember.Signature}' in '{baselineMember.ContainingType}' was removed or its signature changed. Callers will fail to compile.",
                    $"{baselineMember.ContainingType}.{baselineMember.Signature}",
                    baselineMember.FilePath,
                    baselineMember.Line));
            }
        }

        // Also flag newly internal/private members (accessibility reduction is breaking)
        var baselineSignatures = baseline.Select(m => $"{m.ContainingType}|{m.Signature}").ToHashSet();
        // (Access reduction can't be detected from signature alone without semantic model — covered by summary message)

        return changes.OrderBy(c => c.ChangeKind).ThenBy(c => c.AffectedMember).ToList();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsPublicOrProtected(SyntaxTokenList modifiers) =>
        modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));

    private static SyntaxTokenList GetMemberModifiers(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Modifiers,
        PropertyDeclarationSyntax p => p.Modifiers,
        ConstructorDeclarationSyntax c => c.Modifiers,
        FieldDeclarationSyntax f => f.Modifiers,
        EventDeclarationSyntax e => e.Modifiers,
        EventFieldDeclarationSyntax ef => ef.Modifiers,
        _ => default
    };

    private static string BuildTypeSignature(BaseTypeDeclarationSyntax typeDecl)
    {
        var keyword = typeDecl switch
        {
            ClassDeclarationSyntax => "class",
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
            EnumDeclarationSyntax => "enum",
            _ => "type"
        };

        var bases = typeDecl.BaseList?.Types.Count > 0
            ? " : " + string.Join(", ", typeDecl.BaseList.Types.Select(t => t.ToString()))
            : "";

        return $"{keyword} {typeDecl.Identifier.Text}{bases}";
    }

    private static (string Kind, string? Signature) GetMemberSignature(MemberDeclarationSyntax member, string typeName) => member switch
    {
        MethodDeclarationSyntax m =>
            ("Method", $"{m.ReturnType} {m.Identifier.Text}{m.TypeParameterList}{m.ParameterList}"),

        ConstructorDeclarationSyntax c =>
            ("Constructor", $"{typeName}{c.ParameterList}"),

        PropertyDeclarationSyntax p =>
            ("Property", $"{p.Type} {p.Identifier.Text} {{ {(p.AccessorList?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.GetKeyword)) == true ? "get; " : "")}{(p.AccessorList?.Accessors.Any(a => a.Keyword.IsKind(SyntaxKind.SetKeyword) || a.Keyword.IsKind(SyntaxKind.InitKeyword)) == true ? "set; " : "")}}}"),

        FieldDeclarationSyntax f =>
            ("Field", $"{f.Declaration.Type} {string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text))}"),

        EventDeclarationSyntax e =>
            ("Event", $"event {e.Type} {e.Identifier.Text}"),

        EventFieldDeclarationSyntax ef =>
            ("Event", $"event {ef.Declaration.Type} {string.Join(", ", ef.Declaration.Variables.Select(v => v.Identifier.Text))}"),

        _ => ("Member", null)
    };
}
