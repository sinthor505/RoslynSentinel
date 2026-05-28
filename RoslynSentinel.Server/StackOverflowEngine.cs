using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Server;

public enum StackOverflowRisk
{
    Definite, Suspicious, Informational
}

public sealed record StackOverflowFinding(
    string Kind,
    StackOverflowRisk Risk,
    string FilePath,
    int LineNumber,
    string ContainingMember,
    string Description,
    string? CyclePath = null,
    string? Recommendation = null);

public sealed record StackOverflowReport(
    string FilePath,
    int DefiniteCount,
    int SuspiciousCount,
    int InformationalCount,
    List<StackOverflowFinding> Findings,
    string Summary);

public sealed class StackOverflowEngine
{
    private readonly PersistentWorkspaceManager _workspaceManager;

    public StackOverflowEngine(PersistentWorkspaceManager workspaceManager)
        => _workspaceManager = workspaceManager;

    public async Task<StackOverflowReport> AnalyzeStackOverflowRisksAsync(
        string filePath,
        bool includeInformational = false)
    {
        var solution = await _workspaceManager.GetBranchedSolutionAsync();
        var document = solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath != null &&
                d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

        SyntaxNode root;
        SemanticModel? model = null;

        if (document != null)
        {
            var tree = await document.GetSyntaxTreeAsync();
            root = await tree!.GetRootAsync();
            model = await document.GetSemanticModelAsync();
        }
        else
        {
            var source = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath) : "";
            root = CSharpSyntaxTree.ParseText(source).GetRoot();
        }

        var findings = new List<StackOverflowFinding>();

        // Syntactic checks — semantic model used when available for overload disambiguation
        findings.AddRange(DetectDirectRecursion(root, filePath, model));
        findings.AddRange(DetectPropertySelfReference(root, filePath));
        findings.AddRange(DetectOverrideCallingSelf(root, filePath));
        findings.AddRange(DetectInFileInheritanceCycles(root, filePath));
        findings.AddRange(DetectCallGraphCycles(root, filePath));

        // Semantic enhancement — higher accuracy for cross-file cases
        if (model != null)
        {
            findings.AddRange(DetectArgumentNotDecreasing(root, model, filePath));
            findings.AddRange(DetectMisboundOverloadChains(root, model, filePath));
            findings.AddRange(await DetectCrossFileInheritanceCyclesAsync(root, model, solution, filePath));
        }

        if (includeInformational)
        {
            findings.AddRange(DetectDeepCallChain(root, filePath));
        }

        if (!includeInformational)
        {
            findings = findings.Where(f => f.Risk != StackOverflowRisk.Informational).ToList();
        }

        findings = findings
            .DistinctBy(f => (f.Kind, f.LineNumber, f.ContainingMember))
            .OrderByDescending(f => (int)f.Risk)
            .ThenBy(f => f.LineNumber)
            .ToList();

