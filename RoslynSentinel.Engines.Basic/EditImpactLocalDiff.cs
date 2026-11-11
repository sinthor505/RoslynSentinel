using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Syntax-only diff of the parameters, locals (including pattern, out, deconstruction, catch and foreach variables) and local functions declared in the members of the edited files (proposal_semantic_edit_impact_diagnostics.md,
/// "Second slice", step 2). Members are keyed by containing type path, name and parameter text, locals by (member, kind, name): a member that exists on
/// both sides and lost exactly one local name while gaining exactly one of the same kind is a rename, otherwise each lost name is a removal. Added
/// locals are not reported. Kept apart from <see cref="EditImpactSymbolDiff"/> on purpose: <c>RenameLifter</c> counts the renames of
/// <see cref="EditImpactAnalyzer.DiffSymbols"/>, and a local rename must not make a one-method rename look like two.
/// </summary>
internal static class EditImpactLocalDiff
{
    // A working row of the collector, not a wire type.
    private sealed record LocalDeclaration(string Kind, string Name, string File, int Line);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<SymbolChange> Diff(
        IReadOnlyDictionary<string, string?> baselineTexts,
        IReadOnlyDictionary<string, string?> candidateTexts)
    {
        var oldMembers = Collect(baselineTexts);
        var newMembers = Collect(candidateTexts);
        var changes = new List<SymbolChange>();
        foreach (var (key, oldLocals) in oldMembers)
        {
            if (!newMembers.TryGetValue(key, out var newLocals))
            {
                // The member is gone or new on one side: the member diff reports that; its locals say nothing more.
                continue;
            }

            var removed = oldLocals.Where(o => !newLocals.Any(n => n.Kind == o.Kind && n.Name == o.Name)).ToList();
            var added = newLocals.Where(n => !oldLocals.Any(o => o.Kind == n.Kind && o.Name == n.Name)).ToList();
            var container = key.Display;
            if (removed.Count == 1 && added.Count == 1 && removed[0].Kind == added[0].Kind)
            {
                changes.Add(new SymbolChange(SymbolChangeKind.Renamed, removed[0].Kind, container, removed[0].Name, added[0].Name, added[0].File, added[0].Line, null, null));
                continue;
            }

            foreach (var local in removed)
            {
                changes.Add(new SymbolChange(SymbolChangeKind.Removed, local.Kind, container, local.Name, null, local.File, local.Line, null, null));
            }
        }

        return changes;
    }

    /// <summary>True for an entry this class produced.</summary>
    public static bool IsLocal(SymbolChange change) => change.DeclarationKind is "local" or "local function" or "parameter";

    private readonly record struct MemberKey(string Identity, string Display);

    private static Dictionary<MemberKey, List<LocalDeclaration>> Collect(IReadOnlyDictionary<string, string?> texts)
    {
        var result = new Dictionary<MemberKey, List<LocalDeclaration>>();
        foreach (var (path, text) in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var tree = CSharpSyntaxTree.ParseText(text);
            var file = Path.GetFileName(path);
            foreach (var member in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (member.Body is null && member.ExpressionBody is null)
                {
                    continue;
                }

                var typePath = string.Join(".", member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
                var name = member switch
                {
                    MethodDeclarationSyntax m => m.Identifier.ValueText,
                    ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                    _ => member.Kind().ToString(),
                };
                // The member is keyed by its parameter TYPES (whitespace-free), not their names: a renamed parameter must keep the member's key.
                var parameterTypes = Whitespace.Replace(string.Join(",", member.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? string.Empty)), string.Empty);
                var key = new MemberKey($"{typePath}|{name}|{parameterTypes}", typePath.Length == 0 ? name : $"{typePath}.{name}");
                var locals = member.ParameterList.Parameters
                    .Select(p => new LocalDeclaration("parameter", p.Identifier.ValueText, file, LineOf(p.Identifier)))
                    .ToList();
                foreach (var node in member.DescendantNodes())
                {
                    switch (node)
                    {
                        case VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax or ForStatementSyntax or UsingStatementSyntax or FixedStatementSyntax } } declarator:
                            locals.Add(new LocalDeclaration("local", declarator.Identifier.ValueText, file, LineOf(declarator.Identifier)));
                            break;
                        case ForEachStatementSyntax forEach:
                            locals.Add(new LocalDeclaration("local", forEach.Identifier.ValueText, file, LineOf(forEach.Identifier)));
                            break;
                        // Pattern variables (is T x, case T x, property patterns), out variables (out var x, out int x) and deconstruction variables.
                        case SingleVariableDesignationSyntax designation:
                            locals.Add(new LocalDeclaration("local", designation.Identifier.ValueText, file, LineOf(designation.Identifier)));
                            break;
                        case CatchDeclarationSyntax { Identifier.RawKind: not 0 } catchDeclaration:
                            locals.Add(new LocalDeclaration("local", catchDeclaration.Identifier.ValueText, file, LineOf(catchDeclaration.Identifier)));
                            break;
                        case LocalFunctionStatementSyntax localFunction:
                            locals.Add(new LocalDeclaration("local function", localFunction.Identifier.ValueText, file, LineOf(localFunction.Identifier)));
                            break;
                    }
                }

                // A partial type can hold the same member key in two files only if it is declared twice; the later file wins.
                result[key] = locals;
            }
        }

        return result;
    }

    private static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
