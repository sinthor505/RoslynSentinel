using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynSentinel.Engines.Basic;

/// <summary>
/// Plans framework-appropriate category attributes (NUnit <c>[Category]</c>, xUnit <c>[Trait("Category", ...)]</c>,
/// MSTest <c>[TestCategory]</c>) for test methods and fixtures from the types they DIRECTLY reference.
/// Read-only: it computes an in-memory <see cref="TestCategoryPlan"/> (planned edits are data) and never writes a file.
/// </summary>
/// <remarks>
/// What counts as a reference (one semantic-model pass per test method): invocations, object creation, member access,
/// <c>typeof</c>/<c>nameof</c> and generic type arguments, including attribute arguments on the method. An interface
/// member maps to the implementing type only when exactly one implementation exists in the targets; otherwise the
/// interface is the category. NOT counted: container wiring, constructor injection into helpers, calls that reach a type
/// only inside a helper method, indirect callers, and fixture names. Such tests surface as uncategorized.
/// Generated attributes carry the trailing marker comment <see cref="GeneratedMarker"/>; only marked attributes are ever
/// planned for removal.
/// </remarks>
public sealed class TestCategoryTaggingEngine
{
    /// <summary>Trailing comment that marks a generated category attribute; removal considers only marked attributes.</summary>
    public const string GeneratedMarker = "sentinel:auto-category";

    private const double ShareEpsilon = 1e-9;

    private static readonly string[] NUnitTestMarkers =
    [
        "NUnit.Framework.TestAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute",
    ];

    private static readonly string[] XUnitTestMarkers =
    [
        "Xunit.FactAttribute",
        "Xunit.TheoryAttribute",
    ];

    private static readonly string[] MsTestMarkers =
    [
        "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
        "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute",
    ];

    private readonly IWorkspaceManager _workspaceManager;

    public TestCategoryTaggingEngine(IWorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    /// <summary>Computes the plan over the committed solution without writing anything.</summary>
    public async Task<TestCategoryPlan> PlanAsync(TestCategoryPlanOptions options, CancellationToken cancellationToken = default)
    {
        var solution = await _workspaceManager.GetSolutionAsync(ReadSource.Committed, cancellationToken);
        return await PlanForSolutionAsync(solution, options, cancellationToken);
    }

    /// <summary>
    /// Computes the plan over an explicit <paramref name="solution"/>. Failures are returned as
    /// <see cref="TestCategoryPlan.Error"/>; only cancellation is thrown.
    /// </summary>
    public static async Task<TestCategoryPlan> PlanForSolutionAsync(Solution solution, TestCategoryPlanOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            return await PlanCoreAsync(solution, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return TestCategoryPlan.Failed(new ResultError(
                ToolErrorCode.Exception,
                $"Test category planning failed unexpectedly ({ex.GetType().Name}). Retry with a narrower targets/testScope; if it persists, report it as a defect."));
        }
    }

    /// <summary>The attribute line the plan would write for <paramref name="category"/>, marker comment included.</summary>
    public static string FormatAttribute(TestCategoryFramework framework, string category)
    {
        var attribute = framework switch
        {
            TestCategoryFramework.XUnit => $"[Trait(\"Category\", \"{category}\")]",
            TestCategoryFramework.MSTest => $"[TestCategory(\"{category}\")]",
            _ => $"[Category(\"{category}\")]",
        };
        return $"{attribute} // {GeneratedMarker}";
    }

    // ---------------------------------------------------------------------------------------------
    // Core
    // ---------------------------------------------------------------------------------------------

    private static async Task<TestCategoryPlan> PlanCoreAsync(Solution solution, TestCategoryPlanOptions options, CancellationToken cancellationToken)
    {
        var targets = SplitCsv(options.Targets);
        if (targets.Count == 0)
        {
            return TestCategoryPlan.Failed(new ResultError(
                ToolErrorCode.InvalidArgument,
                "targets is required: pass a CSV of project names or namespace prefixes whose types become categories, e.g. 'MyApp.Engines,MyApp.Services'."));
        }

        if (double.IsNaN(options.MaxTestShare) || options.MaxTestShare < 0 || options.MaxTestShare > 1)
        {
            return TestCategoryPlan.Failed(new ResultError(
                ToolErrorCode.InvalidArgument,
                $"maxTestShare must be between 0 and 1 (a fraction of all scanned tests), but was {options.MaxTestShare}. The default is 0.25."));
        }

        if (double.IsNaN(options.ClassLevelThreshold) || options.ClassLevelThreshold < 0 || options.ClassLevelThreshold > 1)
        {
            return TestCategoryPlan.Failed(new ResultError(
                ToolErrorCode.InvalidArgument,
                $"classLevelThreshold must be between 0 and 1 (a fraction of a fixture's test methods), but was {options.ClassLevelThreshold}. The default is 0.5."));
        }

        var testScope = SplitCsv(options.TestScope);
        var excludedTargets = SplitCsv(options.ExcludedTargets);
        var excludedTests = SplitCsv(options.ExcludedTests);
        var warnings = new List<string>();

        // 1. Every C# project with its compilation and the test framework it can see (null = not a test project).
        var infos = new List<ProjectInfo>();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                warnings.Add($"Project '{project.Name}' was skipped: it has no compilation.");
                continue;
            }

            infos.Add(new ProjectInfo(project, compilation, DetectFramework(compilation)));
        }

