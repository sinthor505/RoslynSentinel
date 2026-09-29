using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Engines.Advanced;
public class ResourceSafetyEngine
{
    private readonly SentinelConfiguration _config;
    private readonly IWorkspaceManager _workspaceManager;
    public ResourceSafetyEngine(IWorkspaceManager workspaceManager, SentinelConfiguration config = null)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    private async Task<IEnumerable<(Document Document, SyntaxNode Root, SemanticModel? SemanticModel)>> GetTargetDocumentsAsync(Solution solution, string? projectName, string? filePath, bool includeSemantic = false, CancellationToken cancellationToken = default)
    {
        var projects = solution.Projects.AsEnumerable();
        if (!string.IsNullOrEmpty(projectName))
        {
            projects = projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase) || p.Name.Contains(projectName, StringComparison.OrdinalIgnoreCase));
        }

        var documentList = new List<(Document, SyntaxNode, SemanticModel? )>();
        foreach (var project in projects)
        {
            var docs = project.Documents.AsEnumerable();
            if (!string.IsNullOrEmpty(filePath))
            {
                docs = docs.Where(d => d.Name == filePath || d.FilePath == filePath || (d.FilePath != null && d.FilePath.EndsWith(filePath, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var doc in docs)
            {
                var root = await doc.GetSyntaxRootAsync(cancellationToken);
                if (root == null)
                {
                    continue;
                }

                var model = includeSemantic ? await doc.GetSemanticModelAsync(cancellationToken) : null;
                documentList.Add((doc, root, model));
            }
        }

        return documentList;
    }

    public async Task<List<string>> DetectMemoryLeaksAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("MemoryLeaks"))
        {
            return new List<string>();
        }

        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, null, filePath, false, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            foreach (var classNode in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                bool implementsDisposable = classNode.BaseList?.Types.Any(t => t.ToString().Contains("IDisposable")) ?? false;
                // Find subscriptions to external events (left side is a non-this member access)
                var subscriptions = classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Left is MemberAccessExpressionSyntax ma && ma.Expression is not ThisExpressionSyntax).ToList();
                if (subscriptions.Count == 0)
                {
                    continue;
                }

                // Find the unsubscriptions
                var unsubscribeKeys = new HashSet<string>(classNode.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)).Select(a => $"{a.Left}|{a.Right}"));
                foreach (var sub in subscriptions)
                {
                    bool hasUnsubscribe = unsubscribeKeys.Contains($"{sub.Left}|{sub.Right}");
                    if (!implementsDisposable || !hasUnsubscribe)
                    {
                        var lineSpan = sub.GetLocation().GetLineSpan();
                        string reason = !implementsDisposable ? "class does not implement IDisposable" : "Dispose does not unsubscribe";
                        results.Add($"{target.Document.FilePath ?? target.Document.Name}:{lineSpan.StartLinePosition.Line + 1} " + $"- Class '{classNode.Identifier.Text}' subscribes to '{sub.Left}' but {reason}.");
                    }

                    // Extra: lambda handler captures 'this' or an outer variable -> the publisher
                    // holds a reference to the subscriber even without IDisposable.
                    bool handlerIsLambda = sub.Right is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax;
                    if (!handlerIsLambda)
                    {
                        continue;
                    }

                    bool capturesThis = sub.Right.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any();
                    // Also flag if the lambda accesses a member through 'this' (implicit or explicit)
                    bool capturesMember = !capturesThis && sub.Right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(ma => ma.Expression is ThisExpressionSyntax);
                    if (capturesThis || capturesMember)
                    {
                        var lineSpan2 = sub.GetLocation().GetLineSpan();
                        results.Add($"{target.Document.FilePath ?? target.Document.Name}:{lineSpan2.StartLinePosition.Line + 1} " + $"- Class '{classNode.Identifier.Text}' subscribes to '{sub.Left}' with a lambda that captures " + $"'this' - the publisher will keep this instance alive until unsubscribed.");
                    }
                }
            }

            // --- IDisposable fields not disposed in Dispose() ---
            foreach (var classNode in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var disposableFields = classNode.Members.OfType<FieldDeclarationSyntax>().Where(f => !f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && IsLikelyDisposableType(f.Declaration.Type.ToString())).SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text)).ToHashSet(StringComparer.Ordinal);
                if (disposableFields.Count == 0)
                {
                    continue;
                }

                var disposeMethod = classNode.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "Dispose");
                if (disposeMethod?.Body == null)
                {
                    continue;
                }

                var disposedInMethod = disposeMethod.Body.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(inv =>
                {
                    if (inv.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Dispose")
                    {
                        return ma.Expression.ToString().TrimStart('_');
                    }

                    return null;
                }).Where(n => n != null).ToHashSet(StringComparer.Ordinal);
                foreach (var fieldName in disposableFields)
                {
                    var normalizedName = fieldName.TrimStart('_');
                    if (disposedInMethod.Contains(normalizedName) || disposedInMethod.Contains(fieldName))
                    {
                        continue;
                    }

                    var fieldDecl = classNode.Members.OfType<FieldDeclarationSyntax>().FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));
                    var line = (fieldDecl?.GetLocation().GetLineSpan().StartLinePosition.Line + 1) ?? 0;
                    results.Add($"{target.Document.FilePath ?? target.Document.Name}:{line} " + $"- Class '{classNode.Identifier.Text}': IDisposable field '{fieldName}' is never disposed in Dispose(). " + "Call Dispose() on it to release unmanaged resources.");
                }
            }

            // --- Static collection fields that grow unbounded ---
            foreach (var classNode in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var staticCollectionFields = classNode.Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && IsGrowableCollectionType(f.Declaration.Type.ToString())).SelectMany(f => f.Declaration.Variables.Select(v => (Name: v.Identifier.Text, Line: v.GetLocation().GetLineSpan().StartLinePosition.Line + 1))).ToList();
                if (staticCollectionFields.Count == 0)
                {
                    continue;
                }

                // Look for Clear() calls anywhere in the class targeting these fields
                var clearedFields = classNode.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(inv => inv.Expression is MemberAccessExpressionSyntax ma && (ma.Name.Identifier.Text == "Clear" || ma.Name.Identifier.Text == "Remove" || ma.Name.Identifier.Text == "TryRemove" || ma.Name.Identifier.Text == "RemoveAll")).Select(inv =>
                {
                    if (inv.Expression is MemberAccessExpressionSyntax ma)
                    {
                        return ma.Expression.ToString().TrimStart('_');
                    }

                    return null;
                }).Where(n => n != null).ToHashSet(StringComparer.Ordinal);
                foreach (var(fieldName, line)in staticCollectionFields)
                {
                    if (clearedFields.Contains(fieldName.TrimStart('_')) || clearedFields.Contains(fieldName))
                    {
                        continue;
                    }

                    results.Add($"{target.Document.FilePath ?? target.Document.Name}:{line} " + $"- Static collection field '{classNode.Identifier.Text}.{fieldName}' is never cleared. " + "Static collections live for the AppDomain lifetime and grow without bound unless explicitly cleared or bounded.");
                }
            }
        }

        return results;
    }

    private static bool IsLikelyDisposableType(string typeName)
    {
        // Common disposable types by name suffix or prefix
        return typeName.Contains("Stream") || typeName.Contains("Reader") || typeName.Contains("Writer") || typeName.Contains("Client") || typeName.Contains("Connection") || typeName.Contains("Channel") || typeName.Contains("Timer") || typeName.Contains("Semaphore") || typeName.Contains("Mutex") || typeName.Contains("CancellationTokenSource") || typeName.Contains("SqlConnection") || typeName.Contains("SqlCommand") || typeName.Contains("HttpClient");
    }

    private static bool IsGrowableCollectionType(string typeName)
    {
        return typeName.StartsWith("List<") || typeName.StartsWith("Dictionary<") || typeName.StartsWith("HashSet<") || typeName.StartsWith("ConcurrentDictionary<") || typeName.StartsWith("ConcurrentBag<") || typeName.StartsWith("ConcurrentQueue<") || typeName.StartsWith("Queue<") || typeName.StartsWith("Stack<");
    }

    /// <summary>
    /// Detects classes that both implement IDisposable AND declare a finalizer (~Destructor).
    /// Unless the finalizer guards with a disposed flag, this risks double-freeing managed
    /// resources when the GC calls the finalizer after Dispose() was already called.
    /// </summary>
    public async Task<List<string>> FindFinalizerOnDisposableAsync(string? projectName = null, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        var targets = await GetTargetDocumentsAsync(solution, projectName, null, false, cancellationToken);
        var results = new List<string>();
        foreach (var target in targets)
        {
            foreach (var classDecl in target.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                bool implementsDisposable = classDecl.BaseList?.Types.Any(t => t.ToString().Contains("IDisposable")) ?? false;
                if (!implementsDisposable)
                {
                    continue;
                }

                var finalizer = classDecl.Members.OfType<DestructorDeclarationSyntax>().FirstOrDefault();
                if (finalizer == null)
                {
                    continue;
                }

                // Check if the finalizer guards with a disposed flag (correct IDisposable pattern).
                // Use identifier-level checks only -> "disposed" as a bare word also appears in
                // comments like "/* no disposed guard */" and would produce false negatives.
                var finalizerText = finalizer.ToString();
                bool hasDisposedGuard = finalizerText.Contains("_disposed") || finalizerText.Contains("IsDisposed");
                if (!hasDisposedGuard)
                {
                    var loc = finalizer.GetLocation().GetLineSpan().StartLinePosition;
                    results.Add($"{target.Document.FilePath ?? target.Document.Name}:{loc.Line + 1} " + $"- Class '{classDecl.Identifier.Text}' implements IDisposable and declares a finalizer " + $"without a disposed-flag guard. The GC may call the finalizer after Dispose(), " + $"causing double-free. Add: if (_disposed) return; at the top of the finalizer.");
                }
            }
        }

        return results;
    }
}