namespace RoslynSentinel.Engines.Basic;

/// <summary>Test framework a test project uses; selects the category attribute shape and the test-method markers.</summary>
public enum TestCategoryFramework
{
    /// <summary>Resolve per test project from the test framework types its compilation can see.</summary>
    Auto,

    /// <summary>NUnit: <c>[Category("X")]</c>.</summary>
    NUnit,

    /// <summary>xUnit: <c>[Trait("Category", "X")]</c>.</summary>
    XUnit,

    /// <summary>MSTest: <c>[TestCategory("X")]</c>.</summary>
    MSTest,
}

/// <summary>Where a planned category attribute lives.</summary>
public enum TestCategoryLevel
{
    /// <summary>On the fixture (test class); covers every test method in it.</summary>
    Class,

    /// <summary>On a single test method.</summary>
    Method,
}

/// <summary>What a planned edit does.</summary>
public enum TestCategoryEditKind
{
    /// <summary>Add a generated, marked category attribute.</summary>
    Add,

    /// <summary>Remove a stale attribute that carries the generated marker.</summary>
    RemoveStale,
}

/// <summary>Why a marked attribute is considered stale.</summary>
public enum StaleCategoryReason
{
    /// <summary>The category is not the name of any current target type (the type was deleted, renamed, excluded or re-qualified).</summary>
    TypeNotACategory,

    /// <summary>The type is now auto-excluded as ubiquitous (touched by more than maxTestShare of all scanned tests).</summary>
    TypeNowUbiquitous,

    /// <summary>The test (or fixture, at class level) no longer references the type, or no longer reaches the class-level threshold.</summary>
    NoLongerReferenced,
}

/// <summary>
/// Inputs for <see cref="TestCategoryTaggingEngine"/>. CSV fields are comma-separated lists of project names,
/// namespace prefixes or (for the excluded lists) type full names.
/// </summary>
/// <param name="Targets">Projects or namespace prefixes whose types become categories. Required.</param>
/// <param name="TestScope">Test projects to scan. Null/empty: every C# project whose compilation references a known test framework.</param>
/// <param name="ExcludedTargets">Namespaces/types/projects never used as categories (excludes callees, not test callers).</param>
/// <param name="ExcludedTests">Test projects/namespaces/types skipped as callers.</param>
/// <param name="MaxTestShare">A type touched by more than this fraction of all scanned tests is auto-excluded as ubiquitous.</param>
/// <param name="ClassLevelThreshold">Minimum share of a fixture's test methods touching a type for a class-level attribute.</param>
/// <param name="Framework">Framework to use for every scanned project, or <see cref="TestCategoryFramework.Auto"/>.</param>
public sealed record TestCategoryPlanOptions(
    string Targets,
    string? TestScope = null,
    string? ExcludedTargets = null,
    string? ExcludedTests = null,
    double MaxTestShare = 0.25,
    double ClassLevelThreshold = 0.5,
    TestCategoryFramework Framework = TestCategoryFramework.Auto);

/// <summary>The parameter values a plan was computed with, echoed so the caller can adjust them.</summary>
public sealed record TestCategoryParametersUsed(
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> TestScope,
    IReadOnlyList<string> ExcludedTargets,
    IReadOnlyList<string> ExcludedTests,
    double MaxTestShare,
    double ClassLevelThreshold,
    TestCategoryFramework Framework);

/// <summary>A target type auto-excluded because too large a share of all scanned tests touch it.</summary>
public sealed record UbiquitousTypeInfo(string CategoryName, string TypeFullName, int TestsTouching, int TotalTests, double Share);

/// <summary>Target types that share a simple name, with the category name chosen for each.</summary>
public sealed record CategoryCollisionGroup(string SimpleName, IReadOnlyList<CategoryCollisionMember> Members);

/// <summary>One member of a <see cref="CategoryCollisionGroup"/>.</summary>
public sealed record CategoryCollisionMember(string TypeFullName, string CategoryName);

/// <summary>How many of a fixture's tests touch one category type.</summary>
public sealed record FixtureTypeShare(string CategoryName, string TypeFullName, int TestsTouching, double Share);