        // 2. Resolve the test scope.
        var scoped = new List<ProjectInfo>();
        if (testScope.Count > 0)
        {
            foreach (var entry in testScope)
            {
                var matches = infos.Where(i => Eq(i.Project.Name, entry) || Eq(i.Project.AssemblyName, entry)).ToList();
                if (matches.Count == 0)
                {
                    var available = string.Join(", ", infos.Where(i => i.Detected is not null).Select(i => i.Project.Name).Take(25));
                    return TestCategoryPlan.Failed(new ResultError(
                        ToolErrorCode.NotFound,
                        $"testScope names no project called '{entry}'. Test projects detected in the solution: {(available.Length == 0 ? "(none)" : available)}."));
                }

                scoped.AddRange(matches.Where(m => !scoped.Contains(m)));
            }
        }
        else
        {
            scoped.AddRange(infos.Where(i => i.Detected is not null));
        }

        var testProjectIds = new HashSet<ProjectId>(infos.Where(i => i.Detected is not null).Select(i => i.Project.Id));
        foreach (var info in scoped)
        {
            testProjectIds.Add(info.Project.Id);
        }

        var scanned = new List<(ProjectInfo Info, TestCategoryFramework Framework)>();
        foreach (var info in scoped.OrderBy(i => i.Project.Name, StringComparer.Ordinal))
        {
            var framework = options.Framework != TestCategoryFramework.Auto ? options.Framework : info.Detected;
            if (framework is null)
            {
                warnings.Add($"Test project '{info.Project.Name}' was skipped: no known test framework (NUnit, xUnit, MSTest) is referenced. Pass framework explicitly to override.");
                continue;
            }

            if (excludedTests.Count > 0 && MatchesName(excludedTests, info.Project.Name, info.Project.AssemblyName, string.Empty, string.Empty))
            {
                continue;
            }

            scanned.Add((info, framework.Value));
        }

