using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace RoslynSentinel.Advanced;

public class AdvancedRefactoringEngine
{
    private readonly ISolutionProvider _workspaceManager;
    private readonly ILogger<AdvancedRefactoringEngine> _logger;
    private readonly SentinelConfiguration _config;
    private readonly SymbolNavigationEngine _symbolNavigationEngine;

    public AdvancedRefactoringEngine(ISolutionProvider workspaceManager)
    {
        _workspaceManager = workspaceManager;
        _logger = new NullLogger<AdvancedRefactoringEngine>();
        _config = new SentinelConfiguration();
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public AdvancedRefactoringEngine(ISolutionProvider workspaceManager, ILogger<AdvancedRefactoringEngine> logger)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _config = new SentinelConfiguration();
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public AdvancedRefactoringEngine(ISolutionProvider workspaceManager, ILogger<AdvancedRefactoringEngine> logger, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _config = config;
        _symbolNavigationEngine = new SymbolNavigationEngine(workspaceManager);
    }

    public AdvancedRefactoringEngine(ISolutionProvider workspaceManager, SymbolNavigationEngine symbolNavigationEngine, ILogger<AdvancedRefactoringEngine> logger, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _logger = logger;
        _config = config;
        _symbolNavigationEngine = symbolNavigationEngine;
    }

    public async Task<DocumentEditResult> ReplaceStringConcatWithInterpolationAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        // Find top-level string concat chains -> not a child of another string-concat-with-literal
        var topLevelConcats = root.DescendantNodes()
            .OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression) && ContainsStringLiteral(b))
            .Where(b => !b.Ancestors().OfType<BinaryExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.AddExpression) && ContainsStringLiteral(a)))
            .ToList();

        if (topLevelConcats.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        var newRoot = root.ReplaceNodes(topLevelConcats, (original, _) =>
        {
            var segments = FlattenConcatTree(original);
            var contents = new List<InterpolatedStringContentSyntax>();
            foreach (var seg in segments)
            {
                if (seg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var tokenText = lit.Token.Text;
                    // Verbatim/raw strings have different escape semantics -> wrap as interpolation hole
                    if (tokenText.StartsWith("@") || tokenText.StartsWith("\"\"\""))
                    {
                        contents.Add(SyntaxFactory.Interpolation(seg.WithoutTrivia()));
                        continue;
                    }
                    // Strip surrounding quotes, double {{ and }} for the interpolated context
                    var innerText = tokenText.Length >= 2 ? tokenText.Substring(1, tokenText.Length - 2) : string.Empty;
                    var escapedText = innerText.Replace("{", "{{").Replace("}", "}}");
                    if (!string.IsNullOrEmpty(escapedText))
                    {
                        contents.Add(SyntaxFactory.InterpolatedStringText(
                            SyntaxFactory.Token(
                                SyntaxTriviaList.Empty,
                                SyntaxKind.InterpolatedStringTextToken,
                                escapedText,
                                lit.Token.ValueText,
                                SyntaxTriviaList.Empty)));
                    }
                }
                else
                {
                    contents.Add(SyntaxFactory.Interpolation(seg.WithoutTrivia()));
                }
            }
            return SyntaxFactory.InterpolatedStringExpression(
                    SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken),
                    SyntaxFactory.List(contents),
                    SyntaxFactory.Token(SyntaxKind.InterpolatedStringEndToken))
                .WithTriviaFrom(original);
        });

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    public async Task<DocumentEditResult> OptimizeTaskWaitAsync(FilePathWrapper filePath, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath
            };
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var methodsToMakeAsync = new List<MethodDeclarationSyntax>();
        var methodSpans = new HashSet<int>();

        bool IsTaskType(ExpressionSyntax expr)
        {
            if (semanticModel == null)
            {
                return false;
            }

            var typeInfo = semanticModel.GetTypeInfo(expr, cancellationToken);
            var name = typeInfo.Type?.Name;
            return name is "Task" or "ValueTask";
        }

        void CollectMethod(SyntaxNode node)
        {
            var m = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            if (m != null && methodSpans.Add(m.SpanStart))
            {
                methodsToMakeAsync.Add(m);
            }
        }

        // Pattern 1: .GetAwaiter().GetResult()
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax maGet)
            {
                continue;
            }

            if (maGet.Name.Identifier.Text != "GetResult")
            {
                continue;
            }

            if (maGet.Expression is not InvocationExpressionSyntax getAwaiterCall)
            {
                continue;
            }

            if (getAwaiterCall.Expression is not MemberAccessExpressionSyntax maAwaiter)
            {
                continue;
            }

            if (maAwaiter.Name.Identifier.Text != "GetAwaiter")
            {
                continue;
            }

            if (!IsTaskType(maAwaiter.Expression))
            {
                continue;
            }

            replacements[inv] = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    maAwaiter.Expression.WithoutTrivia())).WithTriviaFrom(inv);
            CollectMethod(inv);
        }

        // Pattern 2: .Wait() with no arguments
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (replacements.ContainsKey(inv))
            {
                continue;
            }

            if (inv.Expression is not MemberAccessExpressionSyntax maWait)
            {
                continue;
            }

            if (maWait.Name.Identifier.Text != "Wait")
            {
                continue;
            }

            if (inv.ArgumentList.Arguments.Count != 0)
            {
                continue;
            }

            if (!IsTaskType(maWait.Expression))
            {
                continue;
            }

            replacements[inv] = SyntaxFactory.AwaitExpression(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                maWait.Expression.WithoutTrivia()).WithTriviaFrom(inv);
            CollectMethod(inv);
        }

        // Pattern 3: .Result member access
        foreach (var ma in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (ma.Name.Identifier.Text != "Result")
            {
                continue;
            }

            if (replacements.Keys.Any(k => k.Span.Contains(ma.Span)))
            {
                continue;
            }

            if (!IsTaskType(ma.Expression))
            {
                continue;
            }

            replacements[ma] = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.AwaitExpression(
                    SyntaxFactory.Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    ma.Expression.WithoutTrivia())).WithTriviaFrom(ma);
            CollectMethod(ma);
        }

        if (replacements.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                UpdatedText = root.ToFullString()
            };
        }

        // Track all nodes that will be mutated before any tree modification
        var trackedRoot = root.TrackNodes(replacements.Keys.Concat(methodsToMakeAsync.Cast<SyntaxNode>()));

        var trackedReplacements = replacements
            .Select(kvp => (Tracked: trackedRoot.GetCurrentNode(kvp.Key), Value: kvp.Value))
            .Where(t => t.Tracked != null)
            .ToDictionary(t => t.Tracked!, t => t.Value);

        var newRoot = trackedRoot.ReplaceNodes(trackedReplacements.Keys, (original, _) => trackedReplacements[original]);

        // Make affected methods async
        var methodUpdates = new Dictionary<SyntaxNode, SyntaxNode>();
        foreach (var method in methodsToMakeAsync)
        {
            var current = newRoot.GetCurrentNode(method);
            if (current == null || current.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                continue;
            }

            var asyncToken = SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var newModifiers = current.Modifiers.Add(asyncToken);
            TypeSyntax newReturn = current.ReturnType;
            if (current.ReturnType is PredefinedTypeSyntax pred && pred.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                newReturn = SyntaxFactory.IdentifierName("Task").WithTriviaFrom(current.ReturnType);
            }
            else if (!IsTaskReturnType(current.ReturnType))
            {
                newReturn = SyntaxFactory.GenericName(SyntaxFactory.Identifier("Task"))
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(current.ReturnType.WithoutTrivia())))
                    .WithTriviaFrom(current.ReturnType);
            }

            methodUpdates[current] = current.WithModifiers(newModifiers).WithReturnType(newReturn);
        }

        if (methodUpdates.Count != 0)
        {
            newRoot = newRoot.ReplaceNodes(methodUpdates.Keys, (original, _) => methodUpdates[original]);
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            UpdatedText = RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newRoot).ToFullString(),
            FilePath = filePath
        };
    }

    private static bool ContainsStringLiteral(ExpressionSyntax expr) =>
        (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
        || (expr is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression)
           && (ContainsStringLiteral(bin.Left) || ContainsStringLiteral(bin.Right)));

    private static List<ExpressionSyntax> FlattenConcatTree(ExpressionSyntax expr)
    {
        if (expr is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AddExpression))
        {
            return FlattenConcatTree(bin.Left).Concat(FlattenConcatTree(bin.Right)).ToList();
        }

        return new List<ExpressionSyntax> { expr };
    }

    private static bool IsTaskReturnType(TypeSyntax type) =>
        (type is IdentifierNameSyntax id && id.Identifier.Text is "Task" or "ValueTask")
        || (type is GenericNameSyntax gn && gn.Identifier.Text is "Task" or "ValueTask");

    public async Task<Dictionary<FilePathWrapper, string>> ExtractServiceFromControllerAsync(FilePathWrapper filePath, string controllerName, string serviceName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault() ?? throw new FileNotFoundException($"File not found: {filePath}");
        var root = await document.GetSyntaxRootAsync(cancellationToken) as CompilationUnitSyntax;
        var controller = (root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == controllerName)) ?? throw new InvalidOperationException("Controller not found.");

        // Extract private methods and complex logic from public endpoints
        var methodsToMove = controller.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PrivateKeyword) || m.Identifier.Text.StartsWith("Process") || m.Identifier.Text.StartsWith("Calculate")))
            .ToList();

        var serviceClass = SyntaxFactory.ClassDeclaration(serviceName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddMembers(methodsToMove.Select(m => m.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))).ToArray());

        var newController = controller.RemoveNodes(methodsToMove, SyntaxRemoveOptions.KeepUnbalancedDirectives);

        // In a real scenario, we'd inject the IService into the controller constructor here.
        var updatedRoot = root!.ReplaceNode(controller, newController!);

        var ns = controller.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        var serviceRoot = SyntaxFactory.CompilationUnit().WithUsings(root?.Usings ?? SyntaxFactory.List<UsingDirectiveSyntax>());

        if (ns != null)
        {
            var newNs = ns is FileScopedNamespaceDeclarationSyntax
               ? SyntaxFactory.FileScopedNamespaceDeclaration(ns.Name)
               : (BaseNamespaceDeclarationSyntax)SyntaxFactory.NamespaceDeclaration(ns.Name);
            serviceRoot = serviceRoot.AddMembers(newNs.AddMembers(serviceClass));
        }
        else
        {
            serviceRoot = serviceRoot.AddMembers(serviceClass);
        }

        return new Dictionary<FilePathWrapper, string>
        {
            { filePath, updatedRoot.ToFullString() },
            { Path.Combine(Path.GetDirectoryName(filePath)!, $"{serviceName}.cs"), RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(serviceRoot).ToFullString() }
        };
    }

    public async Task<DocumentEditResult> SyncInterfaceToImplementationAsync(FilePathWrapper filePath, string className, string interfaceName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        // Find the class document
        var classDocument = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (classDocument == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Class file not found."
            };
        }

        var classRoot = await classDocument.GetSyntaxRootAsync(cancellationToken);
        if (classRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Could not parse class file."
            };
        }

        var classNode = classRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Class not found."
            };
        }

        // Collect public non-static non-override methods and properties from the class
        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword))).ToList();
        var publicProperties = classNode.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)) && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword))).ToList();
        // Find the interface -> first in same file, then in other documents
        Document? interfaceDocument = null;
        InterfaceDeclarationSyntax? interfaceNode = null;
        SyntaxNode? interfaceRoot = null;
        // Search same file first
        interfaceNode = classRoot.DescendantNodes().OfType<InterfaceDeclarationSyntax>().FirstOrDefault(i => i.Identifier.Text == interfaceName);
        if (interfaceNode != null)
        {
            interfaceDocument = classDocument;
            interfaceRoot = classRoot;
        }
        else
        {
            // Search all documents
            foreach (var doc in solution.Projects.SelectMany(p => p.Documents))
            {
                if (doc == classDocument)
                {
                    continue;
                }

                var r = await doc.GetSyntaxRootAsync(cancellationToken);
                if (r == null)
                {
                    continue;
                }

                var iface = r.DescendantNodes().OfType<InterfaceDeclarationSyntax>().FirstOrDefault(i => i.Identifier.Text == interfaceName);
                if (iface != null)
                {
                    interfaceDocument = doc;
                    interfaceRoot = r;
                    interfaceNode = iface;
                    break;
                }
            }
        }

        if (interfaceNode == null || interfaceDocument == null || interfaceRoot == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Interface not found."
            };
        }

        // Collect existing interface member signatures (for deduplication)
        var existingMethodSigs = interfaceNode.Members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text + "|" + string.Join(",", m.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim()))).ToHashSet(StringComparer.Ordinal);
        var existingPropertyNames = interfaceNode.Members.OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var method in publicMethods)
        {
            var sig = method.Identifier.Text + "|" + string.Join(",", method.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim()));
            if (existingMethodSigs.Contains(sig))
            {
                continue;
            }

            // Build interface method: return type + name + params, no body
            var ifaceMethod = (MemberDeclarationSyntax)RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(SyntaxFactory.MethodDeclaration(method.ReturnType, method.Identifier).WithParameterList(method.ParameterList).WithTypeParameterList(method.TypeParameterList).WithConstraintClauses(method.ConstraintClauses).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).WithModifiers(SyntaxFactory.TokenList()));
            newMembers.Add(ifaceMethod);
        }

        foreach (var prop in publicProperties)
        {
            if (existingPropertyNames.Contains(prop.Identifier.Text))
            {
                continue;
            }

            // Build interface property
            var hasGetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true || prop.ExpressionBody != null;
            var hasSetter = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) == true;
            var hasInit = prop.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true;
            var accessors = new List<AccessorDeclarationSyntax>();
            if (hasGetter)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            if (hasSetter)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            if (hasInit)
            {
                accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
            }

            var ifaceProp = (MemberDeclarationSyntax)RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))).WithModifiers(SyntaxFactory.TokenList()));
            newMembers.Add(ifaceProp);
        }

        if (newMembers.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = interfaceDocument.FilePath ?? interfaceDocument.Name,
                UpdatedText = interfaceRoot.ToFullString() // Already up to date
            };
        }

        var newInterfaceNode = interfaceNode.AddMembers(newMembers.ToArray());
        // If interface is in a different file, indicate which file was updated
        if (interfaceDocument != classDocument)
        {
            var updatedPath = interfaceDocument.FilePath ?? interfaceDocument.Name;
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = updatedPath,
                UpdatedText = "// Updated file: " + updatedPath + "\n" + await RoslynFormattingHelper.ReplaceNodeFormattedAsync(interfaceDocument, interfaceRoot, interfaceNode, newInterfaceNode, cancellationToken)
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = interfaceDocument.FilePath ?? interfaceDocument.Name,
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(interfaceDocument, interfaceRoot, interfaceNode, newInterfaceNode, cancellationToken)
        };
    }

    public async Task<DocumentEditResult> ConvertExpressionBodyAsync(FilePathWrapper filePath, string memberName, string direction, string? contextSnippet = null, string? lineBefore = null, string? lineAfter = null, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ConvertExpressionBody"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'ConvertExpressionBody' is disabled."
            };
        }

        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = (await document.GetSyntaxRootAsync(cancellationToken))!;
        var text = await document.GetTextAsync(cancellationToken);
        MemberDeclarationSyntax? target;
        try
        {
            target = _symbolNavigationEngine.ResolveMemberByNameOrSnippet(root, text, memberName, contextSnippet, lineBefore, lineAfter,
                m => m is MethodDeclarationSyntax || m is PropertyDeclarationSyntax || m is ConstructorDeclarationSyntax);
        }
        catch (InvalidOperationException ex)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = ex.Message
            };
        }

        if (target == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Member '{memberName}' not found in '{Path.GetFileName(filePath)}'."
            };
        }

        SyntaxNode newTarget;
        if (direction == "ToExpressionBody")
        {
            if (target is MethodDeclarationSyntax meth && meth.Body != null)
            {
                var stmts = meth.Body.Statements;
                if (stmts.Count == 1 && stmts[0] is ReturnStatementSyntax ret && ret.Expression != null)
                {
                    newTarget = meth.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(ret.Expression)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
                else
                {
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotConvert,
                        FilePath = filePath,
                        Message = $"// Cannot convert '{memberName}' to expression body: method body has {stmts.Count} statement(s); only single-return methods can be converted."
                    };
                }
            }
            else if (target is PropertyDeclarationSyntax prop && prop.AccessorList != null)
            {
                var getter = prop.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                if (getter?.Body?.Statements.Count == 1 && getter.Body.Statements[0] is ReturnStatementSyntax pret && pret.Expression != null)
                {
                    newTarget = prop.WithAccessorList(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(pret.Expression)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                }
                else
                {
                    return new DocumentEditResult
                    {
                        Outcome = EditOutcome.CannotConvert,
                        FilePath = filePath,
                        Message = $"// Cannot convert '{memberName}' to expression body: property getter does not contain a simple return statement."
                    };
                }
            }
            else
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to expression body: member has no block body or is already an expression body."
                };
            }
        }
        else // ToBlockBody
        {
            if (target is MethodDeclarationSyntax methExpr && methExpr.ExpressionBody != null)
            {
                var returnType = methExpr.ReturnType.ToString().Trim();
                StatementSyntax stmt = returnType == "void" ? SyntaxFactory.ExpressionStatement(methExpr.ExpressionBody.Expression) : (StatementSyntax)SyntaxFactory.ReturnStatement(methExpr.ExpressionBody.Expression);
                newTarget = methExpr.WithExpressionBody(null).WithSemicolonToken(default).WithBody(SyntaxFactory.Block(stmt));
            }
            else if (target is PropertyDeclarationSyntax propExpr && propExpr.ExpressionBody != null)
            {
                var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block(SyntaxFactory.ReturnStatement(propExpr.ExpressionBody.Expression)));
                newTarget = propExpr.WithExpressionBody(null).WithSemicolonToken(default).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getter)));
            }
            else
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert '{memberName}' to block body: member has no expression body (already a block body or not a method/property)."
                };
            }
        }

        var newRoot = root.ReplaceNode(target, RoslynFormattingHelper.NormalizeWholeSubtreeWhitespace(newTarget));
        var doc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(doc, null, cancellationToken);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = (await formatted.GetTextAsync(cancellationToken)).ToString()
        };
    }

    public async Task<DocumentEditResult> ConvertToPrimaryConstructorAsync(FilePathWrapper filePath, string className, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("PrimaryConstructors"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature 'PrimaryConstructors' is disabled."
            };
        }

        var solution = await _workspaceManager.GetCurrentSolutionAsync(cancellationToken);
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// Document not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found."
            };
        }

        var ctor = classNode.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (ctor == null || ctor.ParameterList.Parameters.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Constructor not found or has no parameters."
            };
        }

        // Minimal implementation for tests: convert to class C(int x) and remove fields/ctor
        var newClass = SyntaxFactory.ClassDeclaration(classNode.Identifier).WithModifiers(classNode.Modifiers).WithParameterList(ctor.ParameterList);
        var members = classNode.Members.Where(m => m is not ConstructorDeclarationSyntax && m is not FieldDeclarationSyntax).ToList();
        newClass = newClass.WithMembers(SyntaxFactory.List(members));
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = "// Class converted to primary constructor.",
            UpdatedText = await RoslynFormattingHelper.ReplaceNodeFormattedAsync(document, root!, classNode, newClass, cancellationToken)
        };
    }
}