/// <summary>Per-fixture result: per-type shares, which types reach the class-level threshold, and whether the fixture is ambiguous.</summary>
/// <param name="TypeShares">Every categorizable type the fixture's tests touch, highest share first.</param>
/// <param name="ClassLevelCategories">Category names at or above the class-level threshold.</param>
/// <param name="IsAmbiguous">True when the fixture's tests touch categorizable types but none reaches the class-level threshold (method-level attributes only).</param>
public sealed record FixturePlan(
    string FixtureName,
    string FilePath,
    int Line,
    int TestCount,
    IReadOnlyList<FixtureTypeShare> TypeShares,
    IReadOnlyList<string> ClassLevelCategories,
    bool IsAmbiguous);

/// <summary>A test method that directly references no categorizable target type.</summary>
public sealed record UncategorizedTest(string FixtureName, string MethodName, string FilePath, int Line);

/// <summary>
/// One planned edit, as data only. <see cref="AttributeText"/> is the exact attribute line to write for an
/// <see cref="TestCategoryEditKind.Add"/> (marker comment included) and the matched text for a removal.
/// </summary>
/// <param name="MethodName">Null for a class-level edit.</param>
/// <param name="MethodDocCommentId">Declaration id of the method; null for a class-level edit.</param>
/// <param name="Line">1-based line of the attribute for a removal, or of the fixture/method for an add.</param>
/// <param name="StaleReason">Set only for <see cref="TestCategoryEditKind.RemoveStale"/>.</param>
public sealed record PlannedCategoryEdit(
    TestCategoryEditKind Kind,
    TestCategoryLevel Level,
    string ProjectName,
    string FilePath,
    int Line,
    string FixtureName,
    string? MethodName,
    string? MethodDocCommentId,
    string CategoryName,
    string AttributeText,
    StaleCategoryReason? StaleReason = null);

/// <summary>Plan for one test project.</summary>
/// <param name="SkippedExisting">Computed (test, category) pairs whose attribute is already present (marked or hand-written).</param>
public sealed record TestProjectPlan(
    string ProjectName,
    TestCategoryFramework Framework,
    int TestsScanned,
    IReadOnlyList<FixturePlan> Fixtures,
    IReadOnlyList<UncategorizedTest> UncategorizedTests,
    IReadOnlyList<PlannedCategoryEdit> Edits,
    int SkippedExisting)
{
    /// <summary>Number of class-level attributes to add.</summary>
    public int ClassLevelAdds => Edits.Count(e => e.Kind == TestCategoryEditKind.Add && e.Level == TestCategoryLevel.Class);

    /// <summary>Number of method-level attributes to add.</summary>
    public int MethodLevelAdds => Edits.Count(e => e.Kind == TestCategoryEditKind.Add && e.Level == TestCategoryLevel.Method);

    /// <summary>Number of stale marked attributes to remove.</summary>
    public int StaleToRemove => Edits.Count(e => e.Kind == TestCategoryEditKind.RemoveStale);

    /// <summary>Number of ambiguous fixtures.</summary>
    public int AmbiguousFixtures => Fixtures.Count(f => f.IsAmbiguous);
}

/// <summary>
/// The in-memory planning model and report: solution-wide parts (parameters used, auto-excluded ubiquitous types,
/// collision groups) plus one <see cref="TestProjectPlan"/> per scanned test project. <see cref="Error"/> is set
/// (and everything else empty) when the plan could not be computed.
/// </summary>
public sealed record TestCategoryPlan(
    TestCategoryParametersUsed? Parameters,
    IReadOnlyList<UbiquitousTypeInfo> AutoExcludedTypes,
    IReadOnlyList<CategoryCollisionGroup> CollisionGroups,
    IReadOnlyList<TestProjectPlan> Projects,
    IReadOnlyList<string> Warnings,
    ResultError? Error)
{
    /// <summary>Total test methods scanned across all projects.</summary>
    public int TotalTestsScanned => Projects.Sum(p => p.TestsScanned);

    /// <summary>Builds a failed plan carrying only <paramref name="error"/>.</summary>
    public static TestCategoryPlan Failed(ResultError error) => new(null, [], [], [], [], error);
}
