using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace RoslynSentinel.Server;

public record TestComplexityReport(string MethodName, int CyclomaticComplexity, List<string> ConditionalsToTest);
public record TestSkeletonReport(string FilePath, string Content);

public class TestingEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public TestingEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public async Task<TestComplexityReport> CalculateComplexityAsync(string filePath, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new Exception("Could not parse syntax root.");

        var methodNode = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == methodName);
        
        if (methodNode == null) throw new Exception($"Method {methodName} not found.");

        var conditionals = new List<string>();
        int complexity = 1;

        var descendants = methodNode.DescendantNodes().ToList();
        
        complexity += descendants.OfType<IfStatementSyntax>().Count();
        complexity += descendants.OfType<WhileStatementSyntax>().Count();
        complexity += descendants.OfType<ForStatementSyntax>().Count();
        complexity += descendants.OfType<ForEachStatementSyntax>().Count();
        complexity += descendants.OfType<CaseSwitchLabelSyntax>().Count();
        complexity += descendants.OfType<CatchClauseSyntax>().Count();
        complexity += descendants.OfType<ConditionalExpressionSyntax>().Count();
        complexity += descendants.OfType<BinaryExpressionSyntax>().Count(b => b.IsKind(SyntaxKind.LogicalAndExpression) || b.IsKind(SyntaxKind.LogicalOrExpression));

        var ifs = descendants.OfType<IfStatementSyntax>().Select(i => i.Condition.ToString());
        var switches = descendants.OfType<SwitchStatementSyntax>().Select(s => s.Expression.ToString());
        
        conditionals.AddRange(ifs.Select(c => $"If ({c}) is true/false"));
        conditionals.AddRange(switches.Select(s => $"Switch ({s}) covers all cases"));

        return new TestComplexityReport(methodName, complexity, conditionals);
    }

    public async Task<TestSkeletonReport> GenerateTestSkeletonAsync(string filePath, string className, string framework = "NUnit", CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) throw new Exception("File not found.");

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) throw new Exception("Could not parse syntax root.");

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        
        if (classNode == null) throw new Exception($"Class {className} not found.");

        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "Global";
        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)));

        var testClassName = $"{className}Tests";
        var testFilePath = Path.Combine(Path.GetDirectoryName(filePath)!, $"{testClassName}.cs");

        var testAttribute = framework.ToLowerInvariant() == "nunit" ? "[Test]" : "[Fact]";
        var classAttribute = framework.ToLowerInvariant() == "nunit" ? "[TestFixture]" : "";

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        if (framework.ToLowerInvariant() == "nunit") sb.AppendLine("using NUnit.Framework;");
        if (framework.ToLowerInvariant() == "xunit") sb.AppendLine("using Xunit;");
        sb.AppendLine($"using {ns};");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}.Tests");
        sb.AppendLine("{");
        
        if (!string.IsNullOrEmpty(classAttribute))
            sb.AppendLine($"    {classAttribute}");
            
        sb.AppendLine($"    public class {testClassName}");
        sb.AppendLine("    {");

        foreach (var method in publicMethods)
        {
            var testName = $"{method.Identifier.Text}_Should_ReturnExpectedResult_When_ValidInput";
            sb.AppendLine($"        {testAttribute}");
            sb.AppendLine($"        public void {testName}()");
            sb.AppendLine("        {");
            sb.AppendLine("            // Arrange");
            sb.AppendLine("            ");
            sb.AppendLine("            // Act");
            sb.AppendLine("            ");
            sb.AppendLine("            // Assert");
            if (framework.ToLowerInvariant() == "nunit") sb.AppendLine("            Assert.Fail(\"Test not implemented\");");
            if (framework.ToLowerInvariant() == "xunit") sb.AppendLine("            Assert.True(false, \"Test not implemented\");");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return new TestSkeletonReport(testFilePath, sb.ToString());
    }

    public async Task<string> AddBenchmarkStubAsync(string filePath, string className, string methodName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.GetDocumentIdsWithFilePath(filePath).Select(solution.GetDocument).FirstOrDefault();
        if (document == null) return "";

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null) return "";

        var sb = new StringBuilder();
        sb.AppendLine("using BenchmarkDotNet.Attributes;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}Benchmarks");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly {className} _instance = new();");
        sb.AppendLine();
        sb.AppendLine("    [Benchmark]");
        sb.AppendLine($"    public void Benchmark_{methodName}() => _instance.{methodName}();");
        sb.AppendLine("}");

        return sb.ToString();
    }
}
