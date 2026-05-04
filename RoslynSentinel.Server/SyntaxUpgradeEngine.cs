using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class SyntaxUpgradeEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public SyntaxUpgradeEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> UpgradeToModernGuardsAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ModernGuardClauses")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new ModernGuardRewriter();
        var newRoot = rewriter.Visit(root);

        // Return no-op message when no guard clause patterns were found to upgrade
        if (!rewriter.ChangesMade)
            return "// No if-throw guard clause patterns found to upgrade in this file.";

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> AddBracesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IDE0011")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new BracesRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UpgradePatternMatchingAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("PatternMatching")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new PatternMatchingRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseNameofExpressionAsync(string filePath, string contextSnippet, string? lineBefore = null, string? lineAfter = null, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("UnboundNameof")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        var text = await document.GetTextAsync(ct);
        var pos = ContextHelper.TryFindSnippetPosition(text, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0) return $"Error: {snippetError}";
        var node = root?.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, contextSnippet.Length))
            .DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));

        if (node != null)
        {
            var nameofExpr = SyntaxFactory.ParseExpression($"nameof({node.Token.ValueText})").WithTriviaFrom(node);
            return root!.ReplaceNode(node, nameofExpr).NormalizeWhitespace().ToFullString();
        }
        return root?.ToFullString() ?? "";
    }

    public async Task<string> ConvertSwitchToExpressionAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("IfToSwitch")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null) return root?.ToFullString() ?? "";

        var switchStmt = method.DescendantNodes().OfType<SwitchStatementSyntax>().FirstOrDefault();
        if (switchStmt == null) return root?.ToFullString() ?? "";

        var arms = switchStmt.Sections.Select(s => {
            var label = s.Labels.FirstOrDefault();
            var pattern = label is CaseSwitchLabelSyntax c ? SyntaxFactory.ConstantPattern(c.Value) : (PatternSyntax)SyntaxFactory.DiscardPattern();
            var result = s.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression ?? SyntaxFactory.ParseExpression("default");
            return SyntaxFactory.SwitchExpressionArm(pattern, result);
        });

        var switchExpr = SyntaxFactory.SwitchExpression(switchStmt.Expression, SyntaxFactory.SeparatedList(arms));
        var newReturn = SyntaxFactory.ReturnStatement(switchExpr);
        var newRoot = root!.ReplaceNode(switchStmt, newReturn);
        
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertSwitchExpressionToStatementAsync(string filePath, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        return root?.ToFullString() ?? "";
    }

    public async Task<string> CleanupImplicitSpansAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("ImplicitSpanCleanup")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var rewriter = new ImplicitSpanRewriter();
        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseFieldBackedPropertiesAsync(string filePath, CancellationToken ct = default)
    {
        if (!_config.IsFeatureEnabled("FieldBackedProperties")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var classNodes = root.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
        var replaceMap = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var classNode in classNodes)
        {
            var newMembers = new List<MemberDeclarationSyntax>();
            bool changed = false;

            foreach (var member in classNode.Members)
            {
                if (member is PropertyDeclarationSyntax prop && IsAutoProperty(prop))
                {
                    changed = true;
                    var propName = prop.Identifier.Text;
                    var fieldName = "_" + char.ToLowerInvariant(propName[0]) + propName.Substring(1);

                    var fieldDecl = SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(
                            prop.Type,
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(
                                    SyntaxFactory.Identifier(fieldName), null, prop.Initializer))))
                        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

                    var hasSetter = prop.AccessorList!.Accessors.Any(
                        a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration));

                    var getAccessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                        .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(fieldName)))
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

                    AccessorDeclarationSyntax? setAccessor = null;
                    if (hasSetter)
                    {
                        var setKind = prop.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.InitAccessorDeclaration))
                            ? SyntaxKind.InitAccessorDeclaration : SyntaxKind.SetAccessorDeclaration;
                        setAccessor = SyntaxFactory.AccessorDeclaration(setKind)
                            .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                                SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                    SyntaxFactory.IdentifierName(fieldName), SyntaxFactory.IdentifierName("value"))))
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                    }

                    var accessorList = hasSetter && setAccessor != null
                        ? SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { getAccessor, setAccessor }))
                        : SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(getAccessor));

                    var newProp = prop.WithAccessorList(accessorList)
                        .WithInitializer(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));

                    newMembers.Add(fieldDecl);
                    newMembers.Add(newProp);
                }
                else
                {
                    newMembers.Add(member);
                }
            }

            if (changed)
                replaceMap[classNode] = classNode.WithMembers(SyntaxFactory.List(newMembers));
        }

        if (replaceMap.Count == 0) return root.ToFullString();
        var newRoot = root.ReplaceNodes(replaceMap.Keys, (orig, _) => replaceMap[orig]);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static bool IsAutoProperty(PropertyDeclarationSyntax prop) =>
        prop.AccessorList != null &&
        prop.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null);

    public async Task<string> UpgradeToPrimaryConstructorAsync(string filePath, string className, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "// File not found.";

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return "// Could not parse file.";

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return "// Class not found.";

        // Find the constructor that consists entirely of field assignments
        var ctors = classNode.Members.OfType<ConstructorDeclarationSyntax>().ToList();
        var ctor = ctors.FirstOrDefault(c => c.Body != null && c.Body.Statements.Count > 0 &&
            c.Body.Statements.All(s => s is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax));

        if (ctor == null)
            return "// Cannot convert: no eligible constructor (must have only assignment statements).";

        if (ctor.Body!.Statements.Any(s =>
        {
            if (s is not ExpressionStatementSyntax es) return true;
            if (es.Expression is not AssignmentExpressionSyntax asgn) return true;
            // Right side must be a simple identifier (parameter name)
            return asgn.Right is not IdentifierNameSyntax;
        }))
            return "// Cannot convert: constructor has non-assignment logic.";

        // Build mapping: paramName -> fieldName (as in the class)
        var paramToField = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var stmt in ctor.Body.Statements)
        {
            if (stmt is not ExpressionStatementSyntax es) continue;
            if (es.Expression is not AssignmentExpressionSyntax asgn) continue;
            var paramName = (asgn.Right as IdentifierNameSyntax)?.Identifier.Text;
            if (paramName == null) continue;

            string fieldName;
            if (asgn.Left is MemberAccessExpressionSyntax ma && ma.Expression.ToString() == "this")
                fieldName = ma.Name.Identifier.Text;
            else if (asgn.Left is IdentifierNameSyntax fid)
                fieldName = fid.Identifier.Text;
            else
                continue;

            // Check this param exists in the ctor
            if (ctor.ParameterList.Parameters.Any(p => p.Identifier.Text == paramName))
                paramToField[paramName] = fieldName;
        }

        if (paramToField.Count == 0)
            return "// Cannot convert: could not map constructor parameters to fields.";

        // Verify fields exist as private readonly in the class
        var fieldToParam = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (param, field) in paramToField)
        {
            var fieldDecl = classNode.Members.OfType<FieldDeclarationSyntax>()
                .FirstOrDefault(f =>
                    f.Declaration.Variables.Any(v => v.Identifier.Text == field) &&
                    f.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)));
            if (fieldDecl == null) continue;
            fieldToParam[field] = param;
        }

        if (fieldToParam.Count == 0)
            return "// Cannot convert: no matching private readonly fields found.";

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
                return $"// Cannot convert: field '{field}' is assigned multiple times.";
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
                membersToRemove.Add(fieldDecl);
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
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> UseExceptionExpressionsAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) return string.Empty;

        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null) return root.ToFullString();

        var throwStmts = method.DescendantNodes().OfType<ThrowStatementSyntax>()
            .Where(t => t.Expression != null)
            .ToList();

        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var throwStmt in throwStmts)
        {
            if (throwStmt.Expression is not ObjectCreationExpressionSyntax objCreate) continue;

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

        if (!replacements.Any()) return root.ToFullString();

        var newRoot = root.ReplaceNodes(replacements.Keys, (orig, _) => replacements[orig]);
        return newRoot.ToFullString();
    }

    private class FieldToParamRewriter : CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, string> _map;
        public FieldToParamRewriter(Dictionary<string, string> map) { _map = map; }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if (_map.TryGetValue(name, out var replacement))
                return node.WithIdentifier(SyntaxFactory.Identifier(replacement).WithTriviaFrom(node.Identifier));
            return base.VisitIdentifierName(node);
        }
    }

    private class BracesRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
        {
            var newNode = base.VisitIfStatement(node) as IfStatementSyntax;
            if (newNode == null) return node;
            if (newNode.Statement is not BlockSyntax) newNode = newNode.WithStatement(SyntaxFactory.Block(newNode.Statement));
            if (newNode.Else != null && newNode.Else.Statement is not BlockSyntax && newNode.Else.Statement is not IfStatementSyntax)
                newNode = newNode.WithElse(newNode.Else.WithStatement(SyntaxFactory.Block(newNode.Else.Statement)));
            return newNode;
        }
    }

    private class ModernGuardRewriter : CSharpSyntaxRewriter
    {
        public bool ChangesMade { get; private set; }

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
                    string? helper = be2.Kind() switch {
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