        return new StackOverflowReport(
            FilePath: filePath,
            DefiniteCount: findings.Count(f => f.Risk == StackOverflowRisk.Definite),
            SuspiciousCount: findings.Count(f => f.Risk == StackOverflowRisk.Suspicious),
            InformationalCount: findings.Count(f => f.Risk == StackOverflowRisk.Informational),
            Findings: findings,
            Summary: BuildSummary(findings, filePath));
    }

    // ── Direct unconditional / conditional recursion ──────────────────────────

    private static List<StackOverflowFinding> DetectDirectRecursion(
        SyntaxNode root, string filePath, SemanticModel? model = null)
    {
        var findings = new List<StackOverflowFinding>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // Overrides get more precise treatment from DetectOverrideCallingSelf
            if (method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
            {
                continue;
            }

            var name = method.Identifier.Text;
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body == null)
            {
                continue;
            }

            IMethodSymbol? containingSymbol = model?.GetDeclaredSymbol(method) as IMethodSymbol;

            var callingParamNames = method.ParameterList.Parameters
                .Select(p => p.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var call in SelfCalls(body, name))
            {
                // ── Syntactic heuristics: detect overload chain delegation ────────────────
                // These fire even without a semantic model, covering the most common patterns.

                // Heuristic 1: call has MORE positional args than this method has parameters.
                // Delegation inserts extra defaults (e.g., timeout, CancellationToken) that
                // only exist on the target overload — this is a chain call, not recursion.
                if (call.ArgumentList.Arguments.Count > method.ParameterList.Parameters.Count)
                {
                    continue;
                }

                // Heuristic 2: call uses a named argument for a parameter that does not exist
                // on this method. Named args like "timeoutSeconds: 30" targeting a param on the
                // fuller overload are unambiguous evidence of delegation, not recursion.
                if (call.ArgumentList.Arguments.Any(a =>
                        a.NameColon != null &&
                        !callingParamNames.Contains(a.NameColon.Name.Identifier.Text)))
                {
                    continue;
                }

                // ── Semantic overload check (when model available) ────────────────────────
                // Resolves the call to its exact symbol and skips calls to a different overload,
                // catching cases the syntactic heuristics can't distinguish (same arg count,
                // no named args, but different parameter types — e.g., adapter lambdas).
                if (containingSymbol != null)
                {
                    var info = model!.GetSymbolInfo(call);
                    var calledSymbol = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                    if (calledSymbol != null && !SymbolEqualityComparer.Default.Equals(
                            calledSymbol.OriginalDefinition, containingSymbol.OriginalDefinition))
                    {
                        continue;
                    }
                }

                var guarded = IsEffectivelyGuarded(call, method);
                var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new StackOverflowFinding(
                    Kind: guarded ? "ConditionalRecursion" : "DirectRecursion",
                    Risk: guarded ? StackOverflowRisk.Suspicious : StackOverflowRisk.Definite,
                    FilePath: filePath,
                    LineNumber: line,
                    ContainingMember: name,
                    Description: guarded
                        ? $"'{name}' calls itself conditionally — verify all paths have a reachable non-recursive exit"
                        : $"'{name}' calls itself unconditionally — guaranteed StackOverflowException",
                    Recommendation: guarded
                        ? "Confirm the base-case guard is always reached before the recursive call"
                        : "Add a non-recursive base case, or rewrite iteratively with an explicit stack"));
            }
        }

        return findings;
    }

    // ── Property getter / setter reads / writes itself ─────────────────────────

    private static List<StackOverflowFinding> DetectPropertySelfReference(SyntaxNode root, string filePath)
    {
        var findings = new List<StackOverflowFinding>();

        foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            var name = prop.Identifier.Text;

            // Expression-bodied: `public int X => X;`
            if (prop.ExpressionBody != null)
            {
                foreach (var id in SelfIdentifierRefs(prop.ExpressionBody, name))
                {
                    findings.Add(PropertyFinding("PropertySelfRead", filePath,
                        id.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        name, $"Property '{name}' reads itself in expression body — likely missing backing field"));
                }
            }

            if (prop.AccessorList == null)
            {
                continue;
            }

            foreach (var accessor in prop.AccessorList.Accessors)
            {
                var accBody = (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody;
                if (accBody == null)
                {
                    continue;
                }

                if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                {
                    foreach (var id in SelfIdentifierRefs(accBody, name))
                    {
                        findings.Add(PropertyFinding("PropertySelfRead", filePath,
                            id.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            $"{name}.get", $"Property '{name}' getter reads itself — likely missing backing field"));
                    }
                }
                else if (accessor.IsKind(SyntaxKind.SetAccessorDeclaration) ||
                         accessor.IsKind(SyntaxKind.InitAccessorDeclaration))
                {
                    foreach (var assign in accBody.DescendantNodes()
                        .OfType<AssignmentExpressionSyntax>()
                        .Where(a => IsSelfPropertyAccess(a.Left, name)))
                    {
                        findings.Add(PropertyFinding("PropertySelfWrite", filePath,
                            assign.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            $"{name}.set", $"Property '{name}' setter assigns to itself — guaranteed StackOverflowException"));
                    }
                }
            }
        }

        return findings;
    }

    // ── Override calls itself instead of base ─────────────────────────────────

    private static List<StackOverflowFinding> DetectOverrideCallingSelf(SyntaxNode root, string filePath)
    {
        var findings = new List<StackOverflowFinding>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
            {
                continue;
            }

            var name = method.Identifier.Text;
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body == null)
            {
                continue;
            }

            foreach (var call in SelfCalls(body, name))
            {
                var guarded = IsEffectivelyGuarded(call, method);
                var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new StackOverflowFinding(
                    Kind: "OverrideCallsSelf",
                    Risk: guarded ? StackOverflowRisk.Suspicious : StackOverflowRisk.Definite,
                    FilePath: filePath,
                    LineNumber: line,
                    ContainingMember: name,
                    Description: $"Override '{name}' calls itself via virtual dispatch — did you mean 'base.{name}()'?",
                    Recommendation: $"Use 'base.{name}(...)' to delegate to the base implementation, or add a guard to prevent infinite recursion"));
            }
        }

        return findings;
    }

    // ── In-file inheritance dispatch cycles ───────────────────────────────────
    //
    // Catches: DerivedClass.Override → BaseClass.Method (call/prop access) →
    //          BaseClass.Virtual (dispatch) → DerivedClass.VirtualOverride → loops
    //
    // Works purely syntactically — both classes must be in the same file.

    private static List<StackOverflowFinding> DetectInFileInheritanceCycles(SyntaxNode root, string filePath)
    {
        var findings = new List<StackOverflowFinding>();

        var classMap = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .GroupBy(c => c.Identifier.Text)
            .ToDictionary(g => g.Key, g => g.First());

        if (classMap.Count < 2)
        {
            return findings;
        }

        // Single-level inheritance map for classes defined in this file
        var inheritance = new Dictionary<string, string>();
        foreach (var (name, decl) in classMap)
        {
            var baseTypeName = decl.BaseList?.Types.FirstOrDefault()?.Type switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                GenericNameSyntax gn => gn.Identifier.Text,
                _ => null
            };
            if (baseTypeName != null && classMap.ContainsKey(baseTypeName))
            {
                inheritance[name] = baseTypeName;
            }
        }

        foreach (var (derivedName, baseClassName) in inheritance)
        {
            var derivedDecl = classMap[derivedName];
            var baseDecl = classMap[baseClassName];

            // Methods and properties in the base class, keyed by name
            var baseMembers = baseDecl.Members
                .Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax)
                .ToDictionary(
                    m => m switch
                    {
                        MethodDeclarationSyntax md => md.Identifier.Text,
                        PropertyDeclarationSyntax pd => pd.Identifier.Text,
                        _ => ""
                    },
                    m => m);

            // Overriding methods and properties in the derived class
            var derivedOverrides = derivedDecl.Members
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword))
                         && m is MethodDeclarationSyntax or PropertyDeclarationSyntax)
                .ToList();

            var derivedOverrideNames = derivedOverrides
                .Select(m => m switch
                {
                    MethodDeclarationSyntax md => md.Identifier.Text,
                    PropertyDeclarationSyntax pd => pd.Identifier.Text,
                    _ => ""
                })
                .ToHashSet(StringComparer.Ordinal);

            foreach (var overrideMember in derivedOverrides)
            {
                var overrideName = overrideMember switch
                {
                    MethodDeclarationSyntax md => md.Identifier.Text,
                    PropertyDeclarationSyntax pd => pd.Identifier.Text,
                    _ => ""
                };
                var overrideBody = GetMemberBodyNode(overrideMember);
                if (overrideBody == null)
                {
                    continue;
                }

                // What members (methods + PascalCase properties) does this override reference?
                var overrideCalls = CollectReferencedMemberNames(overrideBody);

                foreach (var calledName in overrideCalls.Where(baseMembers.ContainsKey))
                {
                    var baseMember = baseMembers[calledName];
                    var baseBody = GetMemberBodyNode(baseMember);
                    if (baseBody == null)
                    {
                        continue;
                    }

                    // What does the base member dispatch to that the derived class overrides?
                    var baseCalls = CollectReferencedMemberNames(baseBody);

                    foreach (var virtualDispatch in baseCalls.Where(derivedOverrideNames.Contains))
                    {
                        // Loop: overrideName → calledName (base) → virtualDispatch (virtual) → derived override
                        var isDirectLoop = virtualDispatch == overrideName;

                        var dispatchedOverride = derivedOverrides
                            .FirstOrDefault(m => (m is MethodDeclarationSyntax md && md.Identifier.Text == virtualDispatch)
                                              || (m is PropertyDeclarationSyntax pd && pd.Identifier.Text == virtualDispatch));
                        var dispatchedBody = dispatchedOverride != null ? GetMemberBodyNode(dispatchedOverride) : null;
                        var dispatchedCalls = dispatchedBody != null
                            ? CollectReferencedMemberNames(dispatchedBody)
                            : (HashSet<string>)[];

                        var closesLoop = isDirectLoop
                            || dispatchedCalls.Contains(overrideName)
                            || dispatchedCalls.Contains(calledName);

                        // Is the virtual dispatch in the base unconditional?
                        var dispatchNode = baseBody.DescendantNodes()
                            .OfType<InvocationExpressionSyntax>()
                            .FirstOrDefault(inv => GetSimpleInvocationName(inv) == virtualDispatch
                                || (inv.Expression is MemberAccessExpressionSyntax ma
                                    && ma.Name.Identifier.Text == virtualDispatch));

                        var isUnconditional = dispatchNode == null
                            || !IsInsideConditional(dispatchNode, baseMember);

                        var risk = closesLoop
                            ? (isUnconditional ? StackOverflowRisk.Definite : StackOverflowRisk.Suspicious)
                            : StackOverflowRisk.Informational;

                        var cyclePath = $"{derivedName}.{overrideName}"
                            + $" → {baseClassName}.{calledName} (base)"
                            + $" → {baseClassName}.{virtualDispatch} (virtual dispatch)"
                            + $" → {derivedName}.{virtualDispatch} (override)"
                            + (closesLoop ? $" → loops back to {overrideName}" : " [loop not confirmed]");

                        var line = overrideMember.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        findings.Add(new StackOverflowFinding(
                            Kind: "InheritanceCycle",
                            Risk: risk,
                            FilePath: filePath,
                            LineNumber: line,
                            ContainingMember: $"{derivedName}.{overrideName}",
                            Description: $"Inheritance dispatch cycle: {derivedName}.{overrideName} → {baseClassName}.{calledName} → virtual {virtualDispatch} → {derivedName}.{virtualDispatch}",
                            CyclePath: cyclePath,
                            Recommendation: $"Verify '{virtualDispatch}' override does not call '{overrideName}' or '{calledName}' — this creates an infinite dispatch loop through the base class"));
                    }
                }
            }
        }

        return findings;
    }

    // ── Cross-file inheritance cycles (semantic model + solution navigation) ──

    private static async Task<List<StackOverflowFinding>> DetectCrossFileInheritanceCyclesAsync(
        SyntaxNode root, SemanticModel model, Solution solution, string filePath)
    {
        var findings = new List<StackOverflowFinding>();

        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var classSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (classSymbol?.BaseType == null ||
                classSymbol.BaseType.SpecialType == SpecialType.System_Object)
            {
                continue;
            }

            // Skip classes whose base is declared in the same file — already handled above
            if (classSymbol.BaseType.Locations.Any(l => l.SourceTree == root.SyntaxTree))
            {
                continue;
            }

            var derivedOverrides = classDecl.Members
                .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.OverrideKeyword)))
                .Select(m =>
                {
                    ISymbol? sym = m is MethodDeclarationSyntax md ? model.GetDeclaredSymbol(md)
                                 : m is PropertyDeclarationSyntax pd ? model.GetDeclaredSymbol(pd)
                                 : null;
                    string name = m is MethodDeclarationSyntax md2 ? md2.Identifier.Text
                                : m is PropertyDeclarationSyntax pd2 ? pd2.Identifier.Text : "";
                    return (Syntax: m, Symbol: sym, Name: name);
                })
                .Where(x => x.Symbol != null && x.Name.Length > 0)
                .ToList();

            var overrideNames = derivedOverrides.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var (overrideSyntax, overrideSymbol, overrideName) in derivedOverrides)
            {
                ISymbol? baseSymbol = overrideSymbol switch
                {
                    IMethodSymbol m => m.OverriddenMethod,
                    IPropertySymbol p => p.OverriddenProperty,
                    _ => null
                };
                if (baseSymbol == null)
                {
                    continue;
                }

                var baseSource = await FindMemberSourceAsync(baseSymbol, solution);
                if (baseSource == null)
                {
                    continue;
                }

                var baseBody = GetMemberBodyNode(baseSource);
                if (baseBody == null)
                {
                    continue;
                }

                var calledByBase = CollectReferencedMemberNames(baseBody);

                foreach (var calledName in calledByBase.Where(overrideNames.Contains))
                {
                    var cycleOverride = derivedOverrides.FirstOrDefault(o => o.Name == calledName);
                    if (cycleOverride.Syntax == null)
                    {
                        continue;
                    }

                    var cycleBody = GetMemberBodyNode(cycleOverride.Syntax);
                    var cycleCalls = cycleBody != null ? CollectReferencedMemberNames(cycleBody) : (HashSet<string>)[];
                    var closesLoop = cycleCalls.Contains(overrideName) || cycleCalls.Contains(calledName);

                    var risk = closesLoop ? StackOverflowRisk.Suspicious : StackOverflowRisk.Informational;
                    var baseTypeName = baseSymbol.ContainingType?.Name ?? "Base";
                    var line = overrideSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var cyclePath = $"{classDecl.Identifier.Text}.{overrideName}"
                        + $" → {baseTypeName}.{overrideName} (base dispatch)"
                        + $" → {calledName} (virtual dispatch)"
                        + $" → {classDecl.Identifier.Text}.{calledName} (override)"
                        + (closesLoop ? " → calls back" : "");

                    findings.Add(new StackOverflowFinding(
                        Kind: "InheritanceCycle",
                        Risk: risk,
                        FilePath: filePath,
                        LineNumber: line,
                        ContainingMember: $"{classDecl.Identifier.Text}.{overrideName}",
                        Description: $"Cross-file inheritance dispatch cycle: {classDecl.Identifier.Text}.{overrideName} → base.{overrideName} → virtual {calledName} → {classDecl.Identifier.Text}.{calledName}",
                        CyclePath: cyclePath,
                        Recommendation: $"Verify '{calledName}' override does not call '{overrideName}' — infinite dispatch loop via the base class"));
                }
            }
        }

        return findings;
    }

    // ── Argument not decreasing in conditional recursion ─────────────────────

    private static List<StackOverflowFinding> DetectArgumentNotDecreasing(
        SyntaxNode root, SemanticModel model, string filePath)
    {
        var findings = new List<StackOverflowFinding>();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body == null)
            {
                continue;
            }

            var paramNames = method.ParameterList.Parameters
                .Select(p => p.Identifier.Text)
                .ToArray();
            if (paramNames.Length == 0)
            {
                continue;
            }

            IMethodSymbol? containingSymbol = model.GetDeclaredSymbol(method) as IMethodSymbol;

            foreach (var call in SelfCalls(body, name))
            {
                if (!IsEffectivelyGuarded(call, method))
                {
                    continue; // unconditional already caught
                }

                // Syntactic: named arg targeting a param not on this method = chain call
                var paramNameSet = paramNames.ToHashSet(StringComparer.Ordinal);
                if (call.ArgumentList.Arguments.Any(a =>
                        a.NameColon != null &&
                        !paramNameSet.Contains(a.NameColon.Name.Identifier.Text)))
                {
                    continue;
                }

                // Semantic: skip calls that resolve to a different overload
                if (containingSymbol != null)
                {
                    var info = model.GetSymbolInfo(call);
                    var calledSymbol = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                    if (calledSymbol != null && !SymbolEqualityComparer.Default.Equals(
                            calledSymbol.OriginalDefinition, containingSymbol.OriginalDefinition))
                    {
                        continue;
                    }
                }

                var args = call.ArgumentList.Arguments;
                if (args.Count != paramNames.Length)
                {
                    continue;
                }

                for (int i = 0; i < args.Count; i++)
                {
                    var argText = args[i].Expression.ToString().Trim();
                    var param = paramNames[i];

                    if (argText == param)
                    {
                        findings.Add(new StackOverflowFinding(
                            Kind: "ArgumentNotDecreasing",
                            Risk: StackOverflowRisk.Suspicious,
                            FilePath: filePath,
                            LineNumber: call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            ContainingMember: name,
                            Description: $"'{name}' passes '{param}' unchanged to itself — recursion will not terminate",
                            Recommendation: $"Reduce '{param}' in the recursive call (e.g. '{param} - 1') to guarantee termination"));
                        break;
                    }

                    if (IsIncreasing(argText, param))
                    {
                        findings.Add(new StackOverflowFinding(
                            Kind: "ArgumentIncreasing",
                            Risk: StackOverflowRisk.Suspicious,
                            FilePath: filePath,
                            LineNumber: call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                            ContainingMember: name,
                            Description: $"'{name}' passes '{argText}' — '{param}' grows with each call, recursion will not terminate",
                            Recommendation: $"The termination condition must bound the growth of '{param}'"));
                        break;
                    }
                }
            }
        }

        return findings;
    }

    // ── Mutual recursion via in-file call graph ───────────────────────────────

    private static List<StackOverflowFinding> DetectCallGraphCycles(SyntaxNode root, string filePath)
    {
        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body != null || m.ExpressionBody != null)
            .ToList();

        if (methods.Count < 2)
        {
            return [];
        }

        // Merge overloads — combine calls from all same-named methods
        var callMap = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var firstSyntax = new Dictionary<string, MethodDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var method in methods)
        {
            var n = method.Identifier.Text;
            var calls = CollectInvocationNames((SyntaxNode?)method.Body ?? method.ExpressionBody!);
            if (callMap.TryGetValue(n, out var existing))
            {
                existing.UnionWith(calls);
            }
            else
            {
                callMap[n] = calls;
                firstSyntax[n] = method;
            }
        }

        var allNames = callMap.Keys.ToHashSet(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var findings = new List<StackOverflowFinding>();

        foreach (var (start, startSyntax) in firstSyntax)
        {
            FindMutualRecursion(start, start, [start], callMap, allNames, reported, findings, startSyntax, filePath);
        }

        return findings;
    }

    private static void FindMutualRecursion(
        string start, string current, List<string> path,
        Dictionary<string, HashSet<string>> callMap,
        HashSet<string> allNames,
        HashSet<string> reported,
        List<StackOverflowFinding> findings,
        MethodDeclarationSyntax startSyntax,
        string filePath)
    {
        if (!callMap.TryGetValue(current, out var callees))
        {
            return;
        }

        foreach (var callee in callees)
        {
            if (!allNames.Contains(callee))
            {
                continue;
            }

            if (callee == start)
            {
                if (path.Count <= 1)
                {
                    continue; // direct self-recursion already in DetectDirectRecursion
                }

                var canonical = string.Join(",", path.Order(StringComparer.Ordinal));
                if (!reported.Add(canonical))
                {
                    continue;
                }

                var cyclePath = string.Join(" → ", path) + " → " + start;
                var line = startSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                findings.Add(new StackOverflowFinding(
                    Kind: "MutualRecursion",
                    Risk: StackOverflowRisk.Suspicious,
                    FilePath: filePath,
                    LineNumber: line,
                    ContainingMember: start,
                    Description: $"Mutual recursion: {cyclePath}",
                    CyclePath: cyclePath,
                    Recommendation: "Add a reachable non-recursive base case on all paths through this cycle"));
                continue;
            }

            if (path.Contains(callee, StringComparer.Ordinal))
            {
                continue;
            }

            if (path.Count >= 5)
            {
                continue; // depth limit
            }

            path.Add(callee);
            FindMutualRecursion(start, callee, path, callMap, allNames, reported, findings, startSyntax, filePath);
            path.RemoveAt(path.Count - 1);
        }
    }

    // ── Deep static call chain (informational) ────────────────────────────────

    private static List<StackOverflowFinding> DetectDeepCallChain(
        SyntaxNode root, string filePath, int threshold = 40)
    {
        var findings = new List<StackOverflowFinding>();

        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Body != null || m.ExpressionBody != null)
            .GroupBy(m => m.Identifier.Text)
            .ToDictionary(g => g.Key, g => g.First());

        var callMap = methods.ToDictionary(
            kvp => kvp.Key,
            kvp => CollectInvocationNames((SyntaxNode?)kvp.Value.Body ?? kvp.Value.ExpressionBody!));

        var depths = new Dictionary<string, int>();
        var computing = new HashSet<string>();

        int GetDepth(string name)
        {
            if (depths.TryGetValue(name, out var d))
            {
                return d;
            }

            if (!callMap.TryGetValue(name, out HashSet<string>? value))
            {
                return 0;
            }

            if (computing.Contains(name))
            {
                return 0; // cycle — stop
            }

            computing.Add(name);
            var max = value.Where(c => c != name).Select(GetDepth).DefaultIfEmpty(0).Max();
            computing.Remove(name);
            depths[name] = max + 1;
            return depths[name];
        }

        foreach (var (name, method) in methods)
        {
            var depth = GetDepth(name);
            if (depth >= threshold)
            {
                findings.Add(new StackOverflowFinding(
                    Kind: "DeepCallChain",
                    Risk: StackOverflowRisk.Informational,
                    FilePath: filePath,
                    LineNumber: method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    ContainingMember: name,
                    Description: $"'{name}' is at least {depth} call levels deep — contributes to deep call stacks",
                    Recommendation: "Consider flattening or rewriting iteratively if this chain is reachable from recursive code"));
            }
        }

        return findings;
    }

    // ── Overload chain validation ─────────────────────────────────────────────
    // Detects three distinct failure modes in same-named overload families:
    //   ChainMissingParameter — a source param is absent from the forwarding call
    //   ChainArgumentOrder    — source params appear in inverted order in the call
    //   OverloadCycle         — two overloads delegate to each other (mutual recursion)

    internal static List<StackOverflowFinding> DetectMisboundOverloadChains(
        SyntaxNode root, SemanticModel model, string filePath)
    {
        var findings = new List<StackOverflowFinding>();

        // Only examine methods that have at least one same-named sibling in this file
        var methodsByName = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .GroupBy(m => m.Identifier.Text)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (methodName, overloads) in methodsByName)
        {
            // Build symbol → syntax map for cycle lookup
            var symbolToSyntax = new Dictionary<IMethodSymbol, MethodDeclarationSyntax>(
                SymbolEqualityComparer.Default);
            foreach (var overload in overloads)
            {
                if (model.GetDeclaredSymbol(overload) is IMethodSymbol sym)
                {
                    symbolToSyntax[sym] = overload;
                }
            }

            foreach (var (containingSymbol, method) in symbolToSyntax)
            {
                var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (body == null)
                {
                    continue;
                }

                var callingParams = method.ParameterList.Parameters
                    .Select(p => p.Identifier.Text)
                    .ToList();

                foreach (var call in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    // Must be a call to a sibling overload of the same method name
                    var invName = call.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.Text,
                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } ma
                            => ma.Name.Identifier.Text,
                        _ => null
                    };
                    if (invName != methodName)
                    {
                        continue;
                    }

                    var callInfo = model.GetSymbolInfo(call);
                    var calledSymbol =
                        (callInfo.Symbol ?? callInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                    if (calledSymbol == null)
                    {
                        continue;
                    }

                    // Same overload = genuine recursion handled elsewhere; skip
                    if (SymbolEqualityComparer.Default.Equals(
                            calledSymbol.OriginalDefinition, containingSymbol.OriginalDefinition))
                    {
                        continue;
                    }

                    var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    var args = call.ArgumentList.Arguments;

                    // ── Check 1: All source parameters are forwarded ──────────
                    var missing = callingParams
                        .Where(p => !args.Any(a => ArgumentReferencesParam(a, p)))
                        .ToList();

                    if (missing.Count > 0)
                    {
                        findings.Add(new StackOverflowFinding(
                            Kind: "ChainMissingParameter",
                            Risk: StackOverflowRisk.Suspicious,
                            FilePath: filePath,
                            LineNumber: line,
                            ContainingMember: methodName,
                            Description: $"Overload chain '{methodName}': parameter(s) [{string.Join(", ", missing)}] are " +
                                "not forwarded to the target overload — callers of this overload lose those values silently",
                            Recommendation: $"Forward [{string.Join(", ", missing)}] in the delegating call; " +
                                "if the omission is intentional, document why with a comment"));
                    }

                    // ── Check 2: Parameters forwarded in wrong relative order ─
                    // Map each source param to the positional arg slot it occupies in the call
                    var positionalArgs = args
                        .Where(a => a.NameColon == null)
                        .Select(a => a.Expression.ToString().Trim())
                        .ToList();

                    var paramPos = new Dictionary<string, int>();
                    for (int ai = 0; ai < positionalArgs.Count; ai++)
                    {
                        var match = callingParams.FirstOrDefault(p => positionalArgs[ai] == p);
                        if (match != null && !paramPos.ContainsKey(match))
                        {
                            paramPos[match] = ai;
                        }
                    }

                    bool orderFlagged = false;
                    for (int i = 0; !orderFlagged && i < callingParams.Count; i++)
                    {
                        for (int j = i + 1; !orderFlagged && j < callingParams.Count; j++)
                        {
                            if (paramPos.TryGetValue(callingParams[i], out var pi) &&
                                paramPos.TryGetValue(callingParams[j], out var pj) && pi > pj)
                            {
                                findings.Add(new StackOverflowFinding(
                                    Kind: "ChainArgumentOrder",
                                    Risk: StackOverflowRisk.Suspicious,
                                    FilePath: filePath,
                                    LineNumber: line,
                                    ContainingMember: methodName,
                                    Description: $"Overload chain '{methodName}': '{callingParams[i]}' appears after " +
                                        $"'{callingParams[j]}' in the forwarding call — inverted relative to the source " +
                                        "parameter order; the call may bind to the wrong overload or semantics may be wrong",
                                    Recommendation: "Verify the target overload's parameter order and reorder the arguments accordingly"));
                                orderFlagged = true;
                            }
                        }
                    }

                    // ── Check 3: Mutual delegation cycle between two overloads ─
                    if (symbolToSyntax.TryGetValue(calledSymbol.OriginalDefinition, out var calledSyntax))
                    {
                        var calledBody = (SyntaxNode?)calledSyntax.Body ?? calledSyntax.ExpressionBody;
                        if (calledBody != null)
                        {
                            var cyclesBack = calledBody.DescendantNodes()
                                .OfType<InvocationExpressionSyntax>()
                                .Any(inv2 =>
                                {
                                    var i2 = model.GetSymbolInfo(inv2);
                                    var s2 = (i2.Symbol ?? i2.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
                                    return s2 != null && SymbolEqualityComparer.Default.Equals(
                                        s2.OriginalDefinition, containingSymbol.OriginalDefinition);
                                });

                            if (cyclesBack)
                            {
                                findings.Add(new StackOverflowFinding(
                                    Kind: "OverloadCycle",
                                    Risk: StackOverflowRisk.Definite,
                                    FilePath: filePath,
                                    LineNumber: line,
                                    ContainingMember: methodName,
                                    Description: $"Overload cycle: '{methodName}' delegates to a sibling overload that " +
                                        "delegates back — mutual recursion between overloads causes StackOverflowException",
                                    Recommendation: "Designate one overload as the canonical implementation; all others " +
                                        "must delegate to it in one direction without a return call"));
                            }
                        }
                    }
                }
            }
        }

        return findings;
    }

    private static bool ArgumentReferencesParam(ArgumentSyntax arg, string paramName) =>
        arg.Expression.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(id => id.Identifier.Text == paramName);

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static IEnumerable<InvocationExpressionSyntax> SelfCalls(SyntaxNode body, string methodName) =>
        body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(inv => GetSimpleInvocationName(inv) == methodName);

    private static string? GetSimpleInvocationName(InvocationExpressionSyntax inv) =>
        inv.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax ma when ma.Expression is ThisExpressionSyntax
                => ma.Name.Identifier.Text,
            _ => null
        };

    private static bool IsInsideConditional(SyntaxNode node, SyntaxNode boundary)
    {
        var current = node.Parent;
        while (current != null && current != boundary)
        {
            if (current is IfStatementSyntax or ConditionalExpressionSyntax or
                SwitchStatementSyntax or SwitchExpressionSyntax or
                WhileStatementSyntax or ForStatementSyntax or DoStatementSyntax)
            {
                return true;
            }

            current = current.Parent;
        }
        return false;
    }

    // Recognises both the "call inside conditional" and the common "early-return before call" pattern:
    //   if (base case) return;  ← top-level, before the recursive call
    //   Recurse();               ← only reached when base case didn't trigger
    private static bool IsEffectivelyGuarded(InvocationExpressionSyntax call, MethodDeclarationSyntax method)
    {
        if (IsInsideConditional(call, method))
        {
            return true;
        }

        if (method.Body == null)
        {
            return false;
        }

        var callPos = call.GetLocation().SourceSpan.Start;
        foreach (var stmt in method.Body.Statements)
        {
            if (stmt.GetLocation().SourceSpan.End > callPos)
            {
                break;
            }

            if (stmt is ReturnStatementSyntax or ThrowStatementSyntax)
            {
                return true; // unconditional early exit before call
            }

            if (stmt is IfStatementSyntax ifStmt && IfBodyAlwaysExits(ifStmt))
            {
                return true; // if-with-exit before call — call is conditional on its inverse
            }
        }
        return false;
    }

    private static bool IfBodyAlwaysExits(IfStatementSyntax ifStmt) =>
        StatementAlwaysExits(ifStmt.Statement)
        && (ifStmt.Else == null || StatementAlwaysExits(ifStmt.Else.Statement));

    private static bool StatementAlwaysExits(StatementSyntax stmt) =>
        stmt is ReturnStatementSyntax or ThrowStatementSyntax
        || (stmt is BlockSyntax block
            && block.Statements.Any(s => s is ReturnStatementSyntax or ThrowStatementSyntax));

    private static IEnumerable<IdentifierNameSyntax> SelfIdentifierRefs(SyntaxNode body, string name) =>
        body.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == name
                      && !(id.Parent is AssignmentExpressionSyntax ass && ass.Left == id)
                      && !(id.Parent is MemberAccessExpressionSyntax ma
                           && ma.Name == id && ma.Expression is not ThisExpressionSyntax));

    private static bool IsSelfPropertyAccess(ExpressionSyntax expr, string name) =>
        (expr is IdentifierNameSyntax id && id.Identifier.Text == name)
        || (expr is MemberAccessExpressionSyntax ma
           && ma.Expression is ThisExpressionSyntax
           && ma.Name.Identifier.Text == name);

    private static bool IsIncreasing(string argText, string param) =>
        argText.StartsWith($"{param} +", StringComparison.Ordinal) ||
        argText.StartsWith($"{param}+", StringComparison.Ordinal);

    private static StackOverflowFinding PropertyFinding(
        string kind, string filePath, int line, string member, string description) =>
        new(kind, StackOverflowRisk.Definite, filePath, line, member, description,
            Recommendation: "Introduce a private backing field (e.g. '_fieldName') and use it instead of the property name");

    private static HashSet<string> CollectInvocationNames(SyntaxNode body) =>
        body.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(inv => inv.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                _ => null
            })
            .Where(n => n != null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

    // Broader than CollectInvocationNames — includes PascalCase member/property accesses,
    // needed for inheritance cycle detection where base calls a property that derived overrides.
    private static HashSet<string> CollectReferencedMemberNames(SyntaxNode body)
    {
        var result = CollectInvocationNames(body);
        foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var text = id.Identifier.Text;
            if (text.Length == 0 || !char.IsUpper(text[0]))
            {
                continue;
            }

            if (id.Parent is InvocationExpressionSyntax inv && inv.Expression == id)
            {
                continue;
            }

            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                continue;
            }

            result.Add(text);
        }
        return result;
    }

    private static async Task<SyntaxNode?> FindMemberSourceAsync(ISymbol symbol, Solution solution)
    {
        foreach (var loc in symbol.Locations.Where(l => l.IsInSource))
        {
            var doc = solution.GetDocument(loc.SourceTree);
            if (doc == null)
            {
                continue;
            }

            var root = await doc.GetSyntaxRootAsync();
            if (root == null)
            {
                continue;
            }

            var node = root.FindNode(loc.SourceSpan);
            while (node != null)
            {
                if (node is MethodDeclarationSyntax or PropertyDeclarationSyntax or AccessorDeclarationSyntax)
                {
                    return node;
                }

                node = node.Parent;
            }
        }
        return null;
    }

    private static SyntaxNode? GetMemberBodyNode(MemberDeclarationSyntax member) =>
        member switch
        {
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            PropertyDeclarationSyntax p => (SyntaxNode?)p.ExpressionBody
                ?? p.AccessorList?.Accessors
                    .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.Body,
            _ => null
        };

    private static SyntaxNode? GetMemberBodyNode(SyntaxNode member) =>
        member switch
        {
            MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            PropertyDeclarationSyntax p => (SyntaxNode?)p.ExpressionBody
                ?? p.AccessorList?.Accessors
                    .FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))?.Body,
            AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
            _ => null
        };

    private static string BuildSummary(List<StackOverflowFinding> findings, string filePath)
    {
        if (findings.Count == 0)
        {
            return $"No stack overflow risks found in {Path.GetFileName(filePath)}";
        }

        var parts = new List<string>();
        var definite = findings.Count(f => f.Risk == StackOverflowRisk.Definite);
        var suspicious = findings.Count(f => f.Risk == StackOverflowRisk.Suspicious);
        var info = findings.Count(f => f.Risk == StackOverflowRisk.Informational);

        if (definite > 0)
        {
            parts.Add($"{definite} definite");
        }

        if (suspicious > 0)
        {
            parts.Add($"{suspicious} suspicious");
        }

        if (info > 0)
        {
            parts.Add($"{info} informational");
        }

        var kindSummary = findings.GroupBy(f => f.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}:{g.Count()}");

        return $"{string.Join(", ", parts)} stack overflow risk(s) in {Path.GetFileName(filePath)} [{string.Join(", ", kindSummary)}]";
    }
}
