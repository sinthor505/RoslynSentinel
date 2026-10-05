using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynSentinel.Engines.Basic;

namespace RoslynSentinel.Tests.Basic;

public class TestCategoryTaggingEngineTests
{
    private const string TargetsProjectName = "Targets";
    private const string TestsProjectName = "Fixtures";

    private static readonly string[] AllReferencePaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator);

    private static readonly Lazy<MetadataReference[]> FullReferences = new(
        () => AllReferencePaths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray());

    // Target projects must not see a test framework, or they would be classified as test projects themselves.
    private static readonly Lazy<MetadataReference[]> NoNUnitReferences = new(
        () => AllReferencePaths
            .Where(p => !Path.GetFileName(p).StartsWith("nunit.", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    private static Solution BuildSolution(string targetsSource, string testsSource, bool useNUnit = true)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        var targets = solution.AddProject(TargetsProjectName, TargetsProjectName, LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(NoNUnitReferences.Value)
            .AddDocument("Targets.cs", SourceText.From(targetsSource), filePath: @"C:\fake\Targets\Targets.cs")
            .Project;

        var tests = targets.Solution.AddProject(TestsProjectName, TestsProjectName, LanguageNames.CSharp)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(useNUnit ? FullReferences.Value : NoNUnitReferences.Value)
            .AddProjectReference(new ProjectReference(targets.Id))
            .AddDocument("Tests.cs", SourceText.From(testsSource), filePath: @"C:\fake\Fixtures\Tests.cs")
            .Project;

        return tests.Solution;
    }

    private static Task<TestCategoryPlan> PlanAsync(string targetsSource, string testsSource, TestCategoryPlanOptions? options = null)
    {
        return TestCategoryTaggingEngine.PlanForSolutionAsync(
            BuildSolution(targetsSource, testsSource),
            options ?? new TestCategoryPlanOptions("Targets", MaxTestShare: 1.0));
    }

    private static TestProjectPlan Project(TestCategoryPlan plan)
    {
        Assert.That(plan.Error, Is.Null, plan.Error?.Message);
        Assert.That(plan.Projects, Has.Count.EqualTo(1));
        return plan.Projects[0];
    }

    private static string[] Adds(TestProjectPlan project, TestCategoryLevel level)
    {
        return project.Edits
            .Where(e => e.Kind == TestCategoryEditKind.Add && e.Level == level)
            .Select(e => (e.MethodName ?? "<class>") + ":" + e.CategoryName)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private const string BasicTargets = @"
namespace Targets.Engines
{
    public class Alpha { public void Run() { } }
    public class Beta { public int Value { get; set; } }
    public class Gamma { public static void Go() { } }
}";

    [Test]
    public async Task NUnit_JunkDrawerFixture_GetsMethodLevelMultiCategories()
    {
        var plan = await PlanAsync(BasicTargets, @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    [TestFixture]
    public class Drawer
    {
        [Test] public void T1() { new Alpha().Run(); }
        [Test] public void T2() { var b = new Beta(); b.Value = 1; }
        [Test] public void T3() { new Alpha().Run(); var n = new Beta().Value; }
        [Test] public void T4() { Gamma.Go(); }
        [Test] public void T5() { Assert.Pass(); }
    }
}");

        var project = Project(plan);

        Assert.That(project.Framework, Is.EqualTo(TestCategoryFramework.NUnit));
        Assert.That(project.TestsScanned, Is.EqualTo(5));
        Assert.That(Adds(project, TestCategoryLevel.Class), Is.Empty);
        Assert.That(Adds(project, TestCategoryLevel.Method), Is.EqualTo(new[]
        {
            "T1:Alpha", "T2:Beta", "T3:Alpha", "T3:Beta", "T4:Gamma",
        }));
        Assert.That(project.Fixtures.Single().IsAmbiguous, Is.True);
        Assert.That(project.Edits.First().AttributeText, Does.EndWith("// sentinel:auto-category"));
        Assert.That(project.Edits.Select(e => e.AttributeText), Does.Contain("[Category(\"Alpha\")] // sentinel:auto-category"));
    }

    [Test]
    public async Task UncategorizedTests_AreReportedByName()
    {
        var plan = await PlanAsync(BasicTargets, @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Drawer
    {
        [Test] public void Touches() { new Alpha().Run(); }
        [Test] public void Touches2() { new Beta(); }
        [Test] public void Nothing() { Assert.Pass(); }
        [TestCase(1)] public void AlsoNothing(int x) { }
    }
}");

        var project = Project(plan);

        Assert.That(project.UncategorizedTests.Select(u => u.MethodName), Is.EquivalentTo(new[] { "Nothing", "AlsoNothing" }));
        Assert.That(project.UncategorizedTests.All(u => u.FixtureName == "Fixtures.Drawer"), Is.True);
    }

    [Test]
    public async Task ClassLevelThreshold_AssignsClassLevelOnlyAtOrAboveThreshold()
    {
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class AlphaFixture
    {
        [Test] public void A1() { new Alpha().Run(); }
        [Test] public void A2() { new Alpha().Run(); }
        [Test] public void A3() { new Alpha().Run(); }
        [Test] public void B1() { new Beta(); }
    }
}";

        var project = Project(await PlanAsync(BasicTargets, tests));

        Assert.That(Adds(project, TestCategoryLevel.Class), Is.EqualTo(new[] { "<class>:Alpha" }));
        Assert.That(Adds(project, TestCategoryLevel.Method), Is.EqualTo(new[] { "B1:Beta" }));
        Assert.That(project.Fixtures.Single().ClassLevelCategories, Is.EqualTo(new[] { "Alpha" }));
        Assert.That(project.Fixtures.Single().IsAmbiguous, Is.False);

        var strict = Project(await PlanAsync(BasicTargets, tests, new TestCategoryPlanOptions("Targets", MaxTestShare: 1.0, ClassLevelThreshold: 0.8)));

        Assert.That(Adds(strict, TestCategoryLevel.Class), Is.Empty);
        Assert.That(Adds(strict, TestCategoryLevel.Method), Is.EqualTo(new[] { "A1:Alpha", "A2:Alpha", "A3:Alpha", "B1:Beta" }));
        Assert.That(strict.Fixtures.Single().IsAmbiguous, Is.True);
    }

    [Test]
    public async Task UbiquitousType_IsAutoExcludedAndNeverACategory()
    {
        // Ubiq is touched by all 8 tests (share 1.0 > 0.25); Alpha by 2 of 8 (0.25, not above the cap).
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Busy
    {
        [Test] public void T1() { new Ubiq(); new Alpha().Run(); }
        [Test] public void T2() { new Ubiq(); new Alpha().Run(); }
        [Test] public void T3() { new Ubiq(); }
        [Test] public void T4() { new Ubiq(); }
        [Test] public void T5() { new Ubiq(); }
        [Test] public void T6() { new Ubiq(); }
        [Test] public void T7() { new Ubiq(); }
        [Test] public void T8() { new Ubiq(); }
    }
}";
        var targets = BasicTargets + "\nnamespace Targets.Engines { public class Ubiq { } }";

        var plan = await PlanAsync(targets, tests, new TestCategoryPlanOptions("Targets"));
        var project = Project(plan);

        Assert.That(plan.AutoExcludedTypes.Select(u => u.CategoryName), Is.EqualTo(new[] { "Ubiq" }));
        Assert.That(plan.AutoExcludedTypes[0].Share, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(plan.AutoExcludedTypes[0].TestsTouching, Is.EqualTo(8));
        Assert.That(project.Edits.Select(e => e.CategoryName), Does.Not.Contain("Ubiq"));
        Assert.That(Adds(project, TestCategoryLevel.Method), Is.EqualTo(new[] { "T1:Alpha", "T2:Alpha" }));
        Assert.That(project.UncategorizedTests, Has.Count.EqualTo(6));
        Assert.That(plan.Parameters!.MaxTestShare, Is.EqualTo(0.25));
    }

    [Test]
    public async Task CollidingSimpleNames_AreQualifiedWithShortestUniqueNamespaceSuffix()
    {
        var targets = @"
namespace Targets.Basic { public class Foo { public void Do() { } } }
namespace Targets.Advanced { public class Foo { public void Do() { } } }
namespace Targets.Basic { public class Bar { public void Do() { } } }";
        var tests = @"
using NUnit.Framework;
namespace Fixtures
{
    public class Mixed
    {
        [Test] public void UsesBasic() { new Targets.Basic.Foo().Do(); }
        [Test] public void UsesAdvanced() { new Targets.Advanced.Foo().Do(); }
        [Test] public void UsesBar() { new Targets.Basic.Bar().Do(); }
        [Test] public void UsesNone() { Assert.Pass(); }
    }
}";

        var plan = await PlanAsync(targets, tests);
        var project = Project(plan);

        Assert.That(plan.CollisionGroups, Has.Count.EqualTo(1));
        Assert.That(plan.CollisionGroups[0].SimpleName, Is.EqualTo("Foo"));
        Assert.That(plan.CollisionGroups[0].Members.Select(m => m.CategoryName), Is.EquivalentTo(new[] { "Advanced.Foo", "Basic.Foo" }));
        Assert.That(Adds(project, TestCategoryLevel.Method), Is.EqualTo(new[]
        {
            "UsesAdvanced:Advanced.Foo", "UsesBar:Bar", "UsesBasic:Basic.Foo",
        }));
    }

    [Test]
    public async Task StaleMarkedAttributes_AreDetected_HandWrittenAreNeverTouched()
    {
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Existing
    {
        [Test]
        [Category(""Alpha"")] // sentinel:auto-category
        public void MarkedButNoLongerReferenced() { new Beta(); }

        [Test]
        [Category(""Gone"")] // sentinel:auto-category
        public void MarkedForDeletedType() { new Alpha().Run(); }

        [Test]
        [Category(""Handwritten"")]
        public void HandWrittenOnly() { new Gamma(); }

        [Test]
        [Category(""Alpha"")] // sentinel:auto-category
        public void MarkedAndStillCorrect() { new Alpha().Run(); }

        [Test]
        [Category(""Beta"")]
        public void HandWrittenEqualsComputed() { new Beta(); }
    }
}";

        var project = Project(await PlanAsync(BasicTargets, tests));
        var stale = project.Edits.Where(e => e.Kind == TestCategoryEditKind.RemoveStale).ToList();

        Assert.That(stale.Select(e => e.MethodName + ":" + e.CategoryName + ":" + e.StaleReason), Is.EquivalentTo(new[]
        {
            "MarkedButNoLongerReferenced:Alpha:NoLongerReferenced",
            "MarkedForDeletedType:Gone:TypeNotACategory",
        }));
        Assert.That(stale.All(e => e.AttributeText.Contains("sentinel:auto-category")), Is.True);
        Assert.That(stale.Select(e => e.CategoryName), Does.Not.Contain("Handwritten"));

        // Missing attributes still get added; present ones (marked or hand-written) are skipped, not re-added.
        Assert.That(Adds(project, TestCategoryLevel.Method), Is.EqualTo(new[]
        {
            "HandWrittenOnly:Gamma", "MarkedButNoLongerReferenced:Beta", "MarkedForDeletedType:Alpha",
        }));
        Assert.That(project.SkippedExisting, Is.EqualTo(2));
        Assert.That(project.StaleToRemove, Is.EqualTo(2));
    }

    [Test]
    public async Task MarkedMethodAttribute_NowCoveredByClassLevel_IsKeptAsSkippedExisting()
    {
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Dominant
    {
        [Test]
        [Category(""Alpha"")] // sentinel:auto-category
        public void A1() { new Alpha().Run(); }
        [Test] public void A2() { new Alpha().Run(); }
        [Test] public void A3() { new Alpha().Run(); }
    }
}";

        var project = Project(await PlanAsync(BasicTargets, tests));

        // Redundant with the new class-level attribute, but not stale: it stays and counts as skipped-existing.
        Assert.That(project.Edits.Where(e => e.Kind == TestCategoryEditKind.RemoveStale), Is.Empty);
        Assert.That(project.StaleToRemove, Is.EqualTo(0));
        Assert.That(project.SkippedExisting, Is.EqualTo(1));
        Assert.That(Adds(project, TestCategoryLevel.Class), Is.EqualTo(new[] { "<class>:Alpha" }));
        Assert.That(Adds(project, TestCategoryLevel.Method), Is.Empty);
    }

    [Test]
    public async Task InterfaceMember_MapsToTheSingleImplementation_ElseTheInterface()
    {
        var targets = @"
namespace Targets.Engines
{
    public interface IOne { void Go(); }
    public class OneImpl : IOne { public void Go() { } }
    public interface ITwo { void Go(); }
    public class TwoA : ITwo { public void Go() { } }
    public class TwoB : ITwo { public void Go() { } }
}";
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Ifaces
    {
        [Test] public void ViaSingle() { IOne one = new OneImpl(); one.Go(); }
        [Test] public void ViaMulti() { ITwo two = new TwoA(); two.Go(); }
    }
}";

        var project = Project(await PlanAsync(targets, tests, new TestCategoryPlanOptions("Targets", MaxTestShare: 1.0, ClassLevelThreshold: 1.0)));
        var methodAdds = Adds(project, TestCategoryLevel.Method);

        Assert.That(methodAdds, Does.Contain("ViaSingle:OneImpl"));
        Assert.That(methodAdds, Does.Not.Contain("ViaSingle:IOne"));
        Assert.That(methodAdds, Does.Contain("ViaMulti:ITwo"));
        Assert.That(methodAdds, Does.Contain("ViaMulti:TwoA"));
    }

    [Test]
    public async Task GenericTypeArgumentAndTypeofAndNameof_CountAsReferences()
    {
        var targets = @"
namespace Targets.Engines
{
    public class ViaGeneric { }
    public class ViaTypeof { }
    public class ViaNameof { public int Member; }
    public static class Helper { public static T Make<T>() where T : new() { return new T(); } }
}";
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Refs
    {
        [Test] public void G() { Helper.Make<ViaGeneric>(); }
        [Test] public void T() { var t = typeof(ViaTypeof); }
        [Test] public void N() { var n = nameof(ViaNameof.Member); }
    }
}";

        var project = Project(await PlanAsync(targets, tests, new TestCategoryPlanOptions("Targets", MaxTestShare: 1.0, ClassLevelThreshold: 1.0)));

        Assert.That(Adds(project, TestCategoryLevel.Method), Is.EqualTo(new[]
        {
            "G:Helper", "G:ViaGeneric", "N:ViaNameof", "T:ViaTypeof",
        }));
    }

    [Test]
    public async Task ExcludedTargets_AreNeverCategories_AndExcludedTestsAreNotScanned()
    {
        var tests = @"
using NUnit.Framework;
using Targets.Engines;
namespace Fixtures
{
    public class Keep { [Test] public void K() { new Alpha().Run(); new Beta(); } }
}
namespace Fixtures.Skipped
{
    public class Skip { [Test] public void S() { new Alpha().Run(); } }
}";

        var plan = await PlanAsync(BasicTargets, tests, new TestCategoryPlanOptions(
            "Targets", ExcludedTargets: "Targets.Engines.Beta", ExcludedTests: "Fixtures.Skipped", MaxTestShare: 1.0));
        var project = Project(plan);

        Assert.That(project.TestsScanned, Is.EqualTo(1));
        Assert.That(Adds(project, TestCategoryLevel.Class), Is.EqualTo(new[] { "<class>:Alpha" }));
        Assert.That(project.Edits.Select(e => e.CategoryName), Does.Not.Contain("Beta"));
        Assert.That(plan.Parameters!.ExcludedTargets, Is.EqualTo(new[] { "Targets.Engines.Beta" }));
    }

    [Test]
    public async Task XUnit_IsDetectedFromReferencedTypes_AndUsesTraitAttributes()
    {
        var tests = @"
namespace Xunit
{
    [System.AttributeUsage(System.AttributeTargets.Method)] public class FactAttribute : System.Attribute { }
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, AllowMultiple = true)]
    public class TraitAttribute : System.Attribute { public TraitAttribute(string name, string value) { } }
}
namespace Fixtures
{
    using Targets.Engines;
    public class XFixture
    {
        [Xunit.Fact] public void F1() { new Alpha().Run(); }
        [Xunit.Fact] public void F2() { new Beta(); }
        [Xunit.Fact] [Xunit.Trait(""Category"", ""Gamma"")] // sentinel:auto-category
        public void F3() { new Beta(); }
    }
}";

        var plan = await TestCategoryTaggingEngine.PlanForSolutionAsync(
            BuildSolution(BasicTargets, tests, useNUnit: false),
            new TestCategoryPlanOptions("Targets", MaxTestShare: 1.0, ClassLevelThreshold: 1.0));
        var project = Project(plan);

        Assert.That(project.Framework, Is.EqualTo(TestCategoryFramework.XUnit));
        Assert.That(project.Edits.Where(e => e.Kind == TestCategoryEditKind.Add).Select(e => e.AttributeText),
            Does.Contain("[Trait(\"Category\", \"Alpha\")] // sentinel:auto-category"));
        Assert.That(project.Edits.Single(e => e.Kind == TestCategoryEditKind.RemoveStale).CategoryName, Is.EqualTo("Gamma"));
    }

    [Test]
    public async Task InvalidOptions_ReturnStructuredErrors()
    {
        var empty = await PlanAsync(BasicTargets, "namespace Fixtures { }", new TestCategoryPlanOptions(" , "));
        Assert.That(empty.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(empty.Error!.Message, Does.Contain("targets"));

        var badShare = await PlanAsync(BasicTargets, "namespace Fixtures { }", new TestCategoryPlanOptions("Targets", MaxTestShare: 1.5));
        Assert.That(badShare.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.InvalidArgument));
        Assert.That(badShare.Error!.Message, Does.Contain("maxTestShare"));

        var badScope = await PlanAsync(BasicTargets, "namespace Fixtures { }", new TestCategoryPlanOptions("Targets", TestScope: "NoSuchProject"));
        Assert.That(badScope.Error?.ErrorCode, Is.EqualTo(ToolErrorCode.NotFound));
        Assert.That(badScope.Error!.Message, Does.Contain("NoSuchProject"));
    }
}
