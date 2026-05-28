using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace RoslynSentinel.Server;

public record GenerationResult(string FilePath, string Content);

public record RepositoryInterfaceResult(
    string InterfaceName,
    string InterfaceCode,
    string DiRegistrationSnippet,
    string MockSetupSnippet
);

public record FluentBuilderResult(
    string BuilderClassName,
    string BuilderCode,
    string UsageExample,
    string? Error = null
);

public record DecoratorResult(
    string ClassName,
    string Namespace,
    string SourceCode,
    string SuggestedFileName
);

public class CodeGenerationEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public CodeGenerationEngine(PersistentWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    private JsonSerializerOptions defaultJsonSerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Generates C# classes from a JSON string.
    /// </summary>
    public GenerationResult GenerateClassesFromJson(string json, string rootClassName, string @namespace)
    {
        using var document = JsonDocument.Parse(json);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine();
        sb.AppendLine($"namespace {@namespace};");
        sb.AppendLine();

        ProcessElement(document.RootElement, rootClassName, sb);

        return new GenerationResult($"{rootClassName}.cs", sb.ToString());
    }

    private void ProcessElement(JsonElement element, string className, System.Text.StringBuilder sb)
    {
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                var type = GetCSharpType(property.Value, property.Name);
                sb.AppendLine($"    [JsonPropertyName(\"{property.Name}\")]");
                sb.AppendLine($"    public {type} {Capitalize(property.Name)} {{ get; set; }}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();

        // Process nested objects
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    ProcessElement(property.Value, Capitalize(property.Name), sb);
                }
                else if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() > 0 && property.Value[0].ValueKind == JsonValueKind.Object)
                {
                    ProcessElement(property.Value[0], Capitalize(property.Name), sb);
                }
            }
        }
    }

    private string GetCSharpType(JsonElement element, string propertyName)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "double",
            JsonValueKind.True => "bool",
            JsonValueKind.False => "bool",
            JsonValueKind.Object => Capitalize(propertyName),
            JsonValueKind.Array => $"List<{GetCSharpType(element.EnumerateArray().FirstOrDefault(), propertyName)}>",
            _ => "object"
        };
    }

    private string Capitalize(string name) => char.ToUpper(name[0]) + name.Substring(1);

    /// <summary>
    /// Returns true if <paramref name="receiverSyntax"/> is typed as IConfiguration (or variant).
    /// Uses semantic type resolution first; falls back to syntactic declaration text and identifier-name heuristics
    /// so test workspaces without full NuGet references still work.
    /// </summary>
    private static bool IsConfigurationReceiver(
        ExpressionSyntax receiverSyntax,
        SemanticModel semanticModel,
        HashSet<string> configTypeNames,
        SyntaxNode root,
        CancellationToken ct)
    {
        // Primary: semantic type info
        var resolved = semanticModel.GetTypeInfo(receiverSyntax, ct).Type;
        if (resolved != null)
        {
            return configTypeNames.Contains(resolved.Name);
        }

        // Fallback 1: look at the declared type text of the identifier in the syntax tree
        if (receiverSyntax is IdentifierNameSyntax id)
        {
            var receiverName = id.Identifier.Text;

            // Search parameter declarations, local variable declarations, and field declarations
            foreach (var paramSyntax in root.DescendantNodes().OfType<ParameterSyntax>())
            {
                if (paramSyntax.Identifier.Text == receiverName && paramSyntax.Type != null)
                {
                    var typeName = paramSyntax.Type.ToString();
                    if (configTypeNames.Any(n => typeName.Contains(n)))
                    {
                        return true;
                    }
                }
            }

            foreach (var varDecl in root.DescendantNodes().OfType<VariableDeclarationSyntax>())
            {
                foreach (var v in varDecl.Variables)
                {
                    if (v.Identifier.Text == receiverName)
                    {
                        var typeName = varDecl.Type.ToString();
                        if (configTypeNames.Any(n => typeName.Contains(n)))
                        {
                            return true;
                        }
                    }
                }
            }

            // Fallback 2: naming convention (config, configuration, _config, appConfiguration, etc.)
            return receiverName.IndexOf("onfig", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        return false;
    }

    public async Task<string> GenerateConstructorAsync(string filePath, string className, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return string.Empty;
        }

        if (classNode.Members.OfType<ConstructorDeclarationSyntax>().Any())
        {
            return root!.ToFullString();
        }

        var fields = classNode.Members
            .OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)) ||
                        f.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)))
            .SelectMany(f => f.Declaration.Variables.Select(v => (
                Name: v.Identifier.Text,
                Type: f.Declaration.Type.ToString())))
            .ToList();

        if (fields.Count == 0)
        {
            return root!.ToFullString();
        }

        var parameters = fields.Select(f =>
        {
            var paramName = f.Name.TrimStart('_');
            paramName = char.ToLower(paramName[0]) + paramName.Substring(1);
            return SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName))
                .WithType(SyntaxFactory.ParseTypeName(f.Type + " "));
        }).ToList();

        var assignments = fields.Select(f =>
        {
            var paramName = f.Name.TrimStart('_');
            paramName = char.ToLower(paramName[0]) + paramName.Substring(1);
            return (StatementSyntax)SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(f.Name)),
                    SyntaxFactory.IdentifierName(paramName)));
        }).ToList();

        var accessibility = classNode.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
            ? SyntaxKind.PublicKeyword : SyntaxKind.InternalKeyword;

        var ctor = SyntaxFactory.ConstructorDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(accessibility))
            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
            .WithBody(SyntaxFactory.Block(assignments));

        var updatedClass = classNode.AddMembers(ctor);
        var updatedRoot = root!.ReplaceNode(classNode, updatedClass);
        var formatted = await Formatter.FormatAsync(document.WithSyntaxRoot(updatedRoot), cancellationToken: ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    // Property name substrings that are likely to contain sensitive data and should be
    // excluded from ToString() output by default.
    private static readonly string[] SensitiveNameSubstrings =
    [
        "password", "passwd", "secret", "apikey", "api_key", "accesstoken", "access_token",
        "refreshtoken", "refresh_token", "privatekey", "private_key", "clientsecret",
        "client_secret", "token", "hash", "salt", "pin", "cvv", "ssn", "creditcard",
        "credit_card", "connectionstring", "connection_string"
    ];

    private static bool IsSensitivePropertyName(string name)
        => SensitiveNameSubstrings.Any(sub =>
            name.Contains(sub, StringComparison.OrdinalIgnoreCase));

    public record GenerateToStringResult(
        string UpdatedContent,
        List<string> IncludedProperties,
        List<string> ExcludedProperties,
        string? Warning = null);

    public async Task<GenerateToStringResult> GenerateToStringAsync(
        string filePath,
        string className,
        string[]? excludeProperties = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return new GenerateToStringResult(string.Empty, [], [], "File not found.");
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            return new GenerateToStringResult(string.Empty, [], [], $"Class '{className}' not found.");
        }

        var alreadyOverrides = classNode.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "ToString" &&
                      m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword)));
        if (alreadyOverrides)
        {
            return new GenerateToStringResult(root!.ToFullString(), [], [], "ToString() override already exists — nothing changed.");
        }

        var allPublicProps = classNode.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            .Select(p => p.Identifier.Text)
            .ToList();

        if (allPublicProps.Count == 0)
        {
            return new GenerateToStringResult(root!.ToFullString(), [], [], "No public properties found — nothing generated.");
        }

        var userExcluded = excludeProperties ?? [];
        var autoExcluded = allPublicProps.Where(IsSensitivePropertyName).ToList();
        var allExcluded = userExcluded.Union(autoExcluded, StringComparer.OrdinalIgnoreCase).ToList();
        var included = allPublicProps.Where(p => !allExcluded.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

        if (included.Count == 0)
        {
            return new GenerateToStringResult(root!.ToFullString(), [], allExcluded,
                "All public properties were excluded (sensitive names or explicitly excluded). No ToString() generated.");
        }

        var parts = string.Join(", ", included.Select(p => p + " = {" + p + "}"));
        var returnStatement = "return $\"" + className + " {{ " + parts + " }}\";";

        var method = SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)),
                "ToString")
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.ParseStatement(returnStatement)));

        var updatedClass = classNode.AddMembers(method);
        var updatedRoot = root!.ReplaceNode(classNode, updatedClass);
        var formatted = await Formatter.FormatAsync(document.WithSyntaxRoot(updatedRoot), cancellationToken: ct);
        var updatedContent = (await formatted.GetTextAsync(ct)).ToString();

        string? warning = allExcluded.Count > 0
            ? $"Excluded sensitive/specified properties: {string.Join(", ", allExcluded)}. These are NOT in the ToString() output."
            : null;

        return new GenerateToStringResult(updatedContent, included, allExcluded, warning);
    }

    /// <summary>
    /// Scans a project for configuration usage (e.g. config["Key"]) and generates a JSON config file.
    /// </summary>
    public async Task<string> GenerateDefaultConfigJsonAsync(string projectName, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var project = solution.Projects.FirstOrDefault(p => p.Name == projectName);
        if (project == null)
        {
            throw new InvalidOperationException("Project not found.");
        }

        var collectedKeys = new SortedSet<string>(StringComparer.Ordinal);

        // IConfiguration type names to recognise the receiver
        var configTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IConfiguration", "IConfigurationRoot", "IConfigurationSection",
            "ConfigurationManager"
        };
        // Method names used to read config values
        var getValueMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetValue", "GetSection", "GetConnectionString", "GetChildren",
            "Get", "GetRequired", "GetRequiredSection"
        };

        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root == null)
            {
                continue;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
            if (semanticModel == null)
            {
                continue;
            }

            // Pattern 1: config["Key"] — only when receiver is IConfiguration
            foreach (var access in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (access.ArgumentList.Arguments.Count != 1)
                {
                    continue;
                }

                if (access.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax lit)
                {
                    continue;
                }

                if (!lit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                if (!IsConfigurationReceiver(access.Expression, semanticModel, configTypeNames, root, cancellationToken))
                {
                    continue;
                }

                collectedKeys.Add(lit.Token.ValueText);
            }

            // Pattern 2: config.GetValue<T>("Key"), config.GetSection("Key"), etc.
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma)
                {
                    continue;
                }

                if (!getValueMethodNames.Contains(ma.Name.Identifier.Text))
                {
                    continue;
                }

                if (inv.ArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                if (inv.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax keyLit)
                {
                    continue;
                }

                if (!keyLit.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                if (!IsConfigurationReceiver(ma.Expression, semanticModel, configTypeNames, root, cancellationToken))
                {
                    continue;
                }

                collectedKeys.Add(keyLit.Token.ValueText);
            }
        }

        // Build nested JSON hierarchy: "Kroger:ClientId" → { "Kroger": { "ClientId": "TODO" } }
        var root2 = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in collectedKeys)
        {
            var parts = key.Split(':');
            var current = root2;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!current.TryGetValue(parts[i], out var child) || child is not Dictionary<string, object> childDict)
                {
                    childDict = new Dictionary<string, object>(StringComparer.Ordinal);
                    current[parts[i]] = childDict;
                }
                current = childDict;
            }
            var leafKey = parts[^1];
            if (!current.ContainsKey(leafKey))
            {
                current[leafKey] = "TODO: Set default value";
            }
        }

        return JsonSerializer.Serialize(root2, defaultJsonSerializerOptions);
    }

    /// <summary>
    /// Given a concrete repository class, generates: interface code, DI registration snippet, and Moq mock setup snippet.
    /// </summary>
    public async Task<RepositoryInterfaceResult> GenerateRepositoryInterfaceAsync(
        string filePath, string className, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;
        var classNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classNode == null)
        {
            throw new InvalidOperationException($"Class '{className}' not found.");
        }

        // Determine namespace
        var ns = root?.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString() ?? "";

        var interfaceName = "I" + className;

        // Collect public instance methods
        var publicMethods = classNode.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                && !m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)))
            .ToList();

        // Collect public instance properties
        var publicProperties = classNode.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))
                && !p.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)))
            .ToList();

        // Build interface code
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }
        sb.AppendLine($"/// <summary>Interface for <see cref=\"{className}\"/>.</summary>");
        sb.AppendLine($"public interface {interfaceName}");
        sb.AppendLine("{");

        foreach (var prop in publicProperties)
        {
            var hasGetter = prop.AccessorList?.Accessors
                .Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? true;
            var hasSetter = prop.AccessorList?.Accessors
                .Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false;
            var accessors = (hasGetter ? "get; " : "") + (hasSetter ? "set; " : "");
            sb.AppendLine($"    {prop.Type} {prop.Identifier.Text} {{ {accessors}}}");
        }

        foreach (var method in publicMethods)
        {
            var typeParams = method.TypeParameterList?.ToString() ?? "";
            var constraints = method.ConstraintClauses.Any()
                ? " " + string.Join(" ", method.ConstraintClauses.Select(c => c.ToString()))
                : "";
            sb.AppendLine($"    {method.ReturnType} {method.Identifier.Text}{typeParams}{method.ParameterList}{constraints};");
        }

        sb.AppendLine("}");

        // DI registration snippet
        var diSnippet = $"services.AddScoped<{interfaceName}, {className}>();";

        // Moq mock setup snippet
        var mockName = "mock" + className;
        var mockSb = new System.Text.StringBuilder();
        mockSb.AppendLine($"var {mockName} = new Mock<{interfaceName}>();");
        foreach (var method in publicMethods)
        {
            var methodName = method.Identifier.Text;
            var returnType = method.ReturnType.ToString().Trim();
            var paramPlaceholders = string.Join(", ", method.ParameterList.Parameters
                .Select(p => $"It.IsAny<{p.Type}>()"));

            if (returnType.StartsWith("Task<") && returnType.EndsWith(">"))
            {
                var inner = returnType[5..^1];
                mockSb.AppendLine($"// {mockName}.Setup(x => x.{methodName}({paramPlaceholders})).ReturnsAsync(default({inner}));");
            }
            else if (returnType == "Task")
            {
                mockSb.AppendLine($"// {mockName}.Setup(x => x.{methodName}({paramPlaceholders})).Returns(Task.CompletedTask);");
            }
            else if (returnType != "void")
            {
                mockSb.AppendLine($"// {mockName}.Setup(x => x.{methodName}({paramPlaceholders})).Returns(default({returnType}));");
            }
        }

        return new RepositoryInterfaceResult(
            InterfaceName: interfaceName,
            InterfaceCode: sb.ToString(),
            DiRegistrationSnippet: diSnippet,
            MockSetupSnippet: mockSb.ToString()
        );
    }

    public async Task<FluentBuilderResult> GenerateFluentBuilderAsync(
        string filePath, string className, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath == filePath || d.Name == filePath);
        if (document == null)
        {
            return new FluentBuilderResult("", "", "",
                Error: $"File not found: '{filePath}'. Verify the path and ensure the solution is loaded.");
        }

        var root = await document.GetSyntaxRootAsync(ct) as CompilationUnitSyntax;

        // Support both class and record declarations
        MemberDeclarationSyntax? typeNode = root?.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className) as MemberDeclarationSyntax
            ?? root?.DescendantNodes().OfType<RecordDeclarationSyntax>()
                   .FirstOrDefault(r => r.Identifier.Text == className);

        if (typeNode == null)
        {
            return new FluentBuilderResult("", "", "",
                Error: $"Class or record '{className}' not found in '{filePath}'. Check spelling and that the file contains this type.");
        }

        var ns = root?.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault()?.Name.ToString() ?? "";

        // Collect properties: explicit body properties + primary constructor parameters (records)
        var properties = new List<(string Name, string Type)>();

        // Primary constructor parameters (records) — become positional init properties
        if (typeNode is RecordDeclarationSyntax recordNode && recordNode.ParameterList != null)
        {
            foreach (var param in recordNode.ParameterList.Parameters)
            {
                if (param.Type != null && param.Identifier.Text.Length > 0)
                {
                    properties.Add((param.Identifier.Text, param.Type.ToString()));
                }
            }
        }

        // Explicit body properties (class or record with property bodies)
        var bodyProps = typeNode.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
                && p.AccessorList?.Accessors.Any(a =>
                    a.IsKind(SyntaxKind.SetAccessorDeclaration) ||
                    a.IsKind(SyntaxKind.InitAccessorDeclaration)) == true)
            .Select(p => (Name: p.Identifier.Text, Type: p.Type.ToString()));
        // Avoid duplicates from primary constructor params already added
        foreach (var bp in bodyProps)
        {
            if (!properties.Any(p => p.Name == bp.Name))
            {
                properties.Add(bp);
            }
        }

        if (properties.Count == 0)
        {
            return new FluentBuilderResult("", "", "",
                Error: $"No settable public properties or primary constructor parameters found on '{className}'. " +
                       $"generate_fluent_builder is designed for POCOs and records with settable properties, not " +
                       $"DI-injected classes. Consider exposing public properties or using a record type.");
        }

        var builderClassName = className + "Builder";
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        sb.AppendLine($"public class {builderClassName}");
        sb.AppendLine("{");

        foreach (var (name, type) in properties)
        {
            var fieldName = "_" + char.ToLower(name[0]) + name.Substring(1);
            sb.AppendLine($"    private {type} {fieldName};");
        }

        if (properties.Count != 0)
        {
            sb.AppendLine();
        }

        foreach (var (name, type) in properties)
        {
            var fieldName = "_" + char.ToLower(name[0]) + name.Substring(1);
            var paramName = char.ToLower(name[0]) + name.Substring(1);
            sb.AppendLine($"    public {builderClassName} With{name}({type} {paramName})");
            sb.AppendLine("    {");
            sb.AppendLine($"        {fieldName} = {paramName};");
            sb.AppendLine("        return this;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine($"    public {className} Build()");
        sb.AppendLine("    {");
        sb.AppendLine($"        return new {className}");
        sb.AppendLine("        {");
        foreach (var (name, _) in properties)
        {
            var fieldName = "_" + char.ToLower(name[0]) + name.Substring(1);
            sb.AppendLine($"            {name} = {fieldName},");
        }
        sb.AppendLine("        };");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        var chainParts = properties.Take(3).Select(p =>
        {
            var paramName = char.ToLower(p.Name[0]) + p.Name.Substring(1);
            return $".With{p.Name}({paramName})";
        });
        var instanceName = char.ToLower(className[0]) + className.Substring(1);
        var usageExample = $"var {instanceName} = new {builderClassName}(){string.Join("", chainParts)}.Build();";

        return new FluentBuilderResult(
            BuilderClassName: builderClassName,
            BuilderCode: sb.ToString(),
            UsageExample: usageExample
        );
    }

    // ── GenerateDecoratorClass ────────────────────────────────────────────────

    public async Task<DecoratorResult?> GenerateDecoratorClassAsync(
        string interfaceName,
        string decoratorPrefix = "Logging",
        string? projectName = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();

        var searchProjects = projectName != null
            ? solution.Projects.Where(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            : solution.Projects;

        INamedTypeSymbol? interfaceSymbol = null;
        foreach (var project in searchProjects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                continue;
            }

            interfaceSymbol = compilation
                .GetSymbolsWithName(interfaceName, SymbolFilter.Type, ct)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.TypeKind == TypeKind.Interface);
            if (interfaceSymbol != null)
            {
                break;
            }
        }

        if (interfaceSymbol == null)
        {
            return null;
        }

        var ns = interfaceSymbol.ContainingNamespace?.IsGlobalNamespace == true
            ? null : interfaceSymbol.ContainingNamespace?.ToDisplayString();

        // Strip the leading 'I' from interface name only when it follows the IXxx interface naming convention
        // Use Substring(1) not TrimStart('I') to avoid stripping multiple I chars (IInventory → nventory)
        var baseName = interfaceName.Length > 1 && interfaceName[0] == 'I' && char.IsUpper(interfaceName[1])
            ? interfaceName.Substring(1)
            : interfaceName;
        var decoratorClassName = decoratorPrefix + baseName + "Decorator";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }
        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// {decoratorPrefix} decorator for <see cref=\"{interfaceName}\"/>.");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public class {decoratorClassName} : {interfaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly {interfaceName} _inner;");
        sb.AppendLine();
        sb.AppendLine($"    public {decoratorClassName}({interfaceName} inner)");
        sb.AppendLine("    {");
        sb.AppendLine("        _inner = inner;");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var member in interfaceSymbol.GetMembers().Where(m => !m.IsImplicitlyDeclared))
        {
            if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
            {
                var returnType = method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var typeParams = method.TypeParameters.Length > 0
                    ? "<" + string.Join(", ", method.TypeParameters.Select(tp => tp.Name)) + ">"
                    : "";
                var paramList = string.Join(", ", method.Parameters.Select(p =>
                    p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + " " + p.Name));
                var argList = string.Join(", ", method.Parameters.Select(p => p.Name));

                sb.AppendLine($"    public {returnType} {method.Name}{typeParams}({paramList})");
                sb.AppendLine("    {");
                sb.AppendLine($"        // TODO: Add {decoratorPrefix.ToLowerInvariant()} cross-cutting concern here");
                if (returnType == "void")
                {
                    sb.AppendLine($"        _inner.{method.Name}{typeParams}({argList});");
                }
                else
                {
                    sb.AppendLine($"        return _inner.{method.Name}{typeParams}({argList});");
                }

                sb.AppendLine("    }");
                sb.AppendLine();
            }
            else if (member is IPropertySymbol property)
            {
                var propType = property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                sb.AppendLine($"    public {propType} {property.Name}");
                sb.AppendLine("    {");
                if (!property.IsWriteOnly)
                {
                    sb.AppendLine($"        // TODO: Add {decoratorPrefix.ToLowerInvariant()} cross-cutting concern here");
                    sb.AppendLine($"        get => _inner.{property.Name};");
                }
                if (!property.IsReadOnly)
                {
                    sb.AppendLine($"        set => _inner.{property.Name} = value;");
                }
                sb.AppendLine("    }");
                sb.AppendLine();
            }
        }

        sb.AppendLine("}");

        return new DecoratorResult(
            ClassName: decoratorClassName,
            Namespace: ns ?? string.Empty,
            SourceCode: sb.ToString(),
            SuggestedFileName: decoratorClassName + ".cs"
        );
    }

    public async Task<string> ImplementInterfaceAsync(string filePath, string className, string interfaceName, CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return $"File '{filePath}' not found in solution.";
        }

        var semanticModel = await document.GetSemanticModelAsync(ct);
        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null || semanticModel == null)
        {
            return "Could not load semantic model.";
        }

        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.Identifier.Text == className);
        if (classDecl == null)
        {
            return $"Class '{className}' not found in file.";
        }

        var classSymbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken: ct) as INamedTypeSymbol;
        if (classSymbol == null)
        {
            return "Could not get class symbol.";
        }

        var ifaceSymbol = classSymbol.AllInterfaces
            .FirstOrDefault(i => i.Name == interfaceName ||
                i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) == interfaceName);
        if (ifaceSymbol == null)
        {
            return $"Interface '{interfaceName}' not found on class. Ensure the class already declares it implements '{interfaceName}'.";
        }

        var unimplemented = ifaceSymbol.GetMembers()
            .Where(m => m.Kind == SymbolKind.Method || m.Kind == SymbolKind.Property)
            .Where(m =>
            {
                var impl = classSymbol.FindImplementationForInterfaceMember(m);
                return impl == null || !impl.ContainingType.Equals(classSymbol, SymbolEqualityComparer.Default);
            })
            .ToList();

        if (unimplemented.Count == 0)
        {
            return $"All members of '{interfaceName}' are already implemented.";
        }

        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in unimplemented)
        {
            if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
            {
                var returnTypeName = method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var returnType = SyntaxFactory.ParseTypeName(returnTypeName);
                var methodParams = method.Parameters.Select(p =>
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name))
                        .WithType(SyntaxFactory.ParseTypeName(
                            p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)).WithTrailingTrivia(SyntaxFactory.Space))
                ).ToArray();

                var body = SyntaxFactory.Block(
                    SyntaxFactory.ThrowStatement(
                        SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("NotImplementedException"))
                            .WithArgumentList(SyntaxFactory.ArgumentList())));

                var methodDecl = SyntaxFactory.MethodDeclaration(returnType, method.Name)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(methodParams)))
                    .WithBody(body)
                    .NormalizeWhitespace();
                newMembers.Add(methodDecl);
            }
            else if (member is IPropertySymbol prop)
            {
                var propType = SyntaxFactory.ParseTypeName(
                    prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                var accessors = new List<AccessorDeclarationSyntax>();
                var throwBody = SyntaxFactory.Block(
                    SyntaxFactory.ThrowStatement(
                        SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("NotImplementedException"))
                            .WithArgumentList(SyntaxFactory.ArgumentList())));
                if (!prop.IsWriteOnly)
                {
                    accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(throwBody));
                }

                if (!prop.IsReadOnly)
                {
                    accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithBody(throwBody));
                }

                var propDecl = SyntaxFactory.PropertyDeclaration(propType, prop.Name)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)))
                    .NormalizeWhitespace();
                newMembers.Add(propDecl);
            }
        }

        var newClass = classDecl.AddMembers(newMembers.ToArray());
        var newRoot = root.ReplaceNode(classDecl, newClass);
        var updatedDoc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(updatedDoc, null, ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    /// <summary>
    /// Converts a property between auto-property and full property with backing field.
    /// Unlike the built-in convert_property, this preserves initializers on ToFullProperty and
    /// correctly handles all modifiers (virtual, override, new). direction: "ToFullProperty" or "ToAutoProperty".
    /// contextSnippet: optional verbatim substring to disambiguate when multiple properties share a name.
    /// </summary>
    public async Task<string> ConvertPropertySafeAsync(
        string filePath,
        string propertyName,
        string direction,
        string? contextSnippet = null,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        PropertyDeclarationSyntax? propNode = null;
        if (contextSnippet != null)
        {
            var srcText = await document.GetTextAsync(ct);
            var pos = ContextHelper.TryFindSnippetPosition(srcText, contextSnippet, out var snippetError, lineBefore, lineAfter);
            if (pos < 0)
            {
                return $"Error: {snippetError}";
            }

            propNode = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0))
                .AncestorsAndSelf()
                .OfType<PropertyDeclarationSyntax>()
                .FirstOrDefault();
        }

        propNode ??= root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (propNode == null)
        {
            return string.Empty;
        }

        SyntaxNode newRoot = direction switch
        {
            "ToFullProperty" => BuildFullProperty(root, propNode),
            "ToAutoProperty" => BuildAutoProperty(root, propNode),
            _ => throw new ArgumentException($"Unknown direction '{direction}'. Use 'ToFullProperty' or 'ToAutoProperty'.")
        };

        var updatedDoc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(updatedDoc, null, ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    private static SyntaxNode BuildFullProperty(SyntaxNode root, PropertyDeclarationSyntax propNode)
    {
        var propName = propNode.Identifier.Text;
        var fieldName = "_" + char.ToLower(propName[0]) + propName.Substring(1);
        var propType = propNode.Type.WithoutTrivia();

        var initializer = propNode.Initializer;
        var varDeclarator = initializer != null
            ? SyntaxFactory.VariableDeclarator(fieldName).WithInitializer(initializer.WithoutTrivia())
            : SyntaxFactory.VariableDeclarator(fieldName);

        var fieldDecl = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(propType).AddVariables(varDeclarator))
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PrivateKeyword));

        bool hasGetter = propNode.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? true;
        bool hasSetter = propNode.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? true;

        var accessors = new List<AccessorDeclarationSyntax>();
        if (hasGetter)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(fieldName)))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (hasSetter)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithExpressionBody(SyntaxFactory.ArrowExpressionClause(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.IdentifierName(fieldName),
                        SyntaxFactory.IdentifierName("value"))))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        var newPropNode = SyntaxFactory.PropertyDeclaration(propType, propNode.Identifier)
            .WithModifiers(propNode.Modifiers)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));

        return ReplacePropertyInType(root, propNode, newPropNode, fieldDecl);
    }

    private static SyntaxNode BuildAutoProperty(SyntaxNode root, PropertyDeclarationSyntax propNode)
    {
        var propName = propNode.Identifier.Text;
        var fieldName = "_" + char.ToLower(propName[0]) + propName.Substring(1);
        var propType = propNode.Type.WithoutTrivia();

        var typeDecl = propNode.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var backingField = typeDecl.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));

        var fieldInitializer = backingField?.Declaration.Variables
            .FirstOrDefault(v => v.Identifier.Text == fieldName)?.Initializer;

        bool hasGetter = propNode.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? true;
        bool hasSetter = propNode.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? true;

        var accessors = new List<AccessorDeclarationSyntax>();
        if (hasGetter)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        if (hasSetter)
        {
            accessors.Add(SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        }

        var newProp = SyntaxFactory.PropertyDeclaration(propType, propNode.Identifier)
            .WithModifiers(propNode.Modifiers)
            .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(accessors)));

        if (fieldInitializer != null)
        {
            newProp = newProp
                .WithInitializer(fieldInitializer.WithoutTrivia())
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        var members = typeDecl.Members.ToList();
        var propIdx = members.FindIndex(m => ReferenceEquals(m, propNode));
        if (propIdx < 0)
        {
            return root;
        }

        members[propIdx] = newProp;
        if (backingField != null)
        {
            var fieldIdx = members.FindIndex(m => ReferenceEquals(m, backingField));
            if (fieldIdx >= 0)
            {
                members.RemoveAt(fieldIdx);
            }
        }

        var newTypeDecl = SetTypeMembers(typeDecl, SyntaxFactory.List(members));
        return root.ReplaceNode(typeDecl, newTypeDecl);
    }

    private static SyntaxNode ReplacePropertyInType(
        SyntaxNode root, PropertyDeclarationSyntax propNode,
        PropertyDeclarationSyntax newPropNode, FieldDeclarationSyntax fieldDecl)
    {
        var typeDecl = propNode.Ancestors().OfType<TypeDeclarationSyntax>().First();
        var members = typeDecl.Members.ToList();
        var propIdx = members.FindIndex(m => ReferenceEquals(m, propNode));
        if (propIdx < 0)
        {
            return root;
        }

        members[propIdx] = newPropNode;
        members.Insert(propIdx, fieldDecl);

        var newTypeDecl = SetTypeMembers(typeDecl, SyntaxFactory.List(members));
        return root.ReplaceNode(typeDecl, newTypeDecl);
    }

    private static TypeDeclarationSyntax SetTypeMembers(TypeDeclarationSyntax typeDecl, SyntaxList<MemberDeclarationSyntax> members)
        => typeDecl switch
        {
            ClassDeclarationSyntax cls => cls.WithMembers(members),
            StructDeclarationSyntax str => str.WithMembers(members),
            InterfaceDeclarationSyntax ifc => ifc.WithMembers(members),
            RecordDeclarationSyntax rec => rec.WithMembers(members),
            _ => typeDecl
        };

    /// <summary>
    /// Converts a string.Format(...) call to an interpolated string ($"...").
    /// Unlike the built-in convert_to_interpolated_string, this resolves const string format arguments
    /// via the semantic model so it works even when the format string is not a literal.
    /// contextSnippet: verbatim substring identifying the string.Format call to convert.
    /// </summary>
    public async Task<string> InterpolateStringAsync(
        string filePath,
        string contextSnippet,
        string? lineBefore = null,
        string? lineAfter = null,
        CancellationToken ct = default)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.Name == filePath || d.FilePath == filePath);
        if (document == null)
        {
            return string.Empty;
        }

        var root = await document.GetSyntaxRootAsync(ct);
        if (root == null)
        {
            return string.Empty;
        }

        var srcText = await document.GetTextAsync(ct);
        var pos = ContextHelper.TryFindSnippetPosition(srcText, contextSnippet, out var snippetError, lineBefore, lineAfter);
        if (pos < 0)
        {
            return $"Error: {snippetError}";
        }

        var invocation = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, 0))
            .AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(IsStringFormatCall);

        if (invocation == null)
        {
            return "No string.Format call found at the given context snippet.";
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 1)
        {
            return "string.Format call has no arguments.";
        }

        var semanticModel = await document.GetSemanticModelAsync(ct);

        string? formatString = null;
        var formatArgExpr = args[0].Expression;

        if (formatArgExpr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            formatString = lit.Token.ValueText;
        }
        else if (semanticModel != null)
        {
            try
            {
                var constVal = semanticModel.GetConstantValue(formatArgExpr, ct);
                if (constVal.HasValue && constVal.Value is string s)
                {
                    formatString = s;
                }
            }
            catch (Exception ex)
            {
                // If constant value resolution fails (e.g., invalid const reference),
                // return a graceful error instead of crashing
                return $"Error: Could not resolve format string constant. Details: {ex.Message}";
            }
        }

        if (formatString == null)
        {
            return "Could not resolve the format string (not a string literal or const string).";
        }

        var fmtArgs = args.Skip(1).Select(a => a.Expression.ToString()).ToList();
        var interpolated = BuildInterpolatedString(formatString, fmtArgs);

        var replacement = SyntaxFactory.ParseExpression(interpolated).WithTriviaFrom(invocation);
        var newRoot = root.ReplaceNode(invocation, replacement);

        var updatedDoc = document.WithSyntaxRoot(newRoot);
        var formatted = await Formatter.FormatAsync(updatedDoc, null, ct);
        return (await formatted.GetTextAsync(ct)).ToString();
    }

    private static bool IsStringFormatCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == "Format")
        {
            return (ma.Expression is PredefinedTypeSyntax pts && pts.Keyword.IsKind(SyntaxKind.StringKeyword))
                || (ma.Expression is IdentifierNameSyntax id && id.Identifier.Text is "String" or "string");
        }
        return false;
    }

    private static string BuildInterpolatedString(string format, List<string> args)
    {
        var sb = new System.Text.StringBuilder("$\"");
        int i = 0;
        while (i < format.Length)
        {
            if (i + 1 < format.Length && format[i] == '{' && format[i + 1] == '{')
            {
                sb.Append("{{");
                i += 2;
                continue;
            }
            if (i + 1 < format.Length && format[i] == '}' && format[i + 1] == '}')
            {
                sb.Append("}}");
                i += 2;
                continue;
            }
            if (format[i] == '{')
            {
                var end = format.IndexOf('}', i + 1);
                if (end > i)
                {
                    var spec = format.Substring(i + 1, end - i - 1);
                    var colonIdx = spec.IndexOf(':');
                    var indexStr = colonIdx >= 0 ? spec.Substring(0, colonIdx) : spec;
                    var formatSpec = colonIdx >= 0 ? string.Concat(":", spec.AsSpan(colonIdx + 1)) : "";
                    if (int.TryParse(indexStr.Trim(), out int idx) && idx < args.Count)
                    {
                        sb.Append('{').Append(args[idx]).Append(formatSpec).Append('}');
                        i = end + 1;
                        continue;
                    }
                }
            }
            if (format[i] == '"')
            {
                sb.Append("\\\"");
            }
            else
            {
                sb.Append(format[i]);
            }

            i++;
        }
        sb.Append('"');
        return sb.ToString();
    }
}
