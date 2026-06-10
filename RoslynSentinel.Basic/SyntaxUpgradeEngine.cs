using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using RoslynSentinel.Common;

namespace RoslynSentinel.Basic;

public class SyntaxUpgradeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public SyntaxUpgradeEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<DocumentEditResult> UpgradeToModernGuardsAsync(FilePath filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ModernGuardClauses"))
        {
            return new DocumentEditResult()
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature ModernGuardClauses is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new ModernGuardRewriter();
        var newRoot = rewriter.Visit(root);

        // Return no-op message when no guard clause patterns were found to upgrade
        if (!rewriter.ChangesMade)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// No if-throw guard clause patterns found to upgrade in this file."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> AddBracesAsync(FilePath filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IDE0011"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Feature IDE0011 is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new BracesRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> UpgradePatternMatchingAsync(FilePath filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("PatternMatching"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Feature PatternMatching is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new PatternMatchingRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> UseNameofExpressionAsync(FilePath filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("UnboundNameof"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Feature UnboundNameof is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = $"// Error: {snippetError}"
            };
        }

        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length))
            .DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        if (node != null)
        {
            var nameofExpr = SyntaxFactory.ParseExpression($"nameof({node.Token.ValueText})").WithTriviaFrom(node);
            var newRoot = root!.ReplaceNode(node, nameofExpr);
            return new DocumentEditResult
            {
                Outcome = EditOutcome.Modified,
                FilePath = filePath,
                UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
            };
        }
        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// No string literal found."
        };
    }

    public async Task<DocumentEditResult> ConvertSwitchToExpressionAsync(FilePath filePath, string methodName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IfToSwitch"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Feature IfToSwitch is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);

        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Method not found."
            };
        }

        var switchStmt = method.DescendantNodes().OfType<SwitchStatementSyntax>().FirstOrDefault();
        if (switchStmt == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Switch statement not found."
            };
        }

        var arms = switchStmt.Sections.Select(s =>
        {
            var label = s.Labels.FirstOrDefault();
            var pattern = label is CaseSwitchLabelSyntax c ? SyntaxFactory.ConstantPattern(c.Value) : (PatternSyntax)SyntaxFactory.DiscardPattern();
            var result = s.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression ?? SyntaxFactory.ParseExpression("default");
            return SyntaxFactory.SwitchExpressionArm(pattern, result);
        });

        var switchExpr = SyntaxFactory.SwitchExpression(switchStmt.Expression, SyntaxFactory.SeparatedList(arms));
        var newReturn = SyntaxFactory.ReturnStatement(switchExpr);
        var newRoot = root!.ReplaceNode(switchStmt, newReturn);

        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> ConvertSwitchExpressionToStatementAsync(FilePath filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        return new DocumentEditResult
        {
            Outcome = EditOutcome.CannotEdit,
            FilePath = filePath,
            Message = "// Conversion not implemented."
        };
    }

    public async Task<DocumentEditResult> CleanupImplicitSpansAsync(FilePath filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ImplicitSpanCleanup"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// Feature ImplicitSpanCleanup is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotEdit,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        var rewriter = new ImplicitSpanRewriter();
        var newRoot = rewriter.Visit(root);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> UseFieldBackedPropertiesAsync(FilePath filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("FieldBackedProperties"))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.FeatureDisabled,
                FilePath = filePath,
                Message = "// Feature FieldBackedProperties is not enabled."
            };
        }

        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// File not found in workspace."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Syntax root not found."
            };
        }

        // Find private backing-field + property pairs per class and collapse them to auto-properties.
        // A "pair" is: a private field `_foo` of type T  +  a property `Foo` of type T whose
        // get/set accessors read/write only that field (expression-body style).
        var classNodes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var classNode in classNodes)
        {
            // Index backing fields: fieldName -> FieldDeclarationSyntax
            var backingFields = classNode.Members
                .OfType<FieldDeclarationSyntax>()
                .Where(f => f.Modifiers.Any(SyntaxKind.PrivateKeyword) &&
                            !f.Modifiers.Any(SyntaxKind.StaticKeyword) &&
                            !f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))
                .SelectMany(f => f.Declaration.Variables.Select(v => (Name: v.Identifier.Text, Field: f, Var: v)))
                .ToDictionary(x => x.Name, x => x);

            var fieldsToRemove = new HashSet<FieldDeclarationSyntax>();
            var propReplacements = new Dictionary<PropertyDeclarationSyntax, PropertyDeclarationSyntax>();

            foreach (var prop in classNode.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (prop.AccessorList == null)
                {
                    continue;
                }

                var accessors = prop.AccessorList.Accessors;

                // Expect exactly get + set (or get + init), both expression-bodied, no custom body
                var getAcc = accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
                var setAcc = accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

                if (getAcc == null || getAcc.ExpressionBody == null)
                {
                    continue;
                }

                if (accessors.Count == 2 && setAcc == null)
                {
                    continue;
                }

                if (accessors.Count > 2)
                {
                    continue;
                }

                // get accessor must return the backing field: => _fieldName
                var getExpr = getAcc.ExpressionBody!.Expression.ToString().Trim();

                // Expect camelCase version of property name with leading underscore
                var expectedFieldName = "_" + char.ToLowerInvariant(prop.Identifier.Text[0]) + prop.Identifier.Text.Substring(1);
                if (getExpr != expectedFieldName)
                {
                    continue;
                }

                if (!backingFields.TryGetValue(expectedFieldName, out var fieldInfo))
                {
                    continue;
                }

                // set/init accessor must assign: => _fieldName = value
                if (setAcc != null)
                {
                    if (setAcc.ExpressionBody == null)
                    {
                        continue;
                    }

                    var setExpr = setAcc.ExpressionBody.Expression.ToString().Trim();
                    if (setExpr != $"{expectedFieldName} = value")
                    {
                        continue;
                    }
                }

                // Field type must match property type
                var fieldType = fieldInfo.Field.Declaration.Type.ToString().Trim();
                var propType = prop.Type.ToString().Trim();
                if (fieldType != propType)
                {
                    continue;
                }

                // Build auto-property: `public T Foo { get; set; }` (or `get; init;`)
                var initOrSet = setAcc?.IsKind(SyntaxKind.InitAccessorDeclaration) == true
                    ? SyntaxKind.InitAccessorDeclaration
                    : SyntaxKind.SetAccessorDeclaration;

                var newGetAcc = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                var autoAccessors = setAcc != null
                    ? new[]
                    {
                        newGetAcc,
                        SyntaxFactory.AccessorDeclaration(initOrSet)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    }
                    : new[] { newGetAcc };

                // Transfer initializer from backing field to auto-property if present
                var fieldInitializer = fieldInfo.Var.Initializer;

                var autoProp = prop
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(autoAccessors)))
                    .WithInitializer(fieldInitializer)
                    .WithSemicolonToken(fieldInitializer != null
                        ? SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                        : SyntaxFactory.Token(SyntaxKind.None));

                propReplacements[prop] = autoProp;
                fieldsToRemove.Add(fieldInfo.Field);
            }

            if (propReplacements.Count == 0)
            {
                continue;
            }

            // Rebuild the class without the removed backing fields, with updated auto-properties
            var newMembers = classNode.Members
                .Where(m => !(m is FieldDeclarationSyntax f && fieldsToRemove.Contains(f)))
                .Select(m => m is PropertyDeclarationSyntax p && propReplacements.TryGetValue(p, out var r) ? r : m)
                .ToList();

            replaceMap[classNode] = classNode.WithMembers(SyntaxFactory.List(newMembers));
        }

        if (replaceMap.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.NoChange,
                FilePath = filePath,
                UpdatedText = root.ToFullString()
            };
        }

        var newRoot = root.ReplaceNodes(replaceMap.Keys, (orig, _) => replaceMap[orig]);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            UpdatedText = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax prop) =>
        prop.AccessorList != null &&
        prop.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);

    public async Task<DocumentEditResult> UpgradeToPrimaryConstructorAsync(FilePath filePath, string className, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.DocumentNotFound,
                FilePath = filePath,
                Message = "// File not found."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Could not parse file."
            };
        }

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Class not found."
            };
        }

        // Find the constructor that consists entirely of field assignments
        var ctors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        var ctor = ctors.FirstOrDefault(c => c.Body != null && c.Body.Statements.Count > 0 &&
            c.Body.Statements.All(s => s is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax));

        if (ctor == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: no eligible constructor (must have only assignment statements)."
            };
        }

        if (ctor.Body!.Statements.Any(s =>
        {
            if (s is not ExpressionStatementSyntax es)
            {
                return true;
            }

            if (es.Expression is not AssignmentExpressionSyntax asgn)
            {
                return true;
            }
            // Right side must be a simple identifier (parameter name)
            return asgn.Right is not IdentifierNameSyntax;
        }))
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: constructor has non-assignment logic."
            };
        }

        // Build mapping: paramName -> fieldName (as in the class)
        var paramToField = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stmt in ctor.Body.Statements)
        {
            if (stmt is not ExpressionStatementSyntax es)
            {
                continue;
            }

            if (es.Expression is not AssignmentExpressionSyntax asgn)
            {
                continue;
            }

            var paramName = (asgn.Right as IdentifierNameSyntax)?.Identifier.Text;
            if (paramName == null)
            {
                continue;
            }

            string fieldName;
            if (asgn.Left is MemberAccessExpressionSyntax ma && ma.Expression.ToString() == "this")
            {
                fieldName = ma.Name.Identifier.Text;
            }
            else if (asgn.Left is IdentifierNameSyntax fid)
            {
                fieldName = fid.Identifier.Text;
            }
            else
            {
                continue;
            }

            // Check this param exists in the ctor
            if (ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == paramName))
            {
                paramToField[paramName] = fieldName;
            }
        }

        if (paramToField.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: could not map constructor parameters to fields."
            };
        }

        // Verify fields exist as private readonly in the class
        var fieldToParam = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (param, field) in paramToField)
        {
            var fieldDecl = classNode.Members.OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f =>
                    f.Declaration.Variables.Any(v => v.Identifier.Text == field) &&
                    f.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)));
            if (fieldDecl == null)
            {
                continue;
            }

            fieldToParam[field] = param;
        }

        if (fieldToParam.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.CannotConvert,
                FilePath = filePath,
                Message = "// Cannot convert: no matching private readonly fields found."
            };
        }

        // Verify each mapped field is assigned exactly once (in this ctor only)
        // and is not assigned in static initializers
        foreach (var field in fieldToParam.Keys)
        {
            var assignmentCount = classNode.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Count(a =>
                {
                    var lhsName = a.Left is IdentifierNameSyntax id ? id.Identifier.Text :
                                  a.Left is MemberAccessExpressionSyntax ma2 && ma2.Expression.ToString() == "this" ? ma2.Name.Identifier.Text : null;
                    return lhsName == field;
                });
            if (assignmentCount > 1)
            {
                return new DocumentEditResult
                {
                    Outcome = EditOutcome.CannotConvert,
                    FilePath = filePath,
                    Message = $"// Cannot convert: field '{field}' is assigned multiple times."
                };
            }
        }

        // Build the new primary constructor parameter list
        // Start from existing ctor params, keeping only those that are mapped
        var newParams = ctor.ParameterList.Parameters
            .Where(p => paramToField.ContainsKey(p.Identifier.Text))
            .ToList();

        // Also keep params not mapped to fields
        var unmappedParams = ctor.ParameterList.Parameters
            .Where(p => !paramToField.ContainsKey(p.Identifier.Text))
            .ToList();
        newParams.AddRange(unmappedParams);

        var paramListSyntax = SyntaxFactory.ParameterList(
            SyntaxFactory.SeparatedList(newParams.Select(p => p.WithoutTrivia())));

        // Rewrite field usages: _field -> param, field -> param  (within the class body except the ctor and field decls)
        // Build a rewriter that substitutes fieldNames with parameter names
        var rewriteMap = fieldToParam; // field -> param

        // Remove the constructor and the field declarations
        var fieldsToRemove = new HashSet<string>(fieldToParam.Keys);
        var membersToRemove = new List<MemberDeclarationSyntax>();
        membersToRemove.Add(ctor);
        foreach (var fieldDecl in classNode.Members.OfType<FieldDeclarationSyntax>())
        {
            if (fieldDecl.Declaration.Variables.All(v => fieldsToRemove.Contains(v.Identifier.Text)))
            {
                membersToRemove.Add(fieldDecl);
            }
        }

        var newMembers = classNode.Members.Where(m => !membersToRemove.Contains(m)).ToList();

        // Rewrite identifiers: field references -> param names
        var newClassNode = classNode.WithMembers(SyntaxFactory.List(newMembers))
            .WithIdentifier(classNode.Identifier)
            .WithParameterList(paramListSyntax);

        // Rewrite all IdentifierNameSyntax references of the field names to param names
        var rewriter = new FieldToParamRewriter(rewriteMap);
        newClassNode = (ClassDeclarationSyntax)rewriter.Visit(newClassNode);

        var newRoot = root.ReplaceNode(classNode, newClassNode);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> UpgradeToFileScopedNamespaceAsync(FilePath filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// File not found in workspace."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Could not retrieve syntax root."
            };
        }

        if (root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().Any())
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Already uses file-scoped namespace declaration."
            };
        }

        var nsDecl = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsDecl == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// No block-form namespace declaration found in this file."
            };
        }

        // Preserve all members, usings, and extern aliases from the block namespace
        var fileScopedNs = SyntaxFactory.FileScopedNamespaceDeclaration(nsDecl.Name)
            .WithExterns(nsDecl.Externs)
            .WithUsings(nsDecl.Usings)
            .WithMembers(nsDecl.Members)
            .WithLeadingTrivia(nsDecl.GetLeadingTrivia())
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed));

        var newRoot = root.ReplaceNode(nsDecl, fileScopedNs);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.NormalizeWhitespace().ToFullString()
        };
    }

    public async Task<DocumentEditResult> UseExceptionExpressionsAsync(FilePath filePath, string methodName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// File not found in workspace."
            };
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// Could not retrieve syntax root."
            };
        }

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = $"// Method '{methodName}' not found in file."
            };
        }

        var throwStmts = method.DescendantNodes().OfType<ThrowStatementSyntax>()
            .Where(t => t.Expression != null)
            .ToList();

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var throwStmt in throwStmts)
        {
            if (throwStmt.Expression is not ObjectCreationExpressionSyntax objCreate)
            {
                continue;
            }

            var typeName = objCreate.Type.ToString();
            var args = objCreate.ArgumentList?.Arguments ?? default;

            if (typeName == "ArgumentNullException" && args.Count >= 1)
            {
                // throw new ArgumentNullException(nameof(x)) → ArgumentNullException.ThrowIfNull(x);
                var nameofArg = args[0].Expression;
                string? paramName = null;
                if (nameofArg is InvocationExpressionSyntax nameofInv &&
                    nameofInv.Expression is IdentifierNameSyntax nameofId &&
                    nameofId.Identifier.Text == "nameof")
                {
                    paramName = nameofInv.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
                }

                if (paramName != null)
                {
                    var replacement = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("ArgumentNullException"),
                                SyntaxFactory.IdentifierName("ThrowIfNull")),
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(paramName))))))
                        .WithLeadingTrivia(throwStmt.GetLeadingTrivia())
                        .WithTrailingTrivia(throwStmt.GetTrailingTrivia());
                    replacements[throwStmt] = replacement;
                }
            }
            else if (typeName == "ArgumentOutOfRangeException" && args.Count >= 1)
            {
                var nameofArg = args[0].Expression;
                string? paramName = null;
                if (nameofArg is InvocationExpressionSyntax nameofInv &&
                    nameofInv.Expression is IdentifierNameSyntax nameofId &&
                    nameofId.Identifier.Text == "nameof")
                {
                    paramName = nameofInv.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString();
                }

                if (paramName != null)
                {
                    var replacement = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("ArgumentOutOfRangeException"),
                                SyntaxFactory.IdentifierName("ThrowIfNegative")),
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(paramName))))))
                        .WithLeadingTrivia(throwStmt.GetLeadingTrivia())
                        .WithTrailingTrivia(throwStmt.GetTrailingTrivia());
                    replacements[throwStmt] = replacement;
                }
            }
        }

        if (replacements.Count == 0)
        {
            return new DocumentEditResult
            {
                Outcome = EditOutcome.TargetNotFound,
                FilePath = filePath,
                Message = "// No replacements found."
            };
        }

        var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
        return new DocumentEditResult
        {
            Outcome = EditOutcome.Modified,
            FilePath = filePath,
            Message = newRoot.ToFullString()
        };
    }

    private class FieldToParamRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _map;
        public FieldToParamRewriter(Dictionary<string, string> map)
        {
            _map = map;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if (_map.TryGetValue(name, out var replacement))
            {
                return node.WithIdentifier(SyntaxFactory.Identifier(replacement).WithTriviaFrom(node.Identifier));
            }

            return base.VisitIdentifierName(node);
        }
    }

    private class BracesRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var newNode = base.VisitIfStatement(node) as IfStatementSyntax;
            if (newNode == null)
            {
                return node;
            }

            if (newNode.Statement is not BlockSyntax)
            {
                newNode = newNode.WithStatement(SyntaxFactory.Block(newNode.Statement));
            }

            if (newNode.Else != null && newNode.Else.Statement is not BlockSyntax && newNode.Else.Statement is not IfStatementSyntax)
            {
                newNode = newNode.WithElse(newNode.Else.WithStatement(SyntaxFactory.Block(newNode.Else.Statement)));
            }

            return newNode;
        }
    }

    private class ModernGuardRewriter : CSharpSyntaxRewriter
    {
        public bool ChangesMade
        {
            get; private set;
        }

        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var throwStmt = node.Statement is ThrowStatementSyntax t ? t :
                            node.Statement is BlockSyntax b && b.Statements.Count == 1 && b.Statements[0] is ThrowStatementSyntax t2 ? t2 : null;

            if (throwStmt != null && throwStmt.Expression is ObjectCreationExpressionSyntax oce)
            {
                var type = oce.Type.ToString();
                var args = oce.ArgumentList?.Arguments;
                var varName = args?.FirstOrDefault()?.Expression.ToString().Replace("nameof(", "").Replace(")", "") ?? "";

                if (type == "ArgumentNullException" && node.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.EqualsExpression) && be.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    ChangesMade = true;
                    return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentNullException"), SyntaxFactory.IdentifierName("ThrowIfNull")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(varName)))))).WithTriviaFrom(node);
                }

                if (type == "ArgumentOutOfRangeException" && node.Condition is BinaryExpressionSyntax be2)
                {
                    string? helper = be2.Kind() switch
                    {
                        SyntaxKind.LessThanExpression when be2.Right.ToString() == "0" => "ThrowIfNegative",
                        SyntaxKind.LessThanOrEqualExpression when be2.Right.ToString() == "0" => "ThrowIfNegativeOrZero",
                        SyntaxKind.EqualsExpression when be2.Right.ToString() == "0" => "ThrowIfZero",
                        SyntaxKind.GreaterThanExpression => "ThrowIfGreaterThan",
                        SyntaxKind.GreaterThanOrEqualExpression => "ThrowIfGreaterThanOrEqual",
                        SyntaxKind.LessThanExpression => "ThrowIfLessThan",
                        SyntaxKind.LessThanOrEqualExpression => "ThrowIfLessThanOrEqual",
                        _ => null
                    };

                    if (helper != null)
                    {
                        ChangesMade = true;
                        var argName = be2.Left.ToString();
                        var argsList = new List<ArgumentSyntax> { SyntaxFactory.Argument(SyntaxFactory.IdentifierName(argName)) };
                        if (helper.Contains("Greater") || helper.Contains("Less"))
                        {
                            argsList.Add(SyntaxFactory.Argument(be2.Right));
                        }

                        return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentOutOfRangeException"), SyntaxFactory.IdentifierName(helper)),
                            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(argsList)))).WithTriviaFrom(node);
                    }
                }

                if (type == "ArgumentException" && node.Condition is InvocationExpressionSyntax ies && ies.Expression.ToString().Contains("IsNullOrEmpty"))
                {
                    ChangesMade = true;
                    var argName = ies.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? varName;
                    return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentException"), SyntaxFactory.IdentifierName("ThrowIfNullOrEmpty")),
                        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(argName)))))).WithTriviaFrom(node);
                }
            }
            return base.VisitIfStatement(node);
        }
    }

    private class PatternMatchingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            if (node.Condition is BinaryExpressionSyntax be && be.IsKind(SyntaxKind.IsExpression))
            {
                var block = node.Statement as BlockSyntax;
                if (block != null && block.Statements.Count > 0)
                {
                    var first = block.Statements[0] as LocalDeclarationStatementSyntax;
                    if (first != null && first.Declaration.Variables.Count == 1)
                    {
                        var variable = first.Declaration.Variables[0];
                        if (variable.Initializer?.Value is CastExpressionSyntax cast && cast.Type.ToString() == be.Right.ToString() && cast.Expression.ToString() == be.Left.ToString())
                        {
                            var newCondition = SyntaxFactory.IsPatternExpression(be.Left, SyntaxFactory.DeclarationPattern((TypeSyntax)be.Right, SyntaxFactory.SingleVariableDesignation(variable.Identifier)));
                            var newBlock = block.WithStatements(block.Statements.RemoveAt(0));
                            return node.WithCondition(newCondition).WithStatement(newBlock);
                        }
                    }
                }
            }
            return base.VisitIfStatement(node);
        }
    }

    private class ImplicitSpanRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            if (node.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "AsSpan")
            {
                return ma.Expression.WithTriviaFrom(node);
            }
            return base.VisitInvocationExpression(node);
        }
    }
}
