using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public class ModernizationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;
    private readonly SentinelConfiguration _config;

    public ModernizationEngine(PersistentWorkspaceManager workspaceManager, SentinelConfiguration config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
    }

    public async Task<string> ClassToRecordAsync(string filePath, string className, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("ClassToRecord")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null) return root?.ToFullString() ?? "";

        // Extract properties to create positional parameters
        var properties = classNode.Members.OfType<PropertyDeclarationSyntax>().ToList();
        
        // Create positional parameters from properties
        var parameters = properties.Select(prop =>
            SyntaxFactory.Parameter(prop.Identifier).WithType(prop.Type)
        ).ToList();

        // Create record declaration with positional parameters
        var recordNode = SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), classNode.Identifier)
            .WithModifiers(classNode.Modifiers)
            .WithParameterList(parameters.Any() 
                ? SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters))
                : SyntaxFactory.ParameterList());

        var members = new List<MemberDeclarationSyntax>();
        
        // Convert auto-properties to init properties, preserving attributes
        foreach (var prop in properties)
        {
            var initProperty = SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier)
                .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                })));
            
            // Preserve attributes from original property
            if (prop.AttributeLists.Count > 0)
            {
                initProperty = initProperty.WithAttributeLists(prop.AttributeLists);
            }
            
            // Preserve initializer (default value)
            if (prop.Initializer != null)
            {
                initProperty = initProperty.WithInitializer(prop.Initializer);
            }
            
            members.Add(initProperty);
        }

        // Add non-property members (like methods)
        members.AddRange(classNode.Members.Where(m => m is not PropertyDeclarationSyntax));
        
        recordNode = recordNode.WithMembers(SyntaxFactory.List(members));

        var newRoot = root!.ReplaceNode(classNode, recordNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> RecordToClassAsync(string filePath, string recordName, CancellationToken cancellationToken = default)
    {
        if (!_config.IsFeatureEnabled("RecordToClass")) return string.Empty;
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return string.Empty;

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var recordNode = root?.DescendantNodes().OfType<RecordDeclarationSyntax>().FirstOrDefault(r => r.Identifier.Text == recordName);
        if (recordNode == null) return root?.ToFullString() ?? "";

        var classNode = SyntaxFactory.ClassDeclaration(recordNode.Identifier).WithModifiers(recordNode.Modifiers);
        
        // Preserve base types (interfaces, base classes)
        if (recordNode.BaseList != null)
        {
            classNode = classNode.WithBaseList(recordNode.BaseList);
        }
        
        var properties = new List<MemberDeclarationSyntax>();

        if (recordNode.ParameterList != null)
        {
            foreach (var parameter in recordNode.ParameterList.Parameters)
            {
                var property = SyntaxFactory.PropertyDeclaration(parameter.Type!, parameter.Identifier)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    })));
                properties.Add(property);
            }
        }

        properties.AddRange(recordNode.Members);
        classNode = classNode.WithMembers(SyntaxFactory.List(properties));

        var newRoot = root!.ReplaceNode(recordNode, classNode);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public async Task<string> ConvertMethodToExpressionBodyAsync(string filePath, string methodName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null) return "";
        var root = await document.GetSyntaxRootAsync(ct);
        var method = root?.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        
        if (method?.Body != null && method.Body.Statements.Count == 1)
        {
            var stmt = method.Body.Statements[0];
            ExpressionSyntax? expr = null;
            if (stmt is ReturnStatementSyntax ret) expr = ret.Expression;
            else if (stmt is ExpressionStatementSyntax es) expr = es.Expression;

            if (expr != null)
            {
                var newMethod = method.WithBody(null)
                    .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr))
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
                
                var newRoot = root!.ReplaceNode(method, newMethod);
                return newRoot.ToFullString();
            }
        }
        return root?.ToFullString() ?? "";
    }
}
