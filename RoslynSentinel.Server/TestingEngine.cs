using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace RoslynSentinel.Server;

public record TestComplexityReport(string MethodName, int CyclomaticComplexity, List<string> ConditionalsToTest);
public record TestSkeletonReport(string FilePath, string Content);
public record TestScaffoldResult(
    string ClassName,
    string TestClassName,
    string Namespace,
    string Code
);

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
            bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            bool returnsTask = method.ReturnType.ToString().StartsWith("Task");
            bool needsAsync = isAsync || returnsTask;
            sb.AppendLine($"        {testAttribute}");
            sb.AppendLine(needsAsync
                ? $"        public async Task {testName}()"
                : $"        public void {testName}()");
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

    public async Task<TestScaffoldResult> GenerateTestScaffoldAsync(string filePath, string className, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath == filePath || d.Name == filePath);

        if (document == null) throw new Exception($"File not found: {filePath}");

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null) throw new Exception("Could not parse syntax root.");

        var classNode = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);

        if (classNode == null) throw new Exception($"Class '{className}' not found in {filePath}");

        var ns = classNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "Global";
        var testClassName = $"{className}Tests";
        var testNamespace = $"{ns}.Tests";

        var ctorParams = classNode.Members
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault()?.ParameterList.Parameters.ToList() ?? new List<ParameterSyntax>();

        var interfaceParams = ctorParams
            .Where(p =>
            {
                var typeName = p.Type?.ToString() ?? "";
                var baseName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
                return baseName.Length > 1 && baseName[0] == 'I' && char.IsUpper(baseName[1]);
            })
            .ToList();

        var publicMethods = classNode.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                     && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)))
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Moq;");
        sb.AppendLine("using Xunit;");
        sb.AppendLine($"using {ns};");
        sb.AppendLine();
        sb.AppendLine($"namespace {testNamespace}");
        sb.AppendLine("{");
        sb.AppendLine($"    public class {testClassName}");
        sb.AppendLine("    {");

        foreach (var param in interfaceParams)
        {
            var typeName = param.Type!.ToString().TrimEnd('?');
            sb.AppendLine($"        private Mock<{typeName}> {GetMockFieldName(typeName)};");
        }
        sb.AppendLine($"        private {className} _sut;");
        sb.AppendLine();

        sb.AppendLine($"        public {testClassName}()");
        sb.AppendLine("        {");
        foreach (var param in interfaceParams)
        {
            var typeName = param.Type!.ToString().TrimEnd('?');
            sb.AppendLine($"            {GetMockFieldName(typeName)} = new Mock<{typeName}>();");
        }
        var mockArgs = string.Join(", ", interfaceParams.Select(p => $"{GetMockFieldName(p.Type!.ToString().TrimEnd('?'))}.Object"));
        sb.AppendLine($"            _sut = new {className}({mockArgs});");
        sb.AppendLine("        }");
        sb.AppendLine();

        var methodNameCounts = publicMethods.GroupBy(m => m.Identifier.Text)
            .ToDictionary(g => g.Key, g => g.Count());
        var methodNameUsage = new Dictionary<string, int>();

        foreach (var method in publicMethods)
        {
            var methodName = method.Identifier.Text;
            methodNameUsage.TryGetValue(methodName, out var usageCount);
            methodNameUsage[methodName] = usageCount + 1;

            var testName = methodNameCounts[methodName] > 1
                ? $"{methodName}_Overload{methodNameUsage[methodName]}_ShouldBehaveCorrectly"
                : $"{methodName}_ShouldBehaveCorrectly";

            bool isAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
            bool returnsTask = method.ReturnType.ToString().StartsWith("Task");
            bool needsAsync = isAsync || returnsTask;

            sb.AppendLine("        [Fact]");
            sb.AppendLine(needsAsync
                ? $"        public async Task {testName}()"
                : $"        public void {testName}()");
            sb.AppendLine("        {");
            sb.AppendLine("            // Arrange");
            sb.AppendLine();
            sb.AppendLine("            // Act");
            sb.AppendLine();
            sb.AppendLine("            // Assert");
            sb.AppendLine("            throw new NotImplementedException();");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return new TestScaffoldResult(className, testClassName, testNamespace, sb.ToString());
    }

    private static string GetMockFieldName(string typeName)
    {
        var baseName = typeName.Contains('<') ? typeName[..typeName.IndexOf('<')] : typeName;
        var withoutI = baseName.Length > 1 && char.IsUpper(baseName[1]) ? baseName[1..] : baseName;
        return $"_mock{withoutI}";
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