        // 3. Every type declared in the targets (outside test projects), then the categorizable subset.
        var allTargetTypes = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);
        foreach (var info in infos.Where(i => !testProjectIds.Contains(i.Project.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectTargetTypes(info, targets, allTargetTypes);
        }

        foreach (var entry in targets)
        {
            if (!allTargetTypes.Values.Any(t => MatchesName([entry], t.Info.Project.Name, t.Info.Project.AssemblyName, t.Namespace, t.FullName)))
            {
                warnings.Add($"Target '{entry}' matched no types in non-test projects.");
            }
        }

        var candidates = allTargetTypes
            .Where(kv => !MatchesName(excludedTargets, kv.Value.Info.Project.Name, kv.Value.Info.Project.AssemblyName, kv.Value.Namespace, kv.Value.FullName))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var implementers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entry in allTargetTypes.Values)
        {
            if (entry.Symbol.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            foreach (var implemented in entry.Symbol.AllInterfaces)
            {
                var key = FullNameOf(implemented);
                if (!implementers.TryGetValue(key, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    implementers[key] = set;
                }

                set.Add(entry.FullName);
            }
        }

        // 4. One pass over the test code.
        var seenTests = new HashSet<string>(StringComparer.Ordinal);
        var projectData = new List<ProjectData>();
        foreach (var (info, framework) in scanned)
        {
            projectData.Add(await CollectProjectAsync(info, framework, excludedTests, implementers, candidates, seenTests, cancellationToken));
        }

        // 5. Category names (collision-qualified across the whole candidate set) and ubiquity.
        var categoryNames = ComputeCategoryNames(candidates.Values, out var collisionGroups);

        var allTests = projectData.SelectMany(p => p.Fixtures.Values).SelectMany(f => f.Tests).ToList();
        var totalTests = allTests.Count;
        var touchCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var test in allTests)
        {
            foreach (var type in test.Types)
            {
                touchCounts[type] = touchCounts.GetValueOrDefault(type) + 1;
            }
        }

        var ubiquitous = new HashSet<string>(StringComparer.Ordinal);
        var ubiquitousInfos = new List<UbiquitousTypeInfo>();
        if (totalTests > 0)
        {
            foreach (var (type, count) in touchCounts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var share = count / (double)totalTests;
                if (share > options.MaxTestShare + ShareEpsilon)
                {
                    ubiquitous.Add(type);
                    ubiquitousInfos.Add(new UbiquitousTypeInfo(categoryNames[type], type, count, totalTests, share));
                }
            }
        }

        foreach (var test in allTests)
        {
            test.Types.ExceptWith(ubiquitous);
        }

        var ubiquitousNames = new HashSet<string>(ubiquitous.Select(t => categoryNames[t]), StringComparer.Ordinal);
        var knownNames = new HashSet<string>(
            categoryNames.Where(kv => !ubiquitous.Contains(kv.Key)).Select(kv => kv.Value),
            StringComparer.Ordinal);

        // 6. Per project: fixtures, class-level vs method-level, existing and stale attributes.
        var projects = new List<TestProjectPlan>();
        foreach (var data in projectData)
        {
            projects.Add(BuildProjectPlan(data, categoryNames, ubiquitousNames, knownNames, options));
        }

        var parameters = new TestCategoryParametersUsed(
            targets,
            scanned.Select(s => s.Info.Project.Name).ToList(),
            excludedTargets,
            excludedTests,
            options.MaxTestShare,
            options.ClassLevelThreshold,
            options.Framework);

        return new TestCategoryPlan(parameters, ubiquitousInfos, collisionGroups, projects, warnings, null);
    }

    // ---------------------------------------------------------------------------------------------
    // Target types and category names
    // ---------------------------------------------------------------------------------------------

    private static void CollectTargetTypes(ProjectInfo info, IReadOnlyList<string> targets, Dictionary<string, TypeEntry> into)
    {
        var namespaces = new Stack<INamespaceSymbol>();
        var types = new Stack<INamedTypeSymbol>();
        namespaces.Push(info.Compilation.Assembly.GlobalNamespace);

        while (namespaces.Count > 0 || types.Count > 0)
        {
            IEnumerable<ISymbol> members;
            if (types.Count > 0)
            {
                members = types.Pop().GetTypeMembers();
            }
            else
            {
                members = namespaces.Pop().GetMembers();
            }

            foreach (var member in members)
            {
                if (member is INamespaceSymbol childNamespace)
                {
                    namespaces.Push(childNamespace);
                    continue;
                }

                if (member is not INamedTypeSymbol type || type.IsImplicitlyDeclared || type.Name.StartsWith('<'))
                {
                    continue;
                }

                types.Push(type);

                var fullName = FullNameOf(type);
                var namespaceName = NamespaceOf(type);
                if (into.ContainsKey(fullName)
                    || !MatchesName(targets, info.Project.Name, info.Project.AssemblyName, namespaceName, fullName))
                {
                    continue;
                }

                into[fullName] = new TypeEntry(fullName, type.Name, QualifierPath(type), namespaceName, info, type);
            }
        }
    }

    private static Dictionary<string, string> ComputeCategoryNames(IReadOnlyCollection<TypeEntry> entries, out List<CategoryCollisionGroup> groups)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        groups = [];

        foreach (var group in entries.GroupBy(e => e.SimpleName, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.OrderBy(m => m.FullName, StringComparer.Ordinal).ToList();
            if (members.Count == 1)
            {
                names[members[0].FullName] = group.Key;
                continue;
            }

            var qualified = Qualify(members);
            var groupMembers = new List<CategoryCollisionMember>();
            for (var i = 0; i < members.Count; i++)
            {
                names[members[i].FullName] = qualified[i];
                groupMembers.Add(new CategoryCollisionMember(members[i].FullName, qualified[i]));
            }

            groups.Add(new CategoryCollisionGroup(group.Key, groupMembers));
        }

        return names;
    }

    /// <summary>Shortest namespace suffix, segment by segment up to the full path, that makes the group unique.</summary>
    private static string[] Qualify(IReadOnlyList<TypeEntry> members)
    {
        var maxLength = members.Max(m => m.QualifierPath.Length);
        for (var k = 1; k <= maxLength; k++)
        {
            var names = members.Select(m => string.Join('.', m.QualifierPath.TakeLast(k).Append(m.SimpleName))).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() == names.Length)
            {
                return names;
            }
        }

        // Same namespace path (for example Foo and Foo<T>): the full display name is unique by construction.
        return members.Select(m => m.FullName).ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // Test collection
    // ---------------------------------------------------------------------------------------------

    private static async Task<ProjectData> CollectProjectAsync(
        ProjectInfo info,
        TestCategoryFramework framework,
        IReadOnlyList<string> excludedTests,
        IReadOnlyDictionary<string, HashSet<string>> implementers,
        IReadOnlyDictionary<string, TypeEntry> candidates,
        HashSet<string> seenTests,
        CancellationToken cancellationToken)
    {
        var data = new ProjectData(info, framework);

        foreach (var document in info.Project.Documents.OrderBy(d => d.FilePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null || IsGeneratedPath(document.FilePath))
            {
                continue;
            }

            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (tree is null)
            {
                continue;
            }

            var model = info.Compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(cancellationToken);

            foreach (var methodSyntax in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var method = model.GetDeclaredSymbol(methodSyntax, cancellationToken);
                if (method is null || !IsTestMethod(method, framework))
                {
                    continue;
                }

                var fixtureSymbol = method.ContainingType;
                var fixtureName = FullNameOf(fixtureSymbol);
                if (excludedTests.Count > 0
                    && MatchesName(excludedTests, info.Project.Name, info.Project.AssemblyName, NamespaceOf(fixtureSymbol), fixtureName))
                {
                    continue;
                }

                // A file linked into several projects (multi-targeting) shows up once per project; scan it once.
                if (!seenTests.Add($"{document.FilePath}|{methodSyntax.SpanStart}"))
                {
                    continue;
                }

                if (!data.Fixtures.TryGetValue(fixtureName, out var fixture))
                {
                    fixture = CreateFixture(info, framework, fixtureSymbol, fixtureName, cancellationToken);
                    data.Fixtures[fixtureName] = fixture;
                }

                var collector = new ReferenceCollector(model, implementers, candidates, cancellationToken);
                collector.Collect(methodSyntax);

                var test = new TestData(
                    method.Name,
                    method.GetDocumentationCommentId(),
                    document.FilePath,
                    methodSyntax.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    collector.Touched,
                    ReadExistingCategories(methodSyntax.AttributeLists, model, framework, TestCategoryLevel.Method, document.FilePath, cancellationToken));
                fixture.Tests.Add(test);
            }
        }

        return data;
    }

    private static FixtureData CreateFixture(ProjectInfo info, TestCategoryFramework framework, INamedTypeSymbol symbol, string fullName, CancellationToken cancellationToken)
    {
        var declarations = symbol.DeclaringSyntaxReferences
            .Select(r => (Reference: r, Path: r.SyntaxTree.FilePath ?? string.Empty))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Reference.Span.Start)
            .ToList();

        var filePath = string.Empty;
        var line = 1;
        var existing = new List<ExistingCategory>();
        for (var i = 0; i < declarations.Count; i++)
        {
            var syntax = declarations[i].Reference.GetSyntax(cancellationToken);
            if (i == 0)
            {
                filePath = declarations[i].Path;
                var location = syntax is BaseTypeDeclarationSyntax typeSyntax ? typeSyntax.Identifier.GetLocation() : syntax.GetLocation();
                line = location.GetLineSpan().StartLinePosition.Line + 1;
            }

            if (syntax is MemberDeclarationSyntax member)
            {
                var model = info.Compilation.GetSemanticModel(declarations[i].Reference.SyntaxTree);
                existing.AddRange(ReadExistingCategories(member.AttributeLists, model, framework, TestCategoryLevel.Class, declarations[i].Path, cancellationToken));
            }
        }

        return new FixtureData(fullName, filePath, line, existing);
    }

    private static bool IsTestMethod(IMethodSymbol method, TestCategoryFramework framework)
    {
        var markers = TestMarkers(framework);
        foreach (var attribute in method.GetAttributes())
        {
            for (var type = attribute.AttributeClass; type is not null; type = type.BaseType)
            {
                if (markers.Contains(type.ToDisplayString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<ExistingCategory> ReadExistingCategories(
        SyntaxList<AttributeListSyntax> lists,
        SemanticModel model,
        TestCategoryFramework framework,
        TestCategoryLevel level,
        string filePath,
        CancellationToken cancellationToken)
    {
        var result = new List<ExistingCategory>();
        var attributeTypeName = CategoryAttributeTypeName(framework);

        foreach (var list in lists)
        {
            var marked = list.CloseBracketToken.TrailingTrivia.Any(t =>
                t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().Contains(GeneratedMarker, StringComparison.Ordinal));
            var text = (list.ToString() + list.GetTrailingTrivia().ToFullString()).Trim();
            var line = list.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            foreach (var attribute in list.Attributes)
            {
                var category = TryGetCategoryName(attribute, model, framework, attributeTypeName, cancellationToken);
                if (category is not null)
                {
                    result.Add(new ExistingCategory(level, category, marked, filePath, line, text));
                }
            }
        }

        return result;
    }

    private static string? TryGetCategoryName(AttributeSyntax attribute, SemanticModel model, TestCategoryFramework framework, string attributeTypeName, CancellationToken cancellationToken)
    {
        var info = model.GetSymbolInfo(attribute, cancellationToken);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        if (symbol?.ContainingType?.ToDisplayString() != attributeTypeName)
        {
            return null;
        }

        AttributeArgumentSyntax[] args = attribute.ArgumentList?.Arguments.ToArray() ?? [];
        string? Constant(AttributeArgumentSyntax argument)
        {
            var value = model.GetConstantValue(argument.Expression, cancellationToken);
            return value.HasValue ? value.Value as string : null;
        }

        if (framework == TestCategoryFramework.XUnit)
        {
            return args.Length >= 2 && Constant(args[0]) == "Category" ? Constant(args[1]) : null;
        }

        return args.Length >= 1 ? Constant(args[0]) : null;
    }

    // ---------------------------------------------------------------------------------------------
    // Plan assembly
    // ---------------------------------------------------------------------------------------------

    private static TestProjectPlan BuildProjectPlan(
        ProjectData data,
        IReadOnlyDictionary<string, string> categoryNames,
        IReadOnlySet<string> ubiquitousNames,
        IReadOnlySet<string> knownNames,
        TestCategoryPlanOptions options)
    {
        var projectName = data.Info.Project.Name;
        var framework = data.Framework;
        var edits = new List<PlannedCategoryEdit>();
        var fixturePlans = new List<FixturePlan>();
        var uncategorized = new List<UncategorizedTest>();
        var skippedExisting = 0;
        var testsScanned = 0;

        StaleCategoryReason ReasonFor(string category)
        {
            if (ubiquitousNames.Contains(category))
            {
                return StaleCategoryReason.TypeNowUbiquitous;
            }

            return !knownNames.Contains(category)
                ? StaleCategoryReason.TypeNotACategory
                : StaleCategoryReason.NoLongerReferenced;
        }

        foreach (var fixture in data.Fixtures.Values.OrderBy(f => f.FullName, StringComparer.Ordinal))
        {
            var tests = fixture.Tests;
            testsScanned += tests.Count;

            var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var test in tests)
            {
                foreach (var type in test.Types)
                {
                    typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
                }
            }

            var shares = typeCounts
                .Select(kv => new FixtureTypeShare(categoryNames[kv.Key], kv.Key, kv.Value, kv.Value / (double)tests.Count))
                .OrderByDescending(s => s.Share)
                .ThenBy(s => s.CategoryName, StringComparer.Ordinal)
                .ToList();

            var classLevel = shares.Where(s => s.Share + ShareEpsilon >= options.ClassLevelThreshold).ToList();
            var classLevelTypes = new HashSet<string>(classLevel.Select(s => s.TypeFullName), StringComparer.Ordinal);
            var classLevelNames = classLevel.Select(s => s.CategoryName).OrderBy(n => n, StringComparer.Ordinal).ToList();

            fixturePlans.Add(new FixturePlan(
                fixture.FullName,
                fixture.FilePath,
                fixture.Line,
                tests.Count,
                shares,
                classLevelNames,
                shares.Count > 0 && classLevel.Count == 0));

            // A class-level name covers a method when it will exist after the run: computed, or hand-written (unmarked).
            var coveringClassNames = new HashSet<string>(fixture.ExistingClassLevel.Where(e => !e.IsMarked).Select(e => e.Category), StringComparer.Ordinal);
            coveringClassNames.UnionWith(classLevelNames);

            foreach (var category in classLevelNames)
            {
                if (fixture.ExistingClassLevel.Any(e => e.Category == category))
                {
                    skippedExisting++;
                    continue;
                }

                edits.Add(new PlannedCategoryEdit(
                    TestCategoryEditKind.Add, TestCategoryLevel.Class, projectName, fixture.FilePath, fixture.Line,
                    fixture.FullName, null, null, category, FormatAttribute(framework, category)));
            }

            foreach (var existing in fixture.ExistingClassLevel.Where(e => e.IsMarked && !classLevelNames.Contains(e.Category)))
            {
                edits.Add(new PlannedCategoryEdit(
                    TestCategoryEditKind.RemoveStale, TestCategoryLevel.Class, projectName, existing.FilePath, existing.Line,
                    fixture.FullName, null, null, existing.Category, existing.Text, ReasonFor(existing.Category)));
            }

            foreach (var test in tests)
            {
                if (test.Types.Count == 0)
                {
                    uncategorized.Add(new UncategorizedTest(fixture.FullName, test.MethodName, test.FilePath, test.Line));
                }

                var methodLevelNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var type in test.Types.OrderBy(t => categoryNames[t], StringComparer.Ordinal))
                {
                    if (classLevelTypes.Contains(type))
                    {
                        continue;
                    }

                    var category = categoryNames[type];
                    methodLevelNames.Add(category);
                    if (test.ExistingMethodLevel.Any(e => e.Category == category) || coveringClassNames.Contains(category))
                    {
                        skippedExisting++;
                        continue;
                    }

                    edits.Add(new PlannedCategoryEdit(
                        TestCategoryEditKind.Add, TestCategoryLevel.Method, projectName, test.FilePath, test.Line,
                        fixture.FullName, test.MethodName, test.DocCommentId, category, FormatAttribute(framework, category)));
                }

                // A method-level attribute whose category a class-level attribute now also covers is kept (marked or
                // not): redundant, but not stale. Only a category that is neither computed method-level nor class-level goes.
                foreach (var existing in test.ExistingMethodLevel)
                {
                    if (classLevelNames.Contains(existing.Category))
                    {
                        skippedExisting++;
                        continue;
                    }

                    if (!existing.IsMarked || methodLevelNames.Contains(existing.Category))
                    {
                        continue;
                    }

                    edits.Add(new PlannedCategoryEdit(
                        TestCategoryEditKind.RemoveStale, TestCategoryLevel.Method, projectName, existing.FilePath, existing.Line,
                        fixture.FullName, test.MethodName, test.DocCommentId, existing.Category, existing.Text, ReasonFor(existing.Category)));
                }
            }
        }

        var orderedEdits = edits
            .OrderBy(e => e.FilePath, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.CategoryName, StringComparer.Ordinal)
            .ToList();

        return new TestProjectPlan(projectName, framework, testsScanned, fixturePlans, uncategorized, orderedEdits, skippedExisting);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static List<string> SplitCsv(string? csv)
    {
        return (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when any entry equals the project (or assembly) name, or is a namespace or type-name prefix of the type.</summary>
    private static bool MatchesName(IReadOnlyList<string> entries, string? projectName, string? assemblyName, string namespaceName, string typeFullName)
    {
        foreach (var entry in entries)
        {
            if (Eq(projectName, entry) || Eq(assemblyName, entry))
            {
                return true;
            }

            if (namespaceName.Length > 0
                && (namespaceName == entry || namespaceName.StartsWith(entry + ".", StringComparison.Ordinal)))
            {
                return true;
            }

            if (typeFullName.Length > 0
                && (typeFullName == entry || typeFullName.StartsWith(entry + ".", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string FullNameOf(INamedTypeSymbol type)
    {
        var name = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;
    }

    private static string NamespaceOf(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
    }

    /// <summary>Namespace segments then containing-type names, outermost first (excludes the type itself).</summary>
    private static string[] QualifierPath(INamedTypeSymbol type)
    {
        var path = new List<string>();
        for (var container = type.ContainingType; container is not null; container = container.ContainingType)
        {
            path.Insert(0, container.Name);
        }

        path.InsertRange(0, NamespaceOf(type).Split('.', StringSplitOptions.RemoveEmptyEntries));
        return path.ToArray();
    }

    private static TestCategoryFramework? DetectFramework(Compilation compilation)
    {
        foreach (var framework in new[] { TestCategoryFramework.NUnit, TestCategoryFramework.XUnit, TestCategoryFramework.MSTest })
        {
            if (compilation.GetTypeByMetadataName(DetectionTypeName(framework)) is not null)
            {
                return framework;
            }
        }

        return null;
    }

    private static string DetectionTypeName(TestCategoryFramework framework) => framework switch
    {
        TestCategoryFramework.XUnit => "Xunit.FactAttribute",
        TestCategoryFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute",
        _ => "NUnit.Framework.TestAttribute",
    };

    private static string[] TestMarkers(TestCategoryFramework framework) => framework switch
    {
        TestCategoryFramework.XUnit => XUnitTestMarkers,
        TestCategoryFramework.MSTest => MsTestMarkers,
        _ => NUnitTestMarkers,
    };

    private static string CategoryAttributeTypeName(TestCategoryFramework framework) => framework switch
    {
        TestCategoryFramework.XUnit => "Xunit.TraitAttribute",
        TestCategoryFramework.MSTest => "Microsoft.VisualStudio.TestTools.UnitTesting.TestCategoryAttribute",
        _ => "NUnit.Framework.CategoryAttribute",
    };

    private static bool IsGeneratedPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var objSegment = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        return path.Contains(objSegment, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // Internal state
    // ---------------------------------------------------------------------------------------------

    private sealed record ProjectInfo(Project Project, Compilation Compilation, TestCategoryFramework? Detected);

    private sealed record TypeEntry(string FullName, string SimpleName, string[] QualifierPath, string Namespace, ProjectInfo Info, INamedTypeSymbol Symbol);

    private sealed record ExistingCategory(TestCategoryLevel Level, string Category, bool IsMarked, string FilePath, int Line, string Text);

    private sealed record TestData(
        string MethodName,
        string? DocCommentId,
        string FilePath,
        int Line,
        HashSet<string> Types,
        List<ExistingCategory> ExistingMethodLevel);

    private sealed class FixtureData(string fullName, string filePath, int line, List<ExistingCategory> existingClassLevel)
    {
        public string FullName { get; } = fullName;

        public string FilePath { get; } = filePath;

        public int Line { get; } = line;

        public List<ExistingCategory> ExistingClassLevel { get; } = existingClassLevel;

        public List<TestData> Tests { get; } = [];
    }

    private sealed class ProjectData(ProjectInfo info, TestCategoryFramework framework)
    {
        public ProjectInfo Info { get; } = info;

        public TestCategoryFramework Framework { get; } = framework;

        public Dictionary<string, FixtureData> Fixtures { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Walks one test method and records the categorizable target types it directly references.</summary>
    private sealed class ReferenceCollector(
        SemanticModel model,
        IReadOnlyDictionary<string, HashSet<string>> implementers,
        IReadOnlyDictionary<string, TypeEntry> candidates,
        CancellationToken cancellationToken)
    {
        public HashSet<string> Touched { get; } = new(StringComparer.Ordinal);

        public void Collect(MethodDeclarationSyntax method)
        {
            foreach (var list in method.AttributeLists)
            {
                Walk(list);
            }

            if (method.Body is not null)
            {
                Walk(method.Body);
            }

            if (method.ExpressionBody is not null)
            {
                Walk(method.ExpressionBody);
            }
        }

        private void Walk(SyntaxNode root)
        {
            foreach (var node in root.DescendantNodesAndSelf())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax invocation:
                        if (invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
                            && model.GetSymbolInfo(invocation, cancellationToken).Symbol is null)
                        {
                            var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
                            if (argument is not null)
                            {
                                AddSymbol(Resolve(model.GetSymbolInfo(argument, cancellationToken)));
                            }
                        }
                        else
                        {
                            AddSymbol(Resolve(model.GetSymbolInfo(invocation, cancellationToken)));
                        }

                        break;

                    case BaseObjectCreationExpressionSyntax creation:
                        var created = Resolve(model.GetSymbolInfo(creation, cancellationToken));
                        if (created is not null)
                        {
                            AddSymbol(created);
                        }
                        else
                        {
                            AddType(model.GetTypeInfo(creation, cancellationToken).Type);
                        }

                        break;

                    case MemberAccessExpressionSyntax memberAccess:
                        AddSymbol(Resolve(model.GetSymbolInfo(memberAccess, cancellationToken)));
                        break;

                    case MemberBindingExpressionSyntax memberBinding:
                        AddSymbol(Resolve(model.GetSymbolInfo(memberBinding, cancellationToken)));
                        break;

                    case TypeOfExpressionSyntax typeOf:
                        AddType(model.GetTypeInfo(typeOf.Type, cancellationToken).Type);
                        break;

                    case TypeArgumentListSyntax typeArguments:
                        foreach (var typeArgument in typeArguments.Arguments)
                        {
                            AddType(model.GetTypeInfo(typeArgument, cancellationToken).Type);
                        }

                        break;
                }
            }
        }

        private static ISymbol? Resolve(SymbolInfo info) => info.Symbol ?? info.CandidateSymbols.FirstOrDefault();

        private void AddSymbol(ISymbol? symbol)
        {
            switch (symbol)
            {
                case INamedTypeSymbol type:
                    AddType(type);
                    break;
                case IMethodSymbol method:
                    AddType((method.ReducedFrom ?? method).ContainingType);
                    break;
                case IPropertySymbol or IFieldSymbol or IEventSymbol:
                    AddType(symbol.ContainingType);
                    break;
            }
        }

        private void AddType(ITypeSymbol? type)
        {
            while (type is IArrayTypeSymbol array)
            {
                type = array.ElementType;
            }

            if (type is not INamedTypeSymbol named)
            {
                return;
            }

            var original = named.OriginalDefinition;
            var fullName = FullNameOf(original);
            if (original.TypeKind == TypeKind.Interface
                && implementers.TryGetValue(fullName, out var implementing)
                && implementing.Count == 1)
            {
                fullName = implementing.First();
            }

            if (candidates.ContainsKey(fullName))
            {
                Touched.Add(fullName);
            }
        }
    }
}
